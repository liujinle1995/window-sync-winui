//! 文本镜像 —— 让中文输入与粘贴内容也能同步到从窗口。
//!
//! 为什么不用键盘钩子：
//! `WH_KEYBOARD_LL` 只能拿到虚拟键码，中文是 IME 组字后由 `WM_IME_COMPOSITION` / `WM_CHAR`
//! 投递给窗口的，低级钩子根本看不到字符；即便拿到键码，`PostMessage(WM_KEYDOWN)` 也是伪造消息，
//! 不会经过 IME，从窗口永远组不出中文。
//!
//! 这里的做法不需要 DLL 注入、不需要共享内存：
//! 1. 定期用 `AttachThreadInput + GetFocus + WM_GETTEXT` 读出主窗口当前焦点控件的文本；
//! 2. 与上一次快照做 diff，得出"末尾追加了什么"或"末尾删了几个字"；
//! 3. 用 `WM_CHAR` / `WM_KEYDOWN(VK_BACK)` 投递给每个从窗口的焦点控件。
//!
//! `WM_CHAR` 可以直接发给后台窗口，因此全程不抢焦点、不移动光标，也不会打断主窗口输入。
//! 局限：目标窗口的输入框必须是标准控件（Edit / RichEdit 等）。自绘输入框读不到文本，
//! 这类窗口只能退回"剪贴板中转"。

use once_cell::sync::Lazy;
use parking_lot::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Duration;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    GetGUIThreadInfo, GetWindowThreadProcessId, IsWindow, PostMessageW, SendMessageTimeoutW,
    SMTO_ABORTIFHUNG, GUITHREADINFO, WM_CHAR, WM_GETTEXT, WM_KEYDOWN, WM_KEYUP,
};

const WM_GETTEXTLENGTH: u32 = 0x000E;
const VK_BACK: usize = 0x08;
/// 单次最多补发的字符数；超出说明是一次大段粘贴或光标处编辑，直接重新对齐快照
const MAX_CHUNK: usize = 200;
/// 采样间隔：打字场景下 80ms 的延迟基本无感
const POLL_MS: u64 = 80;

static ENABLED: AtomicBool = AtomicBool::new(false);
static RUNNING: AtomicBool = AtomicBool::new(false);
static LAST_TEXT: Lazy<Mutex<String>> = Lazy::new(|| Mutex::new(String::new()));

/// 开关文本镜像，返回是否真的发生了变化。
pub fn set_enabled(on: bool) -> bool {
    if ENABLED.swap(on, Ordering::SeqCst) == on { return false; }

    // 切换瞬间丢弃旧快照，避免把切换前的文本误当成"新增"灌给从窗口
    *LAST_TEXT.lock() = String::new();
    if on { spawn_worker(); }
    true
}

pub fn is_enabled() -> bool { ENABLED.load(Ordering::Relaxed) }

/// `stop_sync` 时调用，让工作线程自然退出。
pub fn shutdown() { RUNNING.store(false, Ordering::SeqCst); }

fn spawn_worker() {
    if RUNNING.swap(true, Ordering::SeqCst) { return; }
    thread::spawn(mirror_loop);
}

fn mirror_loop() {
    while RUNNING.load(Ordering::SeqCst) {
        thread::sleep(Duration::from_millis(POLL_MS));

        if !ENABLED.load(Ordering::Relaxed) { continue; }

        let Some((master, slaves)) = crate::mirror_targets() else { continue; };
        if slaves.is_empty() { continue; }

        let Some(current) = read_focus_text(HWND(master as *mut _)) else { continue };

        let mut last = LAST_TEXT.lock();
        if current == *last { continue; }

        let edit = plan_edit(&last, &current);
        *last = current;
        drop(last);

        match edit {
            Some(Edit::Append(text)) => {
                for ch in text.chars() { broadcast_char(&slaves, ch); }
            }
            Some(Edit::Delete(count)) => {
                for _ in 0..count { broadcast_backspace(&slaves); }
            }
            None => crate::log("文本镜像：检测到光标处编辑或大段改动，已重新对齐（本次不同步）"),
        }
    }
}

enum Edit { Append(String), Delete(usize) }

/// 只认"末尾追加"与"末尾删除"两种改动，其余（光标处插入、整段替换）不做同步。
fn plan_edit(old: &str, new: &str) -> Option<Edit> {
    if new.len() > old.len() && new.starts_with(old) {
        let tail = &new[old.len()..];
        return if tail.chars().count() > MAX_CHUNK { None } else { Some(Edit::Append(tail.to_string())) };
    }

    if old.len() > new.len() && old.starts_with(new) {
        let removed = old[new.len()..].chars().count();
        return if removed > MAX_CHUNK { None } else { Some(Edit::Delete(removed)) };
    }

    None
}

/// 取窗口所属线程的焦点控件。
/// 用 `GetGUIThreadInfo` 而不是 `AttachThreadInput + GetFocus`：前者不需要把本线程的输入队列
/// 与目标线程绑在一起，因此不会干扰目标窗口，也没有附加/解绑的时序风险。
fn focus_control(hwnd: HWND) -> Option<HWND> {
    unsafe {
        if !IsWindow(hwnd).as_bool() { return None; }

        let mut pid = 0u32;
        let tid = GetWindowThreadProcessId(hwnd, Some(&mut pid));
        if tid == 0 { return None; }

        let mut info = GUITHREADINFO::default();
        info.cbSize = std::mem::size_of::<GUITHREADINFO>() as u32;
        if GetGUIThreadInfo(tid, &mut info).is_err() { return None; }

        // 焦点控件就是顶层窗口本身时，多半是自绘输入框，WM_GETTEXT 会读到窗口标题，直接放弃
        let focus = info.hwndFocus;
        if focus.0.is_null() || focus == hwnd { None } else { Some(focus) }
    }
}

fn read_focus_text(master: HWND) -> Option<String> {
    let target = focus_control(master)?;

    unsafe {
        let mut length: usize = 0;
        let _ = SendMessageTimeoutW(
            target, WM_GETTEXTLENGTH, WPARAM(0), LPARAM(0), SMTO_ABORTIFHUNG, 40,
            Some(&mut length as *mut usize));
        if length == 0 || length > 8192 { return None; }

        let mut buffer = vec![0u16; length + 1];
        let mut written: usize = 0;
        let result = SendMessageTimeoutW(
            target, WM_GETTEXT, WPARAM(buffer.len()), LPARAM(buffer.as_mut_ptr() as isize),
            SMTO_ABORTIFHUNG, 40, Some(&mut written as *mut usize));

        if result.0 == 0 || written == 0 { return None; }
        Some(String::from_utf16_lossy(&buffer[..written]))
    }
}

fn broadcast_char(slaves: &[i64], ch: char) {
    // UTF-16 代理对拆成两个 WM_CHAR，与系统输入法的行为一致
    let mut buffer = [0u16; 2];
    let units = ch.encode_utf16(&mut buffer);
    for &unit in units.iter() {
        for &h in slaves {
            if let Some(target) = focus_control(HWND(h as *mut _)) {
                unsafe { let _ = PostMessageW(target, WM_CHAR, WPARAM(unit as usize), LPARAM(1)); }
            }
        }
    }
}

fn broadcast_backspace(slaves: &[i64]) {
    for &h in slaves {
        if let Some(target) = focus_control(HWND(h as *mut _)) {
            unsafe {
                let down = (1u32 | (0x0E << 16)) as isize;
                let up = (0xC000_0001u32 | (0x0E << 16)) as isize;
                let _ = PostMessageW(target, WM_KEYDOWN, WPARAM(VK_BACK), LPARAM(down));
                let _ = PostMessageW(target, WM_KEYUP, WPARAM(VK_BACK), LPARAM(up));
            }
        }
    }
}
