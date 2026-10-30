using System.Text.Json.Serialization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LeXiangSync.Models;

/// <summary>窗口在屏幕上的落位。顺序与页面里 ComboBox 的项顺序一致，不要随意调整。</summary>
public enum LayoutSlot
{
    /// <summary>不参与排列，保持游戏自己弹出的位置。</summary>
    None,

    TopLeft,
    BottomLeft,
    Center,
    TopRight,
    BottomRight,
}

/// <summary>一个 Sandboxie 沙箱实例。名字同时用于在窗口标题里认领属于它的窗口。</summary>
public sealed partial class GameAccount : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>
    /// 所属组名，取值来自 <see cref="GameLaunchConfig.Groups"/>。
    /// 多开通常五个号为一组，切组就是换一批号；留空视为第一个组（旧配置没有组的概念）。
    /// </summary>
    [ObservableProperty]
    public partial string Group { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LayoutSlot Slot { get; set; } = LayoutSlot.None;

    /// <summary>
    /// 给 ComboBox 的 SelectedIndex 用的桥接属性：
    /// WinUI 的 x:Bind 不会在 enum 与 ComboBoxItem 之间做转换，走 int 最省事。
    /// </summary>
    [JsonIgnore]
    public int SlotIndex
    {
        get => (int)Slot;
        set => SetProperty(Slot, (LayoutSlot)value, v => Slot = v);
    }

    /// <summary>
    /// 行内的聚焦按钮要绑到命令上，而 DataTemplate 里 x:Bind 只能够到行数据本身，
    /// 所以由所属视图模型在这里注入命令实例。
    /// </summary>
    [JsonIgnore]
    public ICommand? FocusCommand { get; set; }

    /// <summary>流程跑起来时禁掉，避免抢走前台导致后续点击打偏。</summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial bool IsFocusEnabled { get; set; } = true;

    /// <summary>
    /// 「检查各号状态」查出来的结论，直接显示在行上。
    /// 不落盘 —— 每次启动都会变，存进配置只会留一份过期的。
    /// </summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial string StatusText { get; set; } = "未启动";

    /// <summary>落位的中文名，表格里直接显示。</summary>
    [JsonIgnore]
    public string SlotText => Slot switch
    {
        LayoutSlot.TopLeft => "左上",
        LayoutSlot.BottomLeft => "左下",
        LayoutSlot.Center => "居中",
        LayoutSlot.TopRight => "右上",
        LayoutSlot.BottomRight => "右下",
        _ => "不排列",
    };

    /// <summary>
    /// 三个按钮的相对坐标（开始 / 进入 / 登录）汇总成一行显示。
    /// 坐标本身是全局配置，但表格按行展示，所以由视图模型给每行注入同一份文本。
    /// </summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial string PointText { get; set; } = string.Empty;

    /// <summary>行内「启动 / 重启」链接的命令，由视图模型注入（同 FocusCommand）。</summary>
    [JsonIgnore]
    public ICommand? StartCommand { get; set; }

    /// <summary>行内「关闭」链接的命令。</summary>
    [JsonIgnore]
    public ICommand? CloseCommand { get; set; }

    /// <summary>
    /// 状态三态。颜色交给 XAML 的 {ThemeResource}，不在代码里取画笔 ——
    /// Application 级资源查找按系统主题解析，窗口用 RequestedTheme 强制深色时会取反。
    /// </summary>
    [JsonIgnore]
    public bool IsRunningStatus => StatusText is "运行中" or "已就绪";

    [JsonIgnore]
    public bool IsFailedStatus => StatusText == "启动失败";

    [JsonIgnore]
    public bool IsIdleStatus => !IsRunningStatus && !IsFailedStatus;

    /// <summary>运行中的行操作列写「重启」，其余写「启动」。</summary>
    [JsonIgnore]
    public string ActionText => StatusText == "运行中" ? "重启" : "启动";

    partial void OnSlotChanged(LayoutSlot value) => OnPropertyChanged(nameof(SlotText));

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsRunningStatus));
        OnPropertyChanged(nameof(IsFailedStatus));
        OnPropertyChanged(nameof(IsIdleStatus));
    }
}

/// <summary>窗口内的相对坐标：0.5 表示窗口宽度 / 高度的中间位置。</summary>
public sealed class RelativePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// 多开启动配置。所有"按钮在哪、等多久、排在哪里"都在这里，
/// 游戏更新换了 UI 也不用改代码，页面上调完直接生效。
/// </summary>
public sealed class GameLaunchConfig
{
    /// <summary>Sandboxie-Plus 的 Start.exe。</summary>
    public string SandboxiePath { get; set; } = @"C:\Program Files\Sandboxie-Plus\Start.exe";

    /// <summary>游戏启动器本体，会被塞进各个沙箱里分别运行。</summary>
    public string LauncherPath { get; set; } = @"C:\Program Files\梦幻西游时空\MyPCLauncher_x64r.exe";

    public string LauncherProcessName { get; set; } = "MyPCLauncher_x64r";
    public string GameProcessName { get; set; } = "MyGame_x64r";

    /// <summary>
    /// 按钮定位是否走 OCR。关掉就退回纯固定坐标（加 OCR 之前的行为）。
    /// 认不到文字时会自动回落坐标，所以关掉并不会让流程停下来。
    /// </summary>
    public bool OcrEnabled { get; set; } = true;

    public List<GameAccount> Accounts { get; set; } = new()
    {
        new() { Name = "xly", Group = DefaultGroup, Slot = LayoutSlot.TopLeft },
        new() { Name = "hgs", Group = DefaultGroup, Slot = LayoutSlot.BottomLeft },
        new() { Name = "fc", Group = DefaultGroup, Slot = LayoutSlot.Center },
        new() { Name = "pt", Group = DefaultGroup, Slot = LayoutSlot.TopRight },
        new() { Name = "dfc", Group = DefaultGroup, Slot = LayoutSlot.BottomRight },
    };

    /// <summary>默认组名。旧配置（没有组概念）里的账号都会归到这一组。</summary>
    public const string DefaultGroup = "第一组";

    /// <summary>
    /// 组别清单，顺序即界面上的顺序。切换组只显示、只启动这一组的沙箱 ——
    /// 多开时通常五个号为一组，换一组就是换一批号，不用每次改沙箱名。
    /// 至少保留一个组。
    /// </summary>
    public List<string> Groups { get; set; } = new() { DefaultGroup };

    // ── 启动器 / 游戏界面上三个按钮的相对位置 ──────────────────────────────

    /// <summary>启动器上的「开始游戏」。</summary>
    public RelativePoint StartButton { get; set; } = new() { X = 0.80, Y = 0.85 };

    /// <summary>点「登录游戏」之后弹出的账号选择框里的「进入游戏」。</summary>
    public RelativePoint EnterButton { get; set; } = new() { X = 0.58, Y = 0.62 };

    /// <summary>
    /// 登录界面上的「登录游戏」。注意：点它之后才会弹账号选择框，
    /// 而且中途可能拦一个「登录验证失败，请稍候重试」的弹窗出来。
    /// </summary>
    public RelativePoint LoginButton { get; set; } = new() { X = 0.50, Y = 0.65 };

    /// <summary>
    /// 「登录验证失败，请稍候重试」这类弹窗上「确定」的位置。
    /// 游戏是自绘界面，这种弹窗既没有独立窗口也没有控件，认不出来 ——
    /// 程序只能在登录阶段到这个位置点一下（没弹窗时点的是空白，无副作用）。
    /// 填负数表示不处理这类弹窗。
    /// </summary>
    public RelativePoint ConfirmButton { get; set; } = new() { X = 0.50, Y = 0.47 };

    // ── 更新检测 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 启动器上「更新」按钮的位置。X 或 Y 填负数表示不做更新检测 ——
    /// 有更新时程序会替你点「更新」并等它跑完，不想让它碰就关掉。
    /// </summary>
    public RelativePoint UpdateButton { get; set; } = new() { X = 0.80, Y = 0.95 };

    /// <summary>点完「开始游戏」，等这么久还没动静就按需要更新处理。</summary>
    public int UpdateProbeSeconds { get; set; } = 20;

    /// <summary>点「更新」之后最多等更新完成多久（秒）。</summary>
    public int UpdateWaitSeconds { get; set; } = 900;

    // ── 时序（秒）──────────────────────────────────────────────────────────

    /// <summary>每个沙箱启动之间的间隔，太密会让 Sandboxie 丢实例。</summary>
    public int LaunchIntervalSeconds { get; set; } = 2;

    public int LauncherWaitSeconds { get; set; } = 60;
    public int GameWaitSeconds { get; set; } = 60;
    public int StartClickDelaySeconds { get; set; } = 5;
    public int EnterClickDelaySeconds { get; set; } = 8;
    public int LoginClickDelaySeconds { get; set; } = 15;

    /// <summary>登录后到进入主界面的等待，界面没稳定就排版会出问题。</summary>
    public int SettleSeconds { get; set; } = 20;

    // ── 排版 ──────────────────────────────────────────────────────────────

    public int ScreenWidth { get; set; } = 3840;
    public int ScreenHeight { get; set; } = 2160;
    public int TaskbarHeight { get; set; } = 60;

    /// <summary>
    /// 排版基准：多开窗口尺寸可能各不相同，统一以这个沙箱的窗口尺寸为准。
    /// </summary>
    public string ReferenceBox { get; set; } = "fc";
}
