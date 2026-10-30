//! 往从窗口写文本的三条通道。
//!
//! | 方式 | 消息 | 适用 | 代价 |
//! |---|---|---|---|
//! | 增量补字 | `WM_CHAR` | 改动落在末尾 | 最轻，光标自然前移 |
//! | 增量删字 | `WM_CHAR(0x08)` | 末尾删除 | 同上 |
//! | 整段重写 | `WM_SETTEXT` + `EM_SETSEL` | 任意改动 | 会重排内容、丢撤销栈，但一定对 |
//!
//! 为什么整段重写用 `WM_SETTEXT` 而不是 `EM_REPLACESEL`：后者要跨进程传字符串指针，
//! 而只有系统内置的少数消息（`WM_SETTEXT`、`WM_GETTEXT` 等，消息号 < `WM_USER`）由内核
//! 负责参数编组；`EM_REPLACESEL` 不在这个名单里，跨进程发过去对方拿到的是本进程的地址，
//! 轻则乱码重则崩窗口。`WM_SETTEXT` 是唯一稳妥的跨进程整段替换方式。
//!
//! 用 `SendMessageTimeout` 而不是 `PostMessage`：整段重写之后要立刻校验，
//! 必须确保写操作已经落地，否则校验读到的是旧内容，会误判成失败。

use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    PostMessageW, SendMessageTimeoutW, SMTO_ABORTIFHUNG, WM_CHAR,
};

pub const WM_SETTEXT: u32 = 0x000C;
pub const EM_SETSEL: u32 = 0x00B1;

const SEND_TIMEOUT_MS: u32 = 60;
/// 退格在 Edit 里是 `WM_CHAR('\b')`，比伪造一组 KEYDOWN/KEYUP 干净，也不依赖键盘状态
const BACKSPACE_CHAR: u16 = 0x08;
const PENDING: usize = usize::MAX;

/// 逐字投 `WM_CHAR`。返回成功投递的 unit 数。
pub fn post_chars(target: HWND, units: &[u16]) -> usize {
    if target.0.is_null() || units.is_empty() {
        return 0;
    }
    let mut sent = 0;
    for &unit in units {
        let ok = unsafe {
            PostMessageW(target, WM_CHAR, WPARAM(unit as usize), LPARAM(1)).is_ok()
        };
        if ok {
            sent += 1;
        }
    }
    sent
}

/// 投若干个退格。在标准输入框里等价于按 N 次 Backspace。
pub fn post_backspaces(target: HWND, count: usize) -> usize {
    if target.0.is_null() || count == 0 {
        return 0;
    }
    let mut sent = 0;
    for _ in 0..count {
        let ok = unsafe {
            PostMessageW(
                target,
                WM_CHAR,
                WPARAM(BACKSPACE_CHAR as usize),
                LPARAM(1),
            )
            .is_ok()
        };
        if ok {
            sent += 1;
        }
    }
    sent
}

/// 整段替换：`WM_SETTEXT` 之后把光标挪到 `caret`。
///
/// `WM_SETTEXT` 会让输入框自己重排并把选区收到开头，所以必须显式补一次 `EM_SETSEL`，
/// 否则用户继续打字时字会跑到最前面去。
pub fn replace_all(target: HWND, text: &[u16], caret: usize) -> bool {
    if target.0.is_null() {
        return false;
    }

    // WM_SETTEXT 要的是以 NUL 结尾的宽字符串
    let mut buffer = Vec::with_capacity(text.len() + 1);
    buffer.extend_from_slice(text);
    buffer.push(0);

    let mut result: usize = PENDING;
    let _ = unsafe {
        SendMessageTimeoutW(
            target,
            WM_SETTEXT,
            WPARAM(0),
            LPARAM(buffer.as_ptr() as isize),
            SMTO_ABORTIFHUNG,
            SEND_TIMEOUT_MS,
            Some(&mut result),
        )
    };
    // WM_SETTEXT 的返回值是 TRUE/FALSE；调用没落地时出参仍是哨兵
    if result == PENDING || result == 0 {
        return false;
    }

    set_caret(target, caret)
}

/// 把光标放到 `caret`（同时取消选区）。越界由输入框自己夹。
pub fn set_caret(target: HWND, caret: usize) -> bool {
    if target.0.is_null() {
        return false;
    }
    let mut result: usize = PENDING;
    let _ = unsafe {
        SendMessageTimeoutW(
            target,
            EM_SETSEL,
            WPARAM(caret),
            LPARAM(caret as isize),
            SMTO_ABORTIFHUNG,
            SEND_TIMEOUT_MS,
            Some(&mut result),
        )
    };
    result != PENDING
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn null_target_is_rejected() {
        let null = HWND(std::ptr::null_mut());
        assert_eq!(post_chars(null, &[0x41]), 0);
        assert_eq!(post_backspaces(null, 3), 0);
        assert!(!replace_all(null, &[0x41], 1));
        assert!(!set_caret(null, 0));
    }

    #[test]
    fn empty_insert_sends_nothing() {
        let null = HWND(std::ptr::null_mut());
        assert_eq!(post_chars(null, &[]), 0);
    }
}
