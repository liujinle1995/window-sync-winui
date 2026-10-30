using System.Runtime.InteropServices;
using LeXiangSync.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeXiangSync.Services;

/// <summary>
/// 主题与窗口材质。管三件事：
/// 1. XAML 元素主题（Light / Dark / 跟随系统）；
/// 2. 系统标题栏（按钮前景色 + 深色边框，跟随系统时必须按实际主题算，不能按 Default 算）；
/// 3. Windows 11 背景材质（Mica / Mica Alt / 亚克力）与窗口圆角。
/// </summary>
// LibraryImport 源生成要求声明类型为 partial
public sealed partial class ThemeService : IDisposable
{
    // DWMWINDOWATTRIBUTE
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaWindowCornerPreference = 33;

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DwmwcpRound = 2;

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private FrameworkElement? _root;

    public ThemeService(Window window)
    {
        _window = window;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    /// <summary>用户选择的主题，可能是 Default（跟随系统）。</summary>
    public ElementTheme Requested { get; private set; } = ElementTheme.Default;

    /// <summary>实际生效的主题：Default 会被解析成系统当前的 Light / Dark。</summary>
    public ElementTheme Effective { get; private set; } = ElementTheme.Default;

    public bool IsDark => Effective == ElementTheme.Dark;

    /// <summary>实际主题变化，包括「跟随系统」时系统主题被切换的情况。</summary>
    public event EventHandler<ElementTheme>? EffectiveThemeChanged;

    /// <summary>背景材质已重新应用，参数为是否真的生效（系统不支持时为 false）。</summary>
    public event EventHandler<bool>? BackdropChanged;

    /// <summary>Mica / 亚克力需要 Windows 11 22000+。</summary>
    public static bool SupportsBackdrop => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    // ── 主题 ──────────────────────────────────────────────────────────────

    public void Apply(ElementTheme theme)
    {
        Requested = theme;

        if (_window.Content is not FrameworkElement root) return;

        // ActualThemeChanged 是唯一可靠的信号：跟随系统时系统切主题也会触发，
        // 而 RequestedTheme=Default 本身拿不到实际值。
        if (!ReferenceEquals(_root, root))
        {
            if (_root is not null) _root.ActualThemeChanged -= OnActualThemeChanged;
            _root = root;
            root.ActualThemeChanged += OnActualThemeChanged;
        }

        root.RequestedTheme = theme;
        Sync(root);
    }

    /// <summary>在当前深浅之间切换，返回切换后的主题。</summary>
    public ElementTheme Toggle()
    {
        var next = IsDark ? ElementTheme.Light : ElementTheme.Dark;
        Apply(next);
        return next;
    }

    // ── 窗口材质 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 应用背景材质。用 WinUI 3 官方的 <see cref="Window.SystemBackdrop"/>，
    /// 而不是 DWMWA_SYSTEMBACKDROP_TYPE —— 后者在 WinUI 3 里常被 XAML 岛挡住、看不出效果。
    /// 系统不支持时返回 false，调用方据此退回不透明背景。
    /// </summary>
    public bool ApplyBackdrop(AppBackdrop kind)
    {
        ApplyRoundedCorners();

        try
        {
            _window.SystemBackdrop = kind switch
            {
                AppBackdrop.Mica => new MicaBackdrop { Kind = MicaKind.Base },
                AppBackdrop.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                AppBackdrop.Acrylic => new DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception)
        {
            BackdropChanged?.Invoke(this, false);
            return false;
        }

        var applied = SupportsBackdrop && kind != AppBackdrop.None;
        BackdropChanged?.Invoke(this, applied);
        return applied;
    }

    private void ApplyRoundedCorners()
    {
        int round = DwmwcpRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref round, 4);
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    private void Sync(FrameworkElement root)
    {
        // 布局未完成前 ActualTheme 仍是 Default，等 ActualThemeChanged 再同步
        var actual = root.ActualTheme;
        if (actual == ElementTheme.Default || actual == Effective) return;

        Effective = actual;
        ApplyTitleBar();
        ApplyImmersiveDark();
        EffectiveThemeChanged?.Invoke(this, actual);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => Sync(sender);

    /// <summary>标题栏按钮前景色必须按「实际」主题算，跟随系统时按 Default 算会变成深色底上的黑字。</summary>
    private void ApplyTitleBar()
    {
        var titleBar = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd)).TitleBar;

        var dark = IsDark;
        var foreground = dark ? Colors.White : Colors.Black;
        var inactive = dark
            ? Color.FromArgb(0xFF, 0x99, 0x99, 0x99)
            : Color.FromArgb(0xFF, 0x66, 0x66, 0x66);

        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactive;
    }

    /// <summary>让系统绘制的窗口边框 / 标题栏按钮进入深色模式。</summary>
    private void ApplyImmersiveDark()
    {
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, 4);
    }

    public void Dispose()
    {
        if (_root is not null) _root.ActualThemeChanged -= OnActualThemeChanged;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
}
