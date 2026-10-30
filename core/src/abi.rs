//! ABI 契约：版本号、错误码、二进制数据结构。
//!
//! # 为什么要把这些单独拎出来
//!
//! 控制面用 JSON 挺好（可读、好调试、加字段不破坏兼容），但数据面用 JSON 就是在替
//! 序列化库打工：窗口列表几百条、状态每秒推几次、文本动辄几千字，每条都要分配字符串、
//! 拼 JSON、宿主再解析一遍。这里定的都是**定长 POD 结构**和**一段连续字节**，
//! 两边按同一份内存布局读，零分配、零解析。
//!
//! # 兼容性约定
//!
//! * [`ABI_VERSION`] 只在**内存布局变化**时递增（结构体加减字段、改类型）。
//!   JSON 加字段**不用**动它 —— 所以大部分演进根本不需要升版本。
//! * 宿主启动时应当校验 `wsync_abi_version()`，不一致就提示「前端与核心版本不匹配」，
//!   而不是等读到错位的内存再崩。
//! * 所有 `#[repr(C)]` 结构在 C# 侧一律用 `LayoutKind.Sequential` + 同名同宽度字段。
//!   二进制块里会带上 `record_size`，宿主拿自己的 `Marshal.SizeOf` 对一遍，
//!   对不上就报错 —— 宁可显式失败，也不要按错位的偏移去读。

use serde::{Deserialize, Serialize};

/// 二进制布局版本。改动任何 `#[repr(C)]` 结构都要 +1。
pub const ABI_VERSION: u32 = 2;

/// 窗口列表二进制块的魔数 `WSW1`
pub const WINDOWS_MAGIC: u32 = 0x3157_5357;
/// 窗口列表头部固定长度：magic / abi / count / record_size / string_bytes
pub const WINDOWS_HEADER_LEN: usize = 20;

/// 统一错误码。
///
/// 以前只有一句 `error` 文本，宿主只能靠字符串匹配来区分「参数不对」和「引擎没在跑」，
/// 中文文案一改就断。现在每个失败都带一个稳定的码，文案随便改。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[repr(i32)]
pub enum Code {
    Ok = 0,
    /// 参数不合法：空指针、越界、互斥项冲突、数量超限
    InvalidArg = 1,
    /// JSON 解析失败 / 字段类型不对
    InvalidJson = 2,
    /// 同步没在运行，但该操作要求运行中
    NotRunning = 3,
    /// 没有指定主窗口
    NoMaster = 4,
    /// 目标窗口已不存在（销毁 / 句柄被复用）
    WindowGone = 5,
    /// 并发冲突（比如正在停止时又要启动）
    Busy = 6,
    /// 平台不支持 / 该控件不支持该操作
    Unsupported = 7,
    /// 调用方缓冲区不够，`out_len` 里会给出所需大小
    BufferTooSmall = 8,
    /// 引擎内部错误（panic 被兜住也归到这里）
    Internal = 9,
}

impl Code {
    pub fn as_str(self) -> &'static str {
        match self {
            Code::Ok => "OK",
            Code::InvalidArg => "INVALID_ARG",
            Code::InvalidJson => "INVALID_JSON",
            Code::NotRunning => "NOT_RUNNING",
            Code::NoMaster => "NO_MASTER",
            Code::WindowGone => "WINDOW_GONE",
            Code::Busy => "BUSY",
            Code::Unsupported => "UNSUPPORTED",
            Code::BufferTooSmall => "BUFFER_TOO_SMALL",
            Code::Internal => "INTERNAL",
        }
    }

    pub fn as_i32(self) -> i32 {
        self as i32
    }

    /// 把引擎里那些 `Result<_, String>` 的老错误归到一个码上。
    ///
    /// 关键词匹配不好看，但那些错误文本本来就是给人看的，短期内不打算逐条改写成枚举；
    /// 至少先让宿主能分出「没在跑」和「参数不对」这两类最常见的。
    pub fn classify(message: &str) -> Code {
        let m = message;
        if m.contains("未启动") || m.contains("没有在运行") || m.contains("未在运行") {
            Code::NotRunning
        } else if m.contains("主窗口") && (m.contains("未选") || m.contains("没有")) {
            Code::NoMaster
        } else if m.contains("不存在") || m.contains("已失效") || m.contains("已关闭") {
            Code::WindowGone
        } else if m.contains("参数") || m.contains("不能为空") || m.contains("至少") {
            Code::InvalidArg
        } else if m.contains("正在") {
            Code::Busy
        } else {
            Code::Internal
        }
    }
}

/// 状态快照（POD，按值返回，无分配、无序列化）。
///
/// 宿主拿到它只做两件事：比对 [`StatusSnapshot::revision`] 判断要不要刷新 UI，
/// 以及读 `flags` / `slave_count`。真正的状态变化仍然走通知回调 ——
/// 回调只带 revision（一个 u64），不带载荷，所以「批量通知」天然成立：
/// 连续变十次，宿主也只需要拉一次快照。
#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct StatusSnapshot {
    /// 布局版本，宿主用来校验
    pub abi: u32,
    /// 位标志，见下方常量
    pub flags: u32,
    pub slave_count: u32,
    /// 保留，恒为 0（保证 8 字节对齐的填充是显式的，而不是靠编译器猜）
    pub reserved: u32,
    pub master_hwnd: i64,
    /// 单调递增的版本号。任何状态字段变化都会 +1。
    pub revision: u64,
}

pub mod status_flag {
    pub const ACTIVE: u32 = 1 << 0;
    pub const MOUSE: u32 = 1 << 1;
    pub const KEYBOARD: u32 = 1 << 2;
    pub const CLIPBOARD: u32 = 1 << 3;
    pub const TEXT_MIRROR: u32 = 1 << 4;
    pub const VISIBLE: u32 = 1 << 5;
}

/// 一条窗口记录（POD）。字符串不进结构体，放在块尾的 UTF-8 字符串区，
/// 用 `(offset, len)` 引用 —— 定长记录才能被宿主用 `MemoryMarshal` 直接切。
#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct WindowRecordV1 {
    pub hwnd: i64,
    /// 进程启动时间（FILETIME）。防 PID 复用。
    pub process_start_time: u64,
    pub parent_hwnd: i64,
    pub pid: u32,
    pub dpi: u32,
    pub client_width: i32,
    pub client_height: i32,
    /// 1 可见 / 0 不可见
    pub visible: u32,
    pub exe_off: u32,
    pub exe_len: u32,
    pub class_off: u32,
    pub class_len: u32,
    pub title_off: u32,
    pub title_len: u32,
}

/// 二进制窗口列表的编码结果
pub struct WindowsBlob {
    pub bytes: Vec<u8>,
    pub count: u32,
}

/// 把窗口列表编成定长记录 + 字符串区。
///
/// 布局：`[header 20B][record * count][utf8 字符串区]`
pub fn encode_windows(windows: &[crate::WindowInfo]) -> WindowsBlob {
    let mut strings: Vec<u8> = Vec::new();
    let mut records: Vec<WindowRecordV1> = Vec::with_capacity(windows.len());

    for w in windows {
        let mut push = |s: &str| -> (u32, u32) {
            let off = strings.len() as u32;
            strings.extend_from_slice(s.as_bytes());
            (off, s.len() as u32)
        };
        let (exe_off, exe_len) = push(&w.exe_path);
        let (class_off, class_len) = push(&w.class_name);
        let (title_off, title_len) = push(&w.title);
        records.push(WindowRecordV1 {
            hwnd: w.hwnd,
            process_start_time: w.process_start_time,
            parent_hwnd: w.parent_hwnd,
            pid: w.pid,
            dpi: w.dpi,
            client_width: w.client_width,
            client_height: w.client_height,
            visible: if w.visible { 1 } else { 0 },
            exe_off,
            exe_len,
            class_off,
            class_len,
            title_off,
            title_len,
        });
    }

    let record_size = std::mem::size_of::<WindowRecordV1>() as u32;
    let mut bytes = Vec::with_capacity(WINDOWS_HEADER_LEN + records.len() * record_size as usize + strings.len());
    bytes.extend_from_slice(&WINDOWS_MAGIC.to_le_bytes());
    bytes.extend_from_slice(&ABI_VERSION.to_le_bytes());
    bytes.extend_from_slice(&(records.len() as u32).to_le_bytes());
    bytes.extend_from_slice(&record_size.to_le_bytes());
    bytes.extend_from_slice(&(strings.len() as u32).to_le_bytes());
    for r in &records {
        // 结构体是 POD，直接按字节拷；宿主的 Sequential 布局与之一致
        let p = r as *const WindowRecordV1 as *const u8;
        bytes.extend_from_slice(unsafe { std::slice::from_raw_parts(p, record_size as usize) });
    }
    bytes.extend_from_slice(&strings);

    WindowsBlob { bytes, count: records.len() as u32 }
}

/// 从二进制块里读一条记录的字符串字段（给 Rust 侧测试与自检用）
pub fn record_str<'a>(blob: &'a [u8], _rec: &WindowRecordV1, off: u32, len: u32) -> &'a str {
    let start = WINDOWS_HEADER_LEN + blob_record_area_len(blob) + off as usize;
    let end = start + len as usize;
    if end > blob.len() {
        return "";
    }
    std::str::from_utf8(&blob[start..end]).unwrap_or("")
}

fn blob_record_area_len(blob: &[u8]) -> usize {
    if blob.len() < WINDOWS_HEADER_LEN {
        return 0;
    }
    let count = u32::from_le_bytes(blob[8..12].try_into().unwrap()) as usize;
    let record_size = u32::from_le_bytes(blob[12..16].try_into().unwrap()) as usize;
    count * record_size
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::WindowInfo;

    fn sample() -> WindowInfo {
        WindowInfo {
            hwnd: 0x1234,
            pid: 42,
            process_start_time: 987_654_321,
            exe_path: r"C:\Games\乐享\client.exe".into(),
            class_name: "UnityWndClass".into(),
            title: "乐享 · 客户端".into(),
            parent_hwnd: 0,
            client_width: 1280,
            client_height: 720,
            dpi: 144,
            visible: true,
        }
    }

    #[test]
    fn header_reports_layout_for_the_host_to_check() {
        let blob = encode_windows(&[sample()]);
        assert_eq!(&blob.bytes[0..4], &WINDOWS_MAGIC.to_le_bytes());
        assert_eq!(u32::from_le_bytes(blob.bytes[4..8].try_into().unwrap()), ABI_VERSION);
        assert_eq!(u32::from_le_bytes(blob.bytes[8..12].try_into().unwrap()), 1);
        assert_eq!(
            u32::from_le_bytes(blob.bytes[12..16].try_into().unwrap()) as usize,
            std::mem::size_of::<WindowRecordV1>(),
            "record_size 必须是宿主能校验的真实大小"
        );
    }

    #[test]
    fn records_are_fixed_size_and_strings_survive() {
        let blob = encode_windows(&[sample(), sample()]);
        assert_eq!(blob.count, 2);
        let rs = std::mem::size_of::<WindowRecordV1>();
        assert_eq!(blob.bytes.len(), WINDOWS_HEADER_LEN + 2 * rs + blob_strings_len(&blob.bytes));

        let rec: WindowRecordV1 = unsafe {
            std::ptr::read_unaligned(blob.bytes[WINDOWS_HEADER_LEN..].as_ptr() as *const _)
        };
        assert_eq!(rec.hwnd, 0x1234);
        assert_eq!(rec.process_start_time, 987_654_321);
        assert_eq!(rec.dpi, 144);
        assert_eq!(rec.visible, 1);
        assert_eq!(record_str(&blob.bytes, &rec, rec.exe_off, rec.exe_len), r"C:\Games\乐享\client.exe");
        assert_eq!(record_str(&blob.bytes, &rec, rec.class_off, rec.class_len), "UnityWndClass");
        assert_eq!(record_str(&blob.bytes, &rec, rec.title_off, rec.title_len), "乐享 · 客户端");
    }

    fn blob_strings_len(b: &[u8]) -> usize {
        u32::from_le_bytes(b[16..20].try_into().unwrap()) as usize
    }

    #[test]
    fn empty_list_is_still_a_valid_blob() {
        let blob = encode_windows(&[]);
        assert_eq!(blob.count, 0);
        assert_eq!(blob.bytes.len(), WINDOWS_HEADER_LEN);
    }

    #[test]
    fn ascii_and_chinese_take_utf8_bytes() {
        let blob = encode_windows(&[sample()]);
        let strings = &blob.bytes[WINDOWS_HEADER_LEN + std::mem::size_of::<WindowRecordV1>()..];
        // 中文在 UTF-8 里是 3 字节，"乐享" 占 6 字节 —— 长度字段必须是字节数而不是字符数
        assert!(strings.len() > sample().title.len() - 1);
        assert_eq!(std::str::from_utf8(strings).is_ok(), true);
    }

    #[test]
    fn status_snapshot_layout_is_32_bytes() {
        assert_eq!(std::mem::size_of::<StatusSnapshot>(), 32);
        assert_eq!(std::mem::size_of::<WindowRecordV1>(), 72);
    }

    #[test]
    fn error_codes_are_stable_strings() {
        assert_eq!(Code::InvalidArg.as_str(), "INVALID_ARG");
        assert_eq!(Code::InvalidArg.as_i32(), 1);
        assert_eq!(Code::classify("同步未启动"), Code::NotRunning);
        assert_eq!(Code::classify("从窗口已不存在"), Code::WindowGone);
        assert_eq!(Code::classify("参数不能为空"), Code::InvalidArg);
    }
}
