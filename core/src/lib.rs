//! 乐享同步操作 — 核心逻辑
//! 鼠标同步：PostMessageA 客户区坐标，不移动系统光标
//! 键盘同步：PostMessageA WM_KEYDOWN/WM_KEYUP
//! 中文同步：见 `text_mirror` 模块 —— 读主窗口焦点控件文本做 diff，再用 WM_CHAR 投递给从窗口
//!
//! 本文件与 Tauri 版 `src-tauri/src/lib.rs` 逻辑基本一致，仅去掉了 `#[tauri::command]`
//! 与 AppHandle，状态推送改由 `ffi` 层注册的 C 回调完成。

pub mod ffi;
pub mod text_mirror;

use once_cell::sync::Lazy;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::ffi::CString;
use std::os::raw::c_char;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::mpsc::{self, Receiver, SyncSender};
use windows::Win32::Foundation::{BOOL, HWND, HGLOBAL, LPARAM, LRESULT, POINT, WPARAM};
use windows::Win32::Graphics::Gdi::ScreenToClient;
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
    CallNextHookEx, EnumWindows, GetForegroundWindow, GetWindowLongA, GetWindowTextW,
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

#[derive(Debug, Clone)]
enum SyncEvent {
    Mouse         { rel_x: i32, rel_y: i32, msg: u32, wheel_delta: i16 },
    KeyDown       { vk: u32, scan: u32 },
    KeyUp         { vk: u32, scan: u32 },
    ClipboardText { text: String },   // 剪贴板中转：把文本逐字注入从窗口
}

struct SendTx(SyncSender<SyncEvent>);
unsafe impl Sync for SendTx {}

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
static TX:      Lazy<Mutex<Option<SendTx>>>   = Lazy::new(|| Mutex::new(None));

#[inline]
fn enqueue(ev: SyncEvent) {
    if let Some(s) = TX.lock().as_ref() { let _ = s.0.try_send(ev); }
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
    *TX.lock() = None;
}

fn emit_status() {
    let json = match serde_json::to_string(&get_sync_status()) {
        Ok(j) => j,
        Err(_) => return,
    };
    if let Some(cb) = *STATUS_CB.lock() {
        if let Ok(c) = CString::new(json) {
            cb(c.as_ptr());
        }
    }
}

// ── 工作线程：消费事件队列 ───────────────────────────────────────────────
fn start_worker(rx: Receiver<SyncEvent>) {
    std::thread::spawn(move || {
        while let Ok(ev) = rx.recv() {
            if !SYNC_ACTIVE.load(Ordering::Relaxed) { continue; }
            let slaves: Vec<i64> = SLAVES.lock().clone();
            unsafe {
                match ev {
                    SyncEvent::Mouse { rel_x, rel_y, msg, wheel_delta } => {
                        if !SYNC_MOUSE.load(Ordering::Relaxed) { continue; }
                        let lp = make_lp(rel_x, rel_y);
                        for &h in &slaves {
                            let s = HWND(h as *mut _);
                            if !IsWindow(s).as_bool() { continue; }
                            match msg {
                                m if m == WM_MOUSEMOVE   => { let _ = PostMessageA(s, WM_MOUSEMOVE,   WPARAM(0),        lp); }
                                m if m == WM_LBUTTONDOWN => { let _ = PostMessageA(s, WM_LBUTTONDOWN, WPARAM(0x0001),   lp); }
                                m if m == WM_LBUTTONUP   => { let _ = PostMessageA(s, WM_LBUTTONUP,   WPARAM(0),        lp); }
                                m if m == WM_RBUTTONDOWN => { let _ = PostMessageA(s, WM_RBUTTONDOWN, WPARAM(0x0002),   lp); }
                                m if m == WM_RBUTTONUP   => { let _ = PostMessageA(s, WM_RBUTTONUP,   WPARAM(0),        lp); }
                                m if m == WM_MBUTTONDOWN => { let _ = PostMessageA(s, WM_MBUTTONDOWN, WPARAM(0x0010),   lp); }
                                m if m == WM_MBUTTONUP   => { let _ = PostMessageA(s, WM_MBUTTONUP,   WPARAM(0),        lp); }
                                m if m == WM_MOUSEWHEEL  => {
                                    let wp = ((wheel_delta as u32) << 16) as usize;
                                    let _ = PostMessageA(s, WM_MOUSEWHEEL, WPARAM(wp), lp);
                                }
                                _ => {}
                            }
                        }
                    }
                    SyncEvent::KeyDown { vk, scan } => {
                        if !SYNC_KEYBOARD.load(Ordering::Relaxed) { continue; }
                        for &h in &slaves {
                            let s = HWND(h as *mut _);
                            if IsWindow(s).as_bool() {
                                let lp = (1u32 | (scan << 16)) as isize;
                                let _ = PostMessageA(s, WM_KEYDOWN, WPARAM(vk as usize), LPARAM(lp));
                            }
                        }
                    }
                    SyncEvent::KeyUp { vk, scan } => {
                        if !SYNC_KEYBOARD.load(Ordering::Relaxed) { continue; }
                        for &h in &slaves {
                            let s = HWND(h as *mut _);
                            if IsWindow(s).as_bool() {
                                let lp = (0xC0000001u32 | (scan << 16)) as isize;
                                let _ = PostMessageA(s, WM_KEYUP, WPARAM(vk as usize), LPARAM(lp));
                            }
                        }
                    }
                    SyncEvent::ClipboardText { text } => {
                        if !SYNC_CLIPBOARD.load(Ordering::Relaxed) { continue; }
                        let master = HWND(MASTER_HWND.load(Ordering::Relaxed) as *mut _);
                        let chars: Vec<u32> = text.chars().map(|c| c as u32).collect();
                        for &h in &slaves {
                            let slave = HWND(h as *mut _);
                            if !IsWindow(slave).as_bool() { continue; }
                            // 聚焦从窗口，逐字注入 Unicode
                            let _ = SetForegroundWindow(slave);
                            std::thread::sleep(std::time::Duration::from_millis(25));
                            for &ch in &chars {
                                send_unicode_char(ch);
                                // 字符间隔：防止游戏端队列溢出
                                std::thread::sleep(std::time::Duration::from_millis(6));
                            }
                            std::thread::sleep(std::time::Duration::from_millis(20));
                        }
                        // 归还焦点到主窗口
                        if IsWindow(master).as_bool() {
                            std::thread::sleep(std::time::Duration::from_millis(15));
                            let _ = SetForegroundWindow(master);
                        }
                    }
                }
            }
        }
    });
}

#[inline]
fn make_lp(x: i32, y: i32) -> LPARAM {
    LPARAM((((y as u16 as u32) << 16) | (x as u16 as u32)) as isize)
}

// ── start_sync ────────────────────────────────────────────────────────────
pub fn start_sync(config: SyncConfig) -> Result<String, String> {
    if SYNC_ACTIVE.load(Ordering::Relaxed) {
        remove_hooks();
        SYNC_ACTIVE.store(false, Ordering::Relaxed);
        std::thread::sleep(std::time::Duration::from_millis(50));
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

    let (tx, rx) = mpsc::sync_channel::<SyncEvent>(2048);
    *TX.lock() = Some(SendTx(tx));
    start_worker(rx);
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
    text_mirror::set_enabled(config.text_mirror);
    log("同步已启动");
    emit_status();
    Ok("同步已启动".into())
}

pub fn stop_sync() -> Result<String, String> {
    remove_hooks();
    SYNC_ACTIVE.store(false, Ordering::Relaxed);
    text_mirror::set_enabled(false);
    text_mirror::shutdown();
    log("同步已停止");
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
        text_mirror:    text_mirror::is_enabled(),
    }
}

/// 供 `text_mirror` 取用当前的主窗口与从窗口列表。
pub(crate) fn mirror_targets() -> Option<(i64, Vec<i64>)> {
    if !SYNC_ACTIVE.load(Ordering::Relaxed) { return None; }
    let master = MASTER_HWND.load(Ordering::Relaxed);
    if master == 0 { return None; }
    Some((master, SLAVES.lock().clone()))
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
        if GetForegroundWindow() == master {
            let mut pt = POINT { x: ms.pt.x, y: ms.pt.y };
            let _ = ScreenToClient(master, &mut pt);
            let msg = wp.0 as u32;
            let wd  = if msg == WM_MOUSEWHEEL { (ms.mouseData >> 16) as i16 } else { 0 };
            enqueue(SyncEvent::Mouse { rel_x: pt.x, rel_y: pt.y, msg, wheel_delta: wd });
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
        if GetForegroundWindow() == master {
            let n  = wp.0 as u32;
            let vk = kb.vkCode;

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
                            enqueue(SyncEvent::ClipboardText { text });
                        }
                    });
                }
            }

            if SYNC_KEYBOARD.load(Ordering::Relaxed) {
                if n == WM_KEYDOWN || n == WM_SYSKEYDOWN {
                    enqueue(SyncEvent::KeyDown { vk, scan: kb.scanCode });
                } else if n == WM_KEYUP || n == WM_SYSKEYUP {
                    enqueue(SyncEvent::KeyUp { vk, scan: kb.scanCode });
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
