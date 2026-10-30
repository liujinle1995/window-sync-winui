//! 端到端：真正走一遍「主窗口改动 → 从窗口跟上」。
//!
//! 上面 `win_tests` 里那些是零件测试（能不能读、能不能写、光标对不对）。
//! 这里把 `observe()` 当整条管线驱动：基线 → settle → 差分 → 分发 → 提交，
//! 中间每个从窗口都是**另一条线程上**的真实输入框。
//!
//! 为什么从窗口必须另开线程：`GetGUIThreadInfo` 取的是「该窗口所属线程」的焦点，
//! 同一个线程上的两个窗口只有一个焦点，放在一起测会互相串。

#![cfg(test)]

use super::{apply, control, ReadResult};
use once_cell::sync::Lazy;
use parking_lot::{Mutex, MutexGuard};
use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};
use std::sync::mpsc;
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};
use windows::core::w;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::KeyboardAndMouse::SetFocus;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, PeekMessageW, RegisterClassW,
    ShowWindow, MSG, PM_REMOVE, SW_SHOW, WNDCLASSW, WS_CHILD, WS_POPUP, WS_VISIBLE,
};

// ── 测试脚手架 ───────────────────────────────────────────────────────────

/// 这些用例改的是模块级基线状态（`MASTER` / `SLAVES`），cargo 默认并行跑测试，
/// 不串行化就会互相清基线。和 `lib.rs` 里那批真实窗口测试是同一类问题。
static TEST_LOCK: Lazy<Mutex<()>> = Lazy::new(|| Mutex::new(()));

fn serial() -> MutexGuard<'static, ()> {
    TEST_LOCK.lock()
}

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
    let class = w!("LxSyncPipelineTestHost");
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
            w!("pipeline-test"),
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

fn units(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}

/// 从窗口：自己一条线程、自己的消息循环、自己的焦点输入框
struct Slave {
    host: HWND,
    edit: HWND,
    stop: Arc<AtomicBool>,
    join: Option<thread::JoinHandle<()>>,
}

impl Slave {
    fn spawn() -> Self {
        let (tx, rx) = mpsc::channel();
        let stop = Arc::new(AtomicBool::new(false));
        let stop_in_thread = stop.clone();

        let join = thread::spawn(move || unsafe {
            let host = host();
            let edit = edit(host.0);
            let _ = SetFocus(edit.0);
            // 句柄本身不是 Send 的，传裸地址回去再由主线程还原成 HWND
            tx.send((host.0 .0 as isize, edit.0 .0 as isize))
                .expect("回传句柄失败");

            // 从窗口必须持续泵消息，否则主线程的 SendMessage 会一直等
            let mut msg = MSG::default();
            while !stop_in_thread.load(AtomicOrdering::Relaxed) {
                while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                    DispatchMessageW(&msg);
                }
                thread::sleep(Duration::from_millis(1));
            }
            drop(edit);
            drop(host);
        });

        let (host, edit) = rx.recv().expect("从窗口线程没起来");
        Self {
            host: HWND(host as *mut _),
            edit: HWND(edit as *mut _),
            stop,
            join: Some(join),
        }
    }

    fn text(&self) -> Option<String> {
        control::read(self.edit)
            .text()
            .map(|t| String::from_utf16_lossy(t))
    }
}

impl Drop for Slave {
    fn drop(&mut self) {
        self.stop.store(true, AtomicOrdering::Relaxed);
        if let Some(handle) = self.join.take() {
            let _ = handle.join();
        }
    }
}

/// 等从窗口把投过去的字处理完；超时返回 false
fn wait_for(mut check: impl FnMut() -> bool, timeout_ms: u64) -> bool {
    let deadline = Instant::now() + Duration::from_millis(timeout_ms);
    while Instant::now() < deadline {
        if check() {
            return true;
        }
        thread::sleep(Duration::from_millis(5));
    }
    check()
}

/// 主窗口现在的文本与光标，喂给 `observe` 用
fn read_master(edit: HWND) -> (Vec<u16>, usize) {
    match control::read(edit) {
        ReadResult::Text { text, caret, .. } => (text, caret),
        other => panic!("主窗口输入框读不到，实际 {other:?}"),
    }
}

/// 把主窗口输入框改成指定内容，再驱动一拍
fn set_master_and_observe(
    master: HWND,
    content: &str,
    caret: usize,
    slaves: &[HWND],
) -> bool {
    assert!(
        apply::replace_all(master, &units(content), caret),
        "写主窗口输入框失败"
    );
    let (text, caret) = read_master(master);
    let id = control::identify(master).expect("主窗口输入框身份取不到");
    super::observe(id, master, text, caret, slaves)
}

// ── 用例 ─────────────────────────────────────────────────────────────────

#[test]
fn pipeline_carries_tail_insert_to_the_slave() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let master = edit(host.0);
        let _ = SetFocus(master.0);
        super::reset_state();
        let slaves = [slave.host];

        // 第一次观察只建基线，不推任何东西
        assert!(!set_master_and_observe(master.0, "", 0, &slaves), "首次只建基线");
        assert_eq!(slave.text().as_deref(), Some(""));

        // 末尾追加 —— 走增量通道（WM_CHAR）
        assert!(set_master_and_observe(master.0, "abc", 3, &slaves), "应该观察到改动");
        assert!(
            wait_for(|| slave.text().as_deref() == Some("abc"), 800),
            "从窗口没跟上：{:?}",
            slave.text()
        );
    }
}

/// 中间插字 —— 老实现的 `plan_edit` 在这里直接放弃同步
#[test]
fn pipeline_handles_middle_insert() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let master = edit(host.0);
        let _ = SetFocus(master.0);
        super::reset_state();
        let slaves = [slave.host];

        assert!(!set_master_and_observe(master.0, "ac", 2, &slaves));

        // "ac" → "abc"：在中间插一个 b，只能整段重写
        assert!(set_master_and_observe(master.0, "abc", 2, &slaves));
        assert!(
            wait_for(|| slave.text().as_deref() == Some("abc"), 800),
            "中间插字没同步过去：{:?}",
            slave.text()
        );
    }
}

/// 全选替换 —— 前后缀毫无共享
#[test]
fn pipeline_handles_select_all_replace() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let master = edit(host.0);
        let _ = SetFocus(master.0);
        super::reset_state();
        let slaves = [slave.host];

        assert!(!set_master_and_observe(master.0, "hello", 5, &slaves));
        assert!(set_master_and_observe(master.0, "乐享", 2, &slaves));
        assert!(
            wait_for(|| slave.text().as_deref() == Some("乐享"), 800),
            "全选替换没同步过去：{:?}",
            slave.text()
        );
    }
}

/// 末尾删除 —— 走退格通道
#[test]
fn pipeline_handles_tail_delete() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let master = edit(host.0);
        let _ = SetFocus(master.0);
        super::reset_state();
        let slaves = [slave.host];

        assert!(!set_master_and_observe(master.0, "abcde", 5, &slaves));
        assert!(set_master_and_observe(master.0, "abc", 3, &slaves));
        assert!(
            wait_for(|| slave.text().as_deref() == Some("abc"), 800),
            "末尾删除没同步过去：{:?}",
            slave.text()
        );
    }
}

/// 键盘钩子已经把字送到从窗口了，文本同步不能再补一遍。
/// 这里手动把从窗口改成目标文本（模拟键盘通道已经生效），再驱动一次 observe，
/// 结果必须是一个字都没多。
#[test]
fn pipeline_keeps_slave_text_exact_when_keyboard_already_delivered() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let master = edit(host.0);
        let _ = SetFocus(master.0);
        super::reset_state();
        let slaves = [slave.host];

        assert!(!set_master_and_observe(master.0, "ab", 2, &slaves));

        // 主窗口多打了一个 c
        assert!(apply::replace_all(master.0, &units("abc"), 3));
        // 同时从窗口已经被键盘通道送到了 "abc"
        assert!(apply::replace_all(slave.edit, &units("abc"), 3));

        let (text, caret) = read_master(master.0);
        let id = control::identify(master.0).unwrap();
        assert!(super::observe(id, master.0, text, caret, &slaves));

        // 多等一会儿，确保不是「刚好还没轮到」
        thread::sleep(Duration::from_millis(150));
        assert_eq!(
            slave.text().as_deref(),
            Some("abc"),
            "重复插入了：键盘通道已经送过的字不能再补一遍"
        );
    }
}

/// 换控件只重设基线：A 里是 "ab"，切到 B（"abc"，正好以旧的为前缀）时，
/// 不能把 "c" 当成追加推过去 —— 这是老实现最典型的误判。
#[test]
fn pipeline_switching_control_does_not_push_a_diff() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let first = edit(host.0);
        let second = edit(host.0);
        super::reset_state();
        let slaves = [slave.host];

        let _ = SetFocus(first.0);
        assert!(!set_master_and_observe(first.0, "ab", 2, &slaves));
        assert_eq!(slave.text().as_deref(), Some(""), "只建基线，不该推内容");

        // 切到另一个输入框，内容 "abc"
        let _ = SetFocus(second.0);
        let id_a = control::identify(first.0).unwrap();
        let id_b = control::identify(second.0).unwrap();
        assert_ne!(id_a.hwnd, id_b.hwnd, "必须是两个不同控件");

        assert!(
            !set_master_and_observe(second.0, "abc", 3, &slaves),
            "换控件的这一拍不能推差量"
        );
        thread::sleep(Duration::from_millis(150));
        assert_ne!(
            slave.text().as_deref(),
            Some("abc"),
            "换控件被误判成了追加"
        );
    }
}

/// 从窗口落后一截时必须能追上来（后启动的从窗口就是这种状态）
#[test]
fn pipeline_catches_up_a_slave_that_fell_behind() {
    let _guard = serial();
    let slave = Slave::spawn();
    unsafe {
        let host = host();
        let master = edit(host.0);
        let _ = SetFocus(master.0);
        super::reset_state();
        let slaves = [slave.host];

        // 第一次观察按设计只建基线
        assert!(!set_master_and_observe(master.0, "", 0, &slaves));
        assert!(set_master_and_observe(master.0, "abcd", 4, &slaves));
        assert!(
            wait_for(|| slave.text().as_deref() == Some("abcd"), 800),
            "首轮没同步过去：{:?}",
            slave.text()
        );

        // 手动把从窗口退回 "ab"，模拟漏掉一次
        assert!(apply::replace_all(slave.edit, &units("ab"), 2));

        // 主窗口再改一次（末尾追加），从窗口必须直接追到最新的完整文本
        assert!(set_master_and_observe(master.0, "abcdef", 6, &slaves));
        assert!(
            wait_for(|| slave.text().as_deref() == Some("abcdef"), 800),
            "落后的从窗口没追上：{:?}",
            slave.text()
        );
    }
}
