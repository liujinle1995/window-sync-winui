//! 会话的建立与拆解。
//!
//! 这组用例盯的是同一件事：**`start_sync()` 中途失败之后，进程里还能不能像没启动过一样。**
//!
//! 老实现在这里是有洞的 —— 线程先起、钩子后装，第二条钩子失败就 `?` 走人，
//! 于是留下三条无人引用的线程、一条装着事件的总线，和「主窗口 = X」这套看着还有效的
//! 全局状态。下面每一条失败路径都在断言「残渣为零」。

use crate::session;
use crate::test_support as ts;
use crate::{start_sync, stop_sync, SyncConfig, BUS, PRESSED, SLAVES};
use std::sync::atomic::Ordering;

fn config(master: i64, slaves: &[i64]) -> SyncConfig {
    SyncConfig {
        master_hwnd: master,
        slave_hwnds: slaves.to_vec(),
        sync_mouse: true,
        sync_keyboard: true,
        sync_clipboard: false,
        text_mirror: true,
    }
}

/// 一个几乎不可能存在的句柄值：`validate_targets` 靠 `IsWindow` 一眼就能否掉。
const DEAD_HWND: i64 = 0x7FFF_FFF0;

/// 进程级独占资源全部归零 —— 回滚是否干净的判据。
fn assert_no_residue(why: &str) {
    assert_eq!(session::live_hooks(), 0, "{why}：低级钩子没摘干净");
    assert!(!ts::sync_active(), "{why}：还标着同步中");
    assert!(ts::slaves().is_empty(), "{why}：从窗口列表没清空");
    assert!(BUS.lock().is_none(), "{why}：事件总线还挂在全局");
    assert!(PRESSED.lock().is_empty(), "{why}：按键表没清");
    assert!(
        !crate::text_sync::has_worker(),
        "{why}：文本镜像线程还在跑"
    );
    assert!(
        !crate::window_watch::is_running(),
        "{why}：窗口监听线程还在跑"
    );
}

// ── ① 验证所有窗口 ───────────────────────────────────────────────────────

#[test]
fn a_dead_slave_is_rejected_before_anything_is_created() {
    let _guard = ts::serial();
    let master = ts::popup_window();

    let err = start_sync(config(ts::hwnd_value(master.0), &[DEAD_HWND])).unwrap_err();

    assert!(err.contains("已关闭"), "报错要说得清是哪一步：{err}");
    assert!(err.contains("从窗口"), "报错要指向具体窗口：{err}");
    assert_no_residue("从窗口已失效");
}

#[test]
fn a_dead_master_is_rejected_before_anything_is_created() {
    let _guard = ts::serial();
    let slave = ts::popup_window();

    let err = start_sync(config(DEAD_HWND, &[ts::hwnd_value(slave.0)])).unwrap_err();

    assert!(err.contains("主窗口"), "报错要说得清是哪一步：{err}");
    assert_no_residue("主窗口已失效");
}

// ── ③ / ④ 失败回滚 ───────────────────────────────────────────────────────

/// 第一条钩子就失败：连钩子都没装上，剩下的更不该存在。
#[test]
fn a_failed_keyboard_hook_leaves_nothing_behind() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();

    session::inject_failure(session::FAIL_KEYBOARD_HOOK);
    let err = start_sync(config(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    ))
    .unwrap_err();
    assert!(err.contains("注入的失败"), "应当是注入的那一步失败：{err}");

    assert_no_residue("键盘钩子失败");
}

/// 用户报的那个场景：键盘钩子装上了，鼠标钩子失败。
///
/// 老实现到这里直接 `return Err`，键盘钩子留在系统里 —— 之后每一次按键都要
/// 绕一遍这个进程的回调，哪怕同步早就没在跑了。
#[test]
fn a_failed_mouse_hook_also_takes_the_keyboard_hook_down() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();

    session::inject_failure(session::FAIL_MOUSE_HOOK);
    let err = start_sync(config(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    ))
    .unwrap_err();
    assert!(err.contains("注入的失败"), "应当是注入的那一步失败：{err}");

    assert_no_residue("鼠标钩子失败");
}

/// 钩子都装上了，线程那一步才失败 —— 两条钩子同样要摘掉。
#[test]
fn a_failed_worker_start_rolls_both_hooks_back() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();

    session::inject_failure(session::FAIL_WORKERS);
    let err = start_sync(config(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    ))
    .unwrap_err();
    assert!(err.contains("注入的失败"), "应当是注入的那一步失败：{err}");

    assert_no_residue("工作线程启动失败");
}

/// 注入只生效一次：失败之后紧接着重试，必须能正常起来。
#[test]
fn a_retry_after_a_failed_start_succeeds() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();
    let cfg = || {
        config(
            ts::hwnd_value(master.0),
            &[ts::hwnd_value(slave.0)],
        )
    };

    session::inject_failure(session::FAIL_MOUSE_HOOK);
    assert!(start_sync(cfg()).is_err());
    assert_no_residue("首次失败");

    start_sync(cfg()).expect("失败一次之后重试应当成功");
    assert!(ts::sync_active());
    assert_eq!(session::live_hooks(), 2, "键盘 + 鼠标各一条");

    stop_sync().unwrap();
    assert_no_residue("重试成功后再停止");
}

// ── 正常路径 ─────────────────────────────────────────────────────────────

#[test]
fn a_healthy_session_owns_every_resource_and_releases_all_of_them() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();

    start_sync(config(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    ))
    .unwrap();

    assert!(ts::sync_active());
    assert_eq!(session::live_hooks(), 2, "键盘 + 鼠标");
    assert_eq!(ts::slaves().len(), 1);
    assert!(crate::text_sync::has_worker(), "文本镜像线程应当已起");
    assert!(crate::window_watch::is_running(), "窗口监听应当已起");

    stop_sync().unwrap();

    assert_no_residue("正常停止");
}

/// 连着点两次启动：第二套必须替换第一套，而不是叠一层。
#[test]
fn starting_again_replaces_the_previous_session() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();
    let cfg = || {
        config(
            ts::hwnd_value(master.0),
            &[ts::hwnd_value(slave.0)],
        )
    };

    start_sync(cfg()).unwrap();
    start_sync(cfg()).unwrap();

    assert_eq!(
        session::live_hooks(),
        2,
        "重启没有把上一套的钩子摘掉，叠成四条了"
    );

    stop_sync().unwrap();
    assert_no_residue("连续启动后停止");
}

#[test]
fn stopping_when_nothing_is_running_is_a_no_op() {
    let _guard = ts::serial();

    stop_sync().unwrap();
    stop_sync().unwrap();

    assert_no_residue("空转停止");
}

// ── 文本镜像线程的代次回收 ────────────────────────────────────────────────

/// 「开 → 关」必须把线程收回来，不能留一条在空转。
#[test]
fn shutdown_always_reclaims_the_text_worker() {
    let _guard = ts::serial();

    for round in 0..40 {
        crate::text_sync::set_enabled(true);
        assert!(
            crate::text_sync::has_worker(),
            "第 {round} 轮：打开之后没有线程"
        );

        crate::text_sync::shutdown();
        assert!(
            !crate::text_sync::has_worker(),
            "第 {round} 轮：关掉之后线程还活着"
        );
        assert!(!crate::text_sync::is_enabled(), "第 {round} 轮：开关没关");
    }
}

/// 不等线程真正进入循环就连着切两次开关 —— 老实现里这一步会让「新会话以为
/// 线程已经起过」，于是从此一个采样都不做。现在靠代次判断，切几次都还有线程。
#[test]
fn a_quick_toggle_still_leaves_a_live_worker() {
    let _guard = ts::serial();

    for round in 0..40 {
        crate::text_sync::set_enabled(true);
        crate::text_sync::set_enabled(false);
        crate::text_sync::set_enabled(true);

        assert!(
            crate::text_sync::has_worker(),
            "第 {round} 轮：重新打开之后没有线程"
        );

        crate::text_sync::shutdown();
        assert!(
            !crate::text_sync::has_worker(),
            "第 {round} 轮：停止之后线程没退"
        );
    }
}

/// 会话停止时，文本镜像和窗口监听都要跟着收 —— 它们不在总线上的三条线程里。
#[test]
fn stopping_the_session_also_reclaims_the_mirror_and_the_monitor() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();

    start_sync(config(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    ))
    .unwrap();
    assert!(crate::text_sync::has_worker());
    assert!(crate::window_watch::is_running());

    stop_sync().unwrap();

    assert!(!crate::text_sync::has_worker(), "文本镜像线程没收");
    assert!(!crate::window_watch::is_running(), "窗口监听没收");
    assert!(!crate::text_sync::is_enabled(), "文本镜像开关没关");
}

// ── 主从对调 ─────────────────────────────────────────────────────────────

/// 没在同步时不能对调：这时「主窗口」这个概念还不存在，
/// 宿主只要改自己的选择，下次 `start_sync` 会按新配置重建一切。
#[test]
fn swapping_outside_a_session_is_rejected() {
    let _guard = ts::serial();

    let err = crate::swap_master(1234).unwrap_err();
    assert!(err.contains("未启动"), "报错要能映射成 NOT_RUNNING：{err}");
    assert_no_residue("空转对调");
}

#[test]
fn swapping_needs_a_window_that_is_actually_a_slave() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();
    let bystander = ts::popup_window();
    let m = ts::hwnd_value(master.0);
    let s = ts::hwnd_value(slave.0);

    start_sync(config(m, &[s])).unwrap();

    // 一个既不是主窗口、也不在从窗口列表里的窗口
    let err = crate::swap_master(ts::hwnd_value(bystander.0)).unwrap_err();
    assert!(err.contains("不是从窗口"), "{err}");
    assert_eq!(ts::sync_active(), true, "参数错了不该把同步弄停");
    assert_eq!(crate::MASTER_HWND.load(Ordering::Relaxed), m, "主窗口不该变");

    // 拿主窗口自己去换自己
    let err = crate::swap_master(m).unwrap_err();
    assert!(err.contains("已经是主窗口"), "{err}");

    stop_sync().unwrap();
}

#[test]
fn swapping_exchanges_roles_without_restarting_the_session() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave1 = ts::popup_window();
    let slave2 = ts::popup_window();
    let m = ts::hwnd_value(master.0);
    let s1 = ts::hwnd_value(slave1.0);
    let s2 = ts::hwnd_value(slave2.0);

    start_sync(config(m, &[s1, s2])).unwrap();
    let hooks_before = session::live_hooks();

    crate::swap_master(s1).expect("对调应当成功");

    assert_eq!(
        crate::MASTER_HWND.load(Ordering::Relaxed),
        s1,
        "被选中的从窗口应当成为主窗口"
    );
    assert_eq!(
        ts::slaves(),
        vec![s2, m],
        "旧主应当补到列表末尾，其余从窗口的序号不变"
    );
    assert_eq!(
        session::live_hooks(),
        hooks_before,
        "对调不该重装钩子（那就是重启了）"
    );
    assert!(ts::sync_active(), "对调过程中同步不该停");
    assert!(
        crate::text_sync::has_worker(),
        "文本镜像线程不该被带着一起停掉"
    );

    stop_sync().unwrap();
    assert_no_residue("对调后停止");
}

/// 对调之后**立刻**做一次身份复查 —— 这是最容易漏的一步。
///
/// 身份键不重建的话，一秒后的看门狗会把刚升上来的主窗口判成「句柄被系统复用」，
/// 直接把同步停掉：用户看到的是「切了一下主窗口，同步自己关了」。
#[test]
fn the_new_master_survives_an_identity_check_right_after_swapping() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave1 = ts::popup_window();
    let slave2 = ts::popup_window();
    let m = ts::hwnd_value(master.0);
    let s1 = ts::hwnd_value(slave1.0);
    let s2 = ts::hwnd_value(slave2.0);

    start_sync(config(m, &[s1, s2])).unwrap();
    crate::swap_master(s1).unwrap();

    ts::verify_now();

    assert!(ts::sync_active(), "身份复查把新主窗口判成了失效");
    assert_eq!(crate::MASTER_HWND.load(Ordering::Relaxed), s1);
    assert_eq!(ts::slaves(), vec![s2, m], "从窗口被误剔除了");

    // 反方向再换回来，同样要经得起复查
    crate::swap_master(m).unwrap();
    ts::verify_now();
    assert!(ts::sync_active(), "换回去之后被身份复查停了");
    assert_eq!(ts::slaves(), vec![s2, s1]);

    stop_sync().unwrap();
}

/// 切换前必须把还按着的键放掉，否则旧主窗口变从窗口之后就收不到那次抬起。
#[test]
fn pressed_keys_are_drained_by_the_swap() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();
    let m = ts::hwnd_value(master.0);
    let s = ts::hwnd_value(slave.0);

    start_sync(config(m, &[s])).unwrap();
    PRESSED.lock().key_down(0x41, 0x1E);
    PRESSED.lock().button_down(1);

    crate::swap_master(s).unwrap();

    assert!(
        PRESSED.lock().is_empty(),
        "切换前按着的键没有被放掉，会永久卡在旧窗口里"
    );

    stop_sync().unwrap();
}

/// 反复对调不能把状态搞乱：每换一次，主窗口和从窗口列表都得自洽。
#[test]
fn swapping_back_and_forth_stays_consistent() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();
    let m = ts::hwnd_value(master.0);
    let s = ts::hwnd_value(slave.0);

    start_sync(config(m, &[s])).unwrap();

    for round in 0..8 {
        let (from, to) = if round % 2 == 0 { (s, m) } else { (m, s) };
        crate::swap_master(from).expect("对调应当成功");
        assert_eq!(
            crate::MASTER_HWND.load(Ordering::Relaxed),
            from,
            "第 {round} 轮：主窗口没换成目标"
        );
        assert_eq!(ts::slaves(), vec![to], "第 {round} 轮：从窗口列表不对");
        ts::verify_now();
        assert!(ts::sync_active(), "第 {round} 轮：身份复查把同步停了");
    }

    stop_sync().unwrap();
    assert_no_residue("反复对调后停止");
}


// ── 停止顺序 ─────────────────────────────────────────────────────────────

/// ① 禁止接收新事件必须排在 ② 释放按键之前。
///
/// 反过来做的话，补抬起之后仍可能有排队的 KeyDown 落地，从窗口照样卡键。
/// 这里能观察到的是它的**结果**：停止一到，钩子立刻不再记账，按键表是空的。
#[test]
fn stop_stops_accounting_keys_before_it_releases_them() {
    let _guard = ts::serial();
    let master = ts::popup_window();
    let slave = ts::popup_window();

    start_sync(config(
        ts::hwnd_value(master.0),
        &[ts::hwnd_value(slave.0)],
    ))
    .unwrap();

    // 模拟「有一个键还按着」
    PRESSED.lock().key_down(0x41, 0x1E);
    assert!(!PRESSED.lock().is_empty());

    stop_sync().unwrap();

    assert!(PRESSED.lock().is_empty(), "停止之后按键表必须清空");
    assert!(!ts::sync_active(), "停止之后不能再接事件");
    assert!(
        SLAVES.lock().is_empty(),
        "停止之后窗口状态要清干净"
    );
}
