//! 同步会话 —— 一次 `start_sync` 名下的全部资源，用 RAII 管起来。
//!
//! # 为什么要有这个模块
//!
//! 老实现里 `start_sync()` 的顺序是「先起线程，再装钩子」，而且两条钩子都用 `?`
//! 直接返回：
//!
//! ```text
//! start_input_worker() / start_text_worker() / start_focus_watch()   ← 线程已经在跑
//! SetWindowsHookExA(键盘)                                            ← 成功
//! SetWindowsHookExA(鼠标)?                                           ← 失败，直接 return Err
//! ```
//!
//! 于是调用方拿到的是一个「失败了」的返回值，进程里却留着三条无人引用的线程、
//! 一条装着事件的队列、一把还没摘的键盘钩子，以及「主窗口 = X、从窗口 = [...]」
//! 这套看起来仍然有效的全局状态。用户再点一次启动，上一套就彻底变成孤儿 ——
//! 它们的 `SYNC_ACTIVE` 读到的是新会话的值，会跟着新会话一起投递事件。
//!
//! 现在整个会话是一个结构体：所有资源都是它的字段，**每个字段自己负责自己的释放**
//! （钩子在 `Drop` 里 `UnhookWindowsHookEx`，线程在 `Drop` 里被 join，全局状态在
//! 会话拆解的最后一步清空）。`open()` 里任何一步 `?` 返回，已经建好的部分都会
//! 按**停止顺序**自动拆掉，因此调用方拿到 `Err` 时，进程状态和「从来没启动过」一致。
//!
//! # 两条顺序
//!
//! 启动：
//! 1. 验证所有窗口 —— 句柄还在、从窗口不是主窗口自己
//! 2. 创建资源 —— 事件总线、全局窗口状态、按键表
//! 3. 安装钩子 —— 键盘、鼠标
//! 4. 启动工作线程 —— 输入投递、文本注入、失焦看门狗、文本镜像、窗口监听
//! 5. 确认全部成功之后才进入 Active
//!
//! 停止：
//! 1. 禁止接收新事件 —— `SYNC_ACTIVE = false`，钩子回调从此空转
//! 2. 释放所有按键状态 —— 把还按着的键排进可靠队列（必须在卸钩子之前）
//! 3. 卸载钩子
//! 4. 停止工作线程 —— 关总线、停文本镜像、停窗口监听
//! 5. 等待线程退出
//! 6. 清理窗口状态
//!
//! 「先禁止、后释放」不能颠倒：反过来的话，补抬起之后仍可能有排队的 KeyDown 落地，
//! 从窗口照样卡键。

use crate::event_bus::{EventBus, Pos, PressedState, Reliable};
use crate::window_id;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{
    IsWindow, SetWindowsHookExA, UnhookWindowsHookEx, HHOOK, WH_KEYBOARD_LL, WH_MOUSE_LL,
};

use crate::SyncConfig;

// 启动时可以被人为打断的步骤号。故障注入那一节在本文件末尾。
const STEP_KB_HOOK: u32 = 1;
const STEP_MS_HOOK: u32 = 2;
const STEP_WORKERS: u32 = 3;

#[cfg(test)]
use faults::check as fault;

#[cfg(not(test))]
#[inline(always)]
fn fault(_step: u32) -> Result<(), String> {
    Ok(())
}

// ── 钩子 ─────────────────────────────────────────────────────────────────

/// 进程内还装着的低级钩子数量。
///
/// 只用来断言「没有泄漏」——真机上 `SetWindowsHookEx` / `UnhookWindowsHookEx`
/// 都是成功即返回，没有 API 可以反查当前装了几条。
static LIVE_HOOKS: AtomicU32 = AtomicU32::new(0);

/// 低级键盘钩子（`WH_KEYBOARD_LL`）。
///
/// 只做一件事：**在 `Drop` 里卸载自己**。这样 `open()` 中途失败时，
/// 已经装上的那条不需要谁记得去摘 —— 字段被 drop 就摘了。
pub(crate) struct KeyboardHook(HHOOK);
/// 低级鼠标钩子（`WH_MOUSE_LL`）。同上。
pub(crate) struct MouseHook(HHOOK);

unsafe impl Send for KeyboardHook {}
unsafe impl Sync for KeyboardHook {}
unsafe impl Send for MouseHook {}
unsafe impl Sync for MouseHook {}

impl KeyboardHook {
    fn install() -> Result<Self, String> {
        fault(STEP_KB_HOOK)?;
        unsafe { SetWindowsHookExA(WH_KEYBOARD_LL, Some(crate::kb_hook), None, 0) }
            .map(|h| {
                LIVE_HOOKS.fetch_add(1, Ordering::Relaxed);
                KeyboardHook(h)
            })
            .map_err(|e| format!("键盘钩子安装失败：{e}"))
    }
}

impl MouseHook {
    fn install() -> Result<Self, String> {
        fault(STEP_MS_HOOK)?;
        unsafe { SetWindowsHookExA(WH_MOUSE_LL, Some(crate::ms_hook), None, 0) }
            .map(|h| {
                LIVE_HOOKS.fetch_add(1, Ordering::Relaxed);
                MouseHook(h)
            })
            .map_err(|e| format!("鼠标钩子安装失败：{e}"))
    }
}

impl Drop for KeyboardHook {
    fn drop(&mut self) {
        unsafe { let _ = UnhookWindowsHookEx(self.0); }
        LIVE_HOOKS.fetch_sub(1, Ordering::Relaxed);
    }
}

impl Drop for MouseHook {
    fn drop(&mut self) {
        unsafe { let _ = UnhookWindowsHookEx(self.0); }
        LIVE_HOOKS.fetch_sub(1, Ordering::Relaxed);
    }
}

/// 当前还装着几条低级钩子（诊断 / 测试用）
pub fn live_hooks() -> u32 {
    LIVE_HOOKS.load(Ordering::Relaxed)
}

// ── 事件总线与三条消费线程 ────────────────────────────────────────────────

/// 事件总线 + 消费它的三条线程：输入投递、剪贴板文本注入、失焦看门狗。
///
/// 总线和线程放一起，是因为它们的生命周期必须一致：线程靠总线上的 `close()`
/// 才会从阻塞里醒来退出，总线也不能比线程先消失。
struct EventWorker {
    bus: Arc<EventBus>,
    input: Option<JoinHandle<()>>,
    text: Option<JoinHandle<()>>,
    focus: Option<JoinHandle<()>>,
}

impl EventWorker {
    /// 第 ② 步：只建总线（钩子要靠它入队），线程留到第 ④ 步再起。
    fn create() -> Self {
        let bus = Arc::new(EventBus::new());
        *crate::BUS.lock() = Some(bus.clone());
        EventWorker {
            bus,
            input: None,
            text: None,
            focus: None,
        }
    }

    /// 第 ④ 步：起三条消费线程
    fn start(&mut self) -> Result<(), String> {
        fault(STEP_WORKERS)?;
        self.input = Some(crate::spawn_input_worker(self.bus.clone()));
        self.text = Some(crate::spawn_text_worker(self.bus.clone()));
        self.focus = Some(crate::spawn_focus_watch(self.bus.clone()));
        Ok(())
    }

    /// 第 ④⑤ 步：关总线（消费者取完剩余事件就会返回 `None`）+ 等线程退出。
    ///
    /// 关闭放在 join 之前是必须的：三条线程都阻塞在 `next_input` / `next_text` /
    /// `is_closed` 轮询上，不关总线它们不会退。
    fn stop(&mut self) {
        self.bus.close();
        join(self.input.take());
        join(self.text.take());
        join(self.focus.take());
    }
}

/// 等一条线程退出。线程自己叫停自己时不能 join（会永远等下去），直接放生。
///
/// 这条路径真的会被走到：看门狗线程发现主窗口句柄被复用时会自己调 `stop_sync()`。
fn join(slot: Option<JoinHandle<()>>) {
    let Some(handle) = slot else { return };
    if handle.thread().id() != thread::current().id() {
        let _ = handle.join();
    }
}

// ── 文本镜像线程 ─────────────────────────────────────────────────────────

/// 文本镜像（`text_sync` 里的采样线程）。
///
/// 采样逻辑本身不在这里，但**线程的生命周期必须归会话**：`text_sync` 只用代次
/// 保证「同一时刻最多一条线程」，谁在什么时候起、什么时候停，由会话决定。
struct TextMirrorWorker;

impl TextMirrorWorker {
    fn start(enabled: bool) -> Self {
        if enabled {
            crate::text_sync::set_enabled(true);
        }
        TextMirrorWorker
    }
}

impl Drop for TextMirrorWorker {
    fn drop(&mut self) {
        // 不管当初开没开都要走一趟：宿主可以通过 FFI 单独打开文本镜像
        // （`wsync_set_text_mirror`），那时这里的 `enabled` 还是 false，但线程在跑。
        crate::text_sync::shutdown();
    }
}

// ── 剪贴板 ───────────────────────────────────────────────────────────────

/// 剪贴板通道。
///
/// 没有独立线程：Ctrl+C 由键盘钩子直接认出来，读到文本后 `push_text` 进总线，
/// 再由 `EventWorker` 的文本线程逐字注入。这个结构体存在的意义是把「上一轮记下的
/// 剪贴板内容哈希」在会话结束时清掉 —— 不清的话，重新启动后第一次 Ctrl+C 会被
/// 当成「和上次一样」直接忽略。
struct ClipboardHandler;

impl ClipboardHandler {
    fn start() -> Self {
        let mut handler = ClipboardHandler;
        handler.reset();
        handler
    }

    /// 丢掉「上次写入过的内容哈希」，让重启后的第一次 Ctrl+C 一定被当成新输入。
    fn reset(&mut self) {
        *crate::LAST_CB_HASH.lock() = 0;
    }
}

impl Drop for ClipboardHandler {
    fn drop(&mut self) {
        self.reset();
    }
}

// ── 窗口监听 ─────────────────────────────────────────────────────────────

/// 窗口生命周期监听（`window_watch`）：关窗口、切前台立刻知道，不靠轮询。
struct WindowMonitor {
    running: bool,
}

impl WindowMonitor {
    fn start() -> Self {
        crate::window_watch::start();
        WindowMonitor { running: true }
    }
}

impl Drop for WindowMonitor {
    fn drop(&mut self) {
        if self.running {
            self.running = false;
            crate::window_watch::shutdown();
        }
    }
}

// ── 会话 ─────────────────────────────────────────────────────────────────

/// 一次同步会话。
///
/// 字段顺序不代表拆解顺序（拆解顺序见模块文档），但每个字段自己都能被安全地
/// 提前 drop —— 这正是「中途失败也能回滚」的前提。
pub(crate) struct SyncSession {
    events: EventWorker,
    kb_hook: Option<KeyboardHook>,
    ms_hook: Option<MouseHook>,
    text_mirror: Option<TextMirrorWorker>,
    monitor: Option<WindowMonitor>,
    clipboard: ClipboardHandler,
    closed: bool,
}

impl SyncSession {
    /// 按 ①~⑤ 建立会话。任何一步失败，已经建好的部分都会按停止顺序自动拆掉。
    pub(crate) fn open(config: &SyncConfig) -> Result<Self, String> {
        // ① 验证所有窗口
        validate_targets(config)?;

        // ② 创建资源。全局窗口状态从这一步起被写入，因此从这里开始就必须由会话负责还原。
        let mut session = SyncSession::reserve(config);

        // ③ 安装钩子。任一条失败都在这里 `?` 走人，`session` 被 drop → 回滚 ②。
        session.install_hooks(config)?;

        // ④ 启动工作线程
        session.start_workers(config)?;

        // ⑤ 全部成功，这才进入 Active。在此之前钩子回调一律空转（它们第一行就看这个标志）。
        crate::SYNC_ACTIVE.store(true, Ordering::Relaxed);

        Ok(session)
    }

    /// 第 ② 步：总线 + 全局窗口状态 + 按键表。
    ///
    /// 顺序无所谓，但**全都写全局量**，所以这一步之后进程看起来已经像在同步了；
    /// 后面任何一步失败都必须把这些值还原，否则用户会在 UI 上看到「同步中」却收不到任何事件。
    fn reserve(config: &SyncConfig) -> Self {
        crate::MASTER_HWND.store(config.master_hwnd, Ordering::Relaxed);
        *crate::SLAVES.lock() = config.slave_hwnds.clone();

        // 记下主窗口与各从窗口的稳定身份（PID + 启动时间 + exe + 类名）。
        // 之后靠它回答「这个句柄现在指的，还是当初那个窗口吗」—— 句柄和 PID 都会被系统复用。
        window_id::MASTER_KEY.clear();
        window_id::SLAVE_KEYS.clear();
        if let Some(key) = window_id::WindowKey::of(HWND(config.master_hwnd as *mut _)) {
            window_id::MASTER_KEY.insert(config.master_hwnd, key);
        }
        for hwnd in &config.slave_hwnds {
            if let Some(key) = window_id::WindowKey::of(HWND(*hwnd as *mut _)) {
                window_id::SLAVE_KEYS.insert(*hwnd, key);
            }
        }

        crate::SYNC_MOUSE.store(config.sync_mouse, Ordering::Relaxed);
        crate::SYNC_KEYBOARD.store(config.sync_keyboard, Ordering::Relaxed);
        crate::SYNC_CLIPBOARD.store(config.sync_clipboard, Ordering::Relaxed);

        *crate::PRESSED.lock() = PressedState::default();
        *crate::LAST_STATUS.lock() = String::new();
        *crate::LAST_POS.lock() = Pos::default();

        SyncSession {
            events: EventWorker::create(),
            kb_hook: None,
            ms_hook: None,
            text_mirror: None,
            monitor: None,
            clipboard: ClipboardHandler::start(),
            closed: false,
        }
    }

    /// 第 ③ 步
    fn install_hooks(&mut self, config: &SyncConfig) -> Result<(), String> {
        if config.sync_keyboard {
            self.kb_hook = Some(KeyboardHook::install()?);
        }
        if config.sync_mouse {
            self.ms_hook = Some(MouseHook::install()?);
        }
        Ok(())
    }

    /// 第 ④ 步
    fn start_workers(&mut self, config: &SyncConfig) -> Result<(), String> {
        self.events.start()?;
        self.text_mirror = Some(TextMirrorWorker::start(config.text_mirror));
        self.monitor = Some(WindowMonitor::start());
        Ok(())
    }

    /// 按 ①~⑥ 停机，返回「停止前是否还按着键」（用于日志措辞）。
    pub(crate) fn close(mut self) -> bool {
        self.shutdown()
    }

    fn shutdown(&mut self) -> bool {
        if self.closed {
            return false;
        }
        self.closed = true;

        // ① 禁止接收新事件。钩子回调第一行就读它，置 false 之后立刻空转；
        //    窗口监听的最后一次 drain 也会因此空转，不会在拆除过程中再叫一次 stop_sync。
        crate::SYNC_ACTIVE.store(false, Ordering::Relaxed);

        // ② 释放所有按键状态。必须排在卸钩子之前 —— 卸完之后就没有补投的机会了。
        let (keys, buttons) = crate::PRESSED.lock().drain();
        let released = !keys.is_empty() || buttons != 0;
        if released {
            // 走可靠队列：它会排在所有已入队的 KeyDown 之后投出去
            self.events
                .bus
                .push_reliable(Reliable::Release { keys, buttons });
        }

        // ③ 卸载钩子（各自 Drop 里 UnhookWindowsHookEx）
        self.kb_hook = None;
        self.ms_hook = None;

        // ④ 停止工作线程与窗口监听。
        //    先停两个「生产者」（文本镜像、窗口监听），它们各自 join 自己那条线程；
        //    再关总线、join 三条消费线程（⑤）。
        self.text_mirror = None;
        self.monitor = None;

        // ⑤ 关总线并等三条消费线程退出。
        //    `close()` 之后 `next_input` / `next_text` 会先把剩余的取完再返回 `None`，
        //    所以上一步排进去的「补抬起」一定能投出去才退出。
        self.events.stop();

        // ⑥ 清理窗口状态
        crate::PRESSED.lock().drain();
        *crate::SLAVES.lock() = Vec::new();
        *crate::BUS.lock() = None;
        self.clipboard.reset();
        window_id::MASTER_KEY.clear();
        window_id::SLAVE_KEYS.clear();

        released
    }
}

impl Drop for SyncSession {
    fn drop(&mut self) {
        // 走到这里有两种情况：`open()` 中途失败（最常见），或者调用方没走 `close()`。
        // 两种都得按同一套顺序拆，否则失败路径会留下半套资源。
        self.shutdown();
    }
}

// ── ① 验证 ───────────────────────────────────────────────────────────────

/// 验证参与同步的每一个窗口。
///
/// 除了「没选窗口」这类明显错误，这里还要挡住两种「看起来启动了、其实空转」的情况：
/// - 句柄已经失效 —— 用户选完窗口之后又把它关掉了；
/// - 从窗口列表里混进了主窗口自己 —— 那会形成自投递回环，打一个字进去出一串。
///
/// 校验失败时**还没有创建任何资源**，所以直接返回即可，不需要回滚。
fn validate_targets(config: &SyncConfig) -> Result<(), String> {
    if config.master_hwnd == 0 {
        return Err("请先选择主窗口".into());
    }
    if config.slave_hwnds.is_empty() {
        return Err("请至少选择一个从窗口".into());
    }

    if !unsafe { IsWindow(HWND(config.master_hwnd as *mut _)) }.as_bool() {
        return Err("主窗口已关闭，请重新选择".into());
    }

    for &slave in &config.slave_hwnds {
        if slave == config.master_hwnd {
            return Err("从窗口不能是主窗口自己".into());
        }
        if !unsafe { IsWindow(HWND(slave as *mut _)) }.as_bool() {
            return Err(format!("从窗口 0x{slave:X} 已关闭，请重新选择"));
        }
    }

    Ok(())
}

// ── 故障注入（只在测试构建里有效）────────────────────────────────────────
//
// 「键盘钩子装上、鼠标钩子失败」这条路径在真机上没法稳定复现，所以留一个注入口，
// 让用例能精确地在第 N 步失败，再断言回滚干净。发布构建里 `fault()` 是空函数，
// 调用点的写法完全一致 —— 不会因为「测试专用代码」而让生产路径长得不一样。

/// 让下一次 `open()` 在第 `step` 步失败。仅在测试构建里存在。
#[cfg(test)]
pub(crate) fn inject_failure(step: u32) {
    faults::FAIL_AT.store(step, std::sync::atomic::Ordering::SeqCst);
}

#[cfg(test)]
pub(crate) const FAIL_KEYBOARD_HOOK: u32 = STEP_KB_HOOK;
#[cfg(test)]
pub(crate) const FAIL_MOUSE_HOOK: u32 = STEP_MS_HOOK;
#[cfg(test)]
pub(crate) const FAIL_WORKERS: u32 = STEP_WORKERS;

#[cfg(test)]
mod faults {
    use std::sync::atomic::{AtomicU32, Ordering};

    pub(super) static FAIL_AT: AtomicU32 = AtomicU32::new(0);

    pub(super) fn check(step: u32) -> Result<(), String> {
        if FAIL_AT.load(Ordering::SeqCst) == step {
            // 只失效一次：失败之后紧接着重试要能成功
            FAIL_AT.store(0, Ordering::SeqCst);
            return Err(format!("注入的失败（第 {step} 步）"));
        }
        Ok(())
    }
}
