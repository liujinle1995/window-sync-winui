using System.IO;
using System.Runtime.InteropServices;
using LeXiangSync.Helpers;
using LeXiangSync.Models;
using LeXiangSync.Services;
using LeXiangSync.ViewModels;
using LeXiangSync.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace LeXiangSync;

/// <summary>
/// 主窗口。这里同时充当组合根：装配窗口相关的服务（主题 / 托盘 / 热键）与各页面，
/// 并与托盘、热键、关闭行为对接。
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string IconPath = AppPaths.Asset("appicon.ico");

    private readonly Dictionary<string, Page> _pages = new();
    private readonly HiddenMessageWindow _hiddenWindow;
    private readonly TrayService _tray;
    private readonly HotKeyService _hotKey;
    private readonly ThemeService _theme;
    private readonly SetupViewModel _setup;
    private readonly LaunchViewModel _launch;
    private readonly StatusViewModel _status;
    private readonly PlansViewModel _plans;
    private readonly SettingsViewModel _settingsVm;
    private readonly CursorWatchService _cursorWatch;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        var logs = App.Logs;
        var store = App.Store;
        var engine = App.Sync;

        // ── 视图模型与页面 ────────────────────────────────────────────────
        engine.TextMirror = store.Settings.SyncTextMirror;
        ViewModel = new MainViewModel(engine, logs, store.Settings, store);
        ViewModel.AttachEngineNotifications();

        // 启停提示音：热键 / 托盘操作时看不到界面，用声音确认现在是开还是关。
        // 挂在 IsActive 上而不是 Toggle 里，手动点按钮也一样有反馈。
        engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SyncEngine.IsActive) && store.Settings.PlayToggleSound)
                SoundService.PlayToggle(engine.IsActive);
        };

        // 光标监听要在 SetupViewModel 之前建好：悬停同步开关由它下发到这里。
        // 本身的职责是离开主窗口时补投抬起消息，避免从窗口卡在按住状态。
        _cursorWatch = new CursorWatchService(engine, logs);
        _cursorWatch.HoverSync = store.Settings.HoverSyncEnabled;

        _setup = new SetupViewModel(engine, logs, store, _cursorWatch);
        ViewModel.Setup = _setup;

        // 运行指标已并入「同步」页，但仍单独成 VM：它只跟随引擎属性变化，与进程列表互不相干
        _status = new StatusViewModel(engine);

        // 窗口相关服务：主题需要 Window，托盘与热键共用一个隐藏消息窗口
        _theme = new ThemeService(this);
        _theme.EffectiveThemeChanged += (_, effective) =>
        {
            // 深浅切换后日志级别色跟着换，否则深色窗口里会残留浅色文字
            LogEntry.ApplyTheme(effective == ElementTheme.Dark);
        };
        _hiddenWindow = new HiddenMessageWindow();
        _hotKey = new HotKeyService(_hiddenWindow);

        Func<IntPtr> handle = () => WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 删除组要弹确认框（组里的号会一起没），所以把 XamlRoot 的取法一并传进去
        _launch = new LaunchViewModel(store.Launch, logs, store, handle, () => ContentFrame.XamlRoot);
        _launch.Finished += OnMultiLaunchFinished;
        _pages["launch"] = new LaunchPage(_launch);

        // 方案页需要两样窗口相关能力：弹对话框用的 XamlRoot、按当前选择构造方案的回调
        _plans = new PlansViewModel(
            engine, logs, store, handle,
            () => ContentFrame.XamlRoot,
            name => _setup.TryBuildPlan(name));

        // 设置页要在其它页面之前建好：同步页页头的主题分段控件与它共用同一份状态
        _settingsVm = new SettingsViewModel(store.Settings, _theme, _hotKey, logs, store, engine.CoreVersion,
            hover => _cursorWatch.HoverSync = hover);

        _pages["setup"] = new SetupPage(_setup, _status, ViewModel, _settingsVm);
        _pages["plans"] = new PlansPage(_plans, ViewModel);
        _pages["settings"] = new SettingsPage(_settingsVm, ViewModel);

        SelectNavigation("setup");

        // ── 窗口外观 ──────────────────────────────────────────────────────
        AppWindow.Title = "乐享同步操作";
        if (File.Exists(IconPath)) AppWindow.SetIcon(IconPath);

        // 打上「本应用主窗口」标记：第二份进程起来时要靠它把本窗口叫到前台。
        // 越早越好 —— 晚了的话，用户连点两下时第二次可能还找不到这个窗口。
        SingleInstanceGuard.Mark(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // 双击标题栏不再最大化：这条是系统默认行为，只能在窗口过程里掐掉
        TitleBarDoubleClickGuard.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this));

        ConfigureTitleBar();
        ApplyWindowBackdrop();
        RestoreWindowBounds();
        (AppWindow.Presenter as OverlappedPresenter)?.SetBorderAndTitleBar(true, true);
        ApplyMinWindowSize();
        _theme.Apply(store.Settings.Theme);
        LogEntry.ApplyTheme(_theme.IsDark);

        // ── 托盘与热键 ────────────────────────────────────────────────────
        _tray = new TrayService(_hiddenWindow, IconPath);
        _tray.IsSyncActive = () => engine.IsActive;
        _tray.ShowRequested += OnShowRequested;
        _tray.ToggleRequested += (_, _) => engine.Toggle();
        _tray.ExitRequested += (_, _) => RequestExit();
        _tray.Show("乐享同步操作");

        var hk = store.Settings;
        try { _hotKey.Apply(hk.HotKeyModifiers, hk.HotKeyVirtualKey, hk.HotKeyEnabled); }
        catch (Exception ex) { logs.Add(ex.Message, LogLevel.Warning); }
        _hotKey.HotKeyPressed += (_, _) => engine.Toggle();

        // ── 生命周期 ──────────────────────────────────────────────────────
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;

        _ = StartupAsync();
        ShowAdminNotice(logs, store.Settings);
    }

    /// <summary>
    /// 启动流程：先把进程列表刷新一次，再套用方案。顺序不能颠倒 ——
    /// 方案要靠这份最新的进程清单回查窗口句柄；而且套用成功后要立刻停掉周期刷新，
    /// 否则刚加载好的选择会被下一次自动刷新反复打断。
    /// </summary>
    private async Task StartupAsync()
    {
        await _setup.RefreshCommand.ExecuteAsync(null);
        _setup.HasLoadedPlan = await _plans.AutoLoadAsync();
    }

    /// <summary>
    /// 一键启动跑完后的收尾：刷新进程列表 + 套用默认方案。
    /// <para>
    /// 必须刷 —— 客户端是新起来的，之前缓存的窗口句柄早就失效了，
    /// 不刷新的话方案套上去等于没套。
    /// </para>
    /// </summary>
    private async void OnMultiLaunchFinished()
    {
        try
        {
            await _setup.RefreshCommand.ExecuteAsync(null);

            // 刷新完再套方案：AutoLoadAsync 会拿这份最新的进程清单回查窗口
            var loaded = await _plans.AutoLoadAsync();
            _setup.HasLoadedPlan = loaded;
            App.Logs.Add(loaded ? "已自动刷新进程并加载默认方案" : "已自动刷新进程（没有可套用的方案）");
        }
        catch (Exception ex)
        {
            App.Logs.Add(ex, "一键启动后自动刷新失败：");
        }
    }

    // ── 窗口几何 ──────────────────────────────────────────────────────────

    [LibraryImport("user32.dll")]
    private static partial int GetDpiForWindow(IntPtr hwnd);

    /// <summary>当前窗口的 DPI 缩放系数（1.0 = 100%）。</summary>
    private double ScaleFactor
    {
        get
        {
            try
            {
                var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            catch (Exception) { return 1.0; }
        }
    }

    /// <summary>
    /// 恢复上次的窗口尺寸与位置。注意 AppWindow 使用物理像素、XAML 使用有效像素，
    /// 高 DPI 屏上必须换算，否则 150% 缩放时窗口只有设计尺寸的 2/3，内容会被裁掉。
    /// </summary>
    private void RestoreWindowBounds()
    {
        var settings = App.Store.Settings;
        ApplySize(Math.Clamp(settings.WindowWidth, 640, 4000), Math.Clamp(settings.WindowHeight, 480, 4000));

        if (WorkArea is not { } area) return;

        if (settings.WindowX is int x && settings.WindowY is int y)
        {
            MoveTo(x, y);
        }
        else
        {
            // 首次启动：在工作区里居中
            AppWindow.Move(new PointInt32(
                area.X + (area.Width - AppWindow.Size.Width) / 2,
                area.Y + (area.Height - AppWindow.Size.Height) / 2));
        }
    }

    /// <summary>当前显示器工作区（已排除任务栏）。</summary>
    private RectInt32? WorkArea =>
        DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)?.WorkArea;

    /// <summary>
    /// 按有效像素设置窗口尺寸。AppWindow 用物理像素，这里统一换算，
    /// 再按工作区与当前模式（普通 / 迷你）的下限收敛。
    /// </summary>
    private void ApplySize(int widthDip, int heightDip)
    {
        var scale = ScaleFactor;
        var area = WorkArea;
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        if (area is not null)
        {
            width = Math.Min(width, area.Value.Width - 24);
            height = Math.Min(height, area.Value.Height - 24);
        }

        AppWindow.Resize(new SizeInt32(
            Math.Max(width, (int)Math.Round((_compact ? 220 : 320) * scale)),
            Math.Max(height, (int)Math.Round((_compact ? 49 : 240) * scale))));
    }

    /// <summary>按有效像素移动窗口，并保证窗口整体留在可视区内。</summary>
    private void MoveTo(int xDip, int yDip)
    {
        var scale = ScaleFactor;
        AppWindow.Move(new PointInt32((int)Math.Round(xDip * scale), (int)Math.Round(yDip * scale)));
        ClampPosition();
    }

    /// <summary>窗口变大后可能探出屏幕右下角，这里把左上角拉回来。</summary>
    private void ClampPosition()
    {
        if (WorkArea is not { } area) return;

        var size = AppWindow.Size;
        var x = Math.Clamp(AppWindow.Position.X, area.X, Math.Max(area.X, area.X + area.Width - size.Width));
        var y = Math.Clamp(AppWindow.Position.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height));
        if (x != AppWindow.Position.X || y != AppWindow.Position.Y)
            AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>退出前记录窗口尺寸与位置（存有效像素，与 DPI 无关）。</summary>
    private void SaveWindowBounds()
    {
        try
        {
            var settings = App.Store.Settings;
            var scale = ScaleFactor;
            if (_compact)
            {
                // 迷你窗口的尺寸不该被记住，否则下次启动就只剩一个小条
                settings.WindowWidth = _restoreWidth;
                settings.WindowHeight = _restoreHeight;
                settings.WindowX = _restoreX;
                settings.WindowY = _restoreY;
            }
            else
            {
                settings.WindowWidth = (int)Math.Round(AppWindow.Size.Width / scale);
                settings.WindowHeight = (int)Math.Round(AppWindow.Size.Height / scale);
                settings.WindowX = (int)Math.Round(AppWindow.Position.X / scale);
                settings.WindowY = (int)Math.Round(AppWindow.Position.Y / scale);
            }

            App.Store.SaveSettings();
        }
        catch (Exception) { /* 退出阶段不因持久化失败而阻塞 */ }
    }

    // ── 标题栏 / 外观 ─────────────────────────────────────────────────────

    /// <summary>内容延伸到标题栏区域：标题栏由 XAML 绘制，系统只负责右上角三个按钮。</summary>
    private void ConfigureTitleBar()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // 按钮底色透明，让 Mica / 主题背景透出来，避免顶着一块死色
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // WinUI 3 的 Window 没有 Loaded 事件，挂到标题栏元素上，布局完成后再算拖拽区
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarLayout();
    }

    /// <summary>
    /// 右上角的三个系统按钮会盖住自定义按钮，必须让出这段宽度：
    /// RightInset = 系统按钮区宽度（物理像素），换算成 XAML 的有效像素后作为右边距。
    /// </summary>
    private void UpdateTitleBarLayout()
    {
        var rightInset = AppWindow.TitleBar.RightInset / ScaleFactor;
        if (Math.Abs(TitleBarButtons.Margin.Right - (rightInset + 4)) > 0.5)
            TitleBarButtons.Margin = new Thickness(0, 0, rightInset + 4, 0);

        UpdateDragRectangles(rightInset);
    }

    /// <summary>
    /// 拖拽区只覆盖标题栏左半部分：右侧自定义按钮和系统按钮区都要排除，
    /// 否则鼠标按下会被判成拖窗口，按钮点不动。
    /// </summary>
    private void UpdateDragRectangles(double rightInset)
    {
        try
        {
            var height = AppTitleBar.ActualSize.Y;
            var width = AppTitleBar.ActualSize.X - TitleBarButtons.ActualSize.X - rightInset - 4;
            if (height <= 0 || width <= 0) return;

            var scale = ScaleFactor;
            AppWindow.TitleBar.SetDragRectangles(new[]
            {
                new RectInt32(0, 0, (int)Math.Round(width * scale), (int)Math.Round(height * scale)),
            });
        }
        catch (Exception)
        {
            // 布局未完成时拿不到尺寸，SizeChanged 会再试一次
        }
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarLayout();

    /// <summary>窗口最小尺寸：再小底部控制条会挤成一团、导航也会折行。</summary>
    private void ApplyMinWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = ScaleFactor;

        // 迷你模式必须放开下限，否则拖不小
        var (minWidth, minHeight) = _compact ? (300, 49) : (760, 560);
        presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * scale);
    }

    // ── 迷你窗口 ──────────────────────────────────────────────────────────

    /// <summary>迷你条高度（有效像素）：设计稿 42.5，按需 +5 后的实测值。</summary>
    private const int CompactHeight = 49;

    /// <summary>量不出内容宽度时的兜底值（有效像素）。正常情况下用 <see cref="MeasureCompactWidth"/> 实测。</summary>
    private const int CompactFallbackWidth = 412;

    private bool _compact;
    private int _compactWidth = CompactFallbackWidth;
    private double _frameWidthDip;
    private int _restoreWidth = 1180;
    private int _restoreHeight = 780;
    private int _restoreX;
    private int _restoreY;

    private void OnCompactClick(object sender, RoutedEventArgs e) => EnterCompact();

    private void OnRestoreClick(object sender, RoutedEventArgs e) => ExitCompact();

    /// <summary>
    /// 长条上双击不做任何事。折叠后系统标题栏被关掉了，但窗口仍然带着可拖拽的标题区判定，
    /// 双击会被系统当成「双击标题栏 = 最大化」，长条一下就铺满屏幕。这里直接吃掉这个手势。
    /// </summary>
    private void OnCompactDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => e.Handled = true;

    /// <summary>
    /// 迷你条上的最小化。折叠后系统标题栏被关掉了，原来那条最小化按钮也没了，
    /// 所以这里自己提供一个。
    /// </summary>
    private void OnCompactMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    /// <summary>
    /// 迷你条上的关闭。走窗口原本的关闭流程，因此「关闭时最小化到托盘」开关照常生效，
    /// 与点标题栏的关闭按钮行为完全一致。
    /// </summary>
    private void OnCompactCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 折叠成一枚桌面长条：整块界面换成 <c>CompactBar</c>，窗口缩到只够放它。
    /// <para>
    /// 设计稿的迷你条上没有最小化 / 关闭按钮，所以这里把系统边框与标题栏一起关掉
    /// （<see cref="ApplyCompactChrome"/>），拖拽由长条自己实现。
    /// </para>
    /// </summary>
    private void EnterCompact()
    {
        var scale = ScaleFactor;
        _restoreWidth = (int)Math.Round(AppWindow.Size.Width / scale);
        _restoreHeight = (int)Math.Round(AppWindow.Size.Height / scale);
        _restoreX = (int)Math.Round(AppWindow.Position.X / scale);
        _restoreY = (int)Math.Round(AppWindow.Position.Y / scale);

        // 隐形窗口边框（有效像素）。必须在去掉边框之前量 —— 去掉以后客户区就等于整窗了。
        // 迷你条的窗口宽度 = 内容宽度 + 这段边框，少算就会把最右端的关闭按钮切掉。
        _frameWidthDip = Math.Max(0, AppWindow.Size.Width / scale - RootGrid.ActualWidth);

        _compact = true;

        AppTitleBar.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Collapsed;
        LogPanelBorder.Visibility = Visibility.Collapsed;
        CompactBar.Visibility = Visibility.Visible;

        ApplyMinWindowSize();
        _compactWidth = MeasureCompactWidth();
        ApplyCompactChrome(true);
        ApplySize(_compactWidth, CompactHeight);
        ClampPosition();

        // 设计稿写明「常驻置顶」：折叠后默认就在最上层，不用每次手动点
        CompactTopmostToggle.IsChecked = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;

        App.Logs.Add("已切换为迷你窗口：拖动长条可移动，点「还原」回到完整界面");
    }

    private void ExitCompact()
    {
        _compact = false;

        AppTitleBar.Visibility = Visibility.Visible;
        BodyGrid.Visibility = Visibility.Visible;
        CompactBar.Visibility = Visibility.Collapsed;
        UpdateLogPanelVisibility();

        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        CompactTopmostToggle.IsChecked = false;

        ApplyCompactChrome(false);
        ApplyMinWindowSize();
        ApplySize(_restoreWidth, _restoreHeight);
        ClampPosition();
        UpdateTitleBarLayout();
    }

    /// <summary>
    /// 兜底：万一还有别的路径把迷你条撑大或者顶成全屏（双击标题区、系统快捷键都算），
    /// 这里立刻把它按回长条的尺寸。正常路径不会走到这里，所以加上 <c>_compact</c> 判断，
    /// 完整界面下的最大化完全不受影响。
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_compact || !args.DidPresenterChange) return;

        // 被顶成真·全屏：先换回普通窗口，再按长条尺寸摆好
        if (sender.Presenter is FullScreenPresenter)
        {
            sender.SetPresenter(AppWindowPresenterKind.Overlapped);
            ApplyCompactChrome(true);
            ApplySize(_compactWidth, CompactHeight);
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            ApplySize(_compactWidth, CompactHeight);
        }
    }

    /// <summary>
    /// 迷你条该多宽：直接问界面要，而不是写死一个数字。
    /// <para>
    /// 把内容网格按「无限宽」量一遍，得到按钮 + 文字的真实宽度，再加上隐形窗口边框，
    /// 窗口就正好贴着内容。写死宽度的老做法只要比内容大一点点，多出来的这一段就全部
    /// 堆在文字和「置顶」之间，看起来就是一段莫名其妙的空白。
    /// </para>
    /// </summary>
    private int MeasureCompactWidth()
    {
        try
        {
            CompactBarContent.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var content = CompactBarContent.DesiredSize.Width;
            if (double.IsNaN(content) || content < 200) return CompactFallbackWidth;

            return (int)Math.Ceiling(content + _frameWidthDip);
        }
        catch (Exception)
        {
            return CompactFallbackWidth;
        }
    }

    /// <summary>
    /// 折叠时去掉系统边框与标题栏。某些系统版本上 SetBorderAndTitleBar 可能失败，
    /// 失败就退回普通外观 —— 只是多出一条标题栏，功能不受影响，所以不往上抛。
    /// <para>
    /// 同时关掉「可最大化 / 可缩放」：折叠后窗口只剩一条，既不该被拉大，
    /// 也不该因为双击长条被系统当成「双击标题栏」而铺满屏幕。
    /// </para>
    /// </summary>
    private void ApplyCompactChrome(bool compact)
    {
        // 三步各自兜底：任意一步在个别系统版本上不支持，也不该把后面两步一起带掉。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            try { presenter.SetBorderAndTitleBar(!compact, !compact); }
            catch (Exception) { }

            try { presenter.IsMaximizable = !compact; }
            catch (Exception) { }

            try { presenter.IsResizable = !compact; }
            catch (Exception) { }
        }

        try { AppWindow.TitleBar.ExtendsContentIntoTitleBar = !compact; }
        catch (Exception) { }
    }

    // ── 迷你条拖拽 ────────────────────────────────────────────────────────
    // 去掉标题栏后窗口自己就是拖拽区。用 GetCursorPos 取屏幕绝对坐标：
    // AppWindow.Move 用的也是物理像素的屏幕坐标，同一坐标系，不必再做 DPI 换算。

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint pt);

    private bool _compactDragging;
    private CursorPoint _dragCursorOrigin;
    private PointInt32 _dragWindowOrigin;

    private void OnCompactDragStart(object sender, PointerRoutedEventArgs e)
    {
        // 点在按钮上时不要开始拖窗口，否则按钮点不动
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;
        if (!GetCursorPos(out _dragCursorOrigin)) return;

        _dragWindowOrigin = AppWindow.Position;
        _compactDragging = true;
        CompactBar.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCompactDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_compactDragging || !GetCursorPos(out var now)) return;

        AppWindow.Move(new PointInt32(
            _dragWindowOrigin.X + (now.X - _dragCursorOrigin.X),
            _dragWindowOrigin.Y + (now.Y - _dragCursorOrigin.Y)));
    }

    private void OnCompactDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_compactDragging) return;

        _compactDragging = false;
        CompactBar.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>命中点是否落在按钮内（用来把按钮点击和窗口拖拽分开）。</summary>
    private static bool IsInsideButton(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>日志面板的显隐由 ViewModel 决定，但迷你模式下要强制隐藏。</summary>
    private void UpdateLogPanelVisibility()
        => LogPanelBorder.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>迷你条置顶：小窗常驻在游戏上方时才不用每次去任务栏找。</summary>
    private void OnCompactTopmostClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        var topmost = CompactTopmostToggle.IsChecked == true;
        presenter.IsAlwaysOnTop = topmost;
        App.Logs.Add(topmost ? "迷你窗口已置顶" : "迷你窗口已取消置顶");
    }

    /// <summary>标题栏的搜索按钮：跳到同步页并把焦点交给筛选框。</summary>
    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        SelectNavigation("setup");
        if (_pages.TryGetValue("setup", out var page) && page is SetupPage setup)
            setup.FocusFilter();
    }

    private void OnCollapseLogClick(object sender, RoutedEventArgs e) => ViewModel.LogExpanded = false;

    /// <summary>
    /// 当前选中的导航项 Tag。手写导航没有 NavigationView 的选中状态可依，
    /// 由这个字段作为唯一真相，选中视觉与页面跳转都从它派生。
    /// </summary>
    private string _currentNav = "setup";

    private void OnNavTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) SelectNavigation(tag);
    }

    private void OnNavPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag != _currentNav)
            NavHoverOf(tag).Visibility = Visibility.Visible;
    }

    private void OnNavPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            NavHoverOf(tag).Visibility = Visibility.Collapsed;
    }

    private Border NavHoverOf(string tag) => tag switch
    {
        "launch" => NavHoverLaunch,
        "plans" => NavHoverPlans,
        "settings" => NavHoverSettings,
        _ => NavHoverSetup,
    };

    /// <summary>让左侧导航的选中项与代码里的跳转保持一致。</summary>
    private void SelectNavigation(string tag)
    {
        _currentNav = tag;

        UpdateNavItem("setup", NavBarSetup, NavSelSetup, NavHoverSetup, NavIconSetup, NavTextSetup);
        UpdateNavItem("launch", NavBarLaunch, NavSelLaunch, NavHoverLaunch, NavIconLaunch, NavTextLaunch);
        UpdateNavItem("plans", NavBarPlans, NavSelPlans, NavHoverPlans, NavIconPlans, NavTextPlans);
        UpdateNavItem("settings", NavBarSettings, NavSelSettings, NavHoverSettings, NavIconSettings, NavTextSettings);

        NavigateTo(tag);
    }

    /// <summary>
    /// 切换单个导航项的选中视觉。前景色取自 <c>NavPalette</c> 的探针元素而不是
    /// 直接查 Application.Resources：主题资源在 ThemeDictionaries 里，
    /// 从代码查会拿到切换主题前的旧画刷实例；探针由 {ThemeResource} 自动刷新。
    /// </summary>
    private void UpdateNavItem(string tag, Border bar, Border selectedFill, Border hoverFill,
        FontIcon icon, TextBlock text)
    {
        var selected = _currentNav == tag;

        bar.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        selectedFill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (selected) hoverFill.Visibility = Visibility.Collapsed;

        var brush = selected ? PalettePrimary.Fill : PaletteSecondary.Fill;
        if (brush is null) return;

        icon.Foreground = brush;
        text.Foreground = brush;
    }

    /// <summary>Mica 生效时窗口背景必须透明，否则会被纯色盖住；系统不支持时退回纯色。</summary>
    private bool _backdropApplied;

    private void ApplyWindowBackdrop()
    {
        _theme.BackdropChanged += (_, applied) => ApplyRootBackground(applied);
        _theme.EffectiveThemeChanged += (_, _) => ApplyRootBackground(_backdropApplied);
        ApplyRootBackground(_theme.ApplyBackdrop(App.Store.Settings.Backdrop));
    }

    private void ApplyRootBackground(bool materialApplied)
    {
        _backdropApplied = materialApplied;
        RootGrid.Background = BuildRootBackground(materialApplied);
    }

    /// <summary>
    /// 窗口根背景必须显式跟主题走，而且颜色要按窗口自己的实际主题取。
    /// 不能用 Application.Current.Resources 里的主题资源：窗口级 RequestedTheme 覆盖
    /// 不会影响 Application 级查找，系统是浅色时深色窗口会取到浅色值，正是「深色不深」的原因。
    /// 材质生效时留一点透明度让 Mica 透出来，没生效就用不透明色兜底。
    /// </summary>
    private SolidColorBrush BuildRootBackground(bool materialApplied)
    {
        var color = _theme.IsDark
            ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        color.A = materialApplied ? (byte)0xF2 : (byte)0xFF;
        return new SolidColorBrush(color);
    }

    // ── 导航 ──────────────────────────────────────────────────────────────

    private void NavigateTo(string tag)
    {
        if (_pages.TryGetValue(tag, out var page)) ContentFrame.Content = page;
        _setup.IsPageActive = tag == "setup";
    }

    // ── 托盘 / 关闭 ───────────────────────────────────────────────────────

    private void OnShowRequested(object? sender, EventArgs e)
    {
        AppWindow.Show();
        (AppWindow.Presenter as OverlappedPresenter)?.Restore();
        Activate();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!App.Store.Settings.CloseToTray) return;
        args.Cancel = true;
        sender.Hide();
        App.Logs.Add("已最小化到托盘，双击托盘图标可恢复窗口");
    }

    /// <summary>真正退出（托盘菜单 / 关闭开关关闭时）。</summary>
    private void RequestExit()
    {
        App.Store.Settings.CloseToTray = false;
        App.Store.SaveSettings();
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        SaveWindowBounds();
        TitleBarDoubleClickGuard.Detach();
        if (App.Sync.IsActive) App.Sync.Stop();
        // 摘掉 Rust 侧的回调再退出：否则拆同步线程时还会往已经关掉的窗口里推状态
        App.Sync.Dispose();
        _tray.Dispose();
        _hotKey.Dispose();
        _hiddenWindow.Dispose();
        _theme.Dispose();
        _cursorWatch.Dispose();
        _setup.Dispose();
    }

    // ── 首次提示 ──────────────────────────────────────────────────────────

    private static void ShowAdminNotice(LogService logs, AppSettings settings)
    {
        if (settings.AdminNoticeDismissed) return;
        settings.AdminNoticeDismissed = true;

        logs.Add(PrivilegeService.IsElevated
            ? "已以管理员权限启动"
            : "当前为普通权限：若目标窗口以管理员身份运行，需要在「设置」中提权后重启",
            PrivilegeService.IsElevated ? LogLevel.Success : LogLevel.Warning);
    }
}
