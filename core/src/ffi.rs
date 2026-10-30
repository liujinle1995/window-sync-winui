//! C ABI 导出层 —— 供 WinUI 3 前端通过 P/Invoke 调用。
//!
//! 统一约定：
//! * 输入字符串：UTF-8、NUL 结尾的 `*const c_char`（C# 侧 `[MarshalAs(UnmanagedType.LPUTF8Str)]`）。
//! * 返回值：堆上分配的 UTF-8 JSON 信封 `{"ok":true,"data":...}` 或 `{"ok":false,"error":"..."}`，
//!   调用方必须调用 `wsync_free` 释放（Rust 侧用 `CString::from_raw` 回收，分配器一致）。
//! * 回调：宿主注册的 `extern "C" fn(*const c_char)`，字符串生命周期只在回调内有效。

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::panic::{self, AssertUnwindSafe};

use serde::Serialize;

use crate::{
    get_processes, get_sync_status, get_windows, focus_window, kill_process, kill_process_by_name,
    launch_app, start_sync, stop_sync, text_sync, update_sync_options, LogCb, StatusCb, SyncConfig,
};

const VERSION: &str = env!("CARGO_PKG_VERSION");

// ── 工具 ──────────────────────────────────────────────────────────────────

fn to_ptr(s: String) -> *mut c_char {
    CString::new(s).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

fn ok<T: Serialize>(data: &T) -> *mut c_char {
    to_ptr(serde_json::json!({ "ok": true, "data": data }).to_string())
}

fn err(msg: impl std::fmt::Display) -> *mut c_char {
    to_ptr(serde_json::json!({ "ok": false, "error": msg.to_string() }).to_string())
}

/// 包一层 catch_unwind，杜绝 panic 跨越 FFI 边界（release 下 panic=abort 会直接崩进程）
fn guard(f: impl FnOnce() -> *mut c_char) -> *mut c_char {
    match panic::catch_unwind(AssertUnwindSafe(f)) {
        Ok(p) => p,
        Err(_) => err("核心引擎内部错误（panic）"),
    }
}

unsafe fn arg(p: *const c_char) -> Result<String, String> {
    if p.is_null() {
        return Err("收到空指针参数".into());
    }
    Ok(CStr::from_ptr(p).to_string_lossy().into_owned())
}

unsafe fn parse<T: serde::de::DeserializeOwned>(p: *const c_char) -> Result<T, String> {
    let s = arg(p)?;
    serde_json::from_str(&s).map_err(|e| format!("参数解析失败: {e}"))
}

// ── 生命周期与回调 ────────────────────────────────────────────────────────

/// 释放由本模块返回的字符串
#[no_mangle]
pub extern "C" fn wsync_free(s: *mut c_char) {
    if !s.is_null() {
        unsafe { drop(CString::from_raw(s)); }
    }
}

/// 注册状态推送回调（传 null 表示注销）
#[no_mangle]
pub extern "C" fn wsync_set_status_callback(cb: Option<StatusCb>) {
    crate::set_status_callback(cb);
}

/// 注册日志回调（传 null 表示注销）
#[no_mangle]
pub extern "C" fn wsync_set_log_callback(cb: Option<LogCb>) {
    crate::set_log_callback(cb);
}

#[no_mangle]
pub extern "C" fn wsync_version() -> *mut c_char {
    to_ptr(VERSION.to_string())
}

// ── 查询 ──────────────────────────────────────────────────────────────────

/// 枚举所有带可见窗口的进程（按进程名排序）
#[no_mangle]
pub extern "C" fn wsync_get_processes() -> *mut c_char {
    guard(|| ok(&get_processes()))
}

/// 枚举所有可见窗口（平铺）
#[no_mangle]
pub extern "C" fn wsync_get_windows() -> *mut c_char {
    guard(|| ok(&get_windows()))
}

/// 当前同步状态
#[no_mangle]
pub extern "C" fn wsync_get_sync_status() -> *mut c_char {
    guard(|| ok(&get_sync_status()))
}

// ── 同步控制 ──────────────────────────────────────────────────────────────

/// 启动同步。参数 `{"master_hwnd":..,"slave_hwnds":[..],"sync_mouse":..,"sync_keyboard":..,"sync_clipboard":..}`
#[no_mangle]
pub unsafe extern "C" fn wsync_start_sync(config_json: *const c_char) -> *mut c_char {
    guard(|| match parse::<SyncConfig>(config_json) {
        Ok(cfg) => match start_sync(cfg) {
            Ok(msg) => ok(&msg),
            Err(e)  => err(e),
        },
        Err(e) => err(e),
    })
}

/// 停止同步
#[no_mangle]
pub extern "C" fn wsync_stop_sync() -> *mut c_char {
    guard(|| match stop_sync() {
        Ok(msg) => ok(&msg),
        Err(e)  => err(e),
    })
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
            Ok(o) => match update_sync_options(o.sync_mouse, o.sync_keyboard, o.sync_clipboard) {
                Ok(()) => ok(&true),
                Err(e) => err(e),
            },
            Err(e) => err(e),
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
            Err(e) => err(e),
        }
    })
}

/// 聚焦并还原指定窗口
#[no_mangle]
pub extern "C" fn wsync_focus_window(hwnd: i64) -> *mut c_char {
    guard(|| match focus_window(hwnd) {
        Ok(()) => ok(&true),
        Err(e) => err(e),
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
            Ok(a) => match launch_app(a.path, a.args) {
                Ok(pid) => ok(&pid),
                Err(e)  => err(e),
            },
            Err(e) => err(e),
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
            Ok(a) => match kill_process(a.pid) {
                Ok(()) => ok(&true),
                Err(e) => err(e),
            },
            Err(e) => err(e),
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
            Ok(a) => match kill_process_by_name(a.exe_name) {
                Ok(n) => ok(&n),
                Err(e) => err(e),
            },
            Err(e) => err(e),
        }
    })
}
