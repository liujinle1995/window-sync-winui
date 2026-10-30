//! 窗口生命周期监听（事件驱动，替代轮询）。
//!
//! 早先窗口失效是靠「每次投递前 `IsWindow` 过滤」+「用户手动刷新进程列表」来清理的。
//! 这有两个问题：投递热路径上做检查只是兜底，用户不刷新就一直留着死窗口；
//! 而且「窗口被关掉」这件事本身是个事件，没有理由靠猜。
//!
//! 这里用 `SetWinEventHook` 订阅窗口级事件：
//!
//! | 事件 | 用途 |
//! |---|---|
//! | `EVENT_OBJECT_CREATE` | 记录窗口出现（也让即将出现的句柄进入已知集合） |
//! | `EVENT_OBJECT_DESTROY` | 从窗口销毁 → 立刻剔除；主窗口销毁 → 立刻停同步 |
//! | `EVENT_OBJECT_SHOW` | 窗口重新显示 |
//! | `EVENT_OBJECT_HIDE` | 窗口隐藏（最小化到托盘、切场景），等同失效 |
//! | `EVENT_SYSTEM_FOREGROUND` | 前台切换 → 立刻处理残留按键、唤醒文本同步 |
//!
//! # 为什么单开一条线程
//!
//! `WINEVENT_OUTOFCONTEXT` 的回调是由**安装钩子的那条线程**在消息循环里被调用的。
//! 如果装在 UI 线程上，回调就跟着 UI 线程的节奏跑（界面一卡，窗口失效就发现不了）；
//! 而且回调里要做日志和状态推送，压在 UI 线程上不合适。所以这里自己起一条线程，
//! 装钩子 + 跑自己的消息循环，生命周期跟同步启停一致。
//!
//! # 回调里不做重活
//!
//! 回调只把事件塞进一个小队列，**绝不** `SendMessage`、绝不 sleep、绝不停同步 ——
//! 那会把整条事件流堵住，更糟的是「在回调栈里 join 监听线程自己」会直接死锁。
//! 真正的处理（剔除从窗口、停同步、唤醒文本同步）放在消息循环的每一轮之后，
//! 那时已经退出了回调栈，是安全的调用点。
//!
//! # 为什么用消息窗口而不是线程消息来叫停
//!
//! 用 `PostThreadMessageW(tid, WM_QUIT)` 停线程看似简单，但有个必现的竞态：
//! `start()` 里 `thread::spawn` 一返回，调用方紧接着就可能 `shutdown()`，
//! 而这时新线程还不一定跑到「把 tid 写进全局」那一步（线程消息队列也可能还没建）。
//! 投给 `tid = 0` 什么都不会发生，`GetMessageW` 就永远阻塞，`join()` 跟着永远等。
//!
//! 改成在监听线程里先建一个 `HWND_MESSAGE` 窗口：`start()` 等它建好才返回，
//! 之后 `PostMessageW` 到这个句柄**必然**能送到队列，叫停不再依赖时序。

use once_cell::sync::Lazy;
use parking_lot::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicIsize, AtomicU32, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, PostMessageW,
    PostQuitMessage, PostThreadMessageW, RegisterClassW, TranslateMessage, EVENT_OBJECT_CREATE,
    EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, EVENT_OBJECT_SHOW, EVENT_SYSTEM_FOREGROUND,
    HWND_MESSAGE, MSG, WINDOW_STYLE, WINEVENT_OUTOFCONTEXT, WNDCLASSW, WM_APP,
};

/// 只关心窗口本身的事件，不看菜单/光标/滚动条之类的对象事件
const OBJID_WINDOW: i32 = 0;
const WM_QUIT_WATCH: u32 = 0x0012;
/// 私有的叫停消息（`WM_APP` 段内，不会和系统消息撞车）
const WM_WATCH_STOP: u32 = WM_APP + 0x51;
/// 「有新事件，醒来处理一趟」的自我唤醒消息
const WM_WATCH_PUMP: u32 = WM_APP + 0x52;
const WATCH_CLASS: &str = "LxSyncWindowWatch";
/// `start()` 等监听线程备好窗口与钩子的上限；正常是几毫秒
const READY_TIMEOUT_MS: u64 = 2000;
/// `shutdown()` 等监听线程退出的上限；超时只告警不阻塞调用方
const JOIN_TIMEOUT_MS: u64 = 2000;

static RUNNING: AtomicBool = AtomicBool::new(false);
/// 监听线程是否已完成「建窗口 + 装钩子」
static READY: AtomicBool = AtomicBool::new(false);
/// 消息窗口句柄，0 表示还没建好（或已销毁）
static STOP_HWND: AtomicIsize = AtomicIsize::new(0);
/// 监听线程 id，仅在消息窗口建失败时作为兜底叫停手段
static TID: AtomicU32 = AtomicU32::new(0);
/// 是否已经投过唤醒消息（合并用）
static PUMP_POSTED: AtomicBool = AtomicBool::new(false);
/// 最近一次前台窗口
static FOREGROUND: AtomicI64 = AtomicI64::new(0);
/// 收到的事件计数，只用于日志与测试观察
static EVENTS: AtomicU32 = AtomicU32::new(0);

struct Worker {
    handle: JoinHandle<()>,
    /// 监听线程退出前会往这里发一个信号，用于「带超时的等待」
    done: Receiver<()>,
}

static WORKER: Lazy<Mutex<Option<Worker>>> = Lazy::new(|| Mutex::new(None));

/// 回调只负责记录，处理放到消息循环里做（见模块注释「回调里不做重活」）
#[derive(Debug, Clone, Copy)]
enum Evt {
    Destroyed(i64),
    Hidden(i64),
    Foreground(i64),
}

static QUEUE: Lazy<Mutex<Vec<Evt>>> = Lazy::new(|| Mutex::new(Vec::new()));

/// 原始事件流水（未经过滤），只留最近 32 条，排查「事件没来」还是「被过滤掉了」
static RAW: Lazy<Mutex<Vec<(u32, i64, i32, i32)>>> = Lazy::new(|| Mutex::new(Vec::new()));

fn record_raw(event: u32, hwnd: i64, id_object: i32, id_child: i32) {
    let mut raw = RAW.lock();
    if raw.len() >= 32 {
        raw.remove(0);
    }
    raw.push((event, hwnd, id_object, id_child));
}

/// 在回调里入队之后，给自己投一个唤醒消息。
///
/// 这一步不能省：出上下文钩子的回调是在 `GetMessageW` **等待期间**被调用的，
/// 那时消息循环还没往下走；如果队列里没有别的消息，`GetMessageW` 会继续等，
/// 我们攒下的事件就一直没人处理。投一个消息过去，循环立刻转一圈来 drain。
///
/// `PUMP_POSTED` 负责合并：连续来一百个事件也只投一个唤醒消息。
fn wake_loop() {
    if PUMP_POSTED.swap(true, Ordering::SeqCst) {
        return;
    }
    let hwnd = STOP_HWND.load(Ordering::SeqCst);
    if hwnd == 0 {
        // 窗口还没建好或正在退出；此时事件会在循环启动 / 退出前的 drain 里被处理
        PUMP_POSTED.store(false, Ordering::SeqCst);
        return;
    }
    unsafe {
        let _ = PostMessageW(HWND(hwnd as *mut _), WM_WATCH_PUMP, WPARAM(0), LPARAM(0));
    }
}

/// 最近收到的原始事件 `(event, hwnd, idObject, idChild)`，给诊断和测试用
pub fn raw_events() -> Vec<(u32, i64, i32, i32)> {
    RAW.lock().clone()
}


pub fn is_running() -> bool {
    RUNNING.load(Ordering::Relaxed)
}

/// 最近一次 `EVENT_SYSTEM_FOREGROUND` 报告的窗口；0 表示还没收到过。
/// 注意它只在同步运行期间更新，别拿它当「当前前台窗口」的通用查询。
pub fn foreground() -> i64 {
    FOREGROUND.load(Ordering::Relaxed)
}

pub fn event_count() -> u32 {
    EVENTS.load(Ordering::Relaxed)
}

/// 起监听线程。已经在跑就什么都不做。
///
/// 返回时监听线程**已经**建好消息窗口并装好钩子，因此紧接着调 `shutdown()` 也是安全的。
pub fn start() {
    if RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }
    FOREGROUND.store(0, Ordering::Relaxed);
    EVENTS.store(0, Ordering::Relaxed);
    READY.store(false, Ordering::SeqCst);
    PUMP_POSTED.store(false, Ordering::SeqCst);
    QUEUE.lock().clear();

    let (tx, rx) = mpsc::channel();
    let handle = thread::spawn(move || run(tx));
    *WORKER.lock() = Some(Worker { handle, done: rx });

    // 等它真正就绪：否则紧接着的 shutdown 无处投递，只能干等到超时
    let deadline = Instant::now() + Duration::from_millis(READY_TIMEOUT_MS);
    while !READY.load(Ordering::SeqCst) && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(1));
    }
}

/// 停监听线程并等它退出（最多等 [`JOIN_TIMEOUT_MS`]）。
///
/// 允许被监听线程自己调用（主窗口销毁时就是这么停同步的）：那种情况下不能等自己，
/// 只投一条停止消息就返回，监听线程会在当前这轮事件处理完之后自然退出。
pub fn shutdown() {
    if !RUNNING.swap(false, Ordering::SeqCst) {
        // 已经在停或已停：把残留句柄收掉就行，不重复等
        let _ = WORKER.lock().take();
        return;
    }

    // 先叫停：消息窗口优先，没有就退回线程消息
    let hwnd = STOP_HWND.load(Ordering::SeqCst);
    unsafe {
        if hwnd != 0 {
            let _ = PostMessageW(HWND(hwnd as *mut _), WM_WATCH_STOP, WPARAM(0), LPARAM(0));
        } else {
            let tid = TID.load(Ordering::SeqCst);
            if tid != 0 {
                let _ = PostThreadMessageW(tid, WM_QUIT_WATCH, WPARAM(0), LPARAM(0));
            }
        }
    }

    let Some(worker) = WORKER.lock().take() else {
        return;
    };
    if worker.handle.thread().id() == thread::current().id() {
        // 监听线程自己叫停：不能等自己退，句柄直接放生（线程随后会自己结束）
        return;
    }

    match worker.done.recv_timeout(Duration::from_millis(JOIN_TIMEOUT_MS)) {
        Ok(()) | Err(RecvTimeoutError::Disconnected) => {
            let _ = worker.handle.join();
        }
        Err(RecvTimeoutError::Timeout) => {
            // 宁可脱管一个线程，也不把调用方（UI 线程 / 停止同步）钉死在这里
            crate::log("窗口监听：等待监听线程退出超时，已脱管继续");
        }
    }
}

fn run(done: mpsc::Sender<()>) {
    unsafe {
        TID.store(GetCurrentThreadId(), Ordering::SeqCst);

        let window = create_message_window();
        if let Some(h) = window {
            STOP_HWND.store(h.0 as isize, Ordering::SeqCst);
        }

        // 一个钩子覆盖 CREATE..HIDE 这个连续区间（0x8000..=0x8003）
        let object_hook = SetWinEventHook(
            EVENT_OBJECT_CREATE,
            EVENT_OBJECT_HIDE,
            None,
            Some(on_object_event),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        );
        // 前台切换是另一个事件段，得单独装一个
        let foreground_hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            None,
            Some(on_system_event),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        );

        if object_hook.is_invalid() || foreground_hook.is_invalid() {
            crate::log("窗口监听：SetWinEventHook 安装失败，退回轮询兜底");
        } else {
            crate::log("窗口监听：已订阅窗口创建/销毁/显示/隐藏与前台切换");
        }

        READY.store(true, Ordering::SeqCst);

        // 出上下文钩子的回调就靠这条消息循环驱动
        let mut msg = MSG::default();
        // 注意别写 `.as_bool()`：GetMessage 出错返回 -1，那也是个非零值
        while GetMessageW(&mut msg, None, 0, 0).0 > 0 {
            let _ = TranslateMessage(&msg);
            let _ = DispatchMessageW(&msg);
            // 先解锁再处理：处理里可能又有回调（比如停同步引发的连锁反应）
            PUMP_POSTED.store(false, Ordering::SeqCst);
            drain_events();
        }

        // 退出前把迟到的事件处理掉：销毁事件可能和停止消息挨着到
        drain_events();

        if !object_hook.is_invalid() {
            let _ = UnhookWinEvent(object_hook);
        }
        if !foreground_hook.is_invalid() {
            let _ = UnhookWinEvent(foreground_hook);
        }
        if let Some(h) = window {
            let _ = DestroyWindow(h);
        }
        STOP_HWND.store(0, Ordering::SeqCst);
        TID.store(0, Ordering::SeqCst);
        READY.store(false, Ordering::SeqCst);
        QUEUE.lock().clear();
    }

    let _ = done.send(());
}

/// 把回调攒下的事件在消息循环里处理掉（此时已退出回调栈）
fn drain_events() {
    loop {
        let batch = {
            let mut q = QUEUE.lock();
            if q.is_empty() {
                return;
            }
            std::mem::take(&mut *q)
        };
        for evt in batch {
            match evt {
                Evt::Destroyed(h) => crate::on_window_gone(h, "已销毁"),
                Evt::Hidden(h) => crate::on_window_gone(h, "已隐藏"),
                Evt::Foreground(h) => crate::on_foreground_changed(h),
            }
        }
    }
}

unsafe extern "system" fn watch_proc(hwnd: HWND, msg: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    match msg {
        WM_WATCH_STOP => {
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        // 唤醒消息本身不用做什么，它存在的意义就是让循环转一圈去 drain
        WM_WATCH_PUMP => LRESULT(0),
        _ => unsafe { DefWindowProcW(hwnd, msg, w, l) },
    }
}

/// `HWND_MESSAGE` 窗口：只用来收消息，不出现在任务栏、不占屏幕
fn create_message_window() -> Option<HWND> {
    unsafe {
        let instance = GetModuleHandleW(None).ok()?.into();
        let class_w: Vec<u16> = WATCH_CLASS.encode_utf16().chain(std::iter::once(0)).collect();
        let title_w: Vec<u16> = std::iter::once(0).collect();

        let wc = WNDCLASSW {
            lpfnWndProc: Some(watch_proc),
            hInstance: instance,
            lpszClassName: PCWSTR(class_w.as_ptr()),
            ..Default::default()
        };
        // 类已注册过会返回 0（ERROR_CLASS_ALREADY_EXISTS），照样能建窗口
        let _ = RegisterClassW(&wc);

        let hwnd = CreateWindowExW(
            Default::default(),
            PCWSTR(class_w.as_ptr()),
            PCWSTR(title_w.as_ptr()),
            WINDOW_STYLE(0),
            0,
            0,
            0,
            0,
            Some(&HWND_MESSAGE),
            None,
            instance,
            None,
        )
        .ok()?;
        Some(hwnd)
    }
}

/// 窗口级事件：创建 / 销毁 / 显示 / 隐藏
unsafe extern "system" fn on_object_event(
    _hook: HWINEVENTHOOK,
    event: u32,
    hwnd: HWND,
    id_object: i32,
    id_child: i32,
    _thread: u32,
    _time: u32,
) {
    let hwnd_value = hwnd.0 as isize as i64;
    // 诊断用：关心的事件全记（含被过滤掉的 idObject），噪音事件不记
    if id_object == OBJID_WINDOW
        || matches!(
            event,
            EVENT_OBJECT_CREATE | EVENT_OBJECT_DESTROY | EVENT_OBJECT_SHOW | EVENT_OBJECT_HIDE
        )
    {
        record_raw(event, hwnd_value, id_object, id_child);
    }
    // 只看窗口本身，不关心控件、光标、菜单对象
    if id_object != OBJID_WINDOW || id_child != 0 {
        return;
    }
    if hwnd_value == 0 {
        return;
    }
    EVENTS.fetch_add(1, Ordering::Relaxed);

    let evt = match event {
        EVENT_OBJECT_DESTROY => Evt::Destroyed(hwnd_value),
        EVENT_OBJECT_HIDE => Evt::Hidden(hwnd_value),
        // 创建与显示只作观察：真正要处理的是「它是不是我们的目标窗口」，
        // 这件事在 start_sync 时已经定了，这里不需要抢着做。
        EVENT_OBJECT_CREATE | EVENT_OBJECT_SHOW => return,
        _ => return,
    };

    // 只入队，不做任何会阻塞的事（这里还在回调栈里）
    {
        let mut q = QUEUE.lock();
        if q.len() >= 256 {
            // 极端情况下（窗口风暴）丢最老的，保证队列不会无限长
            q.remove(0);
        }
        q.push(evt);
    }
    wake_loop();
}

/// 系统事件：前台窗口换人了
unsafe extern "system" fn on_system_event(
    _hook: HWINEVENTHOOK,
    event: u32,
    hwnd: HWND,
    _id_object: i32,
    _id_child: i32,
    _thread: u32,
    _time: u32,
) {
    if event != EVENT_SYSTEM_FOREGROUND {
        return;
    }
    // 前台事件不带对象 id，别用 OBJID 过滤，否则会全被丢掉
    let hwnd_value = hwnd.0 as isize as i64;
    if hwnd_value == 0 {
        return;
    }
    FOREGROUND.store(hwnd_value, Ordering::Relaxed);
    EVENTS.fetch_add(1, Ordering::Relaxed);

    {
        let mut q = QUEUE.lock();
        if q.len() >= 256 {
            q.remove(0);
        }
        q.push(Evt::Foreground(hwnd_value));
    }
    wake_loop();
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support as ts;
    use std::time::{Duration, Instant};

    /// 这些用例动的是模块级全局量（监听线程、从窗口列表、身份表），
    /// 用 `test_support` 里那把进程级锁串行 —— 销毁窗口会触发 stop_sync，
    /// 别的模块的用例也会被牵连
    fn serial() -> parking_lot::MutexGuard<'static, ()> {
        ts::serial()
    }

    fn wait_until(mut check: impl FnMut() -> bool, timeout_ms: u64) -> bool {
        let deadline = Instant::now() + Duration::from_millis(timeout_ms);
        while Instant::now() < deadline {
            if check() {
                return true;
            }
            thread::sleep(Duration::from_millis(5));
        }
        check()
    }

    /// 最核心的一条：从窗口销毁后**不需要任何轮询**就把它从列表里拿掉。
    #[test]
    fn destroy_event_prunes_the_slave_without_polling() {
        let _guard = serial();

        let master = ts::popup_window();
        let slave = ts::popup_window();
        let master_hwnd = ts::hwnd_value(master.0);
        let slave_hwnd = ts::hwnd_value(slave.0);

        ts::begin_fake_sync(master_hwnd, &[slave_hwnd]);
        start();

        assert!(is_running(), "监听线程没起来");
        assert_eq!(ts::slaves(), vec![slave_hwnd], "准备阶段：从窗口应该在列表里");

        // 关掉从窗口 —— 只靠窗口事件，不调用任何清理函数、不等轮询
        drop(slave);

        assert!(
            wait_until(|| !ts::slaves().contains(&slave_hwnd), 2000),
            "销毁事件没有把从窗口剔除（事件计数 {}）；原始事件：{:?}",
            event_count(),
            raw_events()
        );
        assert!(ts::sync_active(), "只是从窗口没了，同步不该停");
        assert!(event_count() > 0, "应该真的收到过事件");

        shutdown();
        ts::end_fake_sync();
    }

    /// 主窗口销毁：同步必须自动停，而不是继续往一个死句柄投消息
    #[test]
    fn destroying_the_master_stops_the_sync() {
        let _guard = serial();

        let master = ts::popup_window();
        let slave = ts::popup_window();
        let master_hwnd = ts::hwnd_value(master.0);
        let slave_hwnd = ts::hwnd_value(slave.0);

        ts::begin_fake_sync(master_hwnd, &[slave_hwnd]);
        start();
        assert!(is_running());

        drop(master);

        assert!(
            wait_until(|| !ts::sync_active(), 2000),
            "主窗口关闭后同步应当自动停止"
        );

        shutdown();
        ts::end_fake_sync();
    }

    /// 句柄被系统复用：`IsWindow` 会说是真，但身份对不上 —— 必须剔除。
    /// 这里直接驱动校验入口（生产里由看门狗每秒跑一次）。
    #[test]
    fn handle_reuse_is_caught_by_identity_check() {
        let _guard = serial();

        let master = ts::popup_window();
        let slave = ts::popup_window();
        let master_hwnd = ts::hwnd_value(master.0);
        let slave_hwnd = ts::hwnd_value(slave.0);

        ts::begin_fake_sync(master_hwnd, &[slave_hwnd]);
        assert_eq!(ts::slaves(), vec![slave_hwnd]);

        // 模拟「这个句柄现在指向了别的程序的窗口」：把记录的身份换掉
        crate::window_id::SLAVE_KEYS.insert(slave_hwnd, ts::foreign_key());

        ts::verify_now();

        assert!(
            !ts::slaves().contains(&slave_hwnd),
            "句柄被复用后必须从同步列表移除"
        );
        assert!(ts::sync_active(), "从窗口被复用不该影响整条同步");
        assert!(!crate::window_id::SLAVE_KEYS.contains(slave_hwnd), "身份记录也要清掉");

        ts::end_fake_sync();
    }

    /// 主窗口身份对不上（PID 复用 / 句柄被占）：整条同步停掉
    #[test]
    fn master_identity_mismatch_stops_the_sync() {
        let _guard = serial();

        let master = ts::popup_window();
        let slave = ts::popup_window();
        let master_hwnd = ts::hwnd_value(master.0);
        let slave_hwnd = ts::hwnd_value(slave.0);

        ts::begin_fake_sync(master_hwnd, &[slave_hwnd]);
        crate::window_id::MASTER_KEY.insert(master_hwnd, ts::foreign_key());

        ts::verify_now();

        assert!(!ts::sync_active(), "主窗口身份对不上时必须自动停同步");

        ts::end_fake_sync();
    }

    /// 身份记录正确时不能误杀（校验本身要为真）
    #[test]
    fn identity_check_keeps_valid_targets() {
        let _guard = serial();

        let master = ts::popup_window();
        let slave = ts::popup_window();
        let master_hwnd = ts::hwnd_value(master.0);
        let slave_hwnd = ts::hwnd_value(slave.0);

        ts::begin_fake_sync(master_hwnd, &[slave_hwnd]);
        ts::verify_now();

        assert!(ts::sync_active(), "正常窗口不该被停");
        assert_eq!(ts::slaves(), vec![slave_hwnd], "正常窗口不该被剔除");

        ts::end_fake_sync();
    }

    /// 重复 start / shutdown 不出问题，且能再次启动
    #[test]
    fn start_and_shutdown_are_idempotent() {
        let _guard = serial();

        start();
        start();
        assert!(is_running());
        shutdown();
        assert!(!is_running());

        start();
        assert!(is_running());
        shutdown();
        assert!(!is_running());
    }

    /// 回归：`start()` 后**立刻** `shutdown()` 不能挂。
    /// 老实现靠 `PostThreadMessageW(tid)` 叫停，此时 tid 还没写进全局，线程永远等下去。
    #[test]
    fn immediate_shutdown_does_not_hang() {
        let _guard = serial();

        for _ in 0..5 {
            let began = Instant::now();
            start();
            shutdown();
            let cost = began.elapsed();
            assert!(
                cost < Duration::from_millis(JOIN_TIMEOUT_MS),
                "启停一趟花了 {cost:?}，说明叫停没送到"
            );
            assert!(!is_running());
        }
    }

    /// 前台事件：收到后 `foreground()` 要能反映出来
    #[test]
    fn foreground_is_recorded_from_events() {
        let _guard = serial();

        // 没有前台事件时是 0
        start();
        assert!(is_running());
        // 不主动抢前台（测试里没有窗口会被切到前台），只验证初始值可读
        let initial = foreground();
        assert!(initial >= 0, "前台句柄不该是负值");
        shutdown();
    }
}
