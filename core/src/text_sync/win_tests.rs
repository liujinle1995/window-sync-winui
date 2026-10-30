//! 用真实 Win32 窗口验证「读文本 / 设光标 / 整段重写」这条链路。
//!
//! 纯函数测试证明不了这些：`WM_GETTEXT` 的消息号、`EM_GETSEL` 的打包方式、
//! `WM_SETTEXT` 之后光标会不会自己跑回开头 —— 都只有拿真控件跑一遍才知道。
//!
//! 这里的输入框是真的 `Edit` 类子窗口，和自己线程同属一个消息队列，
//! 所以 `SendMessage` 直接执行窗口过程、`PostMessage` 需要 `pump()` 一下才会落地。

#![cfg(test)]

use super::{apply, control, Capability, ReadResult};
use windows::core::w;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetForegroundWindow,
    PeekMessageW, RegisterClassW, MSG, PM_REMOVE, SW_SHOW, ShowWindow, WNDCLASSW,
    WS_CHILD, WS_POPUP, WS_VISIBLE,
};
use windows::Win32::UI::Input::KeyboardAndMouse::SetFocus;

use windows::Win32::Foundation::{LPARAM, WPARAM};

struct Win(HWND);

impl Drop for Win {
    fn drop(&mut self) {
        unsafe {
            let _ = DestroyWindow(self.0);
        }
    }
}

unsafe extern "system" fn wndproc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> windows::Win32::Foundation::LRESULT {
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}

unsafe fn host() -> Win {
    let class = w!("LxSyncTextSyncTestHost");
    let instance: windows::Win32::Foundation::HINSTANCE =
        unsafe { GetModuleHandleW(None) }.unwrap().into();
    let wc = WNDCLASSW {
        lpfnWndProc: Some(wndproc),
        hInstance: instance,
        lpszClassName: class,
        ..Default::default()
    };
    let _ = unsafe { RegisterClassW(&wc) };

    let hwnd = unsafe {
        CreateWindowExW(
            Default::default(),
            class,
            w!("text-sync-test"),
            WS_POPUP,
            0,
            0,
            400,
            200,
            None,
            None,
            instance,
            None,
        )
    }
    .unwrap();
    let _ = unsafe { ShowWindow(hwnd, SW_SHOW) };
    Win(hwnd)
}

/// 真的 `Edit` 控件：类名 "Edit" 是系统预定义类，不用注册
unsafe fn edit(parent: HWND) -> Win {
    let instance: windows::Win32::Foundation::HINSTANCE =
        unsafe { GetModuleHandleW(None) }.unwrap().into();
    let hwnd = unsafe {
        CreateWindowExW(
            Default::default(),
            w!("Edit"),
            w!(""),
            WS_CHILD | WS_VISIBLE,
            0,
            0,
            200,
            30,
            Some(&parent),
            None,
            instance,
            None,
        )
    }
    .unwrap();
    Win(hwnd)
}

/// 非文本控件，用来验证「类名不是输入框 → NotText」
unsafe fn button(parent: HWND) -> Win {
    let instance: windows::Win32::Foundation::HINSTANCE =
        unsafe { GetModuleHandleW(None) }.unwrap().into();
    let hwnd = unsafe {
        CreateWindowExW(
            Default::default(),
            w!("Button"),
            w!("ok"),
            WS_CHILD | WS_VISIBLE,
            0,
            60,
            80,
            24,
            Some(&parent),
            None,
            instance,
            None,
        )
    }
    .unwrap();
    Win(hwnd)
}

/// 把本线程消息队列里的消息真正派发到窗口过程。
/// `PostMessage` 只入队，不 pump 的话输入框永远收不到字。
fn pump() {
    unsafe {
        let mut msg = MSG::default();
        while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
            DispatchMessageW(&msg);
        }
    }
}

fn units(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}

/// 空的标准输入框读出来是 `Text{空}`，**不是** `NotText`。
/// 这条是「读不到 ≠ 没输入」的正面证据：真正的空文本不会被误判成读不到。
#[test]
fn empty_edit_reads_as_empty_text_not_unreadable() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        let result = control::read(edit.0);
        match result {
            ReadResult::Text { ref text, sel_start, caret } => {
                assert!(text.is_empty(), "新输入框应该是空的");
                assert_eq!((sel_start, caret), (0, 0));
            }
            other => panic!("空输入框必须读成空文本，实际 {other:?}"),
        }
    }
}

/// 焦点控件身份要能认出这是 Edit 类，并且带上进程号
#[test]
fn focus_identity_reports_class_and_capability() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        let id = control::focus_id(host.0).expect("应该能取到焦点控件");
        assert_eq!(id.capability, Capability::Standard);
        assert_eq!(id.class.to_ascii_lowercase(), "edit");
        assert_eq!(id.pid, std::process::id());
        assert_eq!(id.hwnd, edit.0 .0 as isize);
    }
}

/// 按钮不是文本控件 —— 这里走 `NotText`，而不是「读到空文本」
#[test]
fn button_is_not_a_text_control() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let button = button(host.0);
        let _ = SetFocus(button.0);

        assert_eq!(control::read(button.0), ReadResult::NotText);
    }
}

/// 整段重写 + 光标复位：这是中间插字、全选替换的落地方式
#[test]
fn replace_all_writes_text_and_moves_caret() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        let text = units("乐享同步");
        assert!(apply::replace_all(edit.0, &text, 2), "整段重写应该成功");

        match control::read(edit.0) {
            ReadResult::Text { text: got, caret, .. } => {
                assert_eq!(got, text, "写进去的文本必须原样读得回来");
                assert_eq!(caret, 2, "光标必须落在指定位置，而不是被 WM_SETTEXT 顶到开头");
            }
            other => panic!("写完应该读得回来，实际 {other:?}"),
        }
    }
}

/// 覆盖已有内容：从长文本换成长度更短的文本
#[test]
fn replace_all_shrinks_existing_text() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        assert!(apply::replace_all(edit.0, &units("hello world"), 11));
        assert!(apply::replace_all(edit.0, &units("hi"), 2));

        match control::read(edit.0) {
            ReadResult::Text { text, caret, .. } => {
                assert_eq!(String::from_utf16_lossy(&text), "hi");
                assert_eq!(caret, 2);
            }
            other => panic!("实际 {other:?}"),
        }
    }
}

/// `EM_SETSEL` 单独也要能用：增量补字之前要把光标摆到正确位置
#[test]
fn set_caret_is_honoured() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        assert!(apply::replace_all(edit.0, &units("abcdef"), 6));
        assert!(apply::set_caret(edit.0, 3));

        let caret = control::read(edit.0).caret().expect("应该读得到光标");
        assert_eq!(caret, 3);
    }
}

/// 增量通道：投出去的 `WM_CHAR` 必须真的落进输入框（post 之后要 pump）
#[test]
fn posted_chars_land_in_the_edit() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        let sent = apply::post_chars(edit.0, &units("abc"));
        assert_eq!(sent, 3, "三个字都要投出去");
        pump();

        match control::read(edit.0) {
            ReadResult::Text { text, caret, .. } => {
                assert_eq!(String::from_utf16_lossy(&text), "abc");
                assert_eq!(caret, 3, "逐字输入后光标应在末尾");
            }
            other => panic!("投出去的字没落地，实际 {other:?}"),
        }
    }
}

/// 退格通道：末尾删除走 `WM_CHAR('\b')`
#[test]
fn posted_backspaces_delete_from_the_end() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);

        assert!(apply::replace_all(edit.0, &units("abcde"), 5));
        assert_eq!(apply::post_backspaces(edit.0, 2), 2);
        pump();

        match control::read(edit.0) {
            ReadResult::Text { text, caret, .. } => {
                assert_eq!(String::from_utf16_lossy(&text), "abc");
                assert_eq!(caret, 3);
            }
            other => panic!("退格没生效，实际 {other:?}"),
        }
    }
}

/// 换控件后身份必须不同 —— 调度器靠这个判断「不要拿旧文本当差量」
#[test]
fn switching_focus_changes_identity() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let first = edit(host.0);
        let second = edit(host.0);

        let _ = SetFocus(first.0);
        let a = control::focus_id(host.0).unwrap();

        let _ = SetFocus(second.0);
        let b = control::focus_id(host.0).unwrap();

        assert_ne!(a, b, "两个不同输入框的身份不能相同");
        assert_ne!(a.hwnd, b.hwnd);

        // 而且各自读到的文本互不影响：A 有字、B 空
        assert!(apply::replace_all(first.0, &units("甲方"), 2));
        assert_eq!(control::read(first.0).text().map(|t| t.len()), Some(2));
        assert_eq!(control::read(second.0).text().map(|t| t.len()), Some(0));
    }
}

/// 前缀陷阱：A 里是 "ab"，切到 B（内容 "abc"）时，如果不看身份就会把 "c" 当追加。
/// 这里验证两个控件的文本确实是独立的两份，调度器的身份判断才有依据。
#[test]
fn prefix_relationship_between_controls_keeps_them_separate() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let first = edit(host.0);
        let second = edit(host.0);

        assert!(apply::replace_all(first.0, &units("ab"), 2));
        assert!(apply::replace_all(second.0, &units("abc"), 3));

        let a = control::read(first.0);
        let b = control::read(second.0);
        assert_eq!(String::from_utf16_lossy(a.text().unwrap()), "ab");
        assert_eq!(String::from_utf16_lossy(b.text().unwrap()), "abc");
        assert_ne!(a.text(), b.text(), "两个控件是不同的两份文本");
    }
}

/// 同一进程里创建的真实窗口不会是无响应状态：读取必须立刻返回文本而不是超时
#[test]
fn read_never_reports_unresponsive_for_a_live_control() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    unsafe {
        let host = host();
        let edit = edit(host.0);
        let _ = SetFocus(edit.0);
        assert!(apply::replace_all(edit.0, &units("x"), 1));

        // 连读多次都不能退化成 Unresponsive（那说明哨兵值判断写错了）
        for _ in 0..20 {
            match control::read(edit.0) {
                ReadResult::Text { .. } => {}
                other => panic!("活着的控件不该读成 {other:?}"),
            }
        }
    }
}

/// 前台窗口拿不到不代表功能坏了 —— 但至少要能安全调用，不 panic
#[test]
fn foreground_window_probe_is_safe() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    let _ = unsafe { GetForegroundWindow() };
}

/// 密码框（`ES_PASSWORD`）的 `WM_GETTEXT` 读到的是**明文**，星号只是显示效果。
///
/// 这条不是缺陷，而是「密码框里的内容也会被镜像过去」的原因 —— README 里已注明。
/// 写在这里是为了让后面的人不必再猜：真要跳过密码框，得靠样式位判断，不是靠读取失败。
#[test]
fn password_style_edit_exposes_plaintext() {
    // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
    let _guard = crate::test_support::serial();
    const ES_PASSWORD: windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE =
        windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE(0x0020);

    unsafe {
        let host = host();
        let instance: windows::Win32::Foundation::HINSTANCE =
            GetModuleHandleW(None).unwrap().into();
        let ed = CreateWindowExW(
            Default::default(),
            w!("Edit"),
            w!("secret123"),
            WS_CHILD | WS_VISIBLE | ES_PASSWORD,
            0,
            0,
            200,
            30,
            Some(&host.0),
            None,
            instance,
            None,
        )
        .unwrap();
        let _guard = Win(ed);

        let text = control::read(ed)
            .text()
            .map(|t| String::from_utf16_lossy(t));
        assert_eq!(text.as_deref(), Some("secret123"), "密码框读到的是明文，不是星号");
    }
}
