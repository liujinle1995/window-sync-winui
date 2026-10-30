using Microsoft.UI.Xaml;

namespace LeXiangSync.Models;

/// <summary>用户偏好设置，持久化到本地 JSON。</summary>
public sealed class AppSettings
{
    /// <summary>界面主题。</summary>
    public ElementTheme Theme { get; set; } = ElementTheme.Default;

    /// <summary>窗口背景材质（Windows 11 生效）。</summary>
    public AppBackdrop Backdrop { get; set; } = AppBackdrop.Mica;

    /// <summary>进程名筛选关键字（配置页）。</summary>
    public string ProcessFilter { get; set; } = string.Empty;

    /// <summary>配置页进程列表自动刷新周期（秒），0 表示关闭。</summary>
    public int AutoRefreshSeconds { get; set; } = 5;

    /// <summary>关闭主窗口时最小化到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启用全局热键启停同步。</summary>
    public bool HotKeyEnabled { get; set; } = true;

    /// <summary>
    /// 全局热键的修饰键掩码：MOD_ALT=1 / MOD_CONTROL=2 / MOD_SHIFT=4 / MOD_WIN=8。
    /// 默认 Ctrl + Alt（= 3）。
    /// </summary>
    public uint HotKeyModifiers { get; set; } = 0x0003;

    /// <summary>全局热键的主键虚拟键码，默认 S（0x53）。</summary>
    public uint HotKeyVirtualKey { get; set; } = 0x53;

    /// <summary>开始 / 停止同步时播放提示音（热键操作时尤其需要）。</summary>
    public bool PlayToggleSound { get; set; } = true;

    /// <summary>日志面板是否展开。</summary>
    public bool LogPanelExpanded { get; set; }

    /// <summary>文本镜像：同步主窗口输入的中文 / 粘贴内容。默认开启。</summary>
    public bool SyncTextMirror { get; set; } = true;

    /// <summary>
    /// 悬停同步：鼠标移入主窗口自动开始同步，移出主窗口自动停止。
    /// 手动启停与热键仍然可用。默认关闭。
    /// </summary>
    public bool HoverSyncEnabled { get; set; }

    /// <summary>已忽略的"需要管理员权限"提示。</summary>
    public bool AdminNoticeDismissed { get; set; }

    // ── 窗口位置记忆 ──────────────────────────────────────────────────────

    /// <summary>主窗口位置（设备无关像素）。为 null 表示首次启动，走居中逻辑。</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    /// <summary>主窗口尺寸（设备无关像素），会按工作区夹紧。</summary>
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 780;

    /// <summary>最近一次保存 / 套用的方案 Id，仅作记录用；启动加载始终取第一个。</summary>
    public string? AutoLoadPlanId { get; set; }
}
