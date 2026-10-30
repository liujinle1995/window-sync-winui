using System.Collections.Specialized;
using System.ComponentModel;
using LeXiangSync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeXiangSync.Views;

/// <summary>「多开启动」页：多开流程的一键启动、沙箱状态与控制。</summary>
public sealed partial class LaunchPage : Page
{
    public LaunchViewModel ViewModel { get; }

    public LaunchPage(LaunchViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.Progress.CollectionChanged += OnProgressChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateProgressFill();
        _ = ViewModel.RefreshStatusesAsync();
    }

    /// <summary>流程跑完（或被中断）后重新查一次窗口，「状态」列显示的才是当下的事实。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LaunchViewModel.IsRunning) when !ViewModel.IsRunning:
                _ = ViewModel.RefreshStatusesAsync();
                break;
            case nameof(LaunchViewModel.ProgressPercent):
                UpdateProgressFill();
                break;
        }
    }

    /// <summary>
    /// 进度条是自己画的两个 Border（默认 ProgressBar 的轨道只有 2px），
    /// 所以填充宽度得按百分比和当前可用宽度算出来。
    /// </summary>
    private void UpdateProgressFill()
    {
        var available = ProgressTrack.ActualWidth;
        var percent = Math.Clamp(ViewModel.ProgressPercent, 0, 100) / 100.0;
        ProgressFill.Width = available * percent;
    }

    private void OnProgressTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressFill();

    private void OnProgressChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Progress.Count == 0) return;
        var last = ViewModel.Progress[^1];
        DispatcherQueue.TryEnqueue(() => ProgressList.ScrollIntoView(last));
    }
}
