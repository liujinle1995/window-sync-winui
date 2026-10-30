using LeXiangSync.Models;
using LeXiangSync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeXiangSync.Views;

/// <summary>「方案」页：表格化的方案清单 + 行内操作 + 底部批量操作栏。</summary>
public sealed partial class PlansPage : Page
{
    public PlansViewModel ViewModel { get; }

    public MainViewModel Main { get; }

    public PlansPage(PlansViewModel viewModel, MainViewModel main)
    {
        ViewModel = viewModel;
        Main = main;
        InitializeComponent();
        ViewModel.RefreshActiveFlags();
    }

    // 行内操作：DataTemplate 里拿不到页面的 ViewModel，统一从 DataContext 取行对象

    private static SyncPlan? PlanOf(object sender)
        => (sender as FrameworkElement)?.DataContext as SyncPlan;

    /// <summary>
    /// 点整行即选中该方案。Tapped 在按钮的 Click 之后还会冒到行上，
    /// 而删除 / 覆盖更新会当场把这一行换掉，所以要确认对象仍在集合里再选中。
    /// </summary>
    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (PlanOf(sender) is { } plan && ViewModel.Plans.Contains(plan))
        {
            ViewModel.Selected = plan;
        }
    }

    /// <summary>四个文字链接共用一个处理器：动作名走 Tag，行对象走 DataContext。</summary>
    private void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if (PlanOf(sender) is not { } plan) return;
        if ((sender as FrameworkElement)?.Tag is not string action) return;

        ViewModel.Selected = plan;

        switch (action)
        {
            case "load":
                ViewModel.LoadCommand.Execute(plan);
                break;
            case "update":
                ViewModel.UpdateCommand.Execute(plan);
                break;
            case "rename":
                ViewModel.RenameCommand.Execute(plan);
                break;
            case "delete":
                ViewModel.DeleteCommand.Execute(plan);
                break;
        }
    }
}
