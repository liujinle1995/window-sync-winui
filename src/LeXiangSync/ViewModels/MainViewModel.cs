using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeXiangSync.Models;
using LeXiangSync.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeXiangSync.ViewModels;

/// <summary>主窗口 shell 的视图模型：导航、启停控制、日志面板开关。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DataStore _store;

    public MainViewModel(SyncEngine engine, LogService log, AppSettings settings, DataStore store)
    {
        Engine = engine;
        Logs = log;
        _settings = settings;
        _store = store;
        LogExpanded = settings.LogPanelExpanded;

        // 设计稿里日志入口显示「运行日志 128/300」，条数要跟着走
        Logs.Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogCountText));
    }

    /// <summary>底部日志入口的计数文案：`运行日志 128/300`。</summary>
    public string LogCountText => $"运行日志 {Logs.Entries.Count}/300";

    public SyncEngine Engine { get; }
    public LogService Logs { get; }

    /// <summary>文本镜像开关，代理引擎状态并持久化到设置。</summary>
    public bool TextMirror
    {
        get => Engine.TextMirror;
        set
        {
            if (Engine.TextMirror == value) return;
            Engine.TextMirror = value;
            _settings.SyncTextMirror = value;
            _store.SaveSettings();
            OnPropertyChanged();
        }
    }

    public SetupViewModel Setup { get; set; } = null!;

    /// <summary>底部日志面板是否展开。</summary>
    public bool LogExpanded
    {
        get => _logExpanded;
        set
        {
            if (!SetProperty(ref _logExpanded, value)) return;
            _settings.LogPanelExpanded = value;
            _store.SaveSettings();
            OnPropertyChanged(nameof(LogPanelHeight));
        }
    }
    private bool _logExpanded;

    public double LogPanelHeight => LogExpanded ? 150 : 0;

    public string ToggleButtonText => Engine.IsActive ? "停止同步" : "开始同步";

    /// <summary>
    /// 是否正在同步。迷你条上的启停按钮用它决定实心底色（未同步蓝、同步中红），
    /// 所以它必须跟着引擎走 —— 热键、托盘、主窗口按钮三处都能改这个状态。
    /// </summary>
    public bool IsActive => Engine.IsActive;

    /// <summary>标题栏版本胶囊：`v1.0.0 · 核心 0.9.4`。</summary>
    public string VersionText => $"v1.0.0 · 核心 {Engine.CoreVersion}";

    public string StatusText => Engine.IsActive ? "同步中" : "已停止";

    /// <summary>
    /// 状态指示灯画刷。直接给画刷而不是让 XAML 用转换器：x:Bind 的
    /// StaticResource 转换器在 Window 根元素下无法生成代码。
    /// </summary>
    private static readonly SolidColorBrush ActiveBrush = new(Colors.MediumSeaGreen);
    private static readonly SolidColorBrush IdleBrush = new(Colors.Gray);

    public Brush StatusBrush => Engine.IsActive ? ActiveBrush : IdleBrush;

    [RelayCommand]
    private void ToggleSync() => Engine.Toggle();

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    /// <summary>引擎状态变化时刷新依赖它的展示属性。</summary>
    public void AttachEngineNotifications()
    {
        Engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SyncEngine.IsActive) or nameof(SyncEngine.Status))
            {
                OnPropertyChanged(nameof(ToggleButtonText));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        };
    }
}
