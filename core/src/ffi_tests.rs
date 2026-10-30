//! FFI 边界的测试：错误码分类、参数校验、二进制块形状、回调生命周期。
//!
//! 业务逻辑的测试在各自模块里，这里只关心「跨边界那一层做对了没有」。

#![cfg(test)]

use std::ffi::{CStr, CString};
use std::os::raw::c_char;

use serde_json::Value;

use crate::abi::{self, Code};
use crate::ffi::*;
use crate::test_support as ts;

fn decode(p: *mut c_char) -> Value {
    assert!(!p.is_null(), "返回值不该是空指针");
    let s = unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned();
    unsafe { wsync_free(p) };
    serde_json::from_str(&s).expect("返回值必须是合法 JSON")
}

fn code_of(p: *mut c_char) -> String {
    decode(p)["code"].as_str().unwrap_or("").to_string()
}

fn c_arg(s: &str) -> CString {
    CString::new(s).unwrap()
}

#[test]
fn abi_and_core_versions_are_reported() {
    assert!(wsync_abi_version() >= 1);
    let p = wsync_core_version();
    assert!(!p.is_null());
    let v = unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned();
    assert_eq!(v, env!("CARGO_PKG_VERSION"));
    // 静态串：不该被释放，这里只验证它是合法 UTF-8 且没有内嵌 NUL
    assert!(!v.contains('\0'));
}

/// 测试构建是 unwind，所以必须是 0；谁把 profile 改回 abort，这条会红
#[test]
fn panic_mode_matches_the_build_profile() {
    let mode = wsync_panic_mode();
    assert_eq!(mode == 1, cfg!(panic = "abort"));
    assert_eq!(mode, 0, "发布配置必须是 unwind，否则 catch_unwind 是摆设");
}

#[test]
fn null_argument_is_invalid_arg() {
    let p = unsafe { wsync_start_sync(std::ptr::null()) };
    assert_eq!(code_of(p), "INVALID_ARG");
}

#[test]
fn broken_json_is_invalid_json_not_invalid_arg() {
    let arg = c_arg("{this is not json");
    let p = unsafe { wsync_start_sync(arg.as_ptr()) };
    assert_eq!(code_of(p), "INVALID_JSON");
}

#[test]
fn config_validation_points_at_the_offending_field() {
    let case = |json: &str| {
        let arg = c_arg(json);
        let v = decode(unsafe { wsync_start_sync(arg.as_ptr()) });
        (
            v["code"].as_str().unwrap_or("").to_string(),
            v["error"].as_str().unwrap_or("").to_string(),
        )
    };

    let (code, msg) = case(r#"{"master_hwnd":0,"slave_hwnds":[1],"sync_mouse":true}"#);
    assert_eq!(code, "NO_MASTER");
    assert!(msg.contains("master_hwnd"));

    let (code, msg) = case(r#"{"master_hwnd":1,"slave_hwnds":[],"sync_mouse":true}"#);
    assert_eq!(code, "INVALID_ARG");
    assert!(msg.contains("从窗口"));

    let (code, msg) = case(r#"{"master_hwnd":1,"slave_hwnds":[0],"sync_mouse":true}"#);
    assert_eq!(code, "INVALID_ARG");
    assert!(msg.contains("0 句柄"));

    let (code, _) = case(r#"{"master_hwnd":1,"slave_hwnds":[1],"sync_mouse":true}"#);
    assert_eq!(code, "INVALID_ARG", "主窗口不能同时当从窗口");

    let (code, msg) = case(
        r#"{"master_hwnd":1,"slave_hwnds":[2],"sync_mouse":false,"sync_keyboard":false,"sync_clipboard":false}"#,
    );
    assert_eq!(code, "INVALID_ARG");
    assert!(msg.contains("至少"));
}

#[test]
fn focus_window_rejects_zero_handle() {
    assert_eq!(code_of(wsync_focus_window(0)), "INVALID_ARG");
}

#[test]
fn kill_process_rejects_zero_pid() {
    let arg = c_arg(r#"{"pid":0}"#);
    let p = unsafe { wsync_kill_process(arg.as_ptr()) };
    assert_eq!(code_of(p), "INVALID_ARG");
}

// ── 二进制窗口列表 ────────────────────────────────────────────────────────

#[test]
fn windows_blob_is_parseable_and_freed_correctly() {
    let _guard = ts::serial();
    let mut len: u32 = 0;
    let ptr = unsafe { wsync_get_windows_bin(&mut len) };
    assert!(!ptr.is_null(), "窗口列表不该返回空指针");
    assert!(len as usize >= abi::WINDOWS_HEADER_LEN);
    let bytes = unsafe { std::slice::from_raw_parts(ptr, len as usize) };

    let magic = u32::from_le_bytes(bytes[0..4].try_into().unwrap());
    assert_eq!(magic, abi::WINDOWS_MAGIC);
    assert_eq!(u32::from_le_bytes(bytes[4..8].try_into().unwrap()), abi::ABI_VERSION);

    let record_size = u32::from_le_bytes(bytes[12..16].try_into().unwrap()) as usize;
    assert_eq!(record_size, std::mem::size_of::<abi::WindowRecordV1>());

    let count = u32::from_le_bytes(bytes[8..12].try_into().unwrap()) as usize;
    let strings = u32::from_le_bytes(bytes[16..20].try_into().unwrap()) as usize;
    assert_eq!(bytes.len(), abi::WINDOWS_HEADER_LEN + count * record_size + strings);

    unsafe { wsync_free_bytes(ptr, len) };
}

#[test]
fn windows_blob_reports_null_out_len() {
    let rc = unsafe { wsync_get_windows_bin(std::ptr::null_mut()) };
    assert!(rc.is_null(), "out_len 为空时不该返回缓冲区");
    assert_eq!(decode(wsync_last_error())["code"], "INVALID_ARG");
}

// ── 文本缓冲区 ────────────────────────────────────────────────────────────

#[test]
fn text_round_trips_through_the_utf8_buffer() {
    let _guard = ts::serial();
    let host = ts::popup_window();
    let edit = ts::edit_child(host.0);
    let hwnd = ts::hwnd_value(edit);

    let text = "乐享同步 abc";
    let rc = unsafe { wsync_text_write(hwnd, text.as_ptr(), text.len() as u32) };
    assert_eq!(rc, 0, "写入应成功：{:?}", decode(wsync_last_error()));

    // 先问需要多大：cap = 0 时返回 BUFFER_TOO_SMALL，并给出所需字节数
    let mut need: u32 = 0;
    let rc = unsafe { wsync_text_read(hwnd, std::ptr::null_mut(), 0, &mut need) };
    assert_eq!(rc, Code::BufferTooSmall.as_i32());
    assert_eq!(need as usize, text.len(), "所需长度应是 UTF-8 字节数");

    let mut buf = vec![0u8; need as usize];
    let mut got: u32 = 0;
    let rc = unsafe { wsync_text_read(hwnd, buf.as_mut_ptr(), need, &mut got) };
    assert_eq!(rc, 0, "读取应成功：{:?}", decode(wsync_last_error()));
    assert_eq!(String::from_utf8(buf).unwrap(), text);
    assert_eq!(got, need);
}

#[test]
fn text_read_on_a_non_text_window_is_unsupported() {
    let _guard = ts::serial();
    let host = ts::popup_window();
    let hwnd = ts::hwnd_value(host.0);
    let mut buf = [0u8; 64];
    let mut got: u32 = 0;
    let rc = unsafe { wsync_text_read(hwnd, buf.as_mut_ptr(), 64, &mut got) };
    assert_eq!(rc, Code::Unsupported.as_i32(), "对非文本窗口应报不支持");
    assert_eq!(decode(wsync_last_error())["code"], "UNSUPPORTED");
}

#[test]
fn text_write_to_a_bogus_handle_is_window_gone() {
    let _guard = ts::serial();
    let host = ts::popup_window();
    let bogus = ts::hwnd_value(host.0) + 0x1000;
    let text = "x";
    let rc = unsafe { wsync_text_write(bogus, text.as_ptr(), text.len() as u32) };
    assert_eq!(rc, Code::WindowGone.as_i32());
}

// ── 回调生命周期 ──────────────────────────────────────────────────────────

extern "C" fn noop_status(_json: *const c_char) {}
extern "C" fn noop_notify(_rev: u64) {}
extern "C" fn noop_log(_msg: *const c_char) {}

#[test]
fn callbacks_are_registered_and_unregistered_by_token() {
    let _guard = ts::serial();

    let t1 = wsync_set_status_callback(Some(noop_status));
    assert_ne!(t1, 0, "注册应返回非零 token");
    // 错的 token 注销不了，也不会误清掉刚注册的那个
    assert!(!wsync_clear_status_callback(t1 + 9999));
    assert!(wsync_clear_status_callback(t1));
    // 重复注销返回 false
    assert!(!wsync_clear_status_callback(t1));

    let t2 = wsync_set_status_notifier(Some(noop_notify));
    assert_ne!(t2, 0);
    assert!(wsync_clear_status_notifier(t2));

    let t3 = wsync_set_log_callback(Some(noop_log));
    assert_ne!(t3, 0);
    assert!(wsync_clear_log_callback(t3));

    // 传 None 等于注销，返回 0
    assert_eq!(wsync_set_status_callback(None), 0);
}

/// 重新注册后旧 token 注销不掉新回调 —— 这正是加 token 的意义
#[test]
fn a_stale_token_cannot_kill_the_new_callback() {
    let _guard = ts::serial();
    let old = wsync_set_status_callback(Some(noop_status));
    let new = wsync_set_status_callback(Some(noop_status));
    assert_ne!(old, new);
    assert!(!wsync_clear_status_callback(old), "旧 token 不该生效");
    assert!(wsync_clear_status_callback(new));
}

/// 状态快照是 POD：版本号、位标志、从窗口数量都要能读出来
#[test]
fn status_snapshot_reflects_flags_without_json() {
    let _guard = ts::serial();
    let before = crate::status_revision();

    let master = ts::popup_window();
    let slave = ts::popup_window();
    ts::begin_fake_sync(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    );

    let snap = wsync_status_snapshot();
    assert_eq!(snap.abi, abi::ABI_VERSION);
    assert_ne!(snap.flags & abi::status_flag::ACTIVE, 0, "fake sync 应为 active");
    assert_eq!(snap.slave_count, 1);
    assert_ne!(snap.master_hwnd, 0);
    assert!(snap.revision >= before);

    ts::end_fake_sync();
}
