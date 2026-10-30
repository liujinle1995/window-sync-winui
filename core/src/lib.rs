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

pub mod event_bus;
pub mod ffi;
pub mod text_sync;

use event_bus::{EventBus, Pos, PressedState, Reliable, Task, BTN_LEFT, BTN_MIDDLE, BTN_RIGHT};

use once_cell::sync::Lazy;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::ffi::CString;
use std::os::raw::c_char;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use windows::Win32::Foundation::{BOOL, HWND, HGLOBAL, LPARAM, LRESULT, POINT, RECT, WPARAM};
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
    CallNextHookEx, EnumWindows, GetClientRect, GetForegroundWindow, GetWindowLongA, GetWindowTextW,
    GetWindowThreadProcessId, IsWindow, IsWindowVisible, PostMessageA, SetForegroundWindow,
    SetWindowsHookExA, ShowWindow, UnhookWindowsHookEx, GWL_STYLE, HHOOK, KBDLLHOOKSTRUCT,
    MSLLHOOKSTRUCT, SW_RESTORE, WH_KEYBOARD_LL, WH_MOUSE_LL, WM_KEYDOWN, WM_KEYUP,
    WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MBUTTONDOWN, WM_MBUTTONUP, WM_MOUSEMOVE, WM_MOUSEWHEEL,
    WM_RBUTTONDOWN, WM_RBUTTONUP, WM_SYSKEYDOWN, WM_SYSKEYUP, WS_VISIBLE,
};

const CF_UNICODETEXT: u32 = 13;

/// 状态推送回调（UTF-8 JSON，内容为 SyncStatus）
pub type StatusCb = extern "C" fn(*const c_char);
/// 日志回调（UTF-8 文本）
pub type LogCb = extern "C" fn(*const c_char);

static STATUS_CB: Lazy<Mutex<Option<StatusCb>>> = Lazy::new(|| Mutex::new(None));
static LOG_CB: Lazy<Mutex<Option<LogCb>>> = Lazy::new(|| Mutex::new(None));

/// 注册 / 注销回调（由 ffi 层调用）
pub fn set_status_callback(cb: Option<StatusCb>) {
    *STATUS_CB.lock() = cb;
}
pub fn set_log_callback(cb: Option<LogCb>) {
    *LOG_CB.lock() = cb;
}

/// 向宿主（WinUI 3）推送一条日志
pub fn log(msg: &str) {
    if let Some(cb) = *LOG_CB.lock() {
        if let Ok(c) = CString::new(msg.to_string()) {
            cb(c.as_ptr());
        }
    }
}

struct SafeHook(HHOOK);
unsafe impl Send for SafeHook {}
unsafe impl Sync for SafeHook {}


static SYNC_ACTIVE:    AtomicBool = AtomicBool::new(false);
static SYNC_MOUSE:     AtomicBool = AtomicBool::new(true);
static SYNC_KEYBOARD:  AtomicBool = AtomicBool::new(true);
static MASTER_HWND:    AtomicI64  = AtomicI64::new(0);

static SYNC_CLIPBOARD: AtomicBool = AtomicBool::new(false);
// 记录上次剪贴板内容的哈希，避免重复处理自己写入的内容
static LAST_CB_HASH:   Lazy<Mutex<u64>> = Lazy::new(|| Mutex::new(0));

static SLAVES:  Lazy<Mutex<Vec<i64>>>         = Lazy::new(|| Mutex::new(vec![]));
static KB_HOOK: Lazy<Mutex<Option<SafeHook>>> = Lazy::new(|| Mutex::new(None));
static MS_HOOK: Lazy<Mutex<Option<SafeHook>>> = Lazy::new(|| Mutex::new(None));
static BUS:          Lazy<Mutex<Option<Arc<EventBus>>>>  = Lazy::new(|| Mutex::new(None));
static INPUT_WORKER: Lazy<Mutex<Option<JoinHandle<()>>>> = Lazy::new(|| Mutex::new(None));
static TEXT_WORKER:  Lazy<Mutex<Option<JoinHandle<()>>>> = Lazy::new(|| Mutex::new(None));
static FOCUS_WATCH:  Lazy<Mutex<Option<JoinHandle<()>>>> = Lazy::new(|| Mutex::new(None));

/// 还按着的键 / 鼠标键。补投抬起消息全靠它，见 `event_bus::PressedState` 的注释。
static PRESSED: Lazy<Mutex<PressedState>> = Lazy::new(|| Mutex::new(PressedState::default()));

/// 上一次推送出去的状态 JSON，用来合并重复通知。
static LAST_STATUS: Lazy<Mutex<String>> = Lazy::new(|| Mutex::new(String::new()));

/// 最后一次鼠标位置：补投鼠标抬起时要用同一套坐标。
static LAST_POS: Lazy<Mutex<Pos>> = Lazy::new(|| Mutex::new(Pos::default()));

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
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowInfo  { pub hwnd: i64, pub title: String, pub pid: u32, pub visible: bool }
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProcessInfo { pub pid: u32, pub name: String, pub windows: Vec<WindowInfo> }
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncConfig  {
    pub master_hwnd: i64,
    pub slave_hwnds: Vec<i64>,
    pub sync_mouse: bool,
    pub sync_keyboard: bool,
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
    let mut tmp = [0u16; 512];
    let n = GetWindowTextW(hwnd, &mut tmp);
    if n == 0 { return BOOL(1); }
    let title = String::from_utf16_lossy(&tmp[..n as usize]).trim().to_string();
    if title.is_empty() { return BOOL(1); }
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid == 0 { return BOOL(1); }
    if GetWindowLongA(hwnd, GWL_STYLE) as u32 & WS_VISIBLE.0 == 0 { return BOOL(1); }
    buf.wins.push(WindowInfo { hwnd: hwnd.0 as i64, title, pid, visible: true });
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
                map.insert(pid, ProcessInfo { pid, name, windows: vec![] });
                if Process32NextW(snap, &mut e).is_err() { break; }
            }
        }
        let _ = windows::Win32::Foundation::CloseHandle(snap);
        let mut buf = EnumBuf { wins: vec![] };
        let _ = EnumWindows(Some(enum_cb), LPARAM(&mut buf as *mut _ as isize));
        for w in buf.wins { if let Some(p) = map.get_mut(&w.pid) { p.windows.push(w); } }
    }
    let mut v: Vec<ProcessInfo> = map.into_values().filter(|p|!p.windows.is_empty()).collect();
    v.sort_by(|a,b| a.name.cmp(&b.name));
    v
}

pub fn get_windows() -> Vec<WindowInfo> {
    unsafe {
        let mut buf = EnumBuf { wins: vec![] };
        let _ = EnumWindows(Some(enum_cb), LPARAM(&mut buf as *mut _ as isize));
        buf.wins
    }
}

fn remove_hooks() {
    unsafe {
        if let Some(h) = KB_HOOK.lock().take()  { let _ = UnhookWindowsHookEx(h.0); }
        if let Some(h) = MS_HOOK.lock().take()  { let _ = UnhookWindowsHookEx(h.0); }
    }
}

fn emit_status() {
    let json = match serde_json::to_string(&get_sync_status()) {
        Ok(j) => j,
        Err(_) => return,
    };

    // 合并重复通知：状态没变就不推。调用点很多（启停、改选项、从窗口掉线…），
    // 同一个状态被连着推几次纯属浪费宿主 UI 的重绘。
    {
        let mut last = LAST_STATUS.lock();
        if *last == json { return; }
        *last = json.clone();
    }

    if let Some(cb) = *STATUS_CB.lock() {
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
        if let Some((wp, lp)) = mouse_message(msg, x, y, pos.screen_x, pos.screen_y, 0, slave) {
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
                WM_MOUSEWHEEL, x, y, pos.screen_x, pos.screen_y, step as i16, slave,
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
            if let Some((wp, lp)) = mouse_message(msg, x, y, pos.screen_x, pos.screen_y, 0, slave) {
                unsafe { let _ = PostMessageA(slave, msg, wp, lp); }
            }
        }
    }
}

fn start_input_worker(bus: Arc<EventBus>) {
    let handle = std::thread::spawn(move || {
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
    });
    *INPUT_WORKER.lock() = Some(handle);
}

/// 文本同步独立线程：注入慢也只影响它自己。
fn start_text_worker(bus: Arc<EventBus>) {
    let handle = std::thread::spawn(move || {
        while let Some(text) = bus.next_text() {
            if !SYNC_ACTIVE.load(Ordering::Relaxed) { continue; }
            inject_text(&text);
        }
    });
    *TEXT_WORKER.lock() = Some(handle);
}

/// 逐字注入文本。每个字之间检查一次停止标志，停止时最多再等一个字的时间。
fn inject_text(text: &str) {
    if !SYNC_CLIPBOARD.load(Ordering::Relaxed) { return; }

    let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);
    for slave in live_slaves() {
        if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }
        unsafe { let _ = SetForegroundWindow(slave); }
        std::thread::sleep(std::time::Duration::from_millis(25));

        for ch in text.chars() {
            if !SYNC_ACTIVE.load(Ordering::Relaxed) { return; }
            unsafe { send_unicode_char(ch as u32); }
            // 字符间隔：防止游戏端队列溢出
            std::thread::sleep(std::time::Duration::from_millis(6));
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }

    // 归还焦点到主窗口
    if unsafe { IsWindow(master).as_bool() } {
        std::thread::sleep(std::time::Duration::from_millis(15));
        unsafe { let _ = SetForegroundWindow(master); }
    }
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
/// 所以再加一个轻量看门狗；30ms 一片睡眠，停止时 join 最多等这么久。
fn start_focus_watch(bus: Arc<EventBus>) {
    let handle = std::thread::spawn(move || {
        while !bus.is_closed() {
            for _ in 0..5 {
                if bus.is_closed() { return; }
                std::thread::sleep(std::time::Duration::from_millis(30));
            }
            if !SYNC_ACTIVE.load(Ordering::Relaxed) { continue; }
            flush_if_focus_lost();
        }
    });
    *FOCUS_WATCH.lock() = Some(handle);
}

/// 停掉工作线程并等它们把队列排空。`close()` 之后消费者会先取完剩余事件再退出。
fn join_worker(slot: &Lazy<Mutex<Option<JoinHandle<()>>>>) {
    if let Some(handle) = slot.lock().take() {
        let _ = handle.join();
    }
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
fn mouse_message(
    msg: u32,
    x: i32,
    y: i32,
    screen_x: i32,
    screen_y: i32,
    wheel_delta: i16,
    slave: HWND,
) -> Option<(WPARAM, LPARAM)> {
    let client = || make_lp(x, y);

    Some(match msg {
        WM_MOUSEMOVE => (WPARAM(0), client()),
        WM_LBUTTONDOWN => (WPARAM(0x0001), client()), // MK_LBUTTON
        WM_LBUTTONUP => (WPARAM(0), client()),
        WM_RBUTTONDOWN => (WPARAM(0x0002), client()), // MK_RBUTTON
        WM_RBUTTONUP => (WPARAM(0), client()),
        WM_MBUTTONDOWN => (WPARAM(0x0010), client()), // MK_MBUTTON
        WM_MBUTTONUP => (WPARAM(0), client()),
        WM_MOUSEWHEEL => {
            // 高 16 位是滚动量（有符号，向上为正）
            let wp = WPARAM(((wheel_delta as u32) << 16) as usize);
            let mut point = POINT { x, y };
            let lp = if unsafe { ClientToScreen(slave, &mut point) }.as_bool() {
                make_lp(point.x, point.y)
            } else {
                // 转不回屏幕坐标时退回原始屏幕坐标：位置会偏，但坐标系至少是对的
                make_lp(screen_x, screen_y)
            };
            (wp, lp)
        }
        _ => return None,
    })
}

/// 客户区尺寸。取不到（窗口已销毁 / 最小化到 0）时返回 `None`，调用方按「不缩放」处理。
fn client_size(h: HWND) -> Option<(i32, i32)> {
    let mut rect = RECT::default();
    if unsafe { GetClientRect(h, &mut rect) }.is_ok() {
        let w = rect.right - rect.left;
        let h = rect.bottom - rect.top;
        if w > 0 && h > 0 { return Some((w, h)); }
    }
    None
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

// ── start_sync ────────────────────────────────────────────────────────────
pub fn start_sync(config: SyncConfig) -> Result<String, String> {
    if SYNC_ACTIVE.load(Ordering::Relaxed) {
        // 重启：走一遍正常停止流程，把残留按键放掉、线程收干净
        let _ = stop_sync();
    }
    if config.master_hwnd == 0       { return Err("请先选择主窗口".into()); }
    if config.slave_hwnds.is_empty() { return Err("请至少选择一个从窗口".into()); }

    MASTER_HWND.store(config.master_hwnd, Ordering::Relaxed);
    *SLAVES.lock() = config.slave_hwnds.clone();
    SYNC_MOUSE.store(config.sync_mouse, Ordering::Relaxed);
    SYNC_KEYBOARD.store(config.sync_keyboard, Ordering::Relaxed);
    SYNC_CLIPBOARD.store(config.sync_clipboard, Ordering::Relaxed);
    // 重置剪贴板哈希，避免误触发旧内容
    *LAST_CB_HASH.lock() = 0;

    // 每次启动都是一套新的总线与工作线程，旧的在 stop 里已经收干净
    let bus = Arc::new(EventBus::new());
    *BUS.lock() = Some(bus.clone());
    *PRESSED.lock() = PressedState::default();
    *LAST_STATUS.lock() = String::new();
    *LAST_POS.lock() = Pos::default();

    start_input_worker(bus.clone());
    start_text_worker(bus.clone());
    start_focus_watch(bus.clone());
    start_clipboard_monitor();

    unsafe {
        // 全局低级 hook：鼠标 + 键盘
        if config.sync_keyboard {
            let h = SetWindowsHookExA(WH_KEYBOARD_LL, Some(kb_hook), None, 0)
                .map_err(|e| e.to_string())?;
            *KB_HOOK.lock() = Some(SafeHook(h));
        }
        if config.sync_mouse {
            let h = SetWindowsHookExA(WH_MOUSE_LL, Some(ms_hook), None, 0)
                .map_err(|e| e.to_string())?;
            *MS_HOOK.lock() = Some(SafeHook(h));
        }
    }

    SYNC_ACTIVE.store(true, Ordering::Relaxed);
    text_sync::set_enabled(config.text_mirror);
    log("同步已启动");
    emit_status();
    Ok("同步已启动".into())
}

pub fn stop_sync() -> Result<String, String> {
    // 顺序很关键：先摘钩子（不再有新事件进来），再补抬起，最后关总线等线程退出。
    // 反过来的话，补抬起之后仍可能有排队的 KeyDown 落地，从窗口照样卡键。
    remove_hooks();
    SYNC_ACTIVE.store(false, Ordering::Relaxed);
    text_sync::set_enabled(false);
    // join 在 shutdown 里：文本线程退出前会把手上那一拍做完，不会留半截状态
    text_sync::shutdown();

    let (keys, buttons) = PRESSED.lock().drain();
    let has_pressed = !keys.is_empty() || buttons != 0;

    if let Some(bus) = BUS.lock().take() {
        if has_pressed {
            // 走可靠队列：它会排在所有已入队的 KeyDown 之后投出去
            bus.push_reliable(Reliable::Release { keys, buttons });
        }
        bus.close();
    }

    // join 是为了让「补抬起」真的投完再返回：调用方拿到「已停止」时，
    // 从窗口的按键状态一定已经复原了。
    join_worker(&INPUT_WORKER);
    join_worker(&TEXT_WORKER);
    join_worker(&FOCUS_WATCH);

    PRESSED.lock().drain();

    if has_pressed {
        log("同步已停止：已补投抬起所有按住的按键");
    } else {
        log("同步已停止");
    }
    emit_status();
    Ok("同步已停止".into())
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

pub fn update_sync_options(sync_mouse: bool, sync_keyboard: bool, sync_clipboard: bool) -> Result<(), String> {
    SYNC_MOUSE.store(sync_mouse, Ordering::Relaxed);
    SYNC_KEYBOARD.store(sync_keyboard, Ordering::Relaxed);
    SYNC_CLIPBOARD.store(sync_clipboard, Ordering::Relaxed);
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

unsafe extern "system" fn ms_hook(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if code >= 0 && SYNC_ACTIVE.load(Ordering::Relaxed) && SYNC_MOUSE.load(Ordering::Relaxed) {
        let ms     = &*(lp.0 as *const MSLLHOOKSTRUCT);
        let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);

        // 鼠标移动频率太高，不为它多做一次窗口查询；按键类事件顺带看看是不是已经失焦
        if wp.0 as u32 != WM_MOUSEMOVE {
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
            };

            let msg = wp.0 as u32;
            if let Some(bus) = bus() {
                match msg {
                    WM_MOUSEMOVE => bus.push_move(pos),
                    WM_MOUSEWHEEL => bus.push_wheel((ms.mouseData >> 16) as i16, pos),
                    WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN => {
                        note_button(msg, true);
                        bus.push_reliable(Reliable::Button { msg, pos });
                    }
                    WM_LBUTTONUP | WM_RBUTTONUP | WM_MBUTTONUP => {
                        note_button(msg, false);
                        bus.push_reliable(Reliable::Button { msg, pos });
                    }
                    _ => {}
                }
            }
        }
    }
    CallNextHookEx(None, code, wp, lp)
}

// 跟踪 Ctrl 键状态（用于检测 Ctrl+C 手动触发剪贴板同步）
static CTRL_DOWN: AtomicBool = AtomicBool::new(false);

unsafe extern "system" fn kb_hook(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if code >= 0 && SYNC_ACTIVE.load(Ordering::Relaxed) {
        let kb     = &*(lp.0 as *const KBDLLHOOKSTRUCT);
        let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);

        // 按住键 Alt+Tab 切走时，之后的抬起不会再转发（下面那句 if 挡住了）。
        // 在这里补一次抬起，避免从窗口永久卡键。
        flush_if_focus_lost();

        if GetForegroundWindow() == master {
            let n  = wp.0 as u32;
            let vk = kb.vkCode;

            // 任何一个键都当作「文本可能变了」的信号去唤醒文本同步：
            // 它会立刻采样一次，比固定轮询快 0~120ms。可打印键顺便计入
            // 「疑似自绘输入框」的嫌疑统计（转发了按键但文本从不变化）。
            if n == WM_KEYDOWN || n == WM_SYSKEYDOWN {
                text_sync::note_key(vk);
            }

            // 跟踪 Ctrl 键
            if vk == 0x11 {  // VK_CONTROL
                CTRL_DOWN.store(n == WM_KEYDOWN || n == WM_SYSKEYDOWN, Ordering::Relaxed);
            }

            // Ctrl+C 检测：仅在 keydown 时触发一次（用 CTRL_DOWN 防重入）
            if SYNC_CLIPBOARD.load(Ordering::Relaxed)
                && (n == WM_KEYDOWN || n == WM_SYSKEYDOWN)
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

            if SYNC_KEYBOARD.load(Ordering::Relaxed) {
                if n == WM_KEYDOWN || n == WM_SYSKEYDOWN {
                    PRESSED.lock().key_down(vk, kb.scanCode);
                    if let Some(bus) = bus() {
                        bus.push_reliable(Reliable::Key { down: true, vk, scan: kb.scanCode });
                    }
                } else if n == WM_KEYUP || n == WM_SYSKEYUP {
                    // 抬起无条件记账：即便总线已经关了，状态表也不能留脏数据
                    PRESSED.lock().key_up(vk);
                    if let Some(bus) = bus() {
                        bus.push_reliable(Reliable::Key { down: false, vk, scan: kb.scanCode });
                    }
                }
            }
        }
    }
    CallNextHookEx(None, code, wp, lp)
}

// 剪贴板监控：不再使用轮询，完全依靠 kb_hook 中的 Ctrl+C 检测触发
// 轮询会导致从窗口收到多次相同文本（序列号变化 + Ctrl+C 各触发一次）
fn start_clipboard_monitor() {
    // 空函数，保留调用点不变
}


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

pub fn kill_process(pid: u32) -> Result<(), String> {
    if pid == u32::MAX { return Err("LNK 启动的进程请手动关闭".to_string()); }
    unsafe {
        let h = OpenProcess(PROCESS_TERMINATE, false, pid).map_err(|e| format!("无法打开进程 ({}): {e}", e.code().0))?;
        TerminateProcess(h, 1).map_err(|e| format!("终止进程失败: {e}"))?;
        let _ = windows::Win32::Foundation::CloseHandle(h);
    }
    Ok(())
}

pub fn kill_process_by_name(exe_name: String) -> Result<u32, String> {
    unsafe {
        let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0).map_err(|e| e.to_string())?;
        let mut e = PROCESSENTRY32W::default();
        e.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let name_low = exe_name.to_lowercase();
        let mut killed = 0u32;
        if Process32FirstW(snap, &mut e).is_ok() {
            loop {
                let n = String::from_utf16_lossy(&e.szExeFile.iter().take_while(|&&c|c!=0).cloned().collect::<Vec<_>>()).to_lowercase();
                if n == name_low || n.starts_with(&name_low.replace(".exe","")) {
                    if let Ok(h) = OpenProcess(PROCESS_TERMINATE, false, e.th32ProcessID) {
                        if TerminateProcess(h, 1).is_ok() { killed += 1; }
                        let _ = windows::Win32::Foundation::CloseHandle(h);
                    }
                }
                if Process32NextW(snap, &mut e).is_err() { break; }
            }
        }
        let _ = windows::Win32::Foundation::CloseHandle(snap);
        Ok(killed)
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
            mouse_message(WM_MOUSEWHEEL, 200, 150, 0, 0, 120, slave.0).unwrap();

        // 落点 = 客户区原点 + 客户区坐标，绝不是把 (200,150) 直接塞进去
        assert_eq!((lp_x(lp), lp_y(lp)), (ox + 200, oy + 150));
        assert_ne!((lp_x(lp), lp_y(lp)), (200, 150), "滚轮不能沿用客户区坐标");

        // 高 16 位是滚动量
        assert_eq!((wp.0 as u32 >> 16) as u16 as i16, 120);
    }

    #[test]
    fn negative_wheel_delta_keeps_sign() {
        let slave = unsafe { popup(1200, 500, 400, 300) };
        let (wp, _) = mouse_message(WM_MOUSEWHEEL, 10, 10, 0, 0, -120, slave.0).unwrap();
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
            let (wp, lp) = mouse_message(msg, 137, 246, 9999, 9999, 0, slave.0).unwrap();
            assert_eq!((lp_x(lp), lp_y(lp)), (137, 246), "消息 {msg:#06X} 的 lParam 不是客户区坐标");
            assert_eq!(wp.0, expected_wp, "消息 {msg:#06X} 的 wParam 不对");
        }
    }

    // ── 端到端：投递路径（真实窗口 + 真实 PostMessage）────────────────────
    //
    // 上面那些测的是「生成什么」；这里测「到底投出去没有」。
    // 丢「抬起」正是用户报的那个 bug 的核心：队列满了抬起被扔，从窗口永久按键。

    /// 这些用例共用 `SLAVES` / `RECEIVED` 等全局状态，并行跑会互相串扰，必须串行。
    /// （单独跑某个用例时它是绿的，一起跑就红 —— 就是并行闹的。）
    static TEST_LOCK: Lazy<Mutex<()>> = Lazy::new(|| Mutex::new(()));

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
        let _guard = TEST_LOCK.lock();
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
        let _guard = TEST_LOCK.lock();
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
        let _guard = TEST_LOCK.lock();
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
        let _guard = TEST_LOCK.lock();
        PRESSED.lock().drain();
        note_button(WM_LBUTTONDOWN, true);
        note_button(WM_RBUTTONDOWN, true);
        assert_eq!(PRESSED.lock().drain().1, BTN_LEFT | BTN_RIGHT);

        note_button(WM_LBUTTONDOWN, true);
        note_button(WM_LBUTTONUP, false);
        assert_eq!(PRESSED.lock().drain(), (vec![], 0));
    }

    #[test]
    fn unknown_message_is_skipped() {
        let slave = unsafe { popup(1200, 500, 400, 300) };
        assert!(mouse_message(0x0200 + 0x99, 10, 10, 0, 0, 0, slave.0).is_none());
    }
}
