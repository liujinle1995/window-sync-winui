using LeXiangSync.Models;
using LeXiangSync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeXiangSync.Views;

/// <summary>
/// 「同步」页：运行指标 + 进程与窗口面板 + 底部常驻栏。
/// <para>
/// 页面上的控件都走命令，代码后置只做三件事：取出行对象、把点击转成命令、
/// 以及把筛选框的焦点暴露给 shell 的搜索按钮。
/// </para>
/// </summary>
public sealed partial class SetupPage : Page
{
    public SetupViewModel ViewModel { get; }
    public StatusViewModel Status { get; }
    public MainViewModel Main { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>右键菜单触发时记住的行：MenuFlyout 不在可视树里，取不到 DataContext。</summary>
    private WindowItem? _contextWindow;

    public SetupPage(
        SetupViewModel viewModel,
        StatusViewModel status,
        MainViewModel main,
        SettingsViewModel settings)
    {
        ViewModel = viewModel;
        Status = status;
        Main = main;
        Settings = settings;
        InitializeComponent();
    }

    /// <summary>「自动刷新」开关：关掉时周期置 0，打开时恢复 2 秒。</summary>
    public bool IsAutoRefreshOn
    {
        get => ViewModel.AutoRefreshSeconds > 0;
        set
        {
            if (value == IsAutoRefreshOn) return;
            ViewModel.AutoRefreshSeconds = value ? 2 : 0;
            Bindings.Update();
        }
    }

    /// <summary>标题栏搜索按钮跳过来时把焦点交给筛选框。</summary>
    public void FocusFilter()
    {
        FilterBox.Focus(FocusState.Programmatic);
        FilterBox.SelectAll();
    }

    // ── 主题分段控件 ──────────────────────────────────────────────────────

    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => Settings.SetTheme(0);

    private void OnThemeLightClick(object sender, RoutedEventArgs e) => Settings.SetTheme(1);

    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => Settings.SetTheme(2);

    // ── 进程树 ────────────────────────────────────────────────────────────

    private void OnGroupCheckClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProcessGroupVm group)
            ViewModel.ToggleGroupCommand.Execute(group);
    }

    /// <summary>组行的展开 / 收起箭头。</summary>
    private void OnGroupExpandClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProcessGroupVm group)
            ViewModel.ToggleGroupExpanded(group);
    }

    // ── 窗口表 ────────────────────────────────────────────────────────────

    /// <summary>表头全选框：已全选则清空，否则全选当前列表。</summary>
    private void OnHeaderCheckClick(object sender, RoutedEventArgs e)
        => ViewModel.ToggleAllVisibleCommand.Execute(null);

    private void OnRowCheckClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindowItem item)
            ViewModel.ToggleSlaveCommand.Execute(item);
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindowItem item) _contextWindow = item;
    }

    private void OnRowSetMasterClick(object sender, RoutedEventArgs e)
    {
        if (_contextWindow is not null) ViewModel.SetMasterCommand.Execute(_contextWindow);
    }

    /// <summary>主从对调：行内按钮与右键菜单共用这一个入口。</summary>
    private void OnRowSwapMasterClick(object sender, RoutedEventArgs e)
    {
        // 行内按钮直接带 DataContext；右键菜单拿不到，退回 _contextWindow
        var item = (sender as FrameworkElement)?.DataContext as WindowItem ?? _contextWindow;
        if (item is not null) ViewModel.SwapMasterCommand.Execute(item);
    }

    private void OnRowToggleSyncClick(object sender, RoutedEventArgs e)
    {
        if (_contextWindow is not null) ViewModel.ToggleSlaveCommand.Execute(_contextWindow);
    }

    private void OnRowFocusClick(object sender, RoutedEventArgs e)
    {
        if (_contextWindow is not null) ViewModel.FocusWindowCommand.Execute(_contextWindow.Window);
    }

    private void OnRowKillProcessClick(object sender, RoutedEventArgs e)
    {
        if (_contextWindow is not null) ViewModel.KillProcessCommand.Execute(_contextWindow.Window.Pid);
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindowItem item)
            ViewModel.FocusWindowCommand.Execute(item.Window);
    }
}
