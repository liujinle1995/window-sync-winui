//! 乐享同步操作 — 核心逻辑
//! 鼠标同步：PostMessageA 投递，不移动系统光标。坐标系按消息类型区分：
//!   - `WM_MOUSEMOVE` / 左右中键：**目标窗口客户区**坐标
//!   - `WM_MOUSEWHEEL`：**屏幕**坐标（lParam 的约定，塞客户区坐标会偏）
//!   - 主/从窗口客户区尺寸不同时，按客户区比例换算，保证落在同一个相对位置
//! 键盘同步：PostMessageA WM_KEYDOWN/WM_KEYUP
//! 中文同步：见 `text_sync` 模块 —— 绑定焦点控件身份后读文本做单区间差分，
//! 再按「末尾增量 / 整段重写」投递给每个从窗口，并各自校验
//!
//! 本文件与 Tauri 版 `src-tauri/src/lib.rs` 逻辑基本一致，仅去掉了 `#[tauri::command]`
//! 与 AppHandle，状态推送改由 `ffi` 层注册的 C 回调完成。

pub mod abi;
pub mod event_bus;
pub mod ffi;
#[cfg(test)]
mod ffi_tests;
pub mod session;
#[cfg(test)]
mod session_tests;
pub mod text_sync;
pub mod window_id;
pub mod window_watch;

use event_bus::{EventBus, Pos, PressedState, Reliable, Task, BTN_LEFT, BTN_MIDDLE, BTN_RIGHT};

use once_cell::sync::Lazy;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::ffi::CString;
use std::os::raw::c_char;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use windows::Win32::Foundation::{BOOL, HWND, HGLOBAL, LPARAM, LRESULT, POINT, WPARAM};
use windows::Win32::Graphics::Gdi::{ClientToScreen, ScreenToClient};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::Threading::{OpenProcess, TerminateProcess, PROCESS_TERMINATE};
use windows::Win32::System::DataExchange::{
    OpenClipboard, CloseClipboard, GetClipboardData, IsClipboardFormatAvailable,
};
use windows::Win32::System::Memory::{GlobalLock, GlobalUnlock};

use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP,
    KEYEVENTF_UNICODE, VIRTUAL_KEY,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, EnumWindows, GetForegroundWindow, GetWindowLongA,
    IsWindow, IsWindowVisible, PostMessageA, PostMessageW, SetForegroundWindow,
    ShowWindow, GWL_STYLE, KBDLLHOOKSTRUCT,
    MSLLHOOKSTRUCT, SW_RESTORE, WM_KEYDOWN, WM_KEYUP,
    WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MBUTTONDOWN, WM_MBUTTONUP, WM_MOUSEMOVE, WM_MOUSEWHEEL,
    WM_RBUTTONDOWN, WM_RBUTTONUP, WM_SYSKEYDOWN, WM_SYSKEYUP, WS_VISIBLE,
};

const CF_UNICODETEXT: u32 = 13;

/// 状态推送回调（UTF-8 JSON，内容为 SyncStatus）
pub type StatusCb = extern "C" fn(*const c_char);
/// 日志回调（UTF-8 文本）
pub type LogCb = extern "C" fn(*const c_char);

/// 不带载荷的状态通知：只告诉宿主「revision 变成这个数了」，让它自己来拉快照。
///
/// 这是高频场景的首选 —— 回调里没有任何序列化、没有字符串分配，宿主也只需要
/// 比对一次 u64 就能决定要不要刷新。
pub type StatusNotifyCb = extern "C" fn(u64);

/// 回调注册槽：`(token, 函数指针)`。token 为 0 表示未注册。
///
/// 带 token 是为了「注销得准」：宿主可能在换窗口/退出时重装回调，
/// 拿旧 token 去注销不能把新装的那个顺手清掉。
struct Slot<T: Copy> {
    token: u64,
    cb: Option<T>,
}

static NEXT_TOKEN: AtomicU64 = AtomicU64::new(1);
static STATUS_CB: Lazy<Mutex<Slot<StatusCb>>> =
    Lazy::new(|| Mutex::new(Slot { token: 0, cb: None }));
static STATUS_NOTIFY: Lazy<Mutex<Slot<StatusNotifyCb>>> =
    Lazy::new(|| Mutex::new(Slot { token: 0, cb: None }));
static LOG_CB: Lazy<Mutex<Slot<LogCb>>> = Lazy::new(|| Mutex::new(Slot { token: 0, cb: None }));

fn register<T: Copy>(slot: &Lazy<Mutex<Slot<T>>>, cb: Option<T>) -> u64 {
    let mut s = slot.lock();
    match cb {
        None => {
            s.token = 0;
            s.cb = None;
            0
        }
        Some(f) => {
            let token = NEXT_TOKEN.fetch_add(1, Ordering::Relaxed);
            s.token = token;
            s.cb = Some(f);
            token
        }
    }
}

/// token 匹配才注销，返回是否真的清掉了
fn unregister<T: Copy>(slot: &Lazy<Mutex<Slot<T>>>, token: u64) -> bool {
    let mut s = slot.lock();
    if token == 0 || s.token != token {
        return false;
    }
    s.token = 0;
    s.cb = None;
    true
}

/// 取出回调副本后**立刻释放锁**再调用。
///
/// 原先是 `if let Some(cb) = *SLOT.lock()`，临时 guard 会活到 if-let 结束 ——
/// 也就是说回调是在持锁状态下被调用的。宿主回调里只要再回调进来一次（读状态、
/// 改选项），就是自己锁自己。这里必须先拷出来。
fn callback_of<T: Copy>(slot: &Lazy<Mutex<Slot<T>>>) -> Option<T> {
    let s = slot.lock();
    if s.token == 0 {
        None
    } else {
        s.cb
    }
}

/// 注册 / 注销状态回调（JSON 载荷，兼容旧宿主）。返回 token，0 表示未注册。
pub fn set_status_callback(cb: Option<StatusCb>) -> u64 {
    register(&STATUS_CB, cb)
}
/// 注册 / 注销无载荷状态通知。返回 token。
pub fn set_status_notifier(cb: Option<StatusNotifyCb>) -> u64 {
    register(&STATUS_NOTIFY, cb)
}
pub fn set_log_callback(cb: Option<LogCb>) -> u64 {
    register(&LOG_CB, cb)
}

pub fn clear_status_callback(token: u64) -> bool {
    unregister(&STATUS_CB, token)
}
pub fn clear_status_notifier(token: u64) -> bool {
    unregister(&STATUS_NOTIFY, token)
}
pub fn clear_log_callback(token: u64) -> bool {
    unregister(&LOG_CB, token)
}

/// 进程退出 / 宿主卸载 DLL 前调用：停同步 + 摘掉所有回调。
///
/// 顺序不能反：先停同步，否则拆线程的过程中还会往宿主回调里推东西，
/// 而那时宿主可能已经在卸载自己的代理了。
pub fn shutdown_all() {
    let _ = stop_sync();
    let _ = set_status_callback(None);
    let _ = set_status_notifier(None);
    let _ = set_log_callback(None);
}

/// 向宿主（WinUI 3）推送一条日志
pub fn log(msg: &str) {
    if let Some(cb) = callback_of(&LOG_CB) {
        if let Ok(c) = CString::new(msg.to_string()) {
            cb(c.as_ptr());
        }
    }
}

// ── 全局状态 ─────────────────────────────────────────────────────────────
//
// 这些量之所以是全局的，是因为它们要被**钩子回调**读 —— 回调是系统在任意线程上
// 直接调进来的，没有参数能带进去。它们的写入与还原由 `session::SyncSession`
// 统一负责（建立时写、拆解时清），其余模块只读不写。

pub(crate) static SYNC_ACTIVE:   AtomicBool = AtomicBool::new(false);
pub(crate) static SYNC_MOUSE:    AtomicBool = AtomicBool::new(true);
pub(crate) static SYNC_KEYBOARD: AtomicBool = AtomicBool::new(true);
pub(crate) static MASTER_HWND:   AtomicI64  = AtomicI64::new(0);

pub(crate) static SYNC_CLIPBOARD: AtomicBool = AtomicBool::new(false);
/// 记录上次剪贴板内容的哈希，避免重复处理自己写入的内容
pub(crate) static LAST_CB_HASH: Lazy<Mutex<u64>> = Lazy::new(|| Mutex::new(0));

pub(crate) static SLAVES: Lazy<Mutex<Vec<i64>>> = Lazy::new(|| Mutex::new(vec![]));
pub(crate) static BUS: Lazy<Mutex<Option<Arc<EventBus>>>> = Lazy::new(|| Mutex::new(None));

/// 还按着的键 / 鼠标键。补投抬起消息全靠它，见 `event_bus::PressedState` 的注释。
pub(crate) static PRESSED: Lazy<Mutex<PressedState>> =
    Lazy::new(|| Mutex::new(PressedState::default()));

/// 上一次推送出去的状态 JSON，用来合并重复通知。
pub(crate) static LAST_STATUS: Lazy<Mutex<String>> = Lazy::new(|| Mutex::new(String::new()));

/// 最后一次鼠标位置：补投鼠标抬起时要用同一套坐标。
pub(crate) static LAST_POS: Lazy<Mutex<Pos>> = Lazy::new(|| Mutex::new(Pos::default()));

/// 当前这一套同步会话。`None` 表示没在同步。
///
/// 锁只用来换指针：拆除动作（要 join 线程、要等监听退出）一律在锁外做，
/// 否则生命周期回调里的 `stop_sync()` 会和正在启动的 `start_sync()` 互相等死。
pub(crate) static SESSION: Lazy<Mutex<Option<session::SyncSession>>> =
    Lazy::new(|| Mutex::new(None));

/// 串行化「角色变更（主从对调）」与「身份复查」。
///
/// 两者都要读「当前主窗口是谁 + 它的身份键是什么」，而角色变更必然先改键再改句柄
/// （或者反过来），中间一定有一瞬间两边对不上。不加这把锁的话，看门狗正好落在那一瞬间，
/// 就会把刚升上来的主窗口判成「句柄被系统复用」，把整条同步停掉。
///
/// 锁内只做读写判断，**不要在持锁时调用 `stop_sync()`** —— 那要 join 线程，
/// 而线程可能正等着这把锁。
pub(crate) static ROLE_LOCK: Lazy<Mutex<()>> = Lazy::new(|| Mutex::new(()));

/// 串行化「同步选项变更」与「钩子里的按键记账」。
///
/// 关掉键盘 / 鼠标同步时要把已经按下的键补一个抬起，但补抬起与钩子记账之间有一个
/// 致命的交错：钩子读到开关还是 true → 变更线程置 false 并把按键表取空、投出抬起 →
/// 钩子这才把这次按下记进表里并投出按下。结果就是**按下的消息排在抬起之后**，
/// 从窗口从此一直认为这个键按着。
///
/// 所以两边都必须拿着这把锁：变更线程在锁内「改开关 + 取状态 + 投抬起」，
/// 钩子在锁内「看开关 + 记账 + 投按下」。锁只包住记账与入队，不做任何耗时的事。
pub(crate) static OPTIONS_LOCK: Lazy<Mutex<()>> = Lazy::new(|| Mutex::new(()));

/// 取当前事件总线。同步没启动时是 `None`，钩子直接放弃即可。
#[inline]
fn bus() -> Option<Arc<EventBus>> {
    BUS.lock().clone()
}

// ── 读取剪贴板文本 ───────────────────────────────────────────────────────
unsafe fn read_clipboard_text() -> Option<String> {
    if IsClipboardFormatAvailable(CF_UNICODETEXT).is_err() { return None; }
    if OpenClipboard(None).is_err() { return None; }

    let mut text = None;
    if let Ok(handle) = GetClipboardData(CF_UNICODETEXT) {
        let hglobal = HGLOBAL(handle.0 as *mut _);
        let ptr = GlobalLock(hglobal) as *const u16;
        if !ptr.is_null() {
            let mut len = 0usize;
            while *ptr.add(len) != 0 { len += 1; }
            if len > 0 {
                let slice = std::slice::from_raw_parts(ptr, len);
                let decoded = String::from_utf16_lossy(slice).to_string();
                if !decoded.is_empty() {
                    text = Some(decoded);
                }
            }
        }
        let _ = GlobalUnlock(hglobal);
    }

    let _ = CloseClipboard();
    text
}

// ── SendInput 工具：向当前前台窗口注入一个 Unicode 字符 ────────────────────
#[inline]
fn make_unicode_input(ch: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk:         VIRTUAL_KEY(0),
                wScan:       ch,
                dwFlags:     KEYEVENTF_UNICODE | flags,
                time:        0,
                dwExtraInfo: 0,
            },
        },
    }
}

unsafe fn send_unicode_char(ch: u32) {
    let sz = std::mem::size_of::<INPUT>() as i32;
    if ch <= 0xFFFF {
        let inputs = [
            make_unicode_input(ch as u16, KEYBD_EVENT_FLAGS(0)),
            make_unicode_input(ch as u16, KEYEVENTF_KEYUP),
        ];
        SendInput(&inputs, sz);
    } else {
        // 超出 BMP：转 UTF-16 代理对
        let c = ch - 0x10000;
        let hi = 0xD800 + (c >> 10) as u16;
        let lo = 0xDC00 + (c & 0x3FF) as u16;
        let inputs = [
            make_unicode_input(hi, KEYBD_EVENT_FLAGS(0)),
            make_unicode_input(hi, KEYEVENTF_KEYUP),
            make_unicode_input(lo, KEYBD_EVENT_FLAGS(0)),
            make_unicode_input(lo, KEYEVENTF_KEYUP),
        ];
        SendInput(&inputs, sz);
    }
}

// ── 数据结构 ──────────────────────────────────────────────────────────────
/// 窗口身份快照。定义在 `window_id`，这里导出以保持调用点不变。
pub use window_id::WindowIdentity as WindowInfo;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProcessInfo {
    pub pid: u32,
    /// 进程名（Toolhelp 给的 szExeFile，只有文件名）
    pub name: String,
    /// 可执行文件完整路径。同名不同目录的多开要靠它区分
    #[serde(default)]
    pub exe_path: String,
    pub windows: Vec<WindowInfo>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncConfig  {
    pub master_hwnd: i64,
    pub slave_hwnds: Vec<i64>,
    // 三项开关都给默认值：宿主少传一个字段不该变成「JSON 解析失败」，
    // 而应该走到下面「至少开一项」这类能看懂的报错。
    #[serde(default)]
    pub sync_mouse: bool,
    #[serde(default)]
    pub sync_keyboard: bool,
    #[serde(default)]
    pub sync_clipboard: bool,
    /// 文本镜像（中文 / 粘贴内容同步）
    #[serde(default)]
    pub text_mirror: bool,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncStatus  {
    pub active: bool,
    pub master_hwnd: i64,
    pub slave_count: usize,
    pub sync_mouse: bool,
    pub sync_keyboard: bool,
    pub sync_clipboard: bool,
    /// 文本镜像是否开启
    pub text_mirror: bool,
}

struct EnumBuf { wins: Vec<WindowInfo> }

unsafe extern "system" fn enum_cb(hwnd: HWND, lp: LPARAM) -> BOOL {
    let buf = &mut *(lp.0 as *mut EnumBuf);
    if !IsWindowVisible(hwnd).as_bool() { return BOOL(1); }
    if GetWindowLongA(hwnd, GWL_STYLE) as u32 & WS_VISIBLE.0 == 0 { return BOOL(1); }

    // 身份采集放在 window_id 里：句柄、PID、启动时间、exe 路径、类名、父窗口、
    // 客户区、DPI 一次采齐。没有标题的窗口对用户没意义，照旧跳过。
    let Some(identity) = window_id::WindowIdentity::of(hwnd) else { return BOOL(1); };
    if identity.title.is_empty() { return BOOL(1); }

    buf.wins.push(identity);
    BOOL(1)
}

pub fn get_processes() -> Vec<ProcessInfo> {
    let mut map: HashMap<u32, ProcessInfo> = HashMap::new();
    unsafe {
        let snap = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) { Ok(s)=>s, Err(_)=>return vec![] };
        let mut e = PROCESSENTRY32W::default();
        e.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        if Process32FirstW(snap, &mut e).is_ok() {
            loop {
                let pid = e.th32ProcessID;
                let name = String::from_utf16_lossy(
                    &e.szExeFile.iter().take_while(|&&c|c!=0).cloned().collect::<Vec<_>>()
                ).to_string();
                map.insert(pid, ProcessInfo { pid, name, exe_path: String::new(), windows: vec![] });
                if Process32NextW(snap, &mut e).is_err() { break; }
            }
        }
        let _ = windows::Win32::Foundation::CloseHandle(snap);
        let mut buf = EnumBuf { wins: vec![] };
        let _ = EnumWindows(Some(enum_cb), LPARAM(&mut buf as *mut _ as isize));
        for w in buf.wins { if let Some(p) = map.get_mut(&w.pid) { p.windows.push(w); } }
    }
    let mut v: Vec<ProcessInfo> = map.into_values().filter(|p|!p.windows.is_empty()).collect();
    // 只给「有窗口」的那几个进程补 exe 路径：这是 OpenProcess，给全部进程补没必要
    for p in &mut v {
        p.exe_path = window_id::process_exe_path(p.pid).unwrap_or_default();
    }
    v.sort_by(|a,b| a.name.cmp(&b.name).then_with(|| a.pid.cmp(&b.pid)));
    v
}

pub fn get_windows() -> Vec<WindowInfo> {
    unsafe {
        let mut buf = EnumBuf { wins: vec![] };
        let _ = EnumWindows(Some(enum_cb), LPARAM(&mut buf as *mut _ as isize));
        buf.wins
    }
}

/// 状态版本号：任何字段变化都会 +1。
/// 宿主靠它判断「这条通知是不是比我已经渲染的新」，也让无载荷通知成为可能 ——
/// 回调不用带任何数据，宿主拿这个数去比自己的最后一次即可。
static STATUS_REVISION: AtomicU64 = AtomicU64::new(1);

pub fn status_revision() -> u64 {
    STATUS_REVISION.load(Ordering::Acquire)
}

/// 一次读出全部状态（POD，无分配）。
///
/// 各处字段本来就是原子量 / 短锁，这里不做「一致快照」的强同步 —— 从窗口数量
/// 与 active 之间允许差一拍，因为宿主渲染的是「当前大概什么样」，
/// 而真正的变更都由随后的通知再拉一次。要精确的话看 `revision` 是否变化即可。
pub fn status_snapshot() -> abi::StatusSnapshot {
    let mut flags = 0u32;
    if SYNC_ACTIVE.load(Ordering::Relaxed) { flags |= abi::status_flag::ACTIVE; }
    if SYNC_MOUSE.load(Ordering::Relaxed) { flags |= abi::status_flag::MOUSE; }
    if SYNC_KEYBOARD.load(Ordering::Relaxed) { flags |= abi::status_flag::KEYBOARD; }
    if SYNC_CLIPBOARD.load(Ordering::Relaxed) { flags |= abi::status_flag::CLIPBOARD; }
    if text_sync::is_enabled() { flags |= abi::status_flag::TEXT_MIRROR; }

    abi::StatusSnapshot {
        abi: abi::ABI_VERSION,
        flags,
        slave_count: SLAVES.lock().len() as u32,
        reserved: 0,
        master_hwnd: MASTER_HWND.load(Ordering::Relaxed),
        revision: STATUS_REVISION.load(Ordering::Acquire),
    }
}

fn emit_status() {
    let status = get_sync_status();
    let json = match serde_json::to_string(&status) {
        Ok(j) => j,
        Err(_) => return,
    };

    // 合并重复通知：状态没变就不推。调用点很多（启停、改选项、从窗口掉线…），
    // 同一个状态被连着推几次纯属浪费宿主 UI 的重绘。
    let revision = {
        let mut last = LAST_STATUS.lock();
        if *last == json { return; }
        *last = json.clone();
        STATUS_REVISION.fetch_add(1, Ordering::AcqRel) + 1
    };

    // 新的无载荷通道：只传版本号，宿主自己来拉快照。
    // 先拿副本再出锁 —— 宿主回调里可能又回调进来读状态。
    if let Some(notify) = callback_of(&STATUS_NOTIFY) {
        notify(revision);
    }

    // 旧的 JSON 通道保留，给还在用它的宿主
    if let Some(cb) = callback_of(&STATUS_CB) {
        if let Ok(c) = CString::new(json) {
            cb(c.as_ptr());
        }
    }
}

// ── 工作线程：消费事件总线 ────────────────────────────────────────────────
//
// 输入与文本分成两个线程：文本注入要逐字 SetForegroundWindow + sleep(6ms)，
// 一次几百字就是好几秒。放在同一个线程里会把鼠标键盘全堵在后面，队列必然堆满 ——
// 而这正是老实现丢事件的直接诱因之一。

/// 当前还活着的从窗口。顺手把已经销毁的窗口从列表里剔除。
fn live_slaves() -> Vec<HWND> {
    let (alive, removed) = {
        let mut list = SLAVES.lock();
        let before = list.len();
        let alive: Vec<HWND> = list
            .iter()
            .map(|&h| HWND(h as *mut _))
            .filter(|h| unsafe { IsWindow(*h).as_bool() })
            .collect();
        let removed = alive.len() != before;
        if removed {
            *list = alive.iter().map(|h| h.0 as isize as i64).collect();
        }
        (alive, removed)
    };

    if removed {
        // 从窗口失效：不再往它投递。它自己的按键状态随窗口销毁一起没了，
        // 其余从窗口的按键状态保持不变 —— 补抬起只针对还活着的窗口，
        // 否则会把别的窗口正常按着的键也放掉。
        log("有从窗口已关闭，已从同步列表移除");
        emit_status();
    }

    alive
}

/// 把鼠标消息投给所有从窗口。`pos` 是主窗口那套坐标，每个从窗口各自换算。
fn post_mouse(msg: u32, pos: Pos) {
    if !SYNC_MOUSE.load(Ordering::Relaxed) { return; }
    *LAST_POS.lock() = pos;

    for slave in live_slaves() {
        let (x, y) = map_client_point(pos.x, pos.y, pos.master_w, pos.master_h, slave);
        if let Some((wp, lp)) = mouse_message(msg, x, y, &pos, 0, slave) {
            unsafe { let _ = PostMessageA(slave, msg, wp, lp); }
        }
    }
}

/// 投递累计后的滚轮量。累计值可能很大（一次攒了几十个刻度），拆成若干笔发。
fn post_wheel(delta: i32, pos: Pos) {
    if !SYNC_MOUSE.load(Ordering::Relaxed) { return; }
    *LAST_POS.lock() = pos;

    // 每个消息最多带 10 个刻度，避免一次极端累计把高 16 位顶出 i16 范围
    const MAX_STEP: i32 = 1200;
    let mut remaining = delta;
    while remaining != 0 {
        let step = remaining.clamp(-MAX_STEP, MAX_STEP);
        remaining -= step;
        for slave in live_slaves() {
            let (x, y) = map_client_point(pos.x, pos.y, pos.master_w, pos.master_h, slave);
            if let Some((wp, lp)) = mouse_message(
                WM_MOUSEWHEEL, x, y, &pos, step as i16, slave,
            ) {
                unsafe { let _ = PostMessageA(slave, WM_MOUSEWHEEL, wp, lp); }
            }
        }
    }
}

fn post_key(down: bool, vk: u32, scan: u32) {
    if !SYNC_KEYBOARD.load(Ordering::Relaxed) { return; }

    let (msg, lp) = if down {
        (WM_KEYDOWN, (1u32 | (scan << 16)) as isize)
    } else {
        (WM_KEYUP, (0xC0000001u32 | (scan << 16)) as isize)
    };

    for slave in live_slaves() {
        unsafe { let _ = PostMessageA(slave, msg, WPARAM(vk as usize), LPARAM(lp)); }
    }
}

/// 补投抬起：把还按着的键和鼠标键全部放开。
fn post_release(keys: &[(u32, u32)], buttons: u32) {
    if keys.is_empty() && buttons == 0 { return; }

    let targets = live_slaves();
    if targets.is_empty() { return; }

    for &(vk, scan) in keys {
        let lp = (0xC0000001u32 | (scan << 16)) as isize;
        for &slave in &targets {
            unsafe { let _ = PostMessageA(slave, WM_KEYUP, WPARAM(vk as usize), LPARAM(lp)); }
        }
    }

    let pos = *LAST_POS.lock();
    for (mask, msg) in [
        (BTN_LEFT, WM_LBUTTONUP),
        (BTN_RIGHT, WM_RBUTTONUP),
        (BTN_MIDDLE, WM_MBUTTONUP),
    ] {
        if buttons & mask == 0 { continue; }
        for &slave in &targets {
            let (x, y) = map_client_point(pos.x, pos.y, pos.master_w, pos.master_h, slave);
            if let Some((wp, lp)) = mouse_message(msg, x, y, &pos, 0, slave) {
                unsafe { let _ = PostMessageA(slave, msg, wp, lp); }
            }
        }
    }
}

/// 输入投递线程：把总线上的任务投给每个从窗口。
///
/// 线程句柄交给调用方（`session::EventWorker`）持有 —— 谁起谁 join，
/// 不再挂在全局静态里等别的模块来收。
pub(crate) fn spawn_input_worker(bus: Arc<EventBus>) -> JoinHandle<()> {
    std::thread::spawn(move || {
        while let Some(batch) = bus.next_input(64) {
            let active = SYNC_ACTIVE.load(Ordering::Relaxed);
            for task in batch {
                match task {
                    // 停止时排进来的「补抬起」必须照投，否则最后按住的那些键会卡在从窗口
                    Task::Reliable(Reliable::Release { keys, buttons }) => post_release(&keys, buttons),
                    other => {
                        if !active { continue; }
                        match other {
                            Task::Move(pos) => post_mouse(WM_MOUSEMOVE, pos),
                            Task::Wheel { delta, pos } => post_wheel(delta, pos),
                            Task::Reliable(Reliable::Button { msg, pos }) => post_mouse(msg, pos),
                            Task::Reliable(Reliable::Key { down, vk, scan }) => post_key(down, vk, scan),
                            Task::Reliable(Reliable::Release { .. }) => {}
                        }
                    }
                }
            }
        }
    })
}

/// 文本同步独立线程：注入慢也只影响它自己。
pub(crate) fn spawn_text_worker(bus: Arc<EventBus>) -> JoinHandle<()> {
    std::thread::spawn(move || {
        while let Some(text) = bus.next_text() {
            if !SYNC_ACTIVE.load(Ordering::Relaxed) { continue; }
            inject_text(&text);
        }
    })
}

/// `WM_CHAR`：字符消息，`wParam` 是 UTF-16 码元。
const WM_CHAR: u32 = 0x0102;

/// 无焦点写入：直接往焦点控件投 `WM_CHAR`。
///
/// 为什么只投 `WM_CHAR`、不补 `WM_KEYDOWN`：标准 Edit 收到字符消息就会把它插进去，
/// 再补一个按下消息，目标自己的 `TranslateMessage` 会**又**生成一次字符 —— 实测多打一个字。
///
/// 目标控件必须是 `text_sync::Capability::Standard`（原生 Edit / RichEdit / WinForms /
/// VCL / Scintilla）。WPF、Electron、Unity 这些自绘编辑器一条 `WM_CHAR` 都不吃
/// （实测 WPF 的 TextBox 文本是靠它自己的 `WM_KEYDOWN` 生成的），只能走前台切换通道。
fn post_text_without_focus(control: HWND, text: &str) -> bool {
    let mut posted = false;
    for unit in text.encode_utf16() {
        if !SYNC_ACTIVE.load(Ordering::Relaxed) { break; }
        unsafe {
            // 必须是 W 版：`PostMessageA` 会把 `WM_CHAR` 的字符按 ANSI 转换一遍，
            // 0x4E2D（中）被截成 0x2D（-）—— 中文全变乱码。实测踩过这个坑。
            if PostMessageW(control, WM_CHAR, WPARAM(unit as usize), LPARAM(1)).is_ok() {
                posted = true;
            }
        }
    }
    posted
}

/// 把前台还给「进来时是谁」，包括中途停止、出错提前返回的所有路径。
///
/// 手写 `SetForegroundWindow(master)` 的收尾只能盖住正常走完的那一条路：
/// 注入过程中用户点了停止，函数在循环里 `return`，前台就留在最后一个从窗口上了。
struct ForegroundGuard(Option<HWND>);

impl ForegroundGuard {
    fn take() -> Self {
        let current = unsafe { GetForegroundWindow() };
        Self(if current.0.is_null() { None } else { Some(current) })
    }
}

impl Drop for ForegroundGuard {
    fn drop(&mut self) {
        let Some(h) = self.0 else { return };
        unsafe {
            if IsWindow(h).as_bool() {
                let _ = SetForegroundWindow(h);
            }
        }
    }
}

/// 文本注入：先试无焦点通道，不行才切前台。
///
/// 这个函数只服务**剪贴板中转**（Ctrl+C 复制 → 灌给从窗口）。文本镜像走的是另一条路
/// （`text_sync::apply`，直接 `WM_SETTEXT` / 增量写回），不经过这里。
///
/// 两条通道的分工不是「实现细节」，而是**能力边界**，必须在日志和界面上说清楚：
///
/// | 通道 | 目标 | 代价 |
/// |---|---|---|
/// | 无焦点（`WM_CHAR`） | 标准 `Edit` / `RichEdit` / Scintilla 控件 | 不抢前台，不打断用户 |
/// | 前台切换（`SendInput`） | 自绘控件、游戏、WPF、Electron | 会短暂切走前台，输入打到「当前前台」而不是指定窗口 |
///
/// 之所以不强行统一成一种：唯一能进游戏的通道就是模拟真实输入，而它天然要抢前台；
/// 反过来对标准输入框用 `SendInput` 是完全没必要的打断。
fn inject_text(text: &str) {
    if !SYNC_CLIPBOARD.load(Ordering::Relaxed) { return; }

    for slave in live_slaves() {
        if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }

        // ① 无焦点通道：焦点落在标准文本控件上就直投 WM_CHAR，全程不动前台
        if let Some(control) = text_sync::focus_child(slave) {
            let class = text_sync::class_of(control);
            if text_sync::classify(&class) == text_sync::Capability::Standard
                && post_text_without_focus(control, text)
            {
                log(&format!("文本已无焦点写入 [{class}]（未切换前台）"));
                continue;
            }
        }

        // ② 兜底：自绘控件 / 游戏。只能切前台 + 模拟输入，切完必须还回去
        inject_text_with_focus(slave, text);
    }
}

/// 前台切换式注入：`SetForegroundWindow` + 逐字 `SendInput`。
///
/// 每个字之间停 6ms 是给游戏端的输入队列留余量；停止时最多再等一个字的时间。
/// `ForegroundGuard` 保证任何提前返回都会把前台还回去。
fn inject_text_with_focus(slave: HWND, text: &str) {
    let _restore = ForegroundGuard::take();

    unsafe { let _ = SetForegroundWindow(slave); }
    std::thread::sleep(std::time::Duration::from_millis(25));
    log("目标不是标准输入框，改走前台切换通道（期间会短暂切换前台）");

    for ch in text.chars() {
        if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }
        unsafe { send_unicode_char(ch as u32); }
        // 字符间隔：防止游戏端队列溢出
        std::thread::sleep(std::time::Duration::from_millis(6));
    }
    std::thread::sleep(std::time::Duration::from_millis(20));
}

/// 记录鼠标键的按下 / 抬起，供补投抬起时使用。
fn note_button(msg: u32, down: bool) {
    let mask = match msg {
        WM_LBUTTONDOWN | WM_LBUTTONUP => BTN_LEFT,
        WM_RBUTTONDOWN | WM_RBUTTONUP => BTN_RIGHT,
        _ => BTN_MIDDLE,
    };

    let mut state = PRESSED.lock();
    if down {
        state.button_down(mask);
    } else {
        state.button_up(mask);
    }
}

/// 主窗口失焦 / 停止时的兜底：把还按着的键放掉。
fn flush_pressed(reason: &str) {
    let (keys, buttons) = PRESSED.lock().drain();
    if keys.is_empty() && buttons == 0 { return; }

    let count = keys.len() + buttons.count_ones() as usize;

    match bus() {
        // 走队列是为了排在已入队的按下事件之后：先补抬起再投 KeyDown，等于没补
        Some(bus) => bus.push_reliable(Reliable::Release { keys, buttons }),
        None => post_release(&keys, buttons),
    }

    log(&format!("{reason}：已补投抬起 {count} 个按键 / 鼠标键"));
}

/// 控制权已经不在主窗口、但还有键按着 —— 之后的消息不会再转发，不主动放就是永久卡键。
fn flush_if_focus_lost() {
    if PRESSED.lock().is_empty() { return; }

    let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);
    if unsafe { GetForegroundWindow() } == master { return; }

    // 多余的抬起消息对接收方无副作用，所以这里不必区分「焦点去了从窗口（我们自己在注入文本）」
    // 还是「用户切走了」—— 只要还有按着的键就补一次。
    flush_pressed("控制权已离开主窗口");
}

/// 失焦兜底不能只靠钩子：用户 Alt+Tab 走开之后可能一个输入事件都不再产生。
///
/// 有了 `window_watch` 的前台事件之后，这条路径已经是**兜底**而不是主路径，
/// 所以节奏从 30ms 放到 250ms；睡眠切成 10ms 一片，停止时 join 仍然最多等 10ms。
/// 顺便每一秒做一次从窗口身份校验（`verify_targets`）—— 那是 `OpenProcess`，
/// 不能放进投递热路径。
pub(crate) fn spawn_focus_watch(bus: Arc<EventBus>) -> JoinHandle<()> {
    const SLICE_MS: u64 = 10;
    const SLICES_PER_CYCLE: u32 = 25;   // ≈250ms
    const VERIFY_EVERY_CYCLES: u32 = 4; // ≈1s

    std::thread::spawn(move || {
        let mut cycles: u32 = 0;
        while !bus.is_closed() {
            for _ in 0..SLICES_PER_CYCLE {
                if bus.is_closed() { return; }
                std::thread::sleep(std::time::Duration::from_millis(SLICE_MS));
            }
            if !SYNC_ACTIVE.load(Ordering::Relaxed) { continue; }

            flush_if_focus_lost();

            cycles = cycles.wrapping_add(1);
            if cycles % VERIFY_EVERY_CYCLES == 0 {
                verify_targets();
            }
        }
    })
}

// ── 窗口生命周期回调（由 `window_watch` 线程调用）────────────────────────
//
// 这两条回调都在监听线程上跑，必须足够轻：只碰内存里的列表和状态，不做 SendMessage。

/// 窗口销毁 / 隐藏。
///
/// 销毁一律剔除 —— 那个句柄马上可能被别的窗口拿走。
/// 隐藏只记日志、**不剔除**：从窗口最小化到托盘或切场景时也会发 HIDE，
/// 但它仍然是个有效窗口，剔掉就等于擅自丢了用户选的窗口。
pub(crate) fn on_window_gone(hwnd: i64, why: &str) {
    if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }

    let master = MASTER_HWND.load(Ordering::Relaxed);
    if hwnd == master {
        if why == "已销毁" {
            log("主窗口已关闭，同步自动停止");
            let _ = stop_sync();
        } else {
            log("主窗口已隐藏，同步继续（消息仍可投递，只是看不见）");
        }
        return;
    }

    if why != "已销毁" {
        // 隐藏：只提示，不动列表
        let is_slave = SLAVES.lock().iter().any(|&h| h == hwnd);
        if is_slave {
            log(&format!("从窗口 0x{hwnd:X} 已隐藏（最小化到托盘 / 切场景），仍保留在同步列表"));
        }
        return;
    }

    let removed = {
        let mut list = SLAVES.lock();
        let before = list.len();
        list.retain(|&h| h != hwnd);
        list.len() != before
    };
    if removed {
        window_id::SLAVE_KEYS.remove(hwnd);
        log(&format!("从窗口 0x{hwnd:X} 已关闭，立即从同步列表移除"));
        emit_status();
    }
}

/// 前台窗口换人了。立刻做两件事：唤醒文本同步采样、把还按着的键放掉。
///
/// 这条把「失焦补抬起」从最多 250ms 的轮询延迟压到事件级。
pub(crate) fn on_foreground_changed(hwnd: i64) {
    if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }
    text_sync::note_input();
    if hwnd != MASTER_HWND.load(Ordering::Relaxed) {
        flush_if_focus_lost();
    }
}

/// 校验主窗口 / 从窗口还是不是原来那个窗口。
///
/// 单靠 `IsWindow` 不够：句柄会被系统复用，`IsWindow` 对一个「恰好占用了同一个句柄值」
/// 的新窗口照样返回真。这里比的是 PID + 进程启动时间 + EXE 路径 + 窗口类名。
///
/// 判断在 [`ROLE_LOCK`] 里做、处置在锁外做：处置会走 `stop_sync()`，那要 join 线程，
/// 持着锁等线程退出会把「线程也在等这把锁」变成死锁。
fn verify_targets() {
    if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }

    let (master_gone, gone): (bool, Vec<i64>) = {
        let _role = ROLE_LOCK.lock();

        let master = MASTER_HWND.load(Ordering::Relaxed);
        let master_gone = master != 0 && !window_id::MASTER_KEY.still_original(master);

        let list = SLAVES.lock();
        let gone = list
            .iter()
            .copied()
            .filter(|h| !window_id::SLAVE_KEYS.still_original(*h))
            .collect();
        (master_gone, gone)
    };

    if master_gone {
        log("主窗口已失效或句柄被系统复用，同步自动停止");
        let _ = stop_sync();
        return;
    }

    if gone.is_empty() { return; }

    {
        let _role = ROLE_LOCK.lock();
        let mut list = SLAVES.lock();
        list.retain(|h| !gone.contains(h));
        for h in &gone {
            window_id::SLAVE_KEYS.remove(*h);
        }
    }
    log(&format!(
        "{} 个从窗口已失效或句柄被系统复用，已从同步列表移除",
        gone.len()
    ));
    emit_status();
}

#[inline]
fn make_lp(x: i32, y: i32) -> LPARAM {
    LPARAM((((y as u16 as u32) << 16) | (x as u16 as u32)) as isize)
}

/// 生成要投递给从窗口的 `(wParam, lParam)`。坐标系按消息类型区分，这是本模块最容易搞错的地方：
///
/// | 消息 | lParam 坐标系 |
/// |---|---|
/// | `WM_MOUSEMOVE`、左右中键的按下 / 抬起 | 目标窗口**客户区**坐标 |
/// | `WM_MOUSEWHEEL` | **屏幕**坐标 |
///
/// 滚轮如果也塞客户区坐标，接收方会把它当成屏幕点，结果按「客户区原点」整体偏移一段 ——
/// 窗口一挪位置就会滚到别的控件上。这里把映射后的客户区点再用 `ClientToScreen`
/// 转回从窗口的屏幕坐标，保证落点仍在从窗口客户区内。
///
/// 不认识的消息返回 `None`（调用方跳过，不要投递）。
/// 组装一条鼠标消息的 `(wParam, lParam)`。
///
/// `mk` 是这次事件**发生时**的按键状态位。`WM_MOUSEMOVE` 的 `wParam` 不是空的：
/// 里面要带 `MK_LBUTTON` / `MK_RBUTTON` / `MK_MBUTTON` / `MK_SHIFT` / `MK_CONTROL`。
/// 早期实现把它固定成 0，于是靠标准消息判拖动的程序收到一串「没按住任何键的移动」，
/// 拖动要么不生效、要么只挪一下 —— 这类程序不会去查 `GetKeyState`，它只信 `wParam`。
fn mouse_message(
    msg: u32,
    x: i32,
    y: i32,
    pos: &Pos,
    wheel_delta: i16,
    slave: HWND,
) -> Option<(WPARAM, LPARAM)> {
    let client = || make_lp(x, y);
    // 事件发生时的按键状态位，随事件带过来的（见 `Pos::buttons`）
    let mk = pos.buttons;

    // 按下：这次事件本身把键压下去，wParam 要含它（事件发生时它还没被记进状态表）
    // 抬起：这次事件把它松开，wParam 要去掉它
    let with = |bit: u32| WPARAM((mk | bit) as usize);
    let without = |bit: u32| WPARAM((mk & !bit) as usize);

    Some(match msg {
        WM_MOUSEMOVE => (WPARAM(mk as usize), client()),
        WM_LBUTTONDOWN => (with(MK_LBUTTON), client()),
        WM_LBUTTONUP => (without(MK_LBUTTON), client()),
        WM_RBUTTONDOWN => (with(MK_RBUTTON), client()),
        WM_RBUTTONUP => (without(MK_RBUTTON), client()),
        WM_MBUTTONDOWN => (with(MK_MBUTTON), client()),
        WM_MBUTTONUP => (without(MK_MBUTTON), client()),
        WM_MOUSEWHEEL => {
            // 高 16 位是滚动量（有符号，向上为正），低 16 位同样要带按键状态位
            let wp = WPARAM((((wheel_delta as u32) << 16) | (mk & 0xFFFF)) as usize);
            let mut point = POINT { x, y };
            let lp = if unsafe { ClientToScreen(slave, &mut point) }.as_bool() {
                make_lp(point.x, point.y)
            } else {
                // 转不回屏幕坐标时退回原始屏幕坐标：位置会偏，但坐标系至少是对的
                make_lp(pos.screen_x, pos.screen_y)
            };
            (wp, lp)
        }
        _ => return None,
    })
}

/// 客户区尺寸。取不到（窗口已销毁 / 最小化到 0）时返回 `None`，调用方按「不缩放」处理。
///
/// 与 `window_id::client_size` 的区别只在这里：那边如实返回 0×0（身份快照要如实记录），
/// 这边把 0 尺寸当成「取不到」，因为映射时拿 0 做除数会算出垃圾。
fn client_size(h: HWND) -> Option<(i32, i32)> {
    let (w, h) = window_id::client_size(h)?;
    if w > 0 && h > 0 { Some((w, h)) } else { None }
}

/// 把主窗口客户区坐标按**客户区比例**映射到从窗口客户区坐标。
///
/// 两端尺寸一致时就是原值，不做任何取整误差；一侧尺寸未知或为 0 时退回原值，
/// 保证「拿不到尺寸」也不会把坐标算成负数或者 0 导致全部点左上角。
#[inline]
fn map_client_point(x: i32, y: i32, src_w: i32, src_h: i32, target: HWND) -> (i32, i32) {
    let Some((dst_w, dst_h)) = client_size(target) else { return (x, y) };
    if src_w <= 0 || src_h <= 0 { return (x, y); }
    if src_w == dst_w && src_h == dst_h { return (x, y); }

    let nx = (x as f64 / src_w as f64 * dst_w as f64).round() as i32;
    let ny = (y as f64 / src_h as f64 * dst_h as f64).round() as i32;
    // 夹在客户区内：比例换算后可能因为取整落在右/下边界外一像素
    (nx.clamp(0, dst_w - 1), ny.clamp(0, dst_h - 1))
}

// ── start_sync / stop_sync ────────────────────────────────────────────────
//
// 两条都是薄壳：真正的资源管理在 `session::SyncSession` 里。
// 这里只负责「换指针」和「推送状态」，所以启动失败或停止重入都不会留下半套资源。

pub fn start_sync(config: SyncConfig) -> Result<String, String> {
    // 先关旧会话，再建新的。拆除放在锁外做：那个过程要 join 线程、等监听退出，
    // 占着锁会让生命周期回调里的 `stop_sync()` 一直排到超时。
    let previous = { SESSION.lock().take() };
    if let Some(previous) = previous {
        let _ = previous.close();
    }

    let session = session::SyncSession::open(&config)?;

    // 走到这里说明 ①~⑤ 全部成功。中途失败的话 `session` 已经在 `open()` 里被 drop，
    // 全局状态（主窗口 / 从窗口列表 / 身份键 / 按键表）也一并还原了。
    SESSION.lock().replace(session);

    log("同步已启动");
    emit_status();
    Ok("同步已启动".into())
}

pub fn stop_sync() -> Result<String, String> {
    let session = { SESSION.lock().take() };

    let Some(session) = session else {
        // 没在同步。幂等返回 —— 生命周期回调可能连着叫两次，别把重复停止做成错误。
        // 但「活动」标志还是要落一次：正常路径下它是会话拆解的第一步，这里没有会话可拆，
        // 标志却仍可能是从别处写进来的（诊断、测试脚手架），不抹掉就一直显示「同步中」。
        SYNC_ACTIVE.store(false, Ordering::Relaxed);
        // 文本镜像可以被 FFI 单独打开（不经过会话），那条路径也要收一下
        text_sync::shutdown();
        return Ok("同步已停止".into());
    };

    // ①~⑥ 的顺序全在 `close()` 里，包括「补抬起」要排在卸钩子之前。
    // join 也在里面：返回时从窗口的按键状态一定已经复原了。
    let released = session.close();

    if released {
        log("同步已停止：已补投抬起所有按住的按键");
    } else {
        log("同步已停止");
    }
    emit_status();
    Ok("同步已停止".into())
}

/// 按给定的主窗口 / 从窗口列表重采一遍稳定身份。
///
/// 身份键回答的是「这个句柄现在指的，还是当初那个窗口吗」。主从角色一变就必须整体重建 ——
/// 只改 `MASTER_HWND` 而漏掉这里，一秒后的身份复查会把刚升上来的新主窗口判成
/// 「句柄被复用」，直接把同步停掉。
pub(crate) fn capture_identities(master: i64, slaves: &[i64]) {
    window_id::MASTER_KEY.clear();
    window_id::SLAVE_KEYS.clear();
    if let Some(key) = window_id::WindowKey::of(HWND(master as *mut _)) {
        window_id::MASTER_KEY.insert(master, key);
    }
    for &hwnd in slaves {
        if let Some(key) = window_id::WindowKey::of(HWND(hwnd as *mut _)) {
            window_id::SLAVE_KEYS.insert(hwnd, key);
        }
    }
}

/// 主从对调：把 `slave_hwnd` 提升为主窗口，原主窗口降为从窗口。
///
/// 这是「换一个号来操作」的一步操作 —— 不用停下再启动，投递全程不断。
/// 要在运行中安全地换，三件事一件都不能少：
///
/// 1. **先把按住的键放掉，而且同步投出去**。投递读的是「当前从窗口列表」，
///    排进队列的那条 Release 会在列表换完之后才被消费，那时它投给的已经是新列表 ——
///    旧主窗口变成从窗口之后就会漏掉这次抬起，永久卡键。
/// 2. **身份键整体重建**，否则一秒后看门狗会把新主窗口判成句柄复用。
/// 3. **文本基线作废**。基线绑的是旧主窗口焦点控件的内容与光标，不丢的话下一次采样
///    会把新窗口的整段文本当成「在旧内容上大改」，然后整段重写灌给从窗口。
pub fn swap_master(slave_hwnd: i64) -> Result<(), String> {
    if slave_hwnd == 0 {
        return Err("参数错误：从窗口句柄不能为 0".into());
    }

    let _role = ROLE_LOCK.lock();

    // 判「在不在同步」只能看这个标志。`MASTER_HWND` 在停止后是**故意保留**的
    // （宿主靠它恢复上次的选择），拿它非零当「正在跑」会一路走到下面的列表检查，
    // 最后报出一个「该窗口不是从窗口」的错误 —— 那是参数问题，不是状态问题。
    if !SYNC_ACTIVE.load(Ordering::Relaxed) {
        return Err("同步尚未启动，请先开始同步".into());
    }

    let old_master = MASTER_HWND.load(Ordering::Relaxed);
    if old_master == 0 {
        return Err("同步尚未启动，请先开始同步".into());
    }
    if slave_hwnd == old_master {
        return Err("参数错误：该窗口已经是主窗口".into());
    }
    if !SLAVES.lock().contains(&slave_hwnd) {
        return Err("参数错误：该窗口不是从窗口".into());
    }

    // ① 放掉还按着的键，直接投（不走队列，理由见上面的注释）
    let (keys, buttons) = PRESSED.lock().drain();
    if !keys.is_empty() || buttons != 0 {
        post_release(&keys, buttons);
    }

    // ② 换角色：新主从列表里摘掉，旧主补到末尾（其余从窗口的序号不变）
    let new_slaves = {
        let mut list = SLAVES.lock();
        list.retain(|&h| h != slave_hwnd);
        if !list.contains(&old_master) {
            list.push(old_master);
        }
        list.clone()
    };
    MASTER_HWND.store(slave_hwnd, Ordering::Relaxed);
    capture_identities(slave_hwnd, &new_slaves);

    // ③ 文本基线与上次鼠标位置作废：前者绑旧窗口的焦点控件，后者是旧窗口客户区坐标
    text_sync::reset_baseline();
    *LAST_POS.lock() = Pos::default();

    log(&format!(
        "主从已对调：0x{slave_hwnd:X} 成为主窗口，0x{old_master:X} 转为从窗口"
    ));
    emit_status();
    Ok(())
}

pub fn get_sync_status() -> SyncStatus {
    SyncStatus {
        active:         SYNC_ACTIVE.load(Ordering::Relaxed),
        master_hwnd:    MASTER_HWND.load(Ordering::Relaxed),
        slave_count:    SLAVES.lock().len(),
        sync_mouse:     SYNC_MOUSE.load(Ordering::Relaxed),
        sync_keyboard:  SYNC_KEYBOARD.load(Ordering::Relaxed),
        sync_clipboard: SYNC_CLIPBOARD.load(Ordering::Relaxed),
        text_mirror:    text_sync::is_enabled(),
    }
}

/// 供 `text_sync` 取用当前的主窗口与从窗口列表。
///
/// 从窗口列表用 `live_slaves()` 过滤掉的已销毁窗口 —— 否则文本同步每拍都会去
/// `SendMessage` 一个不存在的句柄，白等超时。
pub(crate) fn text_sync_targets() -> Option<(HWND, Vec<HWND>)> {
    if !SYNC_ACTIVE.load(Ordering::Relaxed) { return None; }
    let master = MASTER_HWND.load(Ordering::Relaxed);
    if master == 0 { return None; }
    let slaves = live_slaves();
    if slaves.is_empty() { return None; }
    Some((HWND(master as *mut _), slaves))
}

/// 关掉某个开关时要补投的那一份按键状态：**只取被关掉的那一类**。
///
/// 只关键盘时鼠标键必须留着 —— 用户很可能正拖着画面去关键盘同步，
/// 顺手把鼠标状态也清掉，从窗口那边就一直拖着了。
fn take_releases(keyboard_off: bool, mouse_off: bool) -> (Vec<(u32, u32)>, u32) {
    let mut state = PRESSED.lock();
    let keys = if keyboard_off { state.drain_keys() } else { Vec::new() };
    let buttons = if mouse_off { state.drain_buttons() } else { 0 };
    (keys, buttons)
}

/// 同步选项变更 —— 不是「改三个布尔值」，而是一次状态转换。
///
/// 只改开关会留下一个必现的卡键：用户按下 W → 从窗口收到 KeyDown → 用户在这里关掉键盘同步
/// → 用户松开 W → 钩子因为开关已经是 false 而不再转发 KeyUp → 从窗口永远认为 W 还按着。
/// 鼠标键完全同理（拖拽途中关掉鼠标同步，从窗口会一直拖着）。
///
/// 所以这里必须：
/// 1. 先把开关改掉（之后进来的抬起一律不转发，也就不会再制造新的按下）；
/// 2. **在锁内**把已经按下的键 / 鼠标键取出来，作为一笔「补抬起」入队；
/// 3. 补抬起走队列，天然排在已经入队的按下事件之后 —— 顺序反了等于没补。
///
/// 关键盘只放键盘、关鼠标只放鼠标：用户可能正拖着画面去关键盘同步，不能顺手把鼠标状态清掉。
pub fn update_sync_options(sync_mouse: bool, sync_keyboard: bool, sync_clipboard: bool) -> Result<(), String> {
    let _options = OPTIONS_LOCK.lock();

    let keyboard_was_on = SYNC_KEYBOARD.swap(sync_keyboard, Ordering::SeqCst);
    let mouse_was_on = SYNC_MOUSE.swap(sync_mouse, Ordering::SeqCst);

    // 与钩子互斥地取状态：钩子要么在锁外还没开始记账（那它随后会看到开关已关、
    // 只记账不投递），要么已经记完并把这笔按下投进了队列（那这笔抬起就排在它后面）
    let (keys, buttons) = take_releases(keyboard_was_on && !sync_keyboard, mouse_was_on && !sync_mouse);

    if !keys.is_empty() || buttons != 0 {
        let count = keys.len() + buttons.count_ones() as usize;
        let what = if keys.is_empty() { "鼠标键" } else if buttons == 0 { "按键" } else { "按键与鼠标键" };
        match bus() {
            Some(bus) => bus.push_reliable(Reliable::Release { keys, buttons }),
            None => post_release(&keys, buttons),
        }
        log(&format!("同步选项已关闭：补投抬起 {count} 个{what}"));
    }

    SYNC_CLIPBOARD.store(sync_clipboard, Ordering::SeqCst);

    // 修复：原实现修改选项后不推送状态，宿主 UI 与引擎实际状态会不一致
    emit_status();
    Ok(())
}

pub fn focus_window(hwnd: i64) -> Result<(), String> {
    unsafe {
        let h = HWND(hwnd as *mut _);
        if IsWindow(h).as_bool() {
            let _ = SetForegroundWindow(h);
            let _ = ShowWindow(h, SW_RESTORE);
        }
    }
    Ok(())
}

// ── Hook 回调 ─────────────────────────────────────────────────────────────

// `WM_MOUSEMOVE` / 按键消息 `wParam` 低位的按键状态位，取值与 Windows 的 `MK_*` 一致。
// 不从 `windows` crate 导入是因为这几个常量分散在 `KeyboardAndMouse` 模块里，
// 而这里只需要数值本身。
const MK_LBUTTON: u32 = 0x0001;
const MK_RBUTTON: u32 = 0x0002;
const MK_SHIFT: u32 = 0x0004;
const MK_CONTROL: u32 = 0x0008;
const MK_MBUTTON: u32 = 0x0010;

/// 左右 Ctrl / Shift 都要算，不能只认通用 `VK_CONTROL`。
///
/// 低级键盘钩子给的是**具体**键码：按住右 Ctrl 时 `vkCode` 可能是 `0xA3`，
/// 只比对 `0x11` 就会漏掉 —— 这正是「Ctrl+C 触发不了剪贴板中转」的实际原因。
static SHIFT_DOWN: AtomicBool = AtomicBool::new(false);
static CTRL_DOWN: AtomicBool = AtomicBool::new(false);

fn is_shift_vk(vk: u32) -> bool {
    matches!(vk, 0x10 | 0xA0 | 0xA1) // VK_SHIFT / VK_LSHIFT / VK_RSHIFT
}

fn is_ctrl_vk(vk: u32) -> bool {
    matches!(vk, 0x11 | 0xA2 | 0xA3) // VK_CONTROL / VK_LCONTROL / VK_RCONTROL
}

/// 此刻的鼠标键 / 修饰键状态位。
///
/// 鼠标消息的 `wParam` 必须带上它：很多程序（尤其是靠标准消息判拖动的）
/// 是靠 `MK_LBUTTON` 判断「现在是不是在拖」，只发坐标会被当成单纯的光标移动。
fn mk_flags() -> u32 {
    let buttons = PRESSED.lock().buttons();
    let mut mk = 0;
    if buttons & BTN_LEFT != 0 { mk |= MK_LBUTTON; }
    if buttons & BTN_RIGHT != 0 { mk |= MK_RBUTTON; }
    if buttons & BTN_MIDDLE != 0 { mk |= MK_MBUTTON; }
    if SHIFT_DOWN.load(Ordering::Relaxed) { mk |= MK_SHIFT; }
    if CTRL_DOWN.load(Ordering::Relaxed) { mk |= MK_CONTROL; }
    mk
}

pub(crate) unsafe extern "system" fn ms_hook(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if code >= 0 && SYNC_ACTIVE.load(Ordering::Relaxed) {
        let msg    = wp.0 as u32;
        let ms     = &*(lp.0 as *const MSLLHOOKSTRUCT);
        let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);

        // 关掉鼠标同步后，移动与滚轮一律不采 —— 它们不改状态，也没有「留一半」的问题。
        // 但**按键事件必须继续走完下面这段**：不记账就不知道这个键还按着，
        // 等用户再关一次开关时就没有状态可放，从窗口会一直拖着。
        let is_button = matches!(
            msg,
            WM_LBUTTONDOWN | WM_LBUTTONUP | WM_RBUTTONDOWN | WM_RBUTTONUP | WM_MBUTTONDOWN | WM_MBUTTONUP
        );
        if !is_button && !SYNC_MOUSE.load(Ordering::Relaxed) {
            return CallNextHookEx(None, code, wp, lp);
        }

        // 鼠标移动频率太高，不为它多做一次窗口查询；按键类事件顺带看看是不是已经失焦
        if msg != WM_MOUSEMOVE {
            flush_if_focus_lost();
        }
        if GetForegroundWindow() == master {
            // 钩子只采数，不做任何换算 —— 换算要按「每个从窗口自己的客户区」来算，
            // 放在这里只能按主窗口算，滚轮与不同尺寸窗口都会出错。
            let mut pt = POINT { x: ms.pt.x, y: ms.pt.y };
            let _ = ScreenToClient(master, &mut pt);
            let (master_w, master_h) = client_size(master).unwrap_or((0, 0));
            let pos = Pos {
                x: pt.x,
                y: pt.y,
                screen_x: ms.pt.x,
                screen_y: ms.pt.y,
                master_w,
                master_h,
                // 取的是**这次事件发生时**的按键状态，不是投递时的 —— 队列里可能还压着
                // 一串移动，等它们被投出去时用户早就松手了
                buttons: mk_flags(),
            };

            // 看开关、记账、入队必须在同一把锁里（理由见 `OPTIONS_LOCK` 的注释）
            let _options = OPTIONS_LOCK.lock();
            let forward = SYNC_MOUSE.load(Ordering::SeqCst);

            match msg {
                WM_MOUSEMOVE => {
                    if forward {
                        if let Some(bus) = bus() { bus.push_move(pos); }
                    }
                }
                WM_MOUSEWHEEL => {
                    if forward {
                        if let Some(bus) = bus() { bus.push_wheel((ms.mouseData >> 16) as i16, pos); }
                    }
                }
                WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN => {
                    // 记账无条件：即使此刻不转发，也要知道它按着（松开时要能对上）
                    note_button(msg, true);
                    if forward {
                        if let Some(bus) = bus() { bus.push_reliable(Reliable::Button { msg, pos }); }
                    }
                }
                WM_LBUTTONUP | WM_RBUTTONUP | WM_MBUTTONUP => {
                    note_button(msg, false);
                    if forward {
                        if let Some(bus) = bus() { bus.push_reliable(Reliable::Button { msg, pos }); }
                    }
                }
                _ => {}
            }
        }
    }
    CallNextHookEx(None, code, wp, lp)
}

pub(crate) unsafe extern "system" fn kb_hook(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if code >= 0 && SYNC_ACTIVE.load(Ordering::Relaxed) {
        let kb     = &*(lp.0 as *const KBDLLHOOKSTRUCT);
        let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);

        // 按住键 Alt+Tab 切走时，之后的抬起不会再转发（下面那句 if 挡住了）。
        // 在这里补一次抬起，避免从窗口永久卡键。
        flush_if_focus_lost();

        if GetForegroundWindow() == master {
            let n   = wp.0 as u32;
            let vk  = kb.vkCode;
            let down = n == WM_KEYDOWN || n == WM_SYSKEYDOWN;
            let up   = n == WM_KEYUP || n == WM_SYSKEYUP;

            // 任何一个键都当作「文本可能变了」的信号去唤醒文本同步：
            // 它会立刻采样一次，比固定轮询快 0~120ms。可打印键顺便计入
            // 「疑似自绘输入框」的嫌疑统计（转发了按键但文本从不变化）。
            // 文本镜像与键盘转发是两个独立功能，这里不跟着 `SYNC_KEYBOARD` 走。
            if down {
                text_sync::note_key(vk);
            }

            // 修饰键跟踪：无条件，鼠标消息的 MK_SHIFT / MK_CONTROL 要用
            if is_ctrl_vk(vk) {
                CTRL_DOWN.store(down, Ordering::Relaxed);
            }
            if is_shift_vk(vk) {
                SHIFT_DOWN.store(down, Ordering::Relaxed);
            }

            // Ctrl+C 检测：仅在 keydown 时触发一次（用 CTRL_DOWN 防重入）
            // 放在 `SYNC_KEYBOARD` 之外 —— 剪贴板中转是独立功能，用户关掉键盘转发
            // 仍然可能只想同步文本。
            if SYNC_CLIPBOARD.load(Ordering::Relaxed)
                && down
                && vk == 0x43  // VK_C
                && CTRL_DOWN.load(Ordering::Relaxed)
            {
                // 用 CB_PENDING 原子标志防止 150ms 内多次触发
                static CB_PENDING: AtomicBool = AtomicBool::new(false);
                if CB_PENDING.compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst).is_ok() {
                    std::thread::spawn(|| {
                        std::thread::sleep(std::time::Duration::from_millis(200));
                        CB_PENDING.store(false, Ordering::SeqCst);
                        if !SYNC_ACTIVE.load(Ordering::Relaxed)
                           || !SYNC_CLIPBOARD.load(Ordering::Relaxed) { return; }
                        unsafe {
                            let Some(text) = read_clipboard_text() else { return };
                            if text.is_empty() || text.len() > 800 { return; }
                            use std::collections::hash_map::DefaultHasher;
                            use std::hash::{Hash, Hasher};
                            let mut h = DefaultHasher::new();
                            text.hash(&mut h);
                            let hash = h.finish();
                            let mut last = LAST_CB_HASH.lock();
                            if *last == hash { return; }
                            *last = hash;
                            log(&format!("剪贴板中转：{} 个字符", text.chars().count()));
                            let pushed = bus().map(|b| b.push_text(text)).unwrap_or(false);
                            if !pushed {
                                log("文本队列已满，本次剪贴板内容未同步");
                            }
                        }
                    });
                }
            }

            if down || up {
                // 记账永远是「物理事实」：即使这次不转发，也要记住这个键按着 / 松了。
                // 不记的话，关掉键盘同步那一刻就没有状态可放 —— 从窗口永久卡键。
                let _options = OPTIONS_LOCK.lock();
                {
                    let mut state = PRESSED.lock();
                    if down { state.key_down(vk, kb.scanCode) } else { state.key_up(vk) }
                }
                if SYNC_KEYBOARD.load(Ordering::SeqCst) {
                    if let Some(bus) = bus() {
                        bus.push_reliable(Reliable::Key { down, vk, scan: kb.scanCode });
                    }
                }
            }
        }
    }
    CallNextHookEx(None, code, wp, lp)
}

// 剪贴板监控：没有独立线程，也不再轮询。
// Ctrl+C 由 `kb_hook` 直接识别（轮询会导致同一段文本被投两次：序列号变化一次、Ctrl+C 再触发一次），
// 认出来之后 read_clipboard_text → push_text 进总线，由文本线程逐字注入。
// 会话级的收尾（清掉上一轮的剪贴板哈希）在 `session::ClipboardHandler` 里。


// ── 快捷启动 / 进程管理 ───────────────────────────────────────────────────

pub fn launch_app(path: String, args: Vec<String>) -> Result<u32, String> {
    use std::os::windows::process::CommandExt;
    use std::process::Command;
    const CREATE_NO_WINDOW: u32 = 0x08000000;

    if path.to_lowercase().ends_with(".lnk") {
        unsafe {
            use std::ffi::OsStr;
            use std::os::windows::ffi::OsStrExt;
            use windows::core::PCWSTR;
            use windows::Win32::UI::Shell::ShellExecuteW;
            use windows::Win32::UI::WindowsAndMessaging::SW_SHOWDEFAULT;
            let path_w: Vec<u16> = OsStr::new(&path).encode_wide().chain(std::iter::once(0)).collect();
            let op_w:   Vec<u16> = OsStr::new("open").encode_wide().chain(std::iter::once(0)).collect();
            let r = ShellExecuteW(None, PCWSTR(op_w.as_ptr()), PCWSTR(path_w.as_ptr()), None, None, SW_SHOWDEFAULT);
            if r.0 as usize > 32 { return Ok(u32::MAX); }
            return Err(format!("ShellExecute 失败: {}", r.0 as usize));
        }
    }
    let mut cmd = Command::new(&path);
    if !args.is_empty() { cmd.args(&args); }
    let child = cmd.creation_flags(CREATE_NO_WINDOW).spawn().map_err(|e| format!("启动失败: {e}"))?;
    Ok(child.id())
}

/// 独占的系统句柄：`Drop` 里 `CloseHandle`。
///
/// 手写 `CloseHandle` 的写法在**失败路径**上一定会漏：`TerminateProcess(h)?` 一旦出错，
/// 函数直接返回，后面那行 `CloseHandle` 根本走不到。进程管理是长跑功能（多开、反复开关客户端），
/// 漏几个句柄不至于立刻出事，但会一点点把进程句柄表磨穿。
struct OwnedHandle(windows::Win32::Foundation::HANDLE);

impl OwnedHandle {
    fn new(h: windows::Win32::Foundation::HANDLE) -> Self {
        Self(h)
    }

    fn raw(&self) -> windows::Win32::Foundation::HANDLE {
        self.0
    }
}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = windows::Win32::Foundation::CloseHandle(self.0);
            }
        }
    }
}

pub fn kill_process(pid: u32) -> Result<(), String> {
    if pid == u32::MAX { return Err("LNK 启动的进程请手动关闭".to_string()); }
    unsafe {
        let h = OwnedHandle::new(
            OpenProcess(PROCESS_TERMINATE, false, pid)
                .map_err(|e| format!("无法打开进程 ({}): {e}", e.code().0))?,
        );
        TerminateProcess(h.raw(), 1).map_err(|e| format!("终止进程失败: {e}"))?;
    }
    Ok(())
}

/// 按可执行文件名结束进程。
///
/// 匹配必须是**完整文件名**。老实现还有一条 `n.starts_with(&name_low.replace(".exe",""))`，
/// 于是杀 `game.exe` 时会顺手把 `game-helper.exe`、`gameupdater.exe` 一起带走 ——
/// 用户只是想关掉客户端，结果连更新器 / 辅助进程都被杀了。
/// 需要精确定位时优先用 `kill_process(pid)`，按名字只是兜底。
pub fn kill_process_by_name(exe_name: String) -> Result<u32, String> {
    let want = window_id::file_name(&exe_name).to_lowercase();
    if want.is_empty() {
        return Err("参数错误：进程名为空".to_string());
    }

    unsafe {
        let snap = OwnedHandle::new(
            CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0).map_err(|e| e.to_string())?,
        );
        let mut e = PROCESSENTRY32W::default();
        e.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut killed = 0u32;
        if Process32FirstW(snap.raw(), &mut e).is_ok() {
            loop {
                let n = String::from_utf16_lossy(
                    &e.szExeFile.iter().take_while(|&&c| c != 0).cloned().collect::<Vec<_>>(),
                )
                .to_lowercase();
                if n == want {
                    if let Ok(h) = OpenProcess(PROCESS_TERMINATE, false, e.th32ProcessID) {
                        let h = OwnedHandle::new(h);
                        if TerminateProcess(h.raw(), 1).is_ok() { killed += 1; }
                    }
                }
                if Process32NextW(snap.raw(), &mut e).is_err() { break; }
            }
        }
        Ok(killed)
    }
}

#[cfg(test)]
pub(crate) mod test_support {
    //! 真实窗口的测试脚手架。
    //!
    //! `window_id` / `window_watch` 的用例需要「一个有类名、有标题、尺寸确定」的顶层窗口，
    //! 放在这里给几个测试模块共用，免得每处各写一份 `RegisterClassW`。

    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, RegisterClassW, WNDCLASSW, WS_POPUP,
    };

    /// 进程级串行锁。
    ///
    /// 主窗口 / 从窗口列表、按键状态、文本管线、窗口监听线程都是**全局单例**，
    /// 任何会动它们的用例都必须互斥。尤其是一条跨模块的坑：窗口监听用例销毁窗口
    /// 会触发 `on_window_gone` → `stop_sync()`，那是把整条同步拆掉 ——
    /// 并行的文本管线用例会莫名其妙地「中途停摆」。
    /// 所以锁必须全局唯一，不能每个模块各来一把。
    static GLOBAL_LOCK: parking_lot::Mutex<()> = parking_lot::Mutex::new(());

    pub fn serial() -> parking_lot::MutexGuard<'static, ()> {
        GLOBAL_LOCK.lock()
    }

    /// 测试窗口，Drop 时销毁。
    ///
    /// `WS_POPUP` 没有边框和标题栏，客户区尺寸精确等于创建尺寸，断言可以直接写死。
    pub struct TestWindow(pub HWND);

    impl Drop for TestWindow {
        fn drop(&mut self) {
            unsafe {
                let _ = DestroyWindow(self.0);
            }
        }
    }

    unsafe extern "system" fn wndproc(h: HWND, m: u32, w: WPARAM, l: LPARAM) -> LRESULT {
        unsafe { DefWindowProcW(h, m, w, l) }
    }

    /// 固定规格：400×300、类名 `LxSyncIdentityTestHost`、有标题
    pub fn popup_window() -> TestWindow {
        unsafe { popup("LxSyncIdentityTestHost", "identity-test", 400, 300) }
    }

    /// 在 `parent` 里建一个真正的 `Edit` 子输入框（FFI 的文本缓冲区接口要拿它验）
    pub fn edit_child(parent: HWND) -> HWND {
        use windows::core::w;
        use windows::Win32::UI::WindowsAndMessaging::{WS_CHILD, WS_VISIBLE};
        unsafe {
            let instance: windows::Win32::Foundation::HINSTANCE =
                GetModuleHandleW(None).unwrap().into();
            CreateWindowExW(
                Default::default(),
                w!("Edit"),
                w!(""),
                WS_CHILD | WS_VISIBLE,
                0,
                0,
                220,
                30,
                Some(&parent),
                None,
                instance,
                None,
            )
            .unwrap()
        }
    }

    pub unsafe fn popup(class: &str, title: &str, width: i32, height: i32) -> TestWindow {
        let class_w: Vec<u16> = class.encode_utf16().chain(std::iter::once(0)).collect();
        let title_w: Vec<u16> = title.encode_utf16().chain(std::iter::once(0)).collect();

        let instance: windows::Win32::Foundation::HINSTANCE =
            unsafe { GetModuleHandleW(None) }.unwrap().into();
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            lpszClassName: PCWSTR(class_w.as_ptr()),
            ..Default::default()
        };
        // 同一个类名注册第二次会失败，类已存在照样能用
        let _ = unsafe { RegisterClassW(&wc) };

        let hwnd = unsafe {
            CreateWindowExW(
                Default::default(),
                PCWSTR(class_w.as_ptr()),
                PCWSTR(title_w.as_ptr()),
                WS_POPUP,
                0,
                0,
                width,
                height,
                None,
                None,
                instance,
                None,
            )
        }
        .unwrap();
        TestWindow(hwnd)
    }

    // ── 伪造一套「同步正在运行」的全局状态 ──────────────────────────────
    //
    // 生命周期回调只看这几个全局量，不需要真的起队列、钩子和线程。
    // 这样测试就能直接驱动 `on_window_gone` / `verify_targets` 这两个入口。

    use std::sync::atomic::Ordering;

    pub fn hwnd_value(h: HWND) -> i64 {
        h.0 as isize as i64
    }

    pub fn begin_fake_sync(master: i64, slaves: &[i64]) {
        crate::MASTER_HWND.store(master, Ordering::Relaxed);
        *crate::SLAVES.lock() = slaves.to_vec();

        crate::window_id::MASTER_KEY.clear();
        crate::window_id::SLAVE_KEYS.clear();
        if let Some(key) = crate::window_id::WindowKey::of(HWND(master as isize as *mut _)) {
            crate::window_id::MASTER_KEY.insert(master, key);
        }
        for &slave in slaves {
            if let Some(key) = crate::window_id::WindowKey::of(HWND(slave as isize as *mut _)) {
                crate::window_id::SLAVE_KEYS.insert(slave, key);
            }
        }

        crate::SYNC_ACTIVE.store(true, Ordering::Relaxed);
    }

    pub fn end_fake_sync() {
        crate::SYNC_ACTIVE.store(false, Ordering::Relaxed);
        crate::MASTER_HWND.store(0, Ordering::Relaxed);
        crate::SLAVES.lock().clear();
        crate::window_id::MASTER_KEY.clear();
        crate::window_id::SLAVE_KEYS.clear();
    }

    pub fn slaves() -> Vec<i64> {
        crate::SLAVES.lock().clone()
    }

    pub fn sync_active() -> bool {
        crate::SYNC_ACTIVE.load(Ordering::Relaxed)
    }

    /// 直接跑一次身份校验（生产代码里由看门狗每秒调一次）
    pub fn verify_now() {
        crate::verify_targets();
    }

    /// 一个「绝对不属于当前窗口」的身份，用来模拟句柄被系统复用
    pub fn foreign_key() -> crate::window_id::WindowKey {
        crate::window_id::WindowKey {
            process: crate::window_id::ProcessKey {
                pid: 0xDEAD,
                process_start_time: 1,
                exe_path: r"C:\Somewhere\other.exe".to_string(),
            },
            class_name: "OtherWindowClass".to_string(),
            generation: 0,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::core::w;
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, RegisterClassW, WNDCLASSW, WS_POPUP,
    };

    /// 测试窗口：`WS_POPUP` 没有边框和标题栏，客户区尺寸就精确等于创建时给的尺寸，
    /// 这样断言里的期望值可以直接写死，不必再去减一遍边框宽度。
    struct TestWindow(HWND);

    impl Drop for TestWindow {
        fn drop(&mut self) {
            unsafe { let _ = DestroyWindow(self.0); }
        }
    }

    // DefWindowProcW 在 0.58 里是泛型函数，不能直接当 WNDPROC 指针用，包一层
    unsafe extern "system" fn test_wndproc(
        hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM,
    ) -> LRESULT {
        unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
    }

    unsafe fn popup(x: i32, y: i32, w: i32, h: i32) -> TestWindow {
        let class = w!("LxSyncCoordTestWindow");
        let instance: windows::Win32::Foundation::HINSTANCE =
            unsafe { GetModuleHandleW(None) }.unwrap().into();
        let wc = WNDCLASSW {
            lpfnWndProc: Some(test_wndproc),
            hInstance: instance,
            lpszClassName: class,
            ..Default::default()
        };
        // 同一个进程里注册第二次会返回 0，类已经存在照样能用，所以不看返回值
        let _ = unsafe { RegisterClassW(&wc) };
        let hwnd = unsafe {
            CreateWindowExW(
                Default::default(),
                class,
                w!("test"),
                WS_POPUP,
                x, y, w, h,
                None, None, instance,
                None,
            )
        }.unwrap();
        TestWindow(hwnd)
    }

    #[test]
    fn client_size_matches_popup_size() {
        let window = unsafe { popup(100, 100, 400, 300) };
        assert_eq!(client_size(window.0), Some((400, 300)));
    }

    #[test]
    fn same_size_keeps_point() {
        let slave = unsafe { popup(1000, 400, 400, 300) };
        assert_eq!(map_client_point(120, 90, 400, 300, slave.0), (120, 90));
    }

    #[test]
    fn downscale_maps_proportionally() {
        // 主窗口 1000×600 的中心 → 从窗口 500×300 的中心
        let slave = unsafe { popup(1000, 400, 500, 300) };
        assert_eq!(map_client_point(500, 300, 1000, 600, slave.0), (250, 150));

        // 四分之一位置同样要成比例
        assert_eq!(map_client_point(250, 150, 1000, 600, slave.0), (125, 75));
    }

    #[test]
    fn upscale_maps_proportionally() {
        // 主窗口 500×300 的右下角内侧 → 从窗口 1000×600 的对应点
        let slave = unsafe { popup(1000, 400, 1000, 600) };
        assert_eq!(map_client_point(499, 299, 500, 300, slave.0), (998, 598));
    }

    #[test]
    fn clamps_inside_target_client_area() {
        // 主窗口最后一个像素按比例放大后会落在从窗口右/下边界外，必须夹回来
        let slave = unsafe { popup(1000, 400, 333, 111) };
        let (x, y) = map_client_point(999, 599, 1000, 600, slave.0);
        assert!(x < 333 && y < 111, "映射结果越界: ({x}, {y})");
        assert_eq!((x, y), (332, 110));
    }

    #[test]
    fn unknown_source_size_keeps_point() {
        // 主窗口尺寸取不到（已销毁 / 最小化）时不做缩放，宁可偏一点也不压到左上角
        let slave = unsafe { popup(1000, 400, 500, 300) };
        assert_eq!(map_client_point(10, 20, 0, 0, slave.0), (10, 20));
        assert_eq!(map_client_point(10, 20, -1, 600, slave.0), (10, 20));
    }

    /// 滚轮的 lParam 必须是**屏幕坐标**：换算后的客户区点要能转成从窗口的屏幕点，
    /// 且这个点落在从窗口客户区内部（不能是主窗口那套坐标直接塞过去）。
    #[test]
    fn wheel_point_converts_to_slave_screen_coordinates() {
        let slave = unsafe { popup(1200, 500, 400, 300) };

        // 主窗口 1000×600 的中心 → 从窗口 400×300 的中心 (200, 150)
        let (x, y) = map_client_point(500, 300, 1000, 600, slave.0);
        assert_eq!((x, y), (200, 150));

        let mut point = POINT { x, y };
        let mut origin = POINT { x: 0, y: 0 };
        unsafe {
            assert!(ClientToScreen(slave.0, &mut point).as_bool());
            assert!(ClientToScreen(slave.0, &mut origin).as_bool());
        }

        // ClientToScreen 是平移，减掉客户区原点应还原成客户区坐标
        assert_eq!((point.x - origin.x, point.y - origin.y), (200, 150));

        // 并且确实在客户区内
        let (w, h) = client_size(slave.0).unwrap();
        assert!(point.x >= origin.x && point.x < origin.x + w);
        assert!(point.y >= origin.y && point.y < origin.y + h);
    }

    // ── 消息装配：直接测生产代码里真正用的 mouse_message ──────────────────

    fn lp_x(lp: LPARAM) -> i32 { (lp.0 as u32 & 0xFFFF) as u16 as i16 as i32 }
    fn lp_y(lp: LPARAM) -> i32 { ((lp.0 as u32 >> 16) & 0xFFFF) as u16 as i16 as i32 }

    fn client_origin(hwnd: HWND) -> (i32, i32) {
        let mut p = POINT { x: 0, y: 0 };
        unsafe { assert!(ClientToScreen(hwnd, &mut p).as_bool()) };
        (p.x, p.y)
    }

    #[test]
    fn wheel_lparam_is_screen_coordinates() {
        let slave = unsafe { popup(1200, 500, 400, 300) };
        let (ox, oy) = client_origin(slave.0);

        // 客户区点 (200,150) 的滚轮消息
        let (wp, lp) =
            mouse_message(WM_MOUSEWHEEL, 200, 150, &Pos::default(), 120, slave.0).unwrap();

        // 落点 = 客户区原点 + 客户区坐标，绝不是把 (200,150) 直接塞进去
        assert_eq!((lp_x(lp), lp_y(lp)), (ox + 200, oy + 150));
        assert_ne!((lp_x(lp), lp_y(lp)), (200, 150), "滚轮不能沿用客户区坐标");

        // 高 16 位是滚动量
        assert_eq!((wp.0 as u32 >> 16) as u16 as i16, 120);
    }

    #[test]
    fn negative_wheel_delta_keeps_sign() {
        let slave = unsafe { popup(1200, 500, 400, 300) };
        let (wp, _) = mouse_message(WM_MOUSEWHEEL, 10, 10, &Pos::default(), -120, slave.0).unwrap();
        assert_eq!((wp.0 as u32 >> 16) as u16 as i16, -120);
    }

    #[test]
    fn move_and_buttons_lparam_is_client_coordinates() {
        let slave = unsafe { popup(1200, 500, 400, 300) };

        for (msg, expected_wp) in [
            (WM_MOUSEMOVE, 0usize),
            (WM_LBUTTONDOWN, 0x0001),
            (WM_LBUTTONUP, 0x0000),
            (WM_RBUTTONDOWN, 0x0002),
            (WM_MBUTTONDOWN, 0x0010),
        ] {
            let (wp, lp) = mouse_message(msg, 137, 246, &Pos::default(), 0, slave.0).unwrap();
            assert_eq!((lp_x(lp), lp_y(lp)), (137, 246), "消息 {msg:#06X} 的 lParam 不是客户区坐标");
            assert_eq!(wp.0, expected_wp, "消息 {msg:#06X} 的 wParam 不对");
        }
    }

    /// 拖动能不能成立，全看移动消息的 `wParam` 有没有带 `MK_LBUTTON`。
    /// 老实现把它写死成 0，从窗口收到的是「没按任何键的移动」，拖动要么不生效要么只挪一下。
    #[test]
    fn mouse_messages_carry_button_state_in_wparam() {
        let slave = unsafe { popup(1200, 500, 400, 300) };
        let with_mk = |mk: u32| Pos { buttons: mk, ..Default::default() };

        // 拖着左键移动：状态位要跟着事件走
        let (wp, _) =
            mouse_message(WM_MOUSEMOVE, 10, 10, &with_mk(MK_LBUTTON), 0, slave.0).unwrap();
        assert_eq!(wp.0, MK_LBUTTON as usize, "拖动中的移动没带 MK_LBUTTON");

        // 已经按着右键，再按下左键：两个位都在
        let (wp, _) =
            mouse_message(WM_LBUTTONDOWN, 10, 10, &with_mk(MK_RBUTTON), 0, slave.0).unwrap();
        assert_eq!(wp.0, (MK_LBUTTON | MK_RBUTTON) as usize);

        // 抬起右键只清右键，别把还按着的左键一起抹掉
        let both = with_mk(MK_LBUTTON | MK_RBUTTON);
        let (wp, _) = mouse_message(WM_RBUTTONUP, 10, 10, &both, 0, slave.0).unwrap();
        assert_eq!(wp.0, MK_LBUTTON as usize, "抬起右键把左键状态一起清掉了");

        // 滚轮的低 16 位同样要带状态位
        let (wp, _) =
            mouse_message(WM_MOUSEWHEEL, 10, 10, &with_mk(MK_CONTROL), 120, slave.0).unwrap();
        assert_eq!(wp.0 as u32 & 0xFFFF, MK_CONTROL, "滚轮消息的低字丢了按键状态");
        assert_eq!((wp.0 as u32 >> 16) as u16 as i16, 120, "高位滚动量被状态位挤掉了");
    }

    /// 关掉一个开关只放对应的那一类：不能顺手把另一类的按下状态清掉。
    #[test]
    fn option_change_releases_only_the_kind_that_was_turned_off() {
        let _guard = serial();
        PRESSED.lock().drain();

        // 用户按着 W，同时拖着鼠标左键
        {
            let mut st = PRESSED.lock();
            st.key_down(0x57, 0x11);
            st.button_down(BTN_LEFT);
        }

        // 只关键盘同步：键盘放掉，鼠标留着
        let (keys, buttons) = take_releases(true, false);
        assert_eq!(keys, vec![(0x57, 0x11)], "键盘没被放掉，从窗口会卡住 W");
        assert_eq!(buttons, 0, "只关键盘却把鼠标键也一起放了");
        assert_eq!(PRESSED.lock().buttons(), BTN_LEFT, "鼠标状态不该被清");

        // 再关鼠标同步：这时候鼠标状态才该放掉
        let (keys, buttons) = take_releases(false, true);
        assert!(keys.is_empty());
        assert_eq!(buttons, BTN_LEFT);
        assert!(PRESSED.lock().is_empty());
    }

    /// 无焦点通道必须真的能把字符写进标准输入框，而且**不需要它持有前台**。
    ///
    /// 这条用例造的是「宿主窗口 + 真正的 Edit 子控件」，直接往控件投 `WM_CHAR`，
    /// 全程不调 `SetForegroundWindow`：能读回文本，就说明「不抢焦点也能同步文本」成立。
    #[test]
    fn no_focus_channel_writes_into_a_real_edit_control() {
        use windows::Win32::UI::WindowsAndMessaging::{SendMessageW, WM_GETTEXT};

        let _guard = serial();
        let host = crate::test_support::popup_window();
        let edit = crate::test_support::edit_child(host.0);
        crate::test_support::begin_fake_sync(crate::test_support::hwnd_value(host.0), &[]);

        assert!(
            post_text_without_focus(edit, "中文abc"),
            "投递应当至少成功一次"
        );
        pump();

        let mut buf = [0u16; 64];
        let len = unsafe {
            SendMessageW(
                edit,
                WM_GETTEXT,
                WPARAM(buf.len()),
                LPARAM(buf.as_mut_ptr() as isize),
            )
        };
        let got = String::from_utf16_lossy(&buf[..len.0.clamp(0, buf.len() as isize) as usize]);
        assert_eq!(got, "中文abc", "无焦点通道没把字符写进 Edit 控件");

        crate::test_support::end_fake_sync();
    }

    // ── 端到端：投递路径（真实窗口 + 真实 PostMessage）────────────────────
    //
    // 上面那些测的是「生成什么」；这里测「到底投出去没有」。
    // 丢「抬起」正是用户报的那个 bug 的核心：队列满了抬起被扔，从窗口永久按键。

    /// 这些用例共用 `SLAVES` / `RECEIVED` 等全局状态，并行跑会互相串扰，必须串行。
    /// （单独跑某个用例时它是绿的，一起跑就红 —— 就是并行闹的。）
    /// 用 `test_support` 里那把进程级锁：窗口监听用例触发的 `stop_sync()` 同样会拆这里的东西。
    fn serial() -> parking_lot::MutexGuard<'static, ()> {
        crate::test_support::serial()
    }

    /// 用来接消息的窗口：所有收到的消息都记到这个静态表里
    static RECEIVED: Lazy<Mutex<Vec<(u32, usize, isize)>>> = Lazy::new(|| Mutex::new(Vec::new()));

    unsafe extern "system" fn record_wndproc(
        hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM,
    ) -> LRESULT {
        RECEIVED.lock().push((msg, wparam.0, lparam.0));
        unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
    }

    unsafe fn recording_window() -> TestWindow {
        use windows::Win32::UI::WindowsAndMessaging::{PeekMessageW, PM_REMOVE, MSG};

        let class = w!("LxSyncRecordWindow");
        let instance: windows::Win32::Foundation::HINSTANCE =
            unsafe { GetModuleHandleW(None) }.unwrap().into();
        let wc = WNDCLASSW {
            lpfnWndProc: Some(record_wndproc),
            hInstance: instance,
            lpszClassName: class,
            ..Default::default()
        };
        let _ = unsafe { RegisterClassW(&wc) };
        let hwnd = unsafe {
            CreateWindowExW(
                Default::default(),
                class,
                w!("rec"),
                WS_POPUP,
                700, 400, 320, 240,
                None, None, instance,
                None,
            )
        }
        .unwrap();

        // 建窗口时系统会塞 WM_CREATE 之类的东西，先清一遍
        unsafe {
            let mut msg = MSG::default();
            while PeekMessageW(&mut msg, hwnd, 0, 0, PM_REMOVE).as_bool() {}
        }
        RECEIVED.lock().clear();
        TestWindow(hwnd)
    }

    /// 把本线程队列里的消息真正派发到窗口过程。
    ///
    /// 只 PeekMessage 是不够的：那样只是把消息从队列里取出来，窗口过程根本不会被调用，
    /// 记录表永远是空的（第一版就是这么写错的）。
    fn pump() {
        use windows::Win32::UI::WindowsAndMessaging::{
            DispatchMessageW, PeekMessageW, MSG, PM_REMOVE,
        };
        // 不调 TranslateMessage：那会把 WM_KEYDOWN 再翻出一个 WM_CHAR，
        // 记录表里就会多出不该有的消息，断言没法写死。
        unsafe {
            let mut msg = MSG::default();
            while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                DispatchMessageW(&msg);
            }
        }
    }

    fn received_msgs() -> Vec<u32> {
        pump();
        RECEIVED.lock().iter().map(|(m, _, _)| *m).collect()
    }

    /// 把本测试窗口设为唯一从窗口，测试结束后清空，避免污染其它用例
    fn with_slave<F: FnOnce()>(hwnd: HWND, body: F) {
        *SLAVES.lock() = vec![hwnd.0 as isize as i64];
        body();
        *SLAVES.lock() = vec![];
        PRESSED.lock().drain();
    }

    #[test]
    fn key_down_and_up_reach_the_slave_window() {
        let _guard = serial();
        let slave = unsafe { recording_window() };
        with_slave(slave.0, || {
            post_key(true, 0x41, 0x1E);
            post_key(false, 0x41, 0x1E);
        });

        let msgs = received_msgs();
        assert_eq!(msgs, vec![WM_KEYDOWN, WM_KEYUP], "按下与抬起都必须真的投出去");
    }

    /// 停止同步时补投的抬起，必须落到从窗口上 —— 这是「从窗口一直以为鼠标按着」的修复点
    #[test]
    fn release_message_reaches_slave_for_keys_and_buttons() {
        let _guard = serial();
        let slave = unsafe { recording_window() };
        with_slave(slave.0, || {
            // 模拟「按着 A 和左键，然后停止同步」
            PRESSED.lock().key_down(0x41, 0x1E);
            PRESSED.lock().button_down(BTN_LEFT);

            let (keys, buttons) = PRESSED.lock().drain();
            assert_eq!(keys, vec![(0x41, 0x1E)]);
            assert_eq!(buttons, BTN_LEFT);

            post_release(&keys, buttons);
        });

        let msgs = received_msgs();
        assert!(msgs.contains(&WM_KEYUP), "按键抬起没有投递：{msgs:?}");
        assert!(msgs.contains(&WM_LBUTTONUP), "鼠标抬起没有投递：{msgs:?}");
    }

    /// 失焦 / 停止时的兜底入口：`flush_pressed` 在没有总线的情况下直接投递
    #[test]
    fn flush_pressed_posts_release_without_bus() {
        let _guard = serial();
        let slave = unsafe { recording_window() };
        with_slave(slave.0, || {
            PRESSED.lock().key_down(0x20, 0x39); // 空格
            assert!(bus().is_none(), "本用例前提是没有启动同步");

            flush_pressed("测试");
        });

        assert!(received_msgs().contains(&WM_KEYUP), "补投抬起没有落地");
        assert!(PRESSED.lock().is_empty(), "补投之后状态表必须清空");
    }

    /// 鼠标键状态：按下记位、抬起清位，且不会把别的键位带掉
    #[test]
    fn note_button_tracks_mask() {
        let _guard = serial();
        PRESSED.lock().drain();
        note_button(WM_LBUTTONDOWN, true);
        note_button(WM_RBUTTONDOWN, true);
        assert_eq!(PRESSED.lock().drain().1, BTN_LEFT | BTN_RIGHT);

        note_button(WM_LBUTTONDOWN, true);
        note_button(WM_LBUTTONUP, false);
        assert_eq!(PRESSED.lock().drain(), (vec![], 0));
    }

    /// 无载荷通知：状态一变就带着新 revision 回调一次，回调里没有 JSON 也没有分配
    #[test]
    fn status_notifier_fires_with_a_revision() {
        static HITS: AtomicU64 = AtomicU64::new(0);
        static LAST: AtomicU64 = AtomicU64::new(0);

        extern "C" fn cb(rev: u64) {
            HITS.fetch_add(1, Ordering::Relaxed);
            LAST.store(rev, Ordering::Relaxed);
        }

        let _guard = serial();
        let token = set_status_notifier(Some(cb));
        assert_ne!(token, 0);

        let master = crate::test_support::popup_window();
        let slave = crate::test_support::popup_window();
        crate::test_support::begin_fake_sync(
            crate::test_support::hwnd_value(master.0),
            &[crate::test_support::hwnd_value(slave.0)],
        );
        let rev = status_revision();
        emit_status();

        assert_eq!(HITS.load(Ordering::Relaxed), 1, "状态变了应该通知一次");
        assert!(LAST.load(Ordering::Relaxed) > rev, "通知里的 revision 必须是新的");

        // 状态没再变 -> 不重复通知
        emit_status();
        assert_eq!(HITS.load(Ordering::Relaxed), 1, "状态没变不该重复通知");

        crate::test_support::end_fake_sync();
        emit_status();
        assert!(clear_status_notifier(token));
        let after = HITS.load(Ordering::Relaxed);
        emit_status();
        assert_eq!(HITS.load(Ordering::Relaxed), after, "注销后不该再回调");
    }

    #[test]
    fn unknown_message_is_skipped() {
        let slave = unsafe { popup(1200, 500, 400, 300) };
        assert!(mouse_message(0x0200 + 0x99, 10, 10, &Pos::default(), 0, slave.0).is_none());
    }
}
