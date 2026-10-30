using System.Diagnostics;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeXiangSync.Models;
using LeXiangSync.Services;
using Microsoft.UI.Xaml;

namespace LeXiangSync.ViewModels;

/// <summary>「设置」页：外观、托盘、快捷键、权限与运行时信息。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ThemeService _theme;
    private readonly HotKeyService _hotKey;
    private readonly LogService _log;
    private readonly DataStore _store;
    private readonly Action<bool>? _onHoverSyncChanged;

    public SettingsViewModel(AppSettings settings, ThemeService theme, HotKeyService hotKey,
        LogService log, DataStore store, string coreVersion, Action<bool>? onHoverSyncChanged = null)
    {
        _settings = settings;
        _theme = theme;
        _hotKey = hotKey;
        _log = log;
        _store = store;
        _onHoverSyncChanged = onHoverSyncChanged;
        CoreVersion = coreVersion;
    }

    public string CoreVersion { get; }

    public string DataDirectory => _store.Directory;

    public bool IsElevated => PrivilegeService.IsElevated;

    public string PrivilegeText => IsElevated
        ? "当前以管理员权限运行，可以同步到已提权的窗口"
        : "当前为普通权限，无法向管理员权限的窗口投递消息";

    /// <summary>0 = 跟随系统，1 = 浅色，2 = 深色。</summary>
    public int ThemeIndex
    {
        get => _settings.Theme switch { ElementTheme.Light => 1, ElementTheme.Dark => 2, _ => 0 };
        set
        {
            var theme = value switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
            if (_settings.Theme == theme) return;
            _settings.Theme = theme;
            _theme.Apply(theme);
            _store.SaveSettings();
            OnPropertyChanged();
            RaiseThemeSegments();
        }
    }

    // 分段控件的三段各自绑一个 bool：ToggleButton 不能直接绑 index
    public bool IsThemeSystem => ThemeIndex == 0;
    public bool IsThemeLight => ThemeIndex == 1;
    public bool IsThemeDark => ThemeIndex == 2;

    /// <summary>分段控件点击时调用。</summary>
    public void SetTheme(int index)
    {
        ThemeIndex = index;
        RaiseThemeSegments();
    }

    private void RaiseThemeSegments()
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    /// <summary>0 = Mica，1 = Mica Alt，2 = 亚克力，3 = 纯色。</summary>
    public int BackdropIndex
    {
        get => (int)_settings.Backdrop;
        set
        {
            var kind = value switch
            {
                1 => AppBackdrop.MicaAlt,
                2 => AppBackdrop.Acrylic,
                3 => AppBackdrop.None,
                _ => AppBackdrop.Mica,
            };
            if (_settings.Backdrop == kind) return;
            _settings.Backdrop = kind;
            _theme.ApplyBackdrop(kind);
            _store.SaveSettings();
            OnPropertyChanged();
        }
    }

    public string BackdropHint => ThemeService.SupportsBackdrop
        ? "Mica 会从桌面壁纸取极淡的底色，是 Windows 11 原生观感的材质；亚克力更明显、更费一点性能。"
        : "当前系统不支持 Mica / 亚克力（需要 Windows 11），已自动退回纯色背景。";

    /// <summary>标题栏按钮切换主题后，让仍打开着的设置页同步显示。</summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(BackdropIndex));
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { if (_settings.CloseToTray == value) return; _settings.CloseToTray = value; _store.SaveSettings(); OnPropertyChanged(); }
    }

    public bool HotKeyEnabled
    {
        get => _settings.HotKeyEnabled;
        set
        {
            if (_settings.HotKeyEnabled == value) return;
            _settings.HotKeyEnabled = value;
            _store.SaveSettings();
            OnPropertyChanged();
            ApplyHotKey();
        }
    }

    public double AutoRefreshSeconds
    {
        get => _settings.AutoRefreshSeconds;
        set
        {
            if (double.IsNaN(value)) return;
            var seconds = (int)Math.Clamp(Math.Round(value), 0, 60);
            if (_settings.AutoRefreshSeconds == seconds) return;
            _settings.AutoRefreshSeconds = seconds;
            _store.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>下拉框用的刷新周期选项：1 / 2 / 5 / 10 秒。</summary>
    public int RefreshPeriodIndex
    {
        get => _settings.AutoRefreshSeconds switch { 1 => 0, 2 => 1, 5 => 2, 10 => 3, _ => 2 };
        set => AutoRefreshSeconds = value switch { 0 => 1, 1 => 2, 2 => 5, 3 => 10, _ => 5 };
    }

    public string HotKeyText => HotKeyService.Describe(_settings.HotKeyModifiers, _settings.HotKeyVirtualKey);

    /// <summary>热键按钮上的文案：等待按键时提示，否则显示当前组合。</summary>
    public string HotKeyButtonText => IsCapturing ? "按下新的组合键…（Esc 取消）" : HotKeyText;

    /// <summary>是否正在等用户按下新组合。</summary>
    [ObservableProperty] public partial bool IsCapturing { get; set; }

    public bool IsNotCapturing => !IsCapturing;

    public string HotKeyHint =>
        $"按 {HotKeyText} 开始 / 停止同步；点右边的按钮可改成任意组合（必须带 Ctrl / Alt / Shift / Win）。";

    partial void OnIsCapturingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotCapturing));
        OnPropertyChanged(nameof(HotKeyButtonText));
    }

    /// <summary>设置页捕获到按键后调用：保存并立即重新注册，注册不上就回退到旧组合。</summary>
    public void SetHotKey(uint modifiers, uint key)
    {
        IsCapturing = false;

        if (modifiers == 0)
        {
            _log.Add("热键必须搭配 Ctrl / Alt / Shift / Win 之一，单键会干扰正常输入", LogLevel.Warning);
            return;
        }

        var oldModifiers = _settings.HotKeyModifiers;
        var oldKey = _settings.HotKeyVirtualKey;

        _settings.HotKeyModifiers = modifiers;
        _settings.HotKeyVirtualKey = key;

        try
        {
            _hotKey.Apply(modifiers, key, _settings.HotKeyEnabled);
            _store.SaveSettings();
            OnPropertyChanged(nameof(HotKeyText));
            OnPropertyChanged(nameof(HotKeyHint));
            _log.Add($"全局热键已改为 {HotKeyText}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            // 注册不上必须退回旧组合：否则界面显示的跟实际生效的不是一回事
            _settings.HotKeyModifiers = oldModifiers;
            _settings.HotKeyVirtualKey = oldKey;
            try { _hotKey.Apply(oldModifiers, oldKey, _settings.HotKeyEnabled); }
            catch (Exception rollback) { _log.Add(rollback.Message, LogLevel.Warning); }
            _log.Add(ex.Message, LogLevel.Warning);
        }
    }

    /// <summary>
    /// 悬停同步：与「同步」页的开关共用同一份设置。
    /// 变更要通过回调下发给 CursorWatchService，否则这里改了不生效。
    /// </summary>
    public bool HoverSyncEnabled
    {
        get => _settings.HoverSyncEnabled;
        set
        {
            if (_settings.HoverSyncEnabled == value) return;
            _settings.HoverSyncEnabled = value;
            _store.SaveSettings();
            _onHoverSyncChanged?.Invoke(value);
            OnPropertyChanged();
        }
    }

    public bool PlayToggleSound
    {        get => _settings.PlayToggleSound;
        set
        {
            if (_settings.PlayToggleSound == value) return;
            _settings.PlayToggleSound = value;
            _store.SaveSettings();
            OnPropertyChanged();
        }
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    /// <summary>试听：先后播放「开始」「停止」两种音，方便确认自己分得清。</summary>
    [RelayCommand]
    private async Task TestToggleSoundAsync()
    {
        SoundService.PlayToggle(true);
        await Task.Delay(450);
        SoundService.PlayToggle(false);
    }

    [RelayCommand]
    private void BeginCaptureHotKey()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        _log.Add("请按下新的组合键（Esc 取消）");
    }

    [RelayCommand]
    private void CancelCaptureHotKey()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        _log.Add("已取消修改热键");
    }

    [RelayCommand]
    private void ApplyHotKey()
    {
        try { _hotKey.Apply(_settings.HotKeyModifiers, _settings.HotKeyVirtualKey, _settings.HotKeyEnabled); }
        catch (Exception ex) { _log.Add(ex.Message, LogLevel.Warning); }
    }

    /// <summary>试听：只播上扬音。</summary>
    [RelayCommand]
    private void PlayRising() => SoundService.PlayToggle(true);

    /// <summary>试听：只播下沉音。</summary>
    [RelayCommand]
    private void PlayFalling() => SoundService.PlayToggle(false);

    /// <summary>把数据目录打成 ZIP 放到桌面，并定位到该文件。</summary>
    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var name = $"LeXiangSync-诊断包-{stamp}.zip";
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(folder)) folder = Path.GetTempPath();
            var target = Path.Combine(folder, name);

            ZipFile.CreateFromDirectory(_store.Directory, target, CompressionLevel.Optimal, false);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            _log.Add($"诊断包已导出：{target}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            _log.Add(ex, "导出诊断包失败：");
        }
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (PrivilegeService.RestartAsAdmin()) Application.Current.Exit();
        else _log.Add("已取消提权，仍以普通权限运行", LogLevel.Warning);
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Add(ex.Message, LogLevel.Warning);
        }
    }

    [RelayCommand]
    private void ClearLogs() => _log.Clear();

    // ── 只读信息行（后端暂未暴露可调参数，界面如实展示固定值） ─────────────

    /// <summary>文本镜像 diff 周期由同步核心决定，界面只读展示。</summary>
    public string TextMirrorIntervalText => "120 ms";

    /// <summary>单次剪贴板注入的字符上限，由核心决定。</summary>
    public string ClipboardLimitText => "800 字符";

    /// <summary>日志环形缓冲上限。</summary>
    public string LogRingText => "300 条";

    public string LogLevelText => "信息";

    public string CoreDllText => $"window_sync_core.dll {CoreVersion}";

    /// <summary>单实例保护是常开能力，没有开关。</summary>
    public string SingleInstanceText => "已启用（二次启动会唤醒已有窗口）";

    // ── 常开能力：界面上是置灰的「开」开关，如实反映没有关闭入口 ───────────

    /// <summary>单实例保护在 App.OnLaunched 里无条件执行。</summary>
    public bool SingleInstanceProtected => true;

    /// <summary>光标看守由 CursorWatchService 常驻，没有关闭入口。</summary>
    public bool CursorGuardEnabled => true;

    /// <summary>未捕获异常始终落盘到 %TEMP%\LeXiangSync.crash.log。</summary>
    public bool CrashLogEnabled => true;

    // ── 只读下拉框：选项由后端固定，列表只有当前值一项 ────────────────────

    public string[] SoundStyleOptions { get; } = ["上扬（启动）"];

    public int SoundStyleIndex => 0;

    public string[] TextMirrorIntervalOptions { get; } = ["120 ms"];

    public int TextMirrorIntervalIndex => 0;

    public string[] ClipboardLimitOptions { get; } = ["800 字符"];

    public int ClipboardLimitIndex => 0;

    public string[] LogLevelOptions { get; } = ["信息"];

    public int LogLevelIndex => 0;

    public string[] LogRingOptions { get; } = ["300 条"];

    public int LogRingIndex => 0;

    /// <summary>把外观与通用选项恢复为出厂默认值。</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = new AppSettings();

        _settings.Theme = defaults.Theme;
        _settings.Backdrop = defaults.Backdrop;
        _settings.CloseToTray = defaults.CloseToTray;
        _settings.HotKeyEnabled = defaults.HotKeyEnabled;
        _settings.PlayToggleSound = defaults.PlayToggleSound;
        _settings.AutoRefreshSeconds = defaults.AutoRefreshSeconds;
        _settings.SyncTextMirror = defaults.SyncTextMirror;
        _settings.HoverSyncEnabled = defaults.HoverSyncEnabled;

        _theme.Apply(_settings.Theme);
        _theme.ApplyBackdrop(_settings.Backdrop);
        ApplyHotKey();
        _store.SaveSettings();

        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(BackdropIndex));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(HotKeyEnabled));
        OnPropertyChanged(nameof(PlayToggleSound));
        OnPropertyChanged(nameof(AutoRefreshSeconds));
        RaiseThemeSegments();

        _log.Add("设置已恢复默认", LogLevel.Success);
    }
}
