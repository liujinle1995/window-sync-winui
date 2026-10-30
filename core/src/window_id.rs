//! 窗口身份模型。
//!
//! 光靠 `HWND` 认窗口是不够的，两个方向都会翻车：
//!
//! - **句柄会被系统复用**。从窗口一关，它的 HWND 值可能马上被一个毫不相干的窗口拿走，
//!   这时候还照旧往这个句柄投键鼠消息，就等于在别人的窗口里乱点。
//! - **PID 也会被复用**，只是慢一些。用户关了游戏再开一个别的程序，PID 可能撞上。
//!
//! 所以身份要落在这四样东西上：`PID + 进程启动时间 + EXE 完整路径 + 窗口类名`。
//! 前两个防复用，第三个区分同名程序（同一份客户端放在两个目录里多开就是这种情况），
//! 第四个区分窗口类型（同一进程的主窗口 / 子控件 / 输入法候选窗）。
//!
//! 除此之外，[`WindowIdentity`] 还带上了坐标映射和缩放要用的客户区尺寸与 DPI，
//! 以及父窗口句柄 —— 排查「为什么这个窗口查不到」的时候，这几项基本是第一眼要看的东西。

use std::collections::HashMap;

use once_cell::sync::Lazy;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use windows::Win32::Foundation::{CloseHandle, FILETIME, HWND, MAX_PATH};
use windows::Win32::System::Threading::{
    GetProcessTimes, OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::{
    GetClassNameW, GetClientRect, GetParent, GetWindowTextW, GetWindowThreadProcessId, IsWindow,
    IsWindowVisible,
};

/// 一个窗口的完整身份快照。
///
/// 序列化后直接给宿主 UI 用，字段名与 C# 侧 `WindowInfo` 一一对应（那边按 snake_case 解析）。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowIdentity {
    pub hwnd: i64,
    pub pid: u32,
    /// 进程启动时间（FILETIME，自 1601 起的 100ns）。用来防 PID 复用
    pub process_start_time: u64,
    /// 可执行文件完整路径。用来区分同名程序
    pub exe_path: String,
    /// 窗口类名。`Edit` / `Chrome_WidgetWin_1` / `UnityWndClass` 这类
    pub class_name: String,
    pub title: String,
    /// 父窗口句柄；顶层窗口为 0
    pub parent_hwnd: i64,
    /// 客户区尺寸：坐标映射要用
    pub client_width: i32,
    pub client_height: i32,
    /// 窗口所在显示器的 DPI：换算 DIP 要用
    pub dpi: u32,
    pub visible: bool,
}

impl WindowIdentity {
    /// 采一份完整快照。窗口已经没了返回 `None`。
    ///
    /// 这个函数会 `OpenProcess`，不要在投递热路径上调用 —— 只在枚举、校验时用。
    pub fn of(hwnd: HWND) -> Option<Self> {
        if hwnd.0.is_null() || !unsafe { IsWindow(hwnd).as_bool() } {
            return None;
        }

        let mut pid = 0u32;
        unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
        if pid == 0 {
            return None;
        }

        let (client_width, client_height) = client_size(hwnd).unwrap_or((0, 0));
        let parent = unsafe { GetParent(hwnd) }.unwrap_or(HWND(std::ptr::null_mut()));

        Some(Self {
            hwnd: hwnd.0 as isize as i64,
            pid,
            process_start_time: process_start_time(pid).unwrap_or(0),
            exe_path: process_exe_path(pid).unwrap_or_default(),
            class_name: class_name_of(hwnd),
            title: title_of(hwnd),
            parent_hwnd: parent.0 as isize as i64,
            client_width,
            client_height,
            dpi: dpi_of(hwnd),
            visible: unsafe { IsWindowVisible(hwnd).as_bool() },
        })
    }

    /// 取稳定身份
    pub fn key(&self) -> WindowKey {
        WindowKey {
            pid: self.pid,
            process_start_time: self.process_start_time,
            exe_path: self.exe_path.clone(),
            class_name: self.class_name.clone(),
        }
    }

    /// 日志里怎么称呼它
    pub fn describe(&self) -> String {
        format!(
            "{} [{}] (hwnd=0x{:X}, pid={}, {}x{} @{}dpi)",
            self.title,
            self.class_name,
            self.hwnd,
            self.pid,
            self.client_width,
            self.client_height,
            self.dpi
        )
    }
}

/// 窗口的稳定身份：句柄之外的那部分。
///
/// 只在 `start_sync` 时采一次，之后用来回答一个问题：
/// 「这个句柄现在指的，还是当初那个窗口吗？」
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct WindowKey {
    pub pid: u32,
    pub process_start_time: u64,
    pub exe_path: String,
    pub class_name: String,
}

impl WindowKey {
    pub fn of(hwnd: HWND) -> Option<Self> {
        if hwnd.0.is_null() || !unsafe { IsWindow(hwnd).as_bool() } {
            return None;
        }
        let mut pid = 0u32;
        unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
        if pid == 0 {
            return None;
        }
        Some(Self {
            pid,
            process_start_time: process_start_time(pid).unwrap_or(0),
            exe_path: process_exe_path(pid).unwrap_or_default(),
            class_name: class_name_of(hwnd),
        })
    }

    /// 两份身份是不是同一个窗口。
    ///
    /// 拿不到的字段按「通配」处理：权限不足导致读不到 exe 路径时，
    /// 不能因此把正常窗口判成换了 —— 那会把用户的从窗口无故踢掉。
    /// `pid` 是唯一必须对上的：句柄换了进程，一定是另一个窗口。
    pub fn matches(&self, other: &Self) -> bool {
        if self.pid != other.pid {
            return false;
        }
        same_or_unknown(self.process_start_time, other.process_start_time)
            && same_text(&self.exe_path, &other.exe_path)
            && same_text(&self.class_name, &other.class_name)
    }

    pub fn describe(&self) -> String {
        let exe = file_name(&self.exe_path);
        if exe.is_empty() {
            format!("pid={} [{}]", self.pid, self.class_name)
        } else {
            format!("{exe} (pid={}) [{}]", self.pid, self.class_name)
        }
    }
}

/// 0 表示没取到（老系统 / 权限不足），此时不参与比较
fn same_or_unknown(a: u64, b: u64) -> bool {
    a == 0 || b == 0 || a == b
}

/// 空串表示没取到，不参与比较；路径比较不分大小写
fn same_text(a: &str, b: &str) -> bool {
    a.is_empty() || b.is_empty() || a.eq_ignore_ascii_case(b)
}

/// 从完整路径里取文件名，用于日志
pub fn file_name(path: &str) -> &str {
    path.rsplit(['\\', '/']).next().unwrap_or(path)
}

// ── 单项采集 ──────────────────────────────────────────────────────────────

pub fn class_name_of(hwnd: HWND) -> String {
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

/// 取窗口标题。跨进程时 `GetWindowTextW` 读的是缓存的标题，不会去问对方要文本，
/// 所以对无响应的窗口也是安全的。
pub fn title_of(hwnd: HWND) -> String {
    let mut buf = [0u16; 512];
    let n = unsafe { GetWindowTextW(hwnd, &mut buf) };
    if n <= 0 {
        return String::new();
    }
    String::from_utf16_lossy(&buf[..n as usize]).trim().to_string()
}

/// 客户区尺寸。坐标映射要按这个比例换算，取不到就不能乱映射。
pub fn client_size(hwnd: HWND) -> Option<(i32, i32)> {
    if hwnd.0.is_null() {
        return None;
    }
    let mut rect = windows::Win32::Foundation::RECT::default();
    unsafe { GetClientRect(hwnd, &mut rect).ok()? };
    Some((rect.right - rect.left, rect.bottom - rect.top))
}

/// 窗口所在显示器的 DPI。取不到按 96 处理（100% 缩放）
pub fn dpi_of(hwnd: HWND) -> u32 {
    if hwnd.0.is_null() {
        return 96;
    }
    match unsafe { GetDpiForWindow(hwnd) } {
        0 => 96,
        dpi => dpi,
    }
}

/// 进程启动时间。读不到返回 `None` —— 调用方要能区分「0」和「没取到」
pub fn process_start_time(pid: u32) -> Option<u64> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut creation = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        let ok = GetProcessTimes(handle, &mut creation, &mut exit, &mut kernel, &mut user).is_ok();
        let _ = CloseHandle(handle);
        if !ok {
            return None;
        }
        Some(((creation.dwHighDateTime as u64) << 32) | creation.dwLowDateTime as u64)
    }
}

/// 进程可执行文件完整路径
pub fn process_exe_path(pid: u32) -> Option<String> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = vec![0u16; MAX_PATH as usize * 2];
        let mut len = buf.len() as u32;
        let r = QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_WIN32,
            windows::core::PWSTR(buf.as_mut_ptr()),
            &mut len,
        );
        let _ = CloseHandle(handle);
        r.ok()?;
        if len == 0 {
            return None;
        }
        Some(String::from_utf16_lossy(&buf[..len as usize]))
    }
}

// ── 缓存过的身份校验 ──────────────────────────────────────────────────────
//
// 校验要 OpenProcess，比 IsWindow 贵得多，所以不能放在投递热路径上。
// 这里只给「启动时记住几个窗口、之后偶尔核对」用，数量是个位数。

/// 一组待校验的句柄 → 身份
#[derive(Default)]
pub struct KeyMap {
    inner: Mutex<HashMap<i64, WindowKey>>,
}

impl KeyMap {
    pub fn clear(&self) {
        self.inner.lock().clear();
    }

    pub fn insert(&self, hwnd: i64, key: WindowKey) {
        self.inner.lock().insert(hwnd, key);
    }

    pub fn remove(&self, hwnd: i64) {
        self.inner.lock().remove(&hwnd);
    }

    pub fn contains(&self, hwnd: i64) -> bool {
        self.inner.lock().contains_key(&hwnd)
    }

    pub fn len(&self) -> usize {
        self.inner.lock().len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// 这个句柄现在还是原来的窗口吗？
    ///
    /// 三种情况都算「不是」：窗口没了、身份对不上（句柄被复用 / PID 被复用）。
    /// 没有记录的身份视为「不认识」，按不是处理 —— 调用方只在确实记过的地方用它。
    pub fn still_original(&self, hwnd: i64) -> bool {
        let expected = {
            let map = self.inner.lock();
            map.get(&hwnd).cloned()
        };
        let Some(expected) = expected else {
            return false;
        };
        match WindowKey::of(HWND(hwnd as *mut _)) {
            Some(current) => expected.matches(&current),
            None => false,
        }
    }
}

/// 主窗口身份
pub static MASTER_KEY: Lazy<KeyMap> = Lazy::new(KeyMap::default);
/// 从窗口身份
pub static SLAVE_KEYS: Lazy<KeyMap> = Lazy::new(KeyMap::default);

#[cfg(test)]
mod tests {
    use super::*;

    fn key(pid: u32, started: u64, exe: &str, class: &str) -> WindowKey {
        WindowKey {
            pid,
            process_start_time: started,
            exe_path: exe.to_string(),
            class_name: class.to_string(),
        }
    }

    #[test]
    fn same_window_matches_itself() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let a = key(100, 1234, r"C:\Games\a.exe", "UnityWndClass");
        assert!(a.matches(&a.clone()));
    }

    /// PID 被复用：pid 相同但启动时间不同 —— 这是另一个进程了
    #[test]
    fn pid_reuse_is_detected() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let before = key(100, 1111, r"C:\Games\a.exe", "UnityWndClass");
        let after = key(100, 2222, r"C:\Games\a.exe", "UnityWndClass");
        assert!(!before.matches(&after), "PID 复用必须能识别出来");
    }

    /// 同名程序放在两个目录：pid / 启动时间都不同，路径也不同
    #[test]
    fn same_name_different_folder_is_different() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let a = key(100, 1111, r"C:\Games\A\client.exe", "UnityWndClass");
        let b = key(200, 2222, r"D:\Games\B\client.exe", "UnityWndClass");
        assert!(!a.matches(&b));
    }

    /// 句柄复用最典型的样子：句柄还是那个句柄，进程整个换了
    #[test]
    fn handle_reuse_shows_as_pid_change() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let a = key(100, 1111, r"C:\a.exe", "Edit");
        let b = key(999, 3333, r"C:\b.exe", "Notepad");
        assert!(!a.matches(&b));
    }

    /// 同一个进程里的两个窗口：类名不同就不是同一个
    #[test]
    fn same_process_different_class_is_different() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let a = key(100, 1111, r"C:\a.exe", "UnityWndClass");
        let b = key(100, 1111, r"C:\a.exe", "Edit");
        assert!(!a.matches(&b));
    }

    /// 权限不足读不到 exe 路径时不能误判 —— 否则会莫名其妙踢掉用户的从窗口
    #[test]
    fn unknown_fields_are_wildcards() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let full = key(100, 1111, r"C:\a.exe", "Edit");
        let no_exe = key(100, 1111, "", "Edit");
        let no_time = key(100, 0, r"C:\a.exe", "Edit");
        let no_class = key(100, 1111, r"C:\a.exe", "");
        assert!(full.matches(&no_exe));
        assert!(full.matches(&no_time));
        assert!(full.matches(&no_class));
        assert!(no_exe.matches(&full), "通配要对称");
    }

    /// 但 pid 永远不能通配：句柄换了进程一定是另一个窗口
    #[test]
    fn pid_is_never_a_wildcard() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let a = key(0, 1111, r"C:\a.exe", "Edit");
        let b = key(100, 1111, r"C:\a.exe", "Edit");
        assert!(!a.matches(&b));
    }

    #[test]
    fn file_name_extraction() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        assert_eq!(file_name(r"C:\Games\A\client.exe"), "client.exe");
        assert_eq!(file_name("client.exe"), "client.exe");
        assert_eq!(file_name(""), "");
    }

    /// 真实窗口：身份该有的字段都要有值
    #[test]
    fn identity_of_a_real_window_has_every_field() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let window = crate::test_support::popup_window();
        let identity = WindowIdentity::of(window.0).expect("真实窗口应该采得到身份");

        assert_eq!(identity.pid, std::process::id());
        assert!(identity.process_start_time > 0, "启动时间必须采到");
        assert!(identity.exe_path.to_ascii_lowercase().ends_with(".exe"), "exe 路径不对：{}", identity.exe_path);
        assert!(!identity.class_name.is_empty(), "类名不能为空");
        assert_eq!(identity.class_name, "LxSyncIdentityTestHost");
        assert!(!identity.title.is_empty(), "标题不能为空");
        assert_eq!(identity.parent_hwnd, 0, "顶层窗口没有父窗口");
        assert_eq!((identity.client_width, identity.client_height), (400, 300));
        assert!(identity.dpi >= 96, "DPI 至少是 96，实际 {}", identity.dpi);
    }

    /// 身份里的 key 必须能和「同一个窗口重新采一次」对上
    #[test]
    fn key_is_stable_across_reads() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let window = crate::test_support::popup_window();
        let a = WindowIdentity::of(window.0).unwrap().key();
        let b = WindowIdentity::of(window.0).unwrap().key();
        assert!(a.matches(&b), "同一个窗口两次采集的身份必须一致：{a:?} vs {b:?}");
    }

    /// 销毁后的窗口采不到身份
    #[test]
    fn destroyed_window_has_no_identity() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let window = crate::test_support::popup_window();
        let hwnd = window.0;
        drop(window); // Drop 里 DestroyWindow
        assert!(WindowIdentity::of(hwnd).is_none());
        assert!(WindowKey::of(hwnd).is_none());
    }

    /// KeyMap：记过就认，句柄换了就翻脸
    #[test]
    fn key_map_rejects_a_reused_handle() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let map = KeyMap::default();
        let window = crate::test_support::popup_window();
        let hwnd = window.0 .0 as isize as i64;

        map.insert(hwnd, WindowKey::of(window.0).unwrap());
        assert!(map.still_original(hwnd), "刚记下的窗口必须认得出来");

        // 手动把记录改成另一个进程 / 另一个启动时间的身份 = 模拟句柄被复用
        map.insert(hwnd, key(999, 1, r"C:\other.exe", "Other"));

        assert!(!map.still_original(hwnd), "句柄被别的窗口占用后必须判成不是原窗口");
    }

    #[test]
    fn key_map_forgets_on_clear() {
        // 这些用例会建窗口 / 抢焦点 / 动全局身份表，和别的模块并发会互相拆台
        let _guard = crate::test_support::serial();
        let map = KeyMap::default();
        let window = crate::test_support::popup_window();
        let hwnd = window.0 .0 as isize as i64;
        map.insert(hwnd, WindowKey::of(window.0).unwrap());
        assert_eq!(map.len(), 1);

        map.clear();
        assert!(map.is_empty());
        assert!(!map.still_original(hwnd));
    }
}
