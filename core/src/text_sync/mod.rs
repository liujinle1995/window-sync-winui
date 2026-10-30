//! 文本同步：把主窗口焦点控件里的文本变化，搬到每个从窗口的焦点控件里。
//!
//! # 为什么不能用键盘钩子解决
//! `WH_KEYBOARD_LL` 只拿得到虚拟键码。中文是输入法组字后由 `WM_IME_COMPOSITION` /
//! `WM_CHAR` 投给窗口的，钩子根本看不见那个字；拿键码去 `PostMessage(WM_KEYDOWN)` 也不会
//! 经过输入法，从窗口永远组不出中文。所以中文只能靠「读文本 → 算差量 → 补投」。
//!
//! # 整条链路
//! ```text
//!   键盘钩子 ─┐                                          ┌─ 从窗口 A（跟踪期望值 / 校验）
//!   剪贴板   ─┼→ 唤醒 ─→ 采样焦点控件 ─→ 差分 ─→ 调度 ─┼─ 从窗口 B
//!   焦点变化 ─┘           (身份+文本+光标)  (单区间)     └─ 从窗口 C
//! ```
//!
//! 四个关键设计，逐条对应老实现踩过的坑：
//!
//! 1. **快照绑定控件身份**（[`ControlId`]）。焦点从输入框 A 换到 B 时，即使 B 的文本以
//!    A 的文本为前缀，也只「重设基线、不推差量」—— 老实现会把整个 B 的内容当成
//!    「在 A 里追加的一段」灌过去。
//! 2. **差分是通用的单区间替换**（`diff`）。中间插字、移动光标后编辑、全选替换、
//!    输入法上屏都能算出来，不再只有「末尾追加/删除」两种形状。
//! 3. **每个从窗口独立跟踪**。知道从窗口现在是什么，才能判断它是「落后」「已同步」
//!    还是「被改乱了」，而不是闷头再补一遍。
//! 4. **读不到 ≠ 没输入**（[`ReadResult`]）。空文本、自绘控件、进程无响应是三种状态，
//!    只有第一种才允许说「没有变化」。
//!
//! # 一个必须解决的问题：重复插入
//! 键盘钩子已经把 `WM_KEYDOWN` 转给从窗口了，从窗口自己的消息循环会 `TranslateMessage`
//! 出 `WM_CHAR` 并插入字符。如果文本同步再把同一段文本补一遍，就是**打一个字出两个字**。
//!
//! 解法在 `plan` 的第一条：先读从窗口的文本，**它已经等于目标文本就什么都不做**
//! （`AlreadyInSync`）。这条同时把「从窗口后启动、落后一截」也区分出来 ——
//! 落后才需要补，对齐了就不能动。

mod apply;
mod control;
mod diff;
#[cfg(test)]
mod pipeline_tests;
#[cfg(test)]
mod win_tests;

pub use control::{Capability, ControlId, ReadResult};

use diff::Edit;
use once_cell::sync::Lazy;
use parking_lot::{Condvar, Mutex};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::thread::{self, JoinHandle};
use std::time::Duration;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, IsWindow};

/// 有待处理输入时的采样间隔。键盘钩子会直接唤醒，实际延迟通常远小于这个值
const POLL_FAST_MS: u64 = 30;
/// 空闲时的采样间隔
const POLL_IDLE_MS: u64 = 120;
/// 发现文本变化后再等一小会儿重读，把输入法组字 / 连打 / 粘贴的中间态合并掉
const SETTLE_MS: u64 = 20;
const SETTLE_ROUNDS: usize = 3;
/// 超过这个规模的改动不盲投给读不到文本的从窗口
const INCREMENTAL_MAX: usize = 64;
/// 转发了这么多次可打印按键、文本却一次没动 → 判定为读不到的自绘输入框
const SUSPECT_KEYS: usize = 8;
/// 空闲时每这么多拍做一次一致性校正（≈1 秒）
const PARITY_EVERY: u32 = 8;

static ENABLED: AtomicBool = AtomicBool::new(false);
static RUNNING: AtomicBool = AtomicBool::new(false);
/// 自上次文本变化以来，转发的可打印按键次数
static PRINTABLE_KEYS: AtomicUsize = AtomicUsize::new(0);
static WAKE: Lazy<(Mutex<bool>, Condvar)> = Lazy::new(|| (Mutex::new(false), Condvar::new()));
static MASTER: Lazy<Mutex<Master>> = Lazy::new(|| Mutex::new(Master::default()));
static SLAVES: Lazy<Mutex<Vec<SlaveTrack>>> = Lazy::new(|| Mutex::new(Vec::new()));
static WORKER: Lazy<Mutex<Option<JoinHandle<()>>>> = Lazy::new(|| Mutex::new(None));

/// 主窗口当前焦点控件的状态
#[derive(Default)]
struct Master {
    id: Option<ControlId>,
    /// 基线：上次同步到的文本与光标
    text: Vec<u16>,
    caret: usize,
    /// 文本版本号，每采纳一次改动 +1；从窗口靠它判断自己落到了哪一版
    revision: u64,
    /// 「这个控件读不到文本」只提示一次
    warned_unreadable: bool,
    /// 「类名像输入框但文本从不变化」只提示一次
    warned_suspect: bool,
    parity_ticks: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum SlaveStatus {
    /// 已和主窗口对齐
    Synced,
    /// 读得到、但内容解释不了（多半被用户直接改过），已放弃自动校正
    Diverged,
    /// 读不到文本，只能盲投末尾增量，无法校验
    Unverified,
}

/// 每个从窗口独立跟踪：它现在应该是什么、对齐到了哪个版本
struct SlaveTrack {
    top: isize,
    expected: Vec<u16>,
    synced_revision: u64,
    failures: u32,
    status: SlaveStatus,
    warned: bool,
}

/// 单个从窗口的处理方式
#[derive(Debug, PartialEq, Eq)]
enum Action {
    /// 从窗口已经是目标文本：**必须什么都不做**，否则就是重复插入
    AlreadyInSync,
    /// 末尾改动，逐字补。
    /// **只用于读不到文本的从窗口** —— 读得到的一律整段重写，理由见 `plan` 的说明。
    Incremental,
    /// 整段重写
    ReplaceAll,
    /// 不能安全处理
    Skip(SkipReason),
}

#[derive(Debug, PartialEq, Eq)]
enum SkipReason {
    /// 读不到从窗口文本，且这次改动不是末尾增量
    Unreadable,
    /// 读得到，但内容既不是旧文本也不是新文本，也解释不成「落后」
    Diverged,
}

// ── 对外接口 ─────────────────────────────────────────────────────────────

/// 开关文本同步，返回状态是否真的变化了。关闭时清空全部基线。
pub fn set_enabled(on: bool) -> bool {
    if ENABLED.swap(on, Ordering::SeqCst) == on {
        // 值没变也不能假设线程还在：宿主可能在「启动同步」之前就先设过一次开关，
        // 也可能上一次的线程已经退掉。这里补一次 spawn（幂等），避免出现
        // 「开关是开的、线程却没了」的静默失效。
        if on {
            spawn();
        }
        return false;
    }
    reset_state();
    if on {
        spawn();
    } else {
        wake();
    }
    true
}

pub fn is_enabled() -> bool {
    ENABLED.load(Ordering::Relaxed)
}

/// 键盘钩子每转发一个键调一次：唤醒调度器；可打印字符额外计入「疑似自绘」统计。
pub fn note_key(vk: u32) {
    if !RUNNING.load(Ordering::Relaxed) {
        return;
    }
    if is_printable_vk(vk) {
        PRINTABLE_KEYS.fetch_add(1, Ordering::Relaxed);
    }
    wake();
}

/// 剪贴板 / 焦点之类的其它触发源：只唤醒，不计数
pub fn note_input() {
    if RUNNING.load(Ordering::Relaxed) {
        wake();
    }
}

/// 停止同步时调用：让工作线程立刻退出并等它结束。
pub fn shutdown() {
    RUNNING.store(false, Ordering::SeqCst);
    wake();
    if let Some(handle) = WORKER.lock().take() {
        let _ = handle.join();
    }
    reset_state();
}

/// 大致判断这个虚拟键会不会打出字符。只用于「自绘输入框」的嫌疑统计，不要求精确。
pub fn is_printable_vk(vk: u32) -> bool {
    matches!(vk,
        0x20                    // 空格
        | 0x30..=0x39           // 数字
        | 0x41..=0x5A           // 字母
        | 0x60..=0x69           // 小键盘数字
        | 0xBA..=0xC0           // ; = , - . /
        | 0xDB..=0xDE           // [ \ ] '
    )
}

// ── 线程与唤醒 ───────────────────────────────────────────────────────────

fn spawn() {
    if RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }
    *WORKER.lock() = Some(thread::spawn(run));
}

fn wake() {
    let (lock, cv) = &*WAKE;
    *lock.lock() = true;
    cv.notify_all();
}

/// 睡到被唤醒或者超时。用条件变量而不是裸 sleep，是为了让 `note_key` 和 `shutdown`
/// 能立刻把线程叫醒 —— 前者抹掉最多 120ms 的输入延迟，后者让停止同步不用干等一个周期。
fn wait(ms: u64) {
    let (lock, cv) = &*WAKE;
    let mut pending = lock.lock();
    if !*pending {
        cv.wait_for(&mut pending, Duration::from_millis(ms));
    }
    *pending = false;
}

fn reset_state() {
    *MASTER.lock() = Master::default();
    SLAVES.lock().clear();
    PRINTABLE_KEYS.store(0, Ordering::Relaxed);
}

fn run() {
    let mut sleep_ms = POLL_IDLE_MS;
    while RUNNING.load(Ordering::SeqCst) {
        wait(sleep_ms);
        if !RUNNING.load(Ordering::SeqCst) {
            break;
        }
        if !ENABLED.load(Ordering::Relaxed) {
            sleep_ms = POLL_IDLE_MS;
            continue;
        }
        let changed = tick();
        sleep_ms = if changed { POLL_FAST_MS } else { POLL_IDLE_MS };
    }
}

// ── 一拍 ─────────────────────────────────────────────────────────────────

/// 采样一次并处理。返回是否观察到文本变化 —— 决定下一拍快跑还是慢跑。
fn tick() -> bool {
    let Some((master, slaves)) = crate::text_sync_targets() else {
        return false;
    };

    // 用户不在主窗口上就完全不采样：切回来时基线还在，差量照样算得出来。
    // 这条同时保证「剪贴板中转」切换焦点的过程中不会误采到从窗口的文本。
    if unsafe { GetForegroundWindow() } != master {
        return false;
    }

    let Some(id) = control::focus_id(master) else {
        log_rare("文本同步：主窗口没有焦点子控件（焦点在顶层窗口上），这一拍不采样");
        return false;
    };

    let target = HWND(id.hwnd as *mut _);
    match control::read(target) {
        ReadResult::Text { text, caret, .. } => observe(id, target, text, caret, &slaves),

        ReadResult::NotText => {
            warn_unreadable(&id);
            MASTER.lock().id = Some(id);
            false
        }

        ReadResult::Unresponsive => {
            // 对方卡住：保留基线不动，下一拍照旧重试。绝不能当成「文本变空了」
            log_rare(&format!("文本同步：{} 无响应，本次跳过", id.describe()));
            false
        }

        ReadResult::Gone => {
            // 控件销毁了：清掉身份，等下一拍按新控件重设基线
            MASTER.lock().id = None;
            false
        }
    }
}

/// 拿到一次读取结果之后的所有决策都集中在这里。
///
/// 单独抽出来是为了能被测试直接驱动：`tick` 里那句「必须在主窗口上」的前台判断
/// 在测试进程里永远不成立，但这条管线本身（基线 → settle → 差分 → 分发 → 提交）
/// 恰恰是最该端到端验证的部分。
fn observe(
    id: ControlId,
    target: HWND,
    mut text: Vec<u16>,
    mut caret: usize,
    slaves: &[HWND],
) -> bool {
    // 基线快照。持锁时间要短 —— 后面的 settle 会 sleep
    let (same_control, base_text, base_revision) = {
        let m = MASTER.lock();
        (m.id.as_ref() == Some(&id), m.text.clone(), m.revision)
    };

    if !same_control {
        adopt_baseline(id, text, caret);
        return false;
    }

    if text == base_text {
        // 文本一点没动，但可打印按键已经转发了这么多次 —— 是读不到，不是没输入
        let forwarded = PRINTABLE_KEYS.load(Ordering::Relaxed);
        if forwarded >= SUSPECT_KEYS {
            warn_suspect(&id, forwarded);
        }
        maybe_parity(slaves, &base_text);
        return false;
    }

    // 合并中间态：输入法上屏、连打、粘贴都会在一瞬间产生好几次文本变化
    for _ in 0..SETTLE_ROUNDS {
        thread::sleep(Duration::from_millis(SETTLE_MS));
        match control::read(target) {
            ReadResult::Text { text: again, caret: caret2, .. } => {
                if again == text {
                    break;
                }
                text = again;
                caret = caret2;
            }
            _ => break,
        }
    }

    // settle 期间可能被 set_enabled(false) 或换控件清过基线
    if MASTER.lock().revision != base_revision {
        return false;
    }

    let Some(edit) = diff::diff(&base_text, &text) else {
        // 变回去了（输入法先上屏又撤回），不改基线也不推
        return false;
    };

    let next_revision = base_revision + 1;
    apply_edit(slaves, &edit, &base_text, &text, caret, next_revision);
    commit_baseline(text, caret, next_revision);
    true
}

fn commit_baseline(text: Vec<u16>, caret: usize, revision: u64) {
    let mut m = MASTER.lock();
    m.text = text;
    m.caret = caret;
    m.revision = revision;
    m.parity_ticks = 0;
    m.warned_suspect = false;
    PRINTABLE_KEYS.store(0, Ordering::Relaxed);
}

/// 换了焦点控件：把当前文本当成新基线，**不推任何差量**。
fn adopt_baseline(id: ControlId, text: Vec<u16>, caret: usize) {
    let count = text.len();
    {
        let mut m = MASTER.lock();
        crate::log(&format!(
            "文本同步：焦点切到 {}，重设基线（{count} 字符，本次不推差量）",
            id.describe()
        ));
        m.id = Some(id);
        m.text = text;
        m.caret = caret;
        m.revision += 1;
        m.parity_ticks = 0;
        m.warned_suspect = false;
        m.warned_unreadable = false;
    }
    // 换了控件，从窗口的期望值全部作废：下一轮改动会走整段重写把两边对齐
    SLAVES.lock().clear();
    PRINTABLE_KEYS.store(0, Ordering::Relaxed);
}

fn warn_unreadable(id: &ControlId) {
    let mut m = MASTER.lock();
    if m.warned_unreadable {
        return;
    }
    m.warned_unreadable = true;
    drop(m);
    crate::log(&format!(
        "文本同步：{} 读不到文本内容，不做文本差量 —— 注意这只说明读不到，\
         不代表用户没有输入；这类窗口请用「剪贴板中转」通道",
        id.describe()
    ));
}

fn warn_suspect(id: &ControlId, forwarded: usize) {
    let mut m = MASTER.lock();
    if m.warned_suspect {
        return;
    }
    m.warned_suspect = true;
    drop(m);
    crate::log(&format!(
        "文本同步：{} 已转发 {forwarded} 次按键但文本从未变化，判定为自绘输入框，\
         文本差量对它无效（游戏请用「剪贴板中转」通道）",
        id.describe()
    ));
}

// ── 从窗口调度 ───────────────────────────────────────────────────────────

/// 把一次改动分发到每个从窗口，并各自记账。
fn apply_edit(slaves: &[HWND], edit: &Edit, old: &[u16], new: &[u16], caret: usize, revision: u64) {
    let mut tracks = SLAVES.lock();
    sync_tracks(&mut tracks, slaves);

    for track in tracks.iter_mut() {
        // 每个从窗口独立解析焦点控件：它可能和主窗口停在不同控件上
        let resolved = control::focus_id(HWND(track.top as *mut _));
        let (target, actual) = match resolved {
            Some(id) => {
                let h = HWND(id.hwnd as *mut _);
                (Some(h), control::read(h).text().map(|t| t.to_vec()))
            }
            None => (None, None),
        };

        match plan(edit, old, new, actual.as_deref()) {
            Action::AlreadyInSync => {
                // 键盘通道已经把它带过去了
                mark_synced(track, new, revision);
            }

            Action::Incremental => {
                if let Some(h) = target {
                    // 先删后插，顺序不能反（否则删掉的是刚插进去的字）
                    if edit.removed > 0 {
                        apply::post_backspaces(h, edit.removed);
                    }
                    if !edit.inserted.is_empty() {
                        apply::post_chars(h, &edit.inserted);
                    }
                }
                // PostMessage 是异步的，这一拍校验不了，交给空闲时的校正兜底
                mark_synced(track, new, revision);
                if actual.is_none() {
                    track.status = SlaveStatus::Unverified;
                }
            }

            Action::ReplaceAll => {
                let ok = target
                    .map(|h| replace_verified(h, new, caret))
                    .unwrap_or(false);
                if ok {
                    mark_synced(track, new, revision);
                } else {
                    track.failures += 1;
                    track.status = SlaveStatus::Diverged;
                    log_rare(&format!(
                        "文本同步：从窗口 0x{:X} 整段重写后校验仍不一致（累计 {} 次）",
                        track.top, track.failures
                    ));
                }
            }

            Action::Skip(reason) => {
                track.status = match reason {
                    SkipReason::Diverged => SlaveStatus::Diverged,
                    SkipReason::Unreadable => SlaveStatus::Unverified,
                };
                if !track.warned {
                    track.warned = true;
                    let why = match reason {
                        SkipReason::Diverged => {
                            "内容与主窗口不一致，且解释不成「落后」，跳过自动校正"
                        }
                        SkipReason::Unreadable => {
                            "读不到文本、这次改动又不在末尾，无法安全盲投"
                        }
                    };
                    crate::log(&format!("文本同步：从窗口 0x{:X} {why}", track.top));
                }
            }
        }
    }
}

fn mark_synced(track: &mut SlaveTrack, new: &[u16], revision: u64) {
    track.expected = new.to_vec();
    track.synced_revision = revision;
    track.failures = 0;
    track.status = SlaveStatus::Synced;
}

/// 整段重写 + 校验；失败自动重试一次。
///
/// 重写走的是 `SendMessage`（同步），写完立刻读回来就能判定成败 —— 这是「失败重试」
/// 能真正生效的前提：异步投递的话根本不知道对方到底写没写进去。
fn replace_verified(target: HWND, new: &[u16], caret: usize) -> bool {
    for attempt in 0..2 {
        if attempt > 0 {
            thread::sleep(Duration::from_millis(15));
        }
        if apply::replace_all(target, new, caret) && verify(target, new) {
            return true;
        }
    }
    false
}

fn verify(target: HWND, expected: &[u16]) -> bool {
    control::read(target).text().map(|t| t == expected).unwrap_or(false)
}

/// 让从窗口跟踪表跟当前从窗口列表对齐
fn sync_tracks(tracks: &mut Vec<SlaveTrack>, slaves: &[HWND]) {
    tracks.retain(|t| slaves.iter().any(|h| h.0 as isize == t.top));
    for h in slaves {
        let top = h.0 as isize;
        if tracks.iter().any(|t| t.top == top) {
            continue;
        }
        tracks.push(SlaveTrack {
            top,
            expected: Vec::new(),
            synced_revision: 0,
            failures: 0,
            status: SlaveStatus::Unverified,
            warned: false,
        });
    }
}

/// 空闲时的一致性校正：主窗口文本没变，但从窗口可能落后（后启动的、漏过一次的）。
///
/// 只在「能从落后解释得通」时才写，避免把从窗口上用户自己的内容抹掉。
fn maybe_parity(slaves: &[HWND], new: &[u16]) {
    let (due, revision, caret) = {
        let mut m = MASTER.lock();
        m.parity_ticks = m.parity_ticks.wrapping_add(1);
        (m.parity_ticks % PARITY_EVERY == 0, m.revision, m.caret)
    };
    if !due {
        return;
    }

    let mut tracks = SLAVES.lock();
    sync_tracks(&mut tracks, slaves);

    for track in tracks.iter_mut() {
        // 期望值已经对齐，省一次跨进程读取
        if track.status == SlaveStatus::Synced && track.expected == new {
            continue;
        }

        let Some(top) = alive(track.top) else { continue };
        let Some(control) = control::focus_id(top) else { continue };
        let target = HWND(control.hwnd as *mut _);

        let Some(actual) = control::read(target).text().map(|t| t.to_vec()) else {
            continue;
        };
        if actual == new {
            mark_synced(track, new, revision);
            continue;
        }

        if !looks_behind(new, &actual) {
            if !track.warned {
                track.warned = true;
                crate::log(&format!(
                    "文本同步：从窗口 0x{:X} 内容与主窗口不一致且解释不成「落后」\
                     （它对齐在版本 {}，主窗口是 {revision}），不再自动校正",
                    track.top, track.synced_revision
                ));
            }
            track.status = SlaveStatus::Diverged;
            continue;
        }

        if replace_verified(target, new, caret) {
            mark_synced(track, new, revision);
        } else {
            track.failures += 1;
        }
    }
}

fn alive(top: isize) -> Option<HWND> {
    let h = HWND(top as *mut _);
    if unsafe { IsWindow(h).as_bool() } {
        Some(h)
    } else {
        None
    }
}

// ── 纯决策 ───────────────────────────────────────────────────────────────

/// 能否把「从窗口现在的文本」解释成「主窗口文本的落后版本」。
///
/// 只允许前缀关系：`abc` 落后于 `abcd` 是合理的（漏了最后一下），
/// 但 `xyz` 和 `abc` 之间毫无关系，那多半是用户自己在从窗口上改的，不该被覆盖。
fn looks_behind(expected: &[u16], actual: &[u16]) -> bool {
    expected.starts_with(actual) || actual.starts_with(expected)
}

/// 决定这个从窗口怎么处理。`slave = None` 表示读不到它的文本。
///
/// 读得到的从窗口一律走整段重写，**不用逐字补**。原因是逐字补是相对操作：
/// 我们读从窗口文本的那一刻，键盘钩子转发过去的 `WM_CHAR` 可能还压在对方队列里，
/// 我们判成「落后」补一遍，对方随后再处理那一下，就多出一个字。整段重写是绝对赋值，
/// 不管中间夹了多少条待处理消息，结果都是同一份文本。
///
/// 读不到的从窗口没有这个选择，只能盲投末尾增量 —— 那也是唯一能用的通道。
fn plan(edit: &Edit, old: &[u16], new: &[u16], slave: Option<&[u16]>) -> Action {
    match slave {
        Some(actual) => {
            // 已经是目标文本 —— 键盘通道已经送过去了，再写一遍是白费
            if actual == new {
                return Action::AlreadyInSync;
            }
            // 等于旧文本（我们知道它原来是什么），或者能解释成「落后」
            if actual == old || looks_behind(new, actual) {
                Action::ReplaceAll
            } else {
                Action::Skip(SkipReason::Diverged)
            }
        }
        // 读不到：只有末尾增量敢盲投，整段盲写会把对方已有内容冲掉
        None => {
            if edit.is_tail_only(old.len()) && edit.size() <= INCREMENTAL_MAX {
                Action::Incremental
            } else {
                Action::Skip(SkipReason::Unreadable)
            }
        }
    }
}

fn log_rare(msg: &str) {
    static LAST: Lazy<Mutex<Option<std::time::Instant>>> = Lazy::new(|| Mutex::new(None));
    let mut last = LAST.lock();
    let now = std::time::Instant::now();
    if let Some(t) = *last {
        if now.duration_since(t) < Duration::from_secs(2) {
            return;
        }
    }
    *last = Some(now);
    drop(last);
    crate::log(msg);
}

#[cfg(test)]
mod tests {
    use super::diff::{diff, to_units};
    use super::*;

    fn u(s: &str) -> Vec<u16> {
        to_units(s)
    }

    fn act(old: &str, new: &str, slave: Option<&str>) -> Action {
        let o = u(old);
        let n = u(new);
        let e = diff(&o, &n).expect("文本不同必须有改动");
        let s = slave.map(u);
        plan(&e, &o, &n, s.as_deref())
    }

    // ── 重复插入的防线 ──────────────────────────────────────────────────

    /// 键盘钩子已经把字送进从窗口了，文本同步绝不能再来一遍
    #[test]
    fn slave_already_synced_is_left_alone() {
        assert_eq!(act("ab", "abc", Some("abc")), Action::AlreadyInSync);
    }

    /// 从窗口等于旧文本、且是末尾追加 —— 读得到的从窗口仍然要走整段重写
    /// （逐字补是相对操作，会和键盘通道抢同一个字）
    #[test]
    fn slave_on_old_text_still_gets_a_full_replace() {
        assert_eq!(act("ab", "abc", Some("ab")), Action::ReplaceAll);
        assert_eq!(act("abc", "ab", Some("abc")), Action::ReplaceAll);
    }

    // ── 中间编辑：老实现全部放弃，这里必须能处理 ────────────────────────

    #[test]
    fn middle_insert_on_old_text_is_a_full_replace() {
        assert_eq!(act("ac", "abc", Some("ac")), Action::ReplaceAll);
    }

    #[test]
    fn middle_delete_on_old_text_is_a_full_replace() {
        assert_eq!(act("abcd", "acd", Some("abcd")), Action::ReplaceAll);
    }

    #[test]
    fn select_all_replace_on_old_text_is_a_full_replace() {
        assert_eq!(act("hello", "world", Some("hello")), Action::ReplaceAll);
    }

    /// 从窗口落后一截（后启动 / 漏过一次）→ 靠整段重写追上
    #[test]
    fn slave_that_is_behind_gets_caught_up() {
        assert_eq!(act("ab", "abcd", Some("a")), Action::ReplaceAll);
    }

    /// 从窗口内容跟主窗口毫无关系 → 不覆盖，标记分歧
    #[test]
    fn unrelated_slave_text_is_not_overwritten() {
        assert_eq!(
            act("ab", "abc", Some("xyz")),
            Action::Skip(SkipReason::Diverged)
        );
    }

    // ── 读不到文本的从窗口 ──────────────────────────────────────────────

    #[test]
    fn unreadable_slave_only_accepts_tail_increment() {
        assert_eq!(act("ab", "abc", None), Action::Incremental);
        assert_eq!(act("abc", "ab", None), Action::Incremental);
    }

    #[test]
    fn unreadable_slave_rejects_middle_edits() {
        assert_eq!(act("ac", "abc", None), Action::Skip(SkipReason::Unreadable));
    }

    /// 大段粘贴不盲投：读不到从窗口时整段写会把对方已有内容冲掉
    #[test]
    fn unreadable_slave_rejects_bulk_paste() {
        let big = "x".repeat(INCREMENTAL_MAX + 1);
        assert_eq!(
            act("a", &format!("a{big}"), None),
            Action::Skip(SkipReason::Unreadable)
        );
    }

    // ── 落后判定 ────────────────────────────────────────────────────────

    #[test]
    fn behind_detection() {
        assert!(looks_behind(&u("abcd"), &u("ab")), "少打了尾部算落后");
        assert!(looks_behind(&u("ab"), &u("abcd")), "多了内容也算同一支");
        assert!(looks_behind(&u("abc"), &u("")), "空文本算落后");
        assert!(!looks_behind(&u("abc"), &u("xyz")), "毫无关系不算落后");
        assert!(!looks_behind(&u("abc"), &u("abx")));
    }

    // ── 可打印键判定 ────────────────────────────────────────────────────

    #[test]
    fn printable_key_detection() {
        assert!(is_printable_vk(0x41), "A");
        assert!(is_printable_vk(0x20), "空格");
        assert!(is_printable_vk(0x30), "0");
        assert!(!is_printable_vk(0x1B), "ESC");
        assert!(!is_printable_vk(0x25), "左方向键");
        assert!(!is_printable_vk(0x10), "Shift");
        assert!(!is_printable_vk(0x0D), "回车");
    }
}
