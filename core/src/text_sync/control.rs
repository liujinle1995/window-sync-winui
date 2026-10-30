//! 焦点控件的身份与能力识别。
//!
//! 老实现只读一个字符串快照，没记这个快照属于哪个控件。于是从输入框 A 切到输入框 B，
//! 只要 B 的文本恰好以 A 的文本为前缀，就会被当成「在 A 里追加了一段」——
//! 轻则多打一串字，重则把 B 的原有内容顶掉。
//!
//! 这里给每次快照绑定 [`ControlId`]：窗口句柄 + 进程 + 窗口类名。句柄变了就是换了控件，
//! 调度器会**只重设基线、不推差量**。
//!
//! 另一个必须分清的事情是「读不到」和「空」：
//!
//! - `WM_GETTEXTLENGTH` 对空的标准输入框返回 0，这是**合法的空文本**；
//! - 自绘输入框根本不实现这些消息，返回同样是 0；
//! - 目标进程卡死时 `SendMessageTimeout` 直接超时，`lpdwresult` 压根不会被写。
//!
//! 三者混为一谈就会出现「读不到 → 当作没输入 → 静默不同步」。这里用
//! [`ReadResult`] 把它们拆开，并且用哨兵值判断调用到底有没有落地：
//! 调用前把出参填成 `usize::MAX`，调用后还是 `usize::MAX` 就说明没写进去。

use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    GetClassNameW, GetGUIThreadInfo, GetWindowThreadProcessId, IsWindow, SendMessageTimeoutW,
    GUITHREADINFO, SMTO_ABORTIFHUNG,
};

pub const WM_GETTEXT: u32 = 0x000D;
pub const WM_GETTEXTLENGTH: u32 = 0x000E;
pub const EM_GETSEL: u32 = 0x00B0;

/// 读取调用没落地时出参保持的值
const PENDING: usize = usize::MAX;
/// 单次跨进程读取最多等这么久；超时说明对方无响应，不是「没内容」
const READ_TIMEOUT_MS: u32 = 40;
/// 超过这个长度的不当作要镜像的输入框（十六万字节以上的文本框没有镜像意义）
const MAX_TEXT_UNITS: usize = 16 * 1024;

/// 控件是不是「标准 Windows 文本控件」。
///
/// 判定只看窗口类名，**不看能不能读出文本** —— 空的输入框和自绘输入框都会返回 0 长度，
/// 用返回值来判断必然误判。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Capability {
    /// Edit / RichEdit / Scintilla / WinForms、VCL 的编辑框：支持读文本、设光标
    Standard,
    /// 自绘输入框、顶层窗口本身、非文本子控件：读不到也写不进，只能走别的通道
    NonStandard,
}

impl Capability {
    pub fn label(self) -> &'static str {
        match self {
            Capability::Standard => "标准文本控件",
            Capability::NonStandard => "自绘/非文本控件",
        }
    }
}

/// 依据窗口类名判断能力。
///
/// 覆盖到的常见来源：
/// - 原生 `Edit`、`RichEdit*`（含 20A/20W/50W/60W）；
/// - WinForms 的 `WindowsForms10.EDIT.app.0.x`（内层就是原生 EDIT）；
/// - Delphi/VCL 的 `TEdit`、`TMemo` 之类；
/// - Scintilla（Notepad++ 等）。
pub fn classify(class: &str) -> Capability {
    let l = class.to_ascii_lowercase();

    // 精确命中
    if l == "edit" || l == "scintilla" || l == "textbox" {
        return Capability::Standard;
    }
    // RichEdit 系列
    if l.starts_with("richedit") || l.starts_with("rich edit") {
        return Capability::Standard;
    }
    // WinForms / VCL：类名里带 .edit 或 tedit
    if l.contains(".edit") || l.starts_with("tedit") || l.starts_with("tmemo") {
        return Capability::Standard;
    }

    Capability::NonStandard
}

/// 焦点控件的身份。句柄变了一定是换了控件；句柄没变但类名/进程变了说明窗口被重建。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ControlId {
    pub hwnd: isize,
    pub pid: u32,
    pub class: String,
    pub capability: Capability,
}

impl ControlId {
    pub fn describe(&self) -> String {
        format!("{} (hwnd=0x{:X}, pid={}, {})", self.class, self.hwnd, self.pid, self.capability.label())
    }
}

/// 一次读取的结果。**不要**把 `NotText` 当成「没有输入」。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReadResult {
    /// 从标准文本控件读到了内容。空的输入框也是这个分支，`text` 为空。
    Text {
        text: Vec<u16>,
        /// 选区起点
        sel_start: usize,
        /// 选区终点，同时也是插入点
        caret: usize,
    },
    /// 焦点落在非文本控件上（自绘输入框 / 顶层窗口本身 / 没有焦点子窗口）
    NotText,
    /// 控件在，但消息发不进去（超时、进程无响应）
    Unresponsive,
    /// 窗口已经不在了
    Gone,
}

impl ReadResult {
    pub fn text(&self) -> Option<&[u16]> {
        match self {
            ReadResult::Text { text, .. } => Some(text),
            _ => None,
        }
    }
    pub fn caret(&self) -> Option<usize> {
        match self {
            ReadResult::Text { caret, .. } => Some(*caret),
            _ => None,
        }
    }
}

/// 取窗口类名；取不到返回空串
pub fn class_of(hwnd: HWND) -> String {
    if hwnd.0.is_null() {
        return String::new();
    }
    let mut buf = [0u16; 256];
    let n = unsafe { GetClassNameW(hwnd, &mut buf) };
    if n <= 0 {
        return String::new();
    }
    String::from_utf16_lossy(&buf[..n as usize])
}

pub fn identify(hwnd: HWND) -> Option<ControlId> {
    if hwnd.0.is_null() || !unsafe { IsWindow(hwnd).as_bool() } {
        return None;
    }
    let mut pid = 0u32;
    let tid = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    if tid == 0 {
        return None;
    }
    let class = class_of(hwnd);
    Some(ControlId {
        hwnd: hwnd.0 as isize,
        pid,
        capability: classify(&class),
        class,
    })
}

/// 顶层窗口所属线程当前持有焦点的子控件。
///
/// 用 `GetGUIThreadInfo` 而不是 `AttachThreadInput + GetFocus`：前者不需要把本线程的
/// 输入队列和目标线程绑在一起，不会干扰目标窗口，也没有附加/解绑的时序风险。
pub fn focus_child(top: HWND) -> Option<HWND> {
    if top.0.is_null() || !unsafe { IsWindow(top).as_bool() } {
        return None;
    }

    let mut pid = 0u32;
    let tid = unsafe { GetWindowThreadProcessId(top, Some(&mut pid)) };
    if tid == 0 {
        return None;
    }

    let mut info = GUITHREADINFO::default();
    info.cbSize = std::mem::size_of::<GUITHREADINFO>() as u32;
    if unsafe { GetGUIThreadInfo(tid, &mut info) }.is_err() {
        return None;
    }

    let focus = info.hwndFocus;
    // 焦点就是顶层窗口本身，基本可以断定是自绘输入，读出来只会是窗口标题
    if focus.0.is_null() || focus == top {
        None
    } else {
        Some(focus)
    }
}

/// 焦点控件的身份；取不到焦点子控件时返回 `None`
pub fn focus_id(top: HWND) -> Option<ControlId> {
    identify(focus_child(top)?)
}

/// 读控件文本与光标位置。
///
/// 注意哨兵值的用法：`SendMessageTimeoutW` 超时不会写出参，所以调用前填 `PENDING`，
/// 调用后仍是 `PENDING` 就说明这次调用没落地 —— 与「读到了空字符串」严格区分开。
pub fn read(hwnd: HWND) -> ReadResult {
    if hwnd.0.is_null() {
        return ReadResult::NotText;
    }
    unsafe {
        if !IsWindow(hwnd).as_bool() {
            return ReadResult::Gone;
        }

        // 类名先筛一遍：不是文本控件就不必再去问它要文本了
        if classify(&class_of(hwnd)) == Capability::NonStandard {
            return ReadResult::NotText;
        }

        let mut length: usize = PENDING;
        let _ = SendMessageTimeoutW(
            hwnd,
            WM_GETTEXTLENGTH,
            WPARAM(0),
            LPARAM(0),
            SMTO_ABORTIFHUNG,
            READ_TIMEOUT_MS,
            Some(&mut length),
        );
        if length == PENDING {
            return ReadResult::Unresponsive;
        }
        if length > MAX_TEXT_UNITS {
            return ReadResult::NotText;
        }

        let text = if length == 0 {
            Vec::new()
        } else {
            let mut buffer = vec![0u16; length + 1];
            let mut written: usize = PENDING;
            let _ = SendMessageTimeoutW(
                hwnd,
                WM_GETTEXT,
                WPARAM(buffer.len()),
                LPARAM(buffer.as_mut_ptr() as isize),
                SMTO_ABORTIFHUNG,
                READ_TIMEOUT_MS,
                Some(&mut written),
            );
            if written == PENDING {
                return ReadResult::Unresponsive;
            }
            buffer.truncate(written.min(length));
            buffer
        };

        let (sel_start, caret) = read_selection(hwnd, text.len());
        ReadResult::Text { text, sel_start, caret }
    }
}

/// `EM_GETSEL` 把起点压在低 16 位、终点压在高 16 位。
/// 我们这边文本上限 16K unit，够用；读不到就当作光标在末尾。
fn read_selection(hwnd: HWND, text_len: usize) -> (usize, usize) {
    let mut packed: usize = PENDING;
    let _ = unsafe {
        SendMessageTimeoutW(
            hwnd,
            EM_GETSEL,
            WPARAM(0),
            LPARAM(0),
            SMTO_ABORTIFHUNG,
            READ_TIMEOUT_MS,
            Some(&mut packed),
        )
    };
    if packed == PENDING {
        return (text_len, text_len);
    }

    let start = (packed & 0xFFFF).min(text_len);
    let end = ((packed >> 16) & 0xFFFF).min(text_len);
    if start > end {
        (end, start)
    } else {
        (start, end)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_edit_classes_are_standard() {
        for class in ["Edit", "edit", "RichEdit", "RichEdit20W", "RichEdit50W", "RICHEDIT60W", "Scintilla"] {
            assert_eq!(classify(class), Capability::Standard, "{class} 应该认成标准文本控件");
        }
    }

    /// WinForms 的编辑框内层是原生 EDIT，类名长这样 —— 认不出来就会把正常输入框当自绘
    #[test]
    fn framework_hosted_edit_controls_are_standard() {
        for class in [
            "WindowsForms10.EDIT.app.0.141b42a_r6_ad1",
            "TEdit",
            "TMemo",
            "windowsforms10.edit.app",
        ] {
            assert_eq!(classify(class), Capability::Standard, "{class} 应该认成标准文本控件");
        }
    }

    #[test]
    fn self_drawn_classes_are_not_standard() {
        for class in [
            "HwndWrapper[Game::InputBox]",
            "Chrome_RenderWidgetHostHWND",
            "UnityWndClass",
            "UnrealWindow",
            "SDL_app",
            "",
        ] {
            assert_eq!(classify(class), Capability::NonStandard, "{class} 不该被当成文本控件");
        }
    }

    #[test]
    fn null_window_reads_as_not_text() {
        assert_eq!(read(HWND(std::ptr::null_mut())), ReadResult::NotText);
    }

    #[test]
    fn capability_labels_are_distinct() {
        assert_ne!(Capability::Standard.label(), Capability::NonStandard.label());
    }
}
