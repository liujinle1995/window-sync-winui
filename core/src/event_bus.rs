//! 分级事件队列 + 按键状态管理。
//!
//! ## 为什么不用一个有界通道
//!
//! 旧实现是 `sync_channel(2048)` + `try_send`：队列一满就**静默丢事件**。鼠标移动本来就多，
//! 很容易把通道塞满，紧接着的「鼠标抬起」「键盘抬起」就被丢掉，从窗口于是永远认为按键还按着
//! —— 表现为角色自己往前走、鼠标一直拖着。提高容量只是把概率推后，不解决问题。
//!
//! 这里按「丢了会不会让从窗口卡死」分级：
//!
//! | 事件 | 处理 |
//! |---|---|
//! | 鼠标移动 | 只保留最新一条（覆盖写），位置本来就是「当前状态」 |
//! | 滚轮 | 累计 delta，投递时一次发出去 |
//! | 键盘按下 / 抬起 | 可靠队列，先进先出，绝不丢 |
//! | 鼠标按下 / 抬起 | 可靠队列，绝不丢 |
//! | 文本同步 | 独立队列 + 独立线程，注入慢也不挡鼠标键盘 |
//! | 状态通知 | 由调用方按内容去重（见 `lib.rs` 的 `emit_status`） |
//!
//! 「可靠」队列的真实增长源只有人的手指，速率极低；洪水全部来自移动事件，而它已经被合并掉了。

use std::collections::{HashMap, VecDeque};
use std::sync::{Condvar, Mutex, MutexGuard};

/// 文本队列上限：Ctrl+C 手动触发，正常就一两笔，堆起来只可能是异常。
const TEXT_QUEUE_CAP: usize = 16;

/// 一条输入事件附带的坐标信息。
///
/// `x / y` 是主窗口**客户区**坐标，`screen_*` 是原始屏幕坐标（滚轮兜底用），
/// `master_*` 是主窗口客户区尺寸（给不同大小的从窗口做比例映射）。
#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub struct Pos {
    pub x: i32,
    pub y: i32,
    pub screen_x: i32,
    pub screen_y: i32,
    pub master_w: i32,
    pub master_h: i32,
}

/// 必须可靠投递的事件。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Reliable {
    /// 键盘按下 / 抬起
    Key { down: bool, vk: u32, scan: u32 },
    /// 鼠标左 / 右 / 中键的按下或抬起（`msg` 是原始窗口消息号）
    Button { msg: u32, pos: Pos },
    /// 把所有还按着的键和鼠标键补一个「抬起」。
    ///
    /// 通过队列投递而不是直接调用，是为了让它排在已入队的按下事件**之后** ——
    /// 先发的 KeyDown 还没投出去就先补抬起，等于没补。
    Release { keys: Vec<(u32, u32)>, buttons: u32 },
}

/// 工作线程要处理的一条任务。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Task {
    Move(Pos),
    Wheel { delta: i32, pos: Pos },
    Reliable(Reliable),
}

#[derive(Default)]
struct WheelAgg {
    delta: i32,
    pos: Pos,
}

#[derive(Default)]
struct Inner {
    reliable: VecDeque<Reliable>,
    latest_move: Option<Pos>,
    wheel: Option<WheelAgg>,
    text: VecDeque<String>,
    closed: bool,
}

impl Inner {
    fn has_input(&self) -> bool {
        // 滚轮净额为 0（向上再向下）不算有待投递内容，否则消费者会被一个空操作反复唤醒
        self.latest_move.is_some()
            || self.wheel.as_ref().is_some_and(|w| w.delta != 0)
            || !self.reliable.is_empty()
    }

    fn is_idle(&self) -> bool {
        !self.has_input() && self.text.is_empty()
    }
}

/// 事件总线：一个锁 + 一个条件变量，两个消费者（输入线程、文本线程）各取各的。
pub struct EventBus {
    inner: Mutex<Inner>,
    cv: Condvar,
}

impl Default for EventBus {
    fn default() -> Self {
        Self::new()
    }
}

impl EventBus {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(Inner::default()),
            cv: Condvar::new(),
        }
    }

    /// 锁中毒也要继续用：这里存的是事件，没有「一半更新」的不变式，
    /// 因为某个线程 panic 就把整条同步链路卡死才是更糟的结果。
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// 鼠标移动：覆盖上一条，不排队。
    pub fn push_move(&self, pos: Pos) {
        self.lock().latest_move = Some(pos);
        self.cv.notify_all();
    }

    /// 滚轮：累计滚动量，位置取最后一次。
    pub fn push_wheel(&self, delta: i16, pos: Pos) {
        let mut g = self.lock();
        let agg = g.wheel.get_or_insert_with(WheelAgg::default);
        agg.delta += delta as i32;
        agg.pos = pos;
        drop(g);
        self.cv.notify_all();
    }

    pub fn push_reliable(&self, ev: Reliable) {
        self.lock().reliable.push_back(ev);
        self.cv.notify_all();
    }

    /// 文本同步进独立队列。返回 `false` 表示队列已满（调用方负责记日志）。
    pub fn push_text(&self, text: String) -> bool {
        let mut g = self.lock();
        if g.text.len() >= TEXT_QUEUE_CAP {
            return false;
        }
        g.text.push_back(text);
        drop(g);
        self.cv.notify_all();
        true
    }

    /// 关闭：不再接收新事件，消费者把剩下的取完就退出。
    pub fn close(&self) {
        self.lock().closed = true;
        self.cv.notify_all();
    }

    pub fn is_closed(&self) -> bool {
        self.lock().closed
    }

    /// 阻塞取一批输入任务：先移动、再滚轮，最后最多 `max_reliable` 条可靠事件。
    ///
    /// 返回 `None` 表示队列已关闭且已排空，消费者可以退出。
    pub fn next_input(&self, max_reliable: usize) -> Option<Vec<Task>> {
        let mut g = self.lock();
        loop {
            if g.has_input() {
                let mut out = Vec::new();

                if let Some(pos) = g.latest_move.take() {
                    out.push(Task::Move(pos));
                }

                if let Some(agg) = g.wheel.take() {
                    if agg.delta != 0 {
                        out.push(Task::Wheel {
                            delta: agg.delta,
                            pos: agg.pos,
                        });
                    }
                }

                for _ in 0..max_reliable {
                    match g.reliable.pop_front() {
                        Some(ev) => out.push(Task::Reliable(ev)),
                        None => break,
                    }
                }

                // 例如滚轮净额为 0：这一轮什么都没剩下，继续等
                if out.is_empty() {
                    continue;
                }
                return Some(out);
            }

            if g.closed {
                return None;
            }

            g = self.cv.wait(g).unwrap_or_else(|e| e.into_inner());
        }
    }

    /// 阻塞取一条文本任务。
    pub fn next_text(&self) -> Option<String> {
        let mut g = self.lock();
        loop {
            if let Some(text) = g.text.pop_front() {
                return Some(text);
            }
            if g.closed {
                return None;
            }
            g = self.cv.wait(g).unwrap_or_else(|e| e.into_inner());
        }
    }

    /// 是否有需要补投的残留输入（停止时用来判断还要不要排一次抬起）。
    pub fn has_pending_input(&self) -> bool {
        self.lock().has_input()
    }

    pub fn is_idle(&self) -> bool {
        self.lock().is_idle()
    }

    #[cfg(test)]
    fn reliable_len(&self) -> usize {
        self.lock().reliable.len()
    }
}

/// 鼠标键位掩码，和 `PressedState.buttons` 一致。
pub const BTN_LEFT: u32 = 0x1;
pub const BTN_RIGHT: u32 = 0x2;
pub const BTN_MIDDLE: u32 = 0x4;

/// 按键状态管理：记着「哪些键 / 鼠标键还按着」。
///
/// 这个表是补投抬起消息的唯一依据。以下四个时机会用到它：
/// 1. 停止同步 —— 队列里可能还有按下的键，全放掉；
/// 2. 主窗口失焦 —— 之后的消息钩子不再转发，不主动放就是永久卡键；
/// 3. 从窗口失效 —— 该窗口不再投递，状态跟着清掉；
/// 4. 系统层面（重启进程、崩溃）—— 无解，只能靠用户重开客户端。
#[derive(Default)]
pub struct PressedState {
    keys: HashMap<u32, u32>, // vk -> scan code
    buttons: u32,
}

impl PressedState {
    pub fn key_down(&mut self, vk: u32, scan: u32) {
        // 自动重复的 KeyDown 会重复 insert，覆盖成最新的扫描码即可
        self.keys.insert(vk, scan);
    }

    pub fn key_up(&mut self, vk: u32) {
        self.keys.remove(&vk);
    }

    pub fn button_down(&mut self, mask: u32) {
        self.buttons |= mask;
    }

    pub fn button_up(&mut self, mask: u32) {
        self.buttons &= !mask;
    }

    pub fn is_empty(&self) -> bool {
        self.keys.is_empty() && self.buttons == 0
    }

    pub fn pressed_key_count(&self) -> usize {
        self.keys.len()
    }

    /// 取走全部按下状态。返回的键按 vk 排序，保证同样的状态产生同样的顺序（便于测试与排障）。
    pub fn drain(&mut self) -> (Vec<(u32, u32)>, u32) {
        let mut keys: Vec<(u32, u32)> = self.keys.drain().collect();
        keys.sort_by_key(|(vk, _)| *vk);
        let buttons = std::mem::take(&mut self.buttons);
        (keys, buttons)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pos(x: i32, y: i32) -> Pos {
        Pos { x, y, ..Default::default() }
    }

    fn key(down: bool, vk: u32) -> Reliable {
        Reliable::Key { down, vk, scan: vk }
    }

    // ── 鼠标移动合并 ─────────────────────────────────────────────────────

    #[test]
    fn moves_are_coalesced_to_the_latest() {
        let bus = EventBus::new();
        bus.push_move(pos(1, 1));
        bus.push_move(pos(2, 2));
        bus.push_move(pos(3, 3));

        let batch = bus.next_input(32).unwrap();
        assert_eq!(batch, vec![Task::Move(pos(3, 3))]);
        assert!(!bus.has_pending_input(), "合并后不应残留旧移动事件");
    }

    #[test]
    fn move_flood_does_not_grow_the_queue() {
        let bus = EventBus::new();
        for i in 0..100_000 {
            bus.push_move(pos(i, i));
        }
        let batch = bus.next_input(32).unwrap();
        assert_eq!(batch.len(), 1);
        assert_eq!(batch[0], Task::Move(pos(99_999, 99_999)));
    }

    // ── 滚轮累计 ─────────────────────────────────────────────────────────

    #[test]
    fn wheel_deltas_accumulate() {
        let bus = EventBus::new();
        bus.push_wheel(120, pos(5, 5));
        bus.push_wheel(120, pos(6, 6));
        bus.push_wheel(-120, pos(7, 7));

        let batch = bus.next_input(32).unwrap();
        assert_eq!(
            batch,
            vec![Task::Wheel { delta: 120, pos: pos(7, 7) }],
            "三笔滚动应合并成一笔净额，位置取最后一次"
        );
    }

    #[test]
    fn wheel_netting_to_zero_is_dropped() {
        let bus = EventBus::new();
        bus.push_wheel(120, pos(1, 1));
        bus.push_wheel(-120, pos(1, 1));

        // 净额为 0 的滚轮没有意义，不能投递；队列应该空着等下一笔
        assert!(!bus.has_pending_input());
    }

    // ── 可靠事件：一条都不能少 ───────────────────────────────────────────

    #[test]
    fn reliable_events_are_never_dropped_and_stay_in_order() {
        let bus = EventBus::new();

        // 模拟「按下 1000 个键再抬起 1000 个键」，同时夹着海量鼠标移动
        for i in 0..1000u32 {
            bus.push_move(pos(i as i32, i as i32));
            bus.push_reliable(key(true, 0x41 + (i % 26)));
            bus.push_reliable(key(false, 0x41 + (i % 26)));
        }
        assert_eq!(bus.reliable_len(), 2000, "可靠事件不应被合并或丢弃");

        let mut got = Vec::new();
        while let Some(batch) = bus.next_input(64) {
            for task in batch {
                if let Task::Reliable(ev) = task {
                    got.push(ev);
                }
            }
            bus.close();
        }

        assert_eq!(got.len(), 2000);
        for i in 0..1000u32 {
            assert_eq!(got[i as usize * 2], key(true, 0x41 + (i % 26)));
            assert_eq!(got[i as usize * 2 + 1], key(false, 0x41 + (i % 26)));
        }
    }

    #[test]
    fn batch_limits_reliable_items_per_round() {
        let bus = EventBus::new();
        for i in 0..10u32 {
            bus.push_reliable(key(true, i));
        }

        let batch = bus.next_input(4).unwrap();
        assert_eq!(batch.len(), 4);
        assert_eq!(bus.reliable_len(), 6);

        let rest = bus.next_input(32).unwrap();
        assert_eq!(rest.len(), 6);
        assert!(!bus.has_pending_input());
    }

    /// 丢「抬起」正是原 bug 的成因：队列满 → 抬起被扔 → 从窗口永久按键。
    /// 这里把移动洪水灌满，再断言抬起仍然送达。
    #[test]
    fn release_survives_input_flood() {
        let bus = EventBus::new();
        bus.push_reliable(Reliable::Button {
            msg: 0x0201, // WM_LBUTTONDOWN
            pos: pos(10, 10),
        });
        for i in 0..50_000 {
            bus.push_move(pos(i, i));
        }
        bus.push_reliable(Reliable::Button {
            msg: 0x0202, // WM_LBUTTONUP
            pos: pos(20, 20),
        });

        let mut buttons = Vec::new();
        while let Some(batch) = bus.next_input(64) {
            for task in batch {
                if let Task::Reliable(Reliable::Button { msg, .. }) = task {
                    buttons.push(msg);
                }
            }
            bus.close();
        }
        assert_eq!(buttons, vec![0x0201u32, 0x0202u32], "按下与抬起都必须送达且顺序正确");
    }

    #[test]
    fn move_is_delivered_before_queued_reliable_events() {
        let bus = EventBus::new();
        bus.push_reliable(key(true, 0x20));
        bus.push_move(pos(7, 8));

        let batch = bus.next_input(8).unwrap();
        assert_eq!(batch[0], Task::Move(pos(7, 8)));
        assert_eq!(batch[1], Task::Reliable(key(true, 0x20)));
    }

    // ── 文本独立队列 ─────────────────────────────────────────────────────

    #[test]
    fn text_has_its_own_queue_and_does_not_block_input() {
        let bus = EventBus::new();
        assert!(bus.push_text("你好".into()));

        // 文本还没被取走，输入队列照样能工作
        bus.push_move(pos(1, 1));
        let input = bus.next_input(8).unwrap();
        assert_eq!(input, vec![Task::Move(pos(1, 1))]);

        assert_eq!(bus.next_text().as_deref(), Some("你好"));
    }

    #[test]
    fn text_queue_is_capped() {
        let bus = EventBus::new();
        for i in 0..TEXT_QUEUE_CAP {
            assert!(bus.push_text(format!("t{i}")));
        }
        assert!(!bus.push_text("overflow".into()), "超出上限应返回 false 让调用方记日志");
    }

    // ── 关闭语义 ─────────────────────────────────────────────────────────

    #[test]
    fn closed_bus_drains_remaining_then_stops() {
        let bus = EventBus::new();
        bus.push_reliable(key(true, 0x41));
        bus.push_text("x".into());
        bus.close();

        // 关闭后已入队的事件仍要投递出去
        let batch = bus.next_input(8).unwrap();
        assert_eq!(batch, vec![Task::Reliable(key(true, 0x41))]);
        assert_eq!(bus.next_text().as_deref(), Some("x"));

        assert!(bus.next_input(8).is_none());
        assert!(bus.next_text().is_none());
    }

    // ── 按键状态管理 ─────────────────────────────────────────────────────

    #[test]
    fn pressed_state_tracks_keys_and_buttons() {
        let mut st = PressedState::default();
        assert!(st.is_empty());

        st.key_down(0x41, 0x1E);
        st.key_down(0x42, 0x30);
        st.button_down(BTN_LEFT);
        assert_eq!(st.pressed_key_count(), 2);

        st.key_up(0x41);
        st.button_up(BTN_LEFT);
        assert_eq!(st.pressed_key_count(), 1);
        assert!(!st.is_empty());

        st.key_up(0x42);
        assert!(st.is_empty());
    }

    #[test]
    fn repeated_key_down_keeps_single_entry() {
        let mut st = PressedState::default();
        st.key_down(0x41, 0x1E);
        st.key_down(0x41, 0x1E); // 系统自动重复
        assert_eq!(st.pressed_key_count(), 1);
    }

    #[test]
    fn drain_returns_all_and_clears() {
        let mut st = PressedState::default();
        st.key_down(0x42, 0x30);
        st.key_down(0x41, 0x1E);
        st.button_down(BTN_LEFT | BTN_RIGHT);

        let (keys, buttons) = st.drain();
        assert_eq!(keys, vec![(0x41, 0x1E), (0x42, 0x30)], "按 vk 排序，输出稳定");
        assert_eq!(buttons, BTN_LEFT | BTN_RIGHT);
        assert!(st.is_empty());
    }

    /// 抬起一个没按下的键不能把别的键状态带坏
    #[test]
    fn stray_key_up_is_harmless() {
        let mut st = PressedState::default();
        st.key_down(0x41, 0x1E);
        st.key_up(0x99);
        assert_eq!(st.pressed_key_count(), 1);
    }
}
