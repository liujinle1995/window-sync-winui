using CommunityToolkit.Mvvm.ComponentModel;
using LeXiangSync.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeXiangSync.ViewModels;

/// <summary>
/// 「同步」页顶部的运行指标卡片。只跟随引擎状态变化，与进程列表互不相干，
/// 因此单独成 VM，页面同时绑定它和 <see cref="SetupViewModel"/>。
/// </summary>
public sealed partial class StatusViewModel : ObservableObject, IDisposable
{
    private readonly SyncEngine _engine;
    private readonly DispatcherQueueTimer _timer;

    /// <summary>本次同步的开始时刻；引擎未运行时为 null。</summary>
    private DateTime? _startedAt;

    public StatusViewModel(SyncEngine engine)
    {
        _engine = engine;
        _startedAt = engine.IsActive ? DateTime.Now : null;

        _engine.PropertyChanged += OnEngineChanged;
        _engine.Slaves.CollectionChanged += (_, _) => RaiseSlaveProps();

        // 运行时长要每秒走字，不能只靠引擎的属性变化事件
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            if (_engine.IsActive) OnPropertyChanged(nameof(StatusDetail));
        };
        _timer.Start();
    }

    // ── 卡片 1：运行状态 ──────────────────────────────────────────────────

    public string StatusText => _engine.IsActive ? "运行中" : "已停止";

    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

    /// <summary>
    /// 状态指示灯画刷。直接给画刷而不是让 XAML 用转换器：x:Bind 的
    /// StaticResource 转换器在 Window 根元素下无法生成代码。
    /// </summary>
    public Brush StatusBrush => _engine.IsActive ? ActiveBrush : IdleBrush;

    /// <summary>运行状态卡的副信息：`已持续 12:34 · 投递延迟 8 ms`。</summary>
    public string StatusDetail
    {
        get
        {
            if (!_engine.IsActive) return "未在同步 · 选好主从窗口后点「开始同步」";

            var uptime = _startedAt is { } start ? DateTime.Now - start : TimeSpan.Zero;
            var text = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}:{uptime.Minutes:00}:{uptime.Seconds:00}"
                : $"{uptime.Minutes:00}:{uptime.Seconds:00}";

            // 核心暂未暴露单次投递耗时，这里如实显示破折号，不做假数据
            return $"已持续 {text} · 投递延迟 —";
        }
    }

    /// <summary>页头「暂停同步」开关：开 = 已暂停（引擎未运行）。</summary>
    public bool IsPaused
    {
        get => !_engine.IsActive;
        set
        {
            if (value == !_engine.IsActive) return;
            _engine.Toggle();
            OnPropertyChanged();
        }
    }

    // ── 卡片 3：从窗口数 ──────────────────────────────────────────────────

    public string SlaveCountText => _engine.SlaveCount.ToString();

    /// <summary>
    /// `就绪 4 · 异常 0 · 待确认 0`。
    /// 引擎里的 Slaves 只保留仍然存活的窗口（失效的会在刷新时被剪掉），
    /// 所以「就绪」就是当前从窗口数；「异常」要等一次刷新把失效项剪掉后才知道，
    /// 由 <see cref="ReportStaleWindows"/> 记录最近一次清理的条数。
    /// </summary>
    public string SlaveDetail => $"就绪 {_engine.SlaveCount} · 异常 {_staleCount} · 待确认 0";

    private int _staleCount;

    /// <summary>进程刷新时剪掉了多少条失效窗口，用于「异常」计数。</summary>
    public void ReportStaleWindows(int count)
    {
        _staleCount = count;
        RaiseSlaveProps();
    }

    // ── 卡片 4：同步项 ────────────────────────────────────────────────────

    private int EnabledItemCount
    {
        get
        {
            var count = 0;
            if (_engine.SyncMouse) count++;
            if (_engine.SyncKeyboard) count++;
            if (_engine.SyncClipboard) count++;
            if (_engine.TextMirror) count++;
            return count;
        }
    }

    public string SyncItemsCountText => EnabledItemCount == 0 ? "未开启" : $"{EnabledItemCount} 项启用";

    /// <summary>开启的同步项枚举。只列开启的，三项平铺信息密度太低。</summary>
    public string SyncItemsDetail
    {
        get
        {
            var items = new List<string>();
            if (_engine.SyncMouse) items.Add("鼠标");
            if (_engine.SyncKeyboard) items.Add("键盘");
            if (_engine.SyncClipboard) items.Add("剪贴板");
            if (_engine.TextMirror) items.Add("文本镜像");
            return items.Count == 0 ? "全部关闭" : string.Join(" · ", items);
        }
    }

    // ── 兼容旧绑定 ────────────────────────────────────────────────────────

    public string MasterTitle => _engine.MasterTitle;

    /// <summary>旧的合并文案，供托盘提示等处复用。</summary>
    public string SyncItemsText => SyncItemsDetail;

    // ── 刷新 ──────────────────────────────────────────────────────────────

    private void OnEngineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SyncEngine.IsActive):
                // 状态切换时重置计时基准，否则暂停再开始会接着累加
                _startedAt = _engine.IsActive ? DateTime.Now : null;
                RefreshAll();
                break;

            case nameof(SyncEngine.Status):
                RefreshAll();
                break;

            case nameof(SyncEngine.Master):
                OnPropertyChanged(nameof(MasterTitle));
                break;

            case nameof(SyncEngine.SlaveCount):
                RaiseSlaveProps();
                break;

            case nameof(SyncEngine.SyncMouse):
            case nameof(SyncEngine.SyncKeyboard):
            case nameof(SyncEngine.SyncClipboard):
            case nameof(SyncEngine.TextMirror):
                OnPropertyChanged(nameof(SyncItemsCountText));
                OnPropertyChanged(nameof(SyncItemsDetail));
                OnPropertyChanged(nameof(SyncItemsText));
                break;
        }
    }

    private void RaiseSlaveProps()
    {
        OnPropertyChanged(nameof(SlaveCountText));
        OnPropertyChanged(nameof(SlaveDetail));
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsPaused));
        RaiseSlaveProps();
        OnPropertyChanged(nameof(SyncItemsCountText));
        OnPropertyChanged(nameof(SyncItemsDetail));
        OnPropertyChanged(nameof(SyncItemsText));
    }

    public void Dispose()
    {
        _timer.Stop();
        _engine.PropertyChanged -= OnEngineChanged;
    }
}
