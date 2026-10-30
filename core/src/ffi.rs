//! C ABI 导出层 —— 供 WinUI 3 前端通过 P/Invoke 调用。
//!
//! # 两条通道
//!
//! | 用途 | 通道 | 代价 |
//! |---|---|---|
//! | 启停同步、改配置、进程管理、聚焦 | **JSON 信封** | 一次分配 + 一次解析，低频无所谓 |
//! | 窗口列表、状态轮询、大段文本 | **二进制 / POD** | 零解析，只有一次内存拷贝 |
//!
//! 控制面用 JSON 是划算的：可读、加字段不破坏兼容、出问题一眼能看出来。
//! 但数据面（几百个窗口、每秒几次状态、几千字文本）用 JSON，等于让两边都替
//! 序列化库打工：Rust 侧拼字符串分配，C# 侧再 `JsonDocument.Parse` 一遍，
//! 中间还夹着一堆转义。所以高频那几项单独开了二进制接口。
//!
//! # 统一错误码
//!
//! 失败信封是 `{"ok":false,"code":"INVALID_ARG","error":"…"}`。
//! `code` 是给程序看的稳定标识（[`abi::Code`]），`error` 是给人看的中文说明 ——
//! 文案随便改，宿主不必跟着改匹配逻辑。
//!
//! 二进制接口没法返回信封，约定是：**返回 `i32` 错误码，0 表示成功**；
//! 需要细节时调 `wsync_last_error()` 拿一条 JSON（调用方负责 `wsync_free`）。
//!
//! # panic
//!
//! 每个导出都套了 `catch_unwind`，但**这只在 `panic = "unwind"` 时才有意义**。
//! `Cargo.toml` 的 release profile 因此显式写成 `panic = "unwind"`；
//! 若有人改回 `abort`，一次 panic 会直接带走整个 WinUI 进程。
//! 宿主可以调 `wsync_panic_mode()` 自查（0 = unwind，1 = abort），
//! 启动时发现是 abort 就该在日志里警告。

use std::cell::RefCell;
use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::panic::{self, AssertUnwindSafe};

use serde::Serialize;

use crate::abi::{self, Code};
use crate::{
    focus_window, get_processes, get_sync_status, get_windows, kill_process, kill_process_by_name,
    launch_app, start_sync, stop_sync, swap_master, text_sync, update_sync_options, LogCb,
    StatusCb, StatusNotifyCb, SyncConfig,
};

const VERSION: &str = env!("CARGO_PKG_VERSION");

/// 静态版本串，**不需要释放**（区别于 `wsync_version`）
static VERSION_C: &str = concat!(env!("CARGO_PKG_VERSION"), "\0");

// ── 最近一次错误（给二进制接口用） ────────────────────────────────────────

thread_local! {
    /// 二进制接口没有返回值可用，失败原因暂存在线程本地。
    /// 用线程本地而不是全局：宿主可能在多个线程上调，互相覆盖反而更难查。
    static LAST_ERROR: RefCell<Option<(Code, String)>> = const { RefCell::new(None) };
}

fn set_last_error(code: Code, msg: impl Into<String>) {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = Some((code, msg.into())));
}

fn clear_last_error() {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = None);
}

// ── 工具 ──────────────────────────────────────────────────────────────────

fn to_ptr(s: String) -> *mut c_char {
    CString::new(s).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

fn ok<T: Serialize>(data: &T) -> *mut c_char {
    to_ptr(serde_json::json!({ "ok": true, "data": data }).to_string())
}

/// 失败信封：带稳定错误码
fn fail(code: Code, msg: impl std::fmt::Display) -> *mut c_char {
    to_ptr(
        serde_json::json!({
            "ok": false,
            "code": code.as_str(),
            "code_value": code.as_i32(),
            "error": msg.to_string(),
        })
        .to_string(),
    )
}

fn err(msg: impl std::fmt::Display) -> *mut c_char {
    let text = msg.to_string();
    fail(Code::classify(&text), text)
}

/// 包一层 catch_unwind，杜绝 panic 跨越 FFI 边界。
///
/// 注意：`panic = "abort"` 构建下这一层完全不起作用（进程直接终止），
/// 所以 release profile 必须是 unwind —— 见 `wsync_panic_mode`。
fn guard(f: impl FnOnce() -> *mut c_char) -> *mut c_char {
    match panic::catch_unwind(AssertUnwindSafe(f)) {
        Ok(p) => p,
        Err(_) => {
            crate::log("核心引擎内部错误（panic 已被拦截）");
            fail(Code::Internal, "核心引擎内部错误（panic）")
        }
    }
}

/// 返回指针的二进制接口的 panic 兜底：出错返回空指针，细节进线程本地
fn guard_ptr(f: impl FnOnce() -> *mut u8) -> *mut u8 {
    match panic::catch_unwind(AssertUnwindSafe(f)) {
        Ok(p) => p,
        Err(_) => {
            set_last_error(Code::Internal, "核心引擎内部错误（panic）");
            std::ptr::null_mut()
        }
    }
}

/// 二进制接口的 panic 兜底：返回错误码，细节进线程本地
fn guard_code(f: impl FnOnce() -> i32) -> i32 {
    match panic::catch_unwind(AssertUnwindSafe(f)) {
        Ok(v) => v,
        Err(_) => {
            set_last_error(Code::Internal, "核心引擎内部错误（panic）");
            Code::Internal.as_i32()
        }
    }
}

unsafe fn arg(p: *const c_char) -> Result<String, String> {
    if p.is_null() {
        return Err("收到空指针参数".into());
    }
    let s = CStr::from_ptr(p).to_string_lossy().into_owned();
    if s.trim().is_empty() {
        return Err("参数是空字符串".into());
    }
    Ok(s)
}

unsafe fn parse<T: serde::de::DeserializeOwned>(p: *const c_char) -> Result<T, (Code, String)> {
    let s = arg(p).map_err(|e| (Code::InvalidArg, e))?;
    serde_json::from_str(&s).map_err(|e| (Code::InvalidJson, format!("参数解析失败: {e}")))
}

/// 把 `Result<_, String>` 统一成信封
fn from_result(r: Result<String, String>) -> *mut c_char {
    match r {
        Ok(msg) => ok(&msg),
        Err(e) => err(e),
    }
}

// ── 版本与 ABI ────────────────────────────────────────────────────────────

/// 二进制布局版本。和核心版本号是两回事：核心改了业务逻辑不用动 ABI，
/// 只有 `#[repr(C)]` 结构或导出签名变了才 +1。
#[no_mangle]
pub extern "C" fn wsync_abi_version() -> u32 {
    abi::ABI_VERSION
}

/// 核心版本号（静态字符串，不需要 `wsync_free`）
#[no_mangle]
pub extern "C" fn wsync_core_version() -> *const c_char {
    VERSION_C.as_ptr() as *const c_char
}

/// 0 = `panic = "unwind"`（panic 能被兜住），1 = `abort`（一次 panic 直接杀进程）。
#[no_mangle]
pub extern "C" fn wsync_panic_mode() -> u32 {
    if cfg!(panic = "abort") { 1 } else { 0 }
}

/// 释放由本模块返回的字符串
#[no_mangle]
pub unsafe extern "C" fn wsync_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

/// 释放二进制块（`wsync_get_windows_bin` 的返回值）。
///
/// 长度必须原样传回来：`Vec<u8>` 转 `Box<[u8]>` 后，回收时要用同一段长度重建。
#[no_mangle]
pub unsafe extern "C" fn wsync_free_bytes(ptr: *mut u8, len: u32) {
    if !ptr.is_null() && len > 0 {
        let slice = std::ptr::slice_from_raw_parts_mut(ptr, len as usize);
        drop(Box::from_raw(slice));
    }
}

/// 最近一次二进制调用的失败原因（JSON：`{"code":"..","error":".."}`）。
/// 成功时为 `{"code":"OK"}`。调用方负责 `wsync_free`。
#[no_mangle]
pub extern "C" fn wsync_last_error() -> *mut c_char {
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some((code, msg)) => to_ptr(
            serde_json::json!({ "code": code.as_str(), "code_value": code.as_i32(), "error": msg })
                .to_string(),
        ),
        None => to_ptr(serde_json::json!({ "code": "OK" }).to_string()),
    })
}

// ── 回调注册 / 注销 ───────────────────────────────────────────────────────
//
// 三个注册入口都返回 token（u64），注销时必须带同一个 token：
// 宿主换回调时不会因为时序问题把新装的清掉。返回 0 表示「本次是注销」。

/// 注册状态回调（JSON 载荷）。返回 token；传 null 表示注销并返回 0。
#[no_mangle]
pub extern "C" fn wsync_set_status_callback(cb: Option<StatusCb>) -> u64 {
    crate::set_status_callback(cb)
}

/// 注册无载荷状态通知（只给 revision）。高频场景用这个。
#[no_mangle]
pub extern "C" fn wsync_set_status_notifier(cb: Option<StatusNotifyCb>) -> u64 {
    crate::set_status_notifier(cb)
}

/// 注册日志回调。
#[no_mangle]
pub extern "C" fn wsync_set_log_callback(cb: Option<LogCb>) -> u64 {
    crate::set_log_callback(cb)
}

#[no_mangle]
pub extern "C" fn wsync_clear_status_callback(token: u64) -> bool {
    crate::clear_status_callback(token)
}

#[no_mangle]
pub extern "C" fn wsync_clear_status_notifier(token: u64) -> bool {
    crate::clear_status_notifier(token)
}

#[no_mangle]
pub extern "C" fn wsync_clear_log_callback(token: u64) -> bool {
    crate::clear_log_callback(token)
}

/// 宿主退出前调用：停同步 + 摘掉所有回调，避免回调打到已经析构的宿主对象上。
#[no_mangle]
pub extern "C" fn wsync_shutdown() {
    let _ = panic::catch_unwind(AssertUnwindSafe(crate::shutdown_all));
}

/// 旧入口，返回堆分配的版本串（保留兼容）
#[no_mangle]
pub extern "C" fn wsync_version() -> *mut c_char {
    to_ptr(VERSION.to_string())
}

// ── 状态：POD 快照（高频） ────────────────────────────────────────────────

/// 一次读出全部状态，按值返回，**零分配**。
#[no_mangle]
pub extern "C" fn wsync_status_snapshot() -> abi::StatusSnapshot {
    crate::status_snapshot()
}

/// 状态版本号；宿主只读这一个数就能判断要不要刷新。
#[no_mangle]
pub extern "C" fn wsync_status_revision() -> u64 {
    crate::status_revision()
}

// ── 查询 ──────────────────────────────────────────────────────────────────

/// 枚举所有带可见窗口的进程（按进程名排序）
#[no_mangle]
pub extern "C" fn wsync_get_processes() -> *mut c_char {
    guard(|| ok(&get_processes()))
}

/// 枚举所有可见窗口（平铺，JSON）。低频或调试用；列表刷新请用 `wsync_get_windows_bin`。
#[no_mangle]
pub extern "C" fn wsync_get_windows() -> *mut c_char {
    guard(|| ok(&get_windows()))
}

/// 枚举所有可见窗口（二进制定长记录）。
///
/// 返回值为堆上分配的字节块，`out_len` 写出长度，**用 `wsync_free_bytes` 释放**。
/// 布局见 [`abi::WindowRecordV1`] 与 [`abi::encode_windows`]。
#[no_mangle]
pub unsafe extern "C" fn wsync_get_windows_bin(out_len: *mut u32) -> *mut u8 {
    if out_len.is_null() {
        set_last_error(Code::InvalidArg, "out_len 是空指针");
        return std::ptr::null_mut();
    }
    guard_ptr(|| {
        let blob = abi::encode_windows(&get_windows());
        let boxed = blob.bytes.into_boxed_slice();
        let len = boxed.len() as u32;
        let raw = Box::into_raw(boxed) as *mut u8;
        *out_len = len;
        clear_last_error();
        raw
    })
}

/// 当前同步状态（JSON，兼容旧宿主）。新代码请用 `wsync_status_snapshot`。
#[no_mangle]
pub extern "C" fn wsync_get_sync_status() -> *mut c_char {
    guard(|| ok(&get_sync_status()))
}

// ── 文本缓冲区（大段文本） ────────────────────────────────────────────────

/// 读目标窗口的文本到调用方缓冲区（UTF-8，不带结尾 0）。
///
/// * 成功返回 0，`out_len` 为写入的字节数。
/// * 缓冲区不够时返回 `BUFFER_TOO_SMALL`，`out_len` 给出**所需**字节数，
///   宿主按这个值扩容后再调一次 —— 避免「先猜大小」的二次分配。
#[no_mangle]
pub unsafe extern "C" fn wsync_text_read(
    hwnd: i64,
    buf: *mut u8,
    cap: u32,
    out_len: *mut u32,
) -> i32 {
    guard_code(|| {
        if out_len.is_null() || (buf.is_null() && cap > 0) {
            set_last_error(Code::InvalidArg, "输出缓冲区或长度指针为空");
            return Code::InvalidArg.as_i32();
        }
        *out_len = 0;
        let h = windows::Win32::Foundation::HWND(hwnd as isize as *mut _);
        let Some(units) = text_sync::read_control_text(h) else {
            let msg = if text_sync::is_standard_control(h) {
                "目标控件无响应，本次没读到文本"
            } else {
                "目标窗口不是标准文本控件（自绘输入框读不到内容）"
            };
            let code = if text_sync::is_standard_control(h) { Code::WindowGone } else { Code::Unsupported };
            set_last_error(code, msg);
            return code.as_i32();
        };
        let utf8 = String::from_utf16_lossy(&units).into_bytes();
        *out_len = utf8.len() as u32;
        if utf8.len() > cap as usize {
            set_last_error(
                Code::BufferTooSmall,
                format!("需要 {} 字节，给了 {cap} 字节", utf8.len()),
            );
            return Code::BufferTooSmall.as_i32();
        }
        std::ptr::copy_nonoverlapping(utf8.as_ptr(), buf, utf8.len());
        clear_last_error();
        Code::Ok.as_i32()
    })
}

/// 把一段 UTF-8 文本整段写进目标窗口的文本控件（替换原内容，光标停到末尾）。
#[no_mangle]
pub unsafe extern "C" fn wsync_text_write(hwnd: i64, buf: *const u8, len: u32) -> i32 {
    guard_code(|| {
        if buf.is_null() && len > 0 {
            set_last_error(Code::InvalidArg, "文本指针为空但长度非零");
            return Code::InvalidArg.as_i32();
        }
        let bytes = if len == 0 {
            &[][..]
        } else {
            std::slice::from_raw_parts(buf, len as usize)
        };
        let Ok(text) = std::str::from_utf8(bytes) else {
            set_last_error(Code::InvalidArg, "文本不是合法的 UTF-8");
            return Code::InvalidArg.as_i32();
        };
        let h = windows::Win32::Foundation::HWND(hwnd as isize as *mut _);
        let units: Vec<u16> = text.encode_utf16().collect();
        if text_sync::write_control_text(h, &units) {
            clear_last_error();
            Code::Ok.as_i32()
        } else {
            set_last_error(Code::WindowGone, "写入失败：目标窗口不存在或不是文本控件");
            Code::WindowGone.as_i32()
        }
    })
}

// ── 同步控制 ──────────────────────────────────────────────────────────────

/// 启动同步。参数 `{"master_hwnd":..,"slave_hwnds":[..],"sync_mouse":..,"sync_keyboard":..,"sync_clipboard":..}`
#[no_mangle]
pub unsafe extern "C" fn wsync_start_sync(config_json: *const c_char) -> *mut c_char {
    guard(|| match parse::<SyncConfig>(config_json) {
        Ok(cfg) => match validate_config(&cfg) {
            Err((code, msg)) => fail(code, msg),
            Ok(()) => from_result(start_sync(cfg)),
        },
        Err((code, msg)) => fail(code, msg),
    })
}

/// 启动参数在过边界时先做一遍校验。
///
/// 这些都是「宿主传错了」的错，不是引擎状态问题：早点拦下来，报错能精确到字段，
/// 也避免带着一个 0 句柄一路跑到投递线程里再炸。
fn validate_config(cfg: &SyncConfig) -> Result<(), (Code, &'static str)> {
    if cfg.master_hwnd == 0 {
        return Err((Code::NoMaster, "master_hwnd 不能为 0"));
    }
    if cfg.slave_hwnds.is_empty() {
        return Err((Code::InvalidArg, "至少需要一个从窗口"));
    }
    if cfg.slave_hwnds.len() > MAX_SLAVES {
        return Err((Code::InvalidArg, "从窗口数量超过上限 16"));
    }
    if cfg.slave_hwnds.iter().any(|&h| h == 0) {
        return Err((Code::InvalidArg, "从窗口列表里有 0 句柄"));
    }
    if cfg.slave_hwnds.iter().any(|&h| h == cfg.master_hwnd) {
        return Err((Code::InvalidArg, "主窗口不能同时出现在从窗口列表里"));
    }
    if !cfg.sync_mouse && !cfg.sync_keyboard && !cfg.sync_clipboard {
        return Err((Code::InvalidArg, "鼠标 / 键盘 / 剪贴板至少要开一项"));
    }
    Ok(())
}

/// 从窗口数量上限：再多也没有实际意义，只会让每帧的投递变成 O(n) 的负担
const MAX_SLAVES: usize = 16;

/// 停止同步
#[no_mangle]
pub extern "C" fn wsync_stop_sync() -> *mut c_char {
    guard(|| from_result(stop_sync()))
}

/// 运行中修改同步项。参数 `{"sync_mouse":..,"sync_keyboard":..,"sync_clipboard":..}`
#[no_mangle]
pub unsafe extern "C" fn wsync_update_sync_options(options_json: *const c_char) -> *mut c_char {
    guard(|| {
        #[derive(serde::Deserialize)]
        struct Opts {
            #[serde(default)]
            sync_mouse: bool,
            #[serde(default)]
            sync_keyboard: bool,
            #[serde(default)]
            sync_clipboard: bool,
        }
        match parse::<Opts>(options_json) {
            Ok(o) => {
                if !o.sync_mouse && !o.sync_keyboard && !o.sync_clipboard {
                    return fail(Code::InvalidArg, "鼠标 / 键盘 / 剪贴板至少要开一项");
                }
                match update_sync_options(o.sync_mouse, o.sync_keyboard, o.sync_clipboard) {
                    Ok(()) => ok(&true),
                    Err(e) => err(e),
                }
            }
            Err((code, msg)) => fail(code, msg),
        }
    })
}

/// 开关文本同步（中文 / 粘贴内容同步）。参数 `{"enabled":true}`
///
/// 入口名保持不变（`wsync_set_text_mirror`），宿主 UI 不用改。
#[no_mangle]
pub unsafe extern "C" fn wsync_set_text_mirror(args_json: *const c_char) -> *mut c_char {
    guard(|| {
        #[derive(serde::Deserialize)]
        struct Args {
            #[serde(default)]
            enabled: bool,
        }
        match parse::<Args>(args_json) {
            Ok(a) => {
                text_sync::set_enabled(a.enabled);
                ok(&true)
            }
            Err((code, msg)) => fail(code, msg),
        }
    })
}

/// 主从对调：把指定从窗口提升为主窗口，原主窗口降为从窗口。
///
/// 同步运行中也能切 —— 引擎会在切换前放掉按住的键、重建窗口身份、作废文本基线，
/// 投递不中断。没在同步时调用会拿到 `NOT_RUNNING`（那时宿主只要改自己的选择即可，
/// 下次 `start_sync` 会按新配置重建一切）。
#[no_mangle]
pub extern "C" fn wsync_swap_master(slave_hwnd: i64) -> *mut c_char {
    guard(|| {
        if slave_hwnd == 0 {
            return fail(Code::InvalidArg, "slave_hwnd 不能为 0");
        }
        match swap_master(slave_hwnd) {
            Ok(()) => ok(&true),
            Err(e) => err(e),
        }
    })
}

/// 聚焦并还原指定窗口
#[no_mangle]
pub extern "C" fn wsync_focus_window(hwnd: i64) -> *mut c_char {
    guard(|| {
        if hwnd == 0 {
            return fail(Code::InvalidArg, "hwnd 不能为 0");
        }
        match focus_window(hwnd) {
            Ok(()) => ok(&true),
            Err(e) => err(e),
        }
    })
}

// ── 进程 / 快捷启动 ───────────────────────────────────────────────────────

/// 启动应用。参数 `{"path":"..","args":[".."]}`，返回 PID（`.lnk` 返回 4294967295）
#[no_mangle]
pub unsafe extern "C" fn wsync_launch_app(args_json: *const c_char) -> *mut c_char {
    guard(|| {
        #[derive(serde::Deserialize)]
        struct Args {
            path: String,
            #[serde(default)]
            args: Vec<String>,
        }
        match parse::<Args>(args_json) {
            Ok(a) if a.path.trim().is_empty() => fail(Code::InvalidArg, "path 不能为空"),
            Ok(a) => match launch_app(a.path, a.args) {
                Ok(pid) => ok(&pid),
                Err(e) => err(e),
            },
            Err((code, msg)) => fail(code, msg),
        }
    })
}

/// 结束进程。参数 `{"pid":123}`
#[no_mangle]
pub unsafe extern "C" fn wsync_kill_process(args_json: *const c_char) -> *mut c_char {
    guard(|| {
        #[derive(serde::Deserialize)]
        struct Args { pid: u32 }
        match parse::<Args>(args_json) {
            Ok(a) if a.pid == 0 => fail(Code::InvalidArg, "pid 不能为 0"),
            Ok(a) => match kill_process(a.pid) {
                Ok(()) => ok(&true),
                Err(e) => err(e),
            },
            Err((code, msg)) => fail(code, msg),
        }
    })
}

/// 按进程名结束进程。参数 `{"exe_name":"foo.exe"}`，返回结束数量
#[no_mangle]
pub unsafe extern "C" fn wsync_kill_process_by_name(args_json: *const c_char) -> *mut c_char {
    guard(|| {
        #[derive(serde::Deserialize)]
        struct Args { exe_name: String }
        match parse::<Args>(args_json) {
            Ok(a) if a.exe_name.trim().is_empty() => fail(Code::InvalidArg, "exe_name 不能为空"),
            Ok(a) => match kill_process_by_name(a.exe_name) {
                Ok(n) => ok(&n),
                Err(e) => err(e),
            },
            Err((code, msg)) => fail(code, msg),
        }
    })
}
