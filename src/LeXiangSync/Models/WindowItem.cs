using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LeXiangSync.Helpers;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeXiangSync.Models;

/// <summary>窗口在同步关系中的角色。</summary>
public enum WindowRole
{
    /// <summary>未加入同步。</summary>
    None = 0,

    /// <summary>主窗口（操作源），全局唯一。</summary>
    Master = 1,

    /// <summary>从窗口（同步目标），可多个。</summary>
    Slave = 2,
}

/// <summary>
/// 「进程与窗口」面板右侧表格的一行。
/// <para>
/// 设计稿把主窗口与从窗口合并到同一张表里，靠「角色」列区分，
/// 因此选中态不再是「本侧是否选中」，而是 <see cref="Role"/> 的三态。
/// </para>
/// </summary>
public sealed partial class WindowItem : ObservableObject
{
    /// <summary>
    /// 注意：作为 XAML <c>x:DataType</c> 引用的类型会被生成的 XamlTypeInfo.g.cs 实例化，
    /// 因此成员必须是可 set 的普通属性，不能用 required / init。
    /// </summary>
    public WindowInfo Window { get; set; } = null!;

    public string Title => Window.Title;
    public string HwndHex => Window.HwndHex;

    /// <summary>所属进程名（用于副标题里的「Client.exe · 同标题 #1」）。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>同标题窗口中的序号，从 1 开始。</summary>
    public int InstanceIndex { get; set; } = 1;

    /// <summary>副标题：进程名 · 同标题序号。</summary>
    public string Subtitle => string.IsNullOrEmpty(ProcessName)
        ? $"同标题 #{InstanceIndex}"
        : $"{ProcessName} · 同标题 #{InstanceIndex}";

    [ObservableProperty] public partial WindowRole Role { get; set; }

    /// <summary>在从窗口列表中的序号（仅 <see cref="WindowRole.Slave"/> 有效）。</summary>
    [ObservableProperty] public partial int SlaveIndex { get; set; }

    // ── 展示派生属性 ──────────────────────────────────────────────────────

    public bool IsMaster => Role == WindowRole.Master;
    public bool IsSlave => Role == WindowRole.Slave;

    /// <summary>未加入同步。<c>IsInSync</c> 的反面，单独给「未加入」那段文字做可见性绑定。</summary>
    public bool IsNone => Role == WindowRole.None;

    /// <summary>是否已加入同步（主或从）。行首勾选框绑这个值。</summary>
    public bool IsInSync => Role != WindowRole.None;

    public string RoleText => Role switch
    {
        WindowRole.Master => "主窗口",
        WindowRole.Slave => $"从窗口 {SlaveIndex}",
        _ => "未加入",
    };

    /// <summary>从窗口列的文案（拆出来给独立 TextBlock 绑定，避免用模型里的画刷导致切主题不刷新）。</summary>
    public string SlaveRoleText => $"从窗口 {SlaveIndex}";

    private static readonly Color MasterColor = Color.FromArgb(0xFF, 0x4A, 0x9E, 0xFF);
    private static readonly Color SlaveColor = Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50);
    private static readonly Color NoneColor = Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);

    /// <summary>胶囊文字色（旧视觉保留给需要的场合，表格已改用主题画刷的纯文字）。</summary>
    public Brush RoleForeground => Role switch
    {
        WindowRole.Master => new SolidColorBrush(MasterColor),
        WindowRole.Slave => new SolidColorBrush(SlaveColor),
        _ => new SolidColorBrush(NoneColor),
    };

    partial void OnRoleChanged(WindowRole value) => RaiseDisplayChanged();

    partial void OnSlaveIndexChanged(int value) => RaiseDisplayChanged();

    /// <summary>
    /// 引擎重新枚举后窗口对象与标题可能变化，而这些是普通属性（非 ObservableProperty），
    /// 必须由构建器显式通知一次，否则表格里仍显示旧标题。
    /// </summary>
    public void OnWindowMetaChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HwndHex));
    }

    private void RaiseDisplayChanged()
    {
        OnPropertyChanged(nameof(IsMaster));
        OnPropertyChanged(nameof(IsSlave));
        OnPropertyChanged(nameof(IsNone));
        OnPropertyChanged(nameof(IsInSync));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(SlaveRoleText));
        OnPropertyChanged(nameof(RoleForeground));
    }
}

/// <summary>
/// 进程分组（一个 exe 实例）。同一软件的多个实例在左侧进程树里聚合：
/// 组内第一行显示软件名与实例数，其余行作为进程实例缩进排在它下面。
/// </summary>
public sealed partial class ProcessGroupVm : ObservableObject
{
    public ProcessGroupVm() => IsExpanded = true;

    public uint Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<WindowItem> Windows { get; } = new();

    /// <summary>
    /// 组名的「人话」版本：优先已知软件的中文名，其次窗口标题的公共前缀，
    /// 都没有才退回 exe 名。设计稿里显示的是 `梦幻西游 (2)` 而不是 `Client.exe (2)`。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否为同名软件一组中的首行：只有首行渲染软件名，其余行渲染进程实例。</summary>
    [ObservableProperty] public partial bool IsFirstOfApp { get; set; }

    /// <summary>展开 / 收起组内的进程实例行。展开的组，标题行会垫一层底色。</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>该软件名下的实例数（多开数量）。</summary>
    [ObservableProperty] public partial int AppProcessCount { get; set; }

    /// <summary>该软件名下的窗口总数。</summary>
    [ObservableProperty] public partial int AppWindowCount { get; set; }

    /// <summary>三态勾选：全部窗口已加入同步 / 部分（null）/ 全部未加入。</summary>
    [ObservableProperty] public partial bool? SelectionState { get; set; }

    /// <summary>组名显示：`梦幻西游 (2)`。</summary>
    public string AppTitle => $"{(string.IsNullOrEmpty(DisplayName) ? Name : DisplayName)} ({AppProcessCount})";

    /// <summary>组行副标题：给出窗口总数，避免和实例数混淆。</summary>
    public string AppSummary => $"{AppProcessCount} 个实例 · {AppWindowCount} 个窗口";

    /// <summary>进程实例行文案：`Client.exe · PID 4820`。</summary>
    public string InstanceText => $"{Name} · PID {Pid}";

    public string Icon => ProcessIcon.Get(Name);

    public string PidText => $"#{Pid}";

    /// <summary>展开态箭头：展开朝下，收起朝右。</summary>
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    /// <summary>该进程内的窗口是否全部已加入同步（用于行内小圆点）。</summary>
    public bool HasAnyInSync => Windows.Any(w => w.IsInSync);

    /// <summary>按窗口角色重算三态勾选。</summary>
    public void RefreshSelection()
    {
        if (Windows.Count == 0) SelectionState = false;
        else
        {
            var selected = Windows.Count(w => w.IsInSync);
            SelectionState = selected == 0
                ? (bool?)false
                : selected == Windows.Count ? (bool?)true : null;
        }

        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(InstanceText));
        OnPropertyChanged(nameof(HasAnyInSync));
    }

    partial void OnAppProcessCountChanged(int value) => OnPropertyChanged(nameof(AppTitle));

    partial void OnAppWindowCountChanged(int value) => OnPropertyChanged(nameof(AppSummary));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));
}
