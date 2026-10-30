using LeXiangSync.Native;
using LeXiangSync.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LeXiangSync.Views;

/// <summary>「设置」页：双列卡片布局，所有选项随改随存。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public MainViewModel Main { get; }

    public SettingsPage(SettingsViewModel viewModel, MainViewModel main)
    {
        ViewModel = viewModel;
        Main = main;
        InitializeComponent();
    }

    // ── 主题分段控件 ──────────────────────────────────────────────────────

    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => ViewModel.SetTheme(0);

    private void OnThemeLightClick(object sender, RoutedEventArgs e) => ViewModel.SetTheme(1);

    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => ViewModel.SetTheme(2);

    // ── 热键捕获 ──────────────────────────────────────────────────────────

    /// <summary>点按钮开始捕获，并立刻把键盘焦点给它 —— 否则收不到后续按键。</summary>
    private void OnHotKeyButtonClick(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginCaptureHotKeyCommand.Execute(null);
        if (sender is Button button) button.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// 捕获状态下的按键。WinUI 的按键事件不直接给修饰键状态，
    /// 用 GetAsyncKeyState 读物理按键，避免漏掉「按住 Ctrl 再点主键」这类情况。
    /// </summary>
    private void OnHotKeyCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsCapturing) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelCaptureHotKeyCommand.Execute(null);
            return;
        }

        // 修饰键自身不算主键，继续等
        if (IsModifierKey(e.Key)) return;

        var modifiers = 0u;
        if (IsKeyDown(VirtualKey.Control)) modifiers |= Win32.MOD_CONTROL;
        if (IsKeyDown(VirtualKey.Menu)) modifiers |= Win32.MOD_ALT;
        if (IsKeyDown(VirtualKey.Shift)) modifiers |= Win32.MOD_SHIFT;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) modifiers |= Win32.MOD_WIN;

        ViewModel.SetHotKey(modifiers, (uint)e.Key);
    }

    private static bool IsModifierKey(VirtualKey key) => key
        is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
        or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsKeyDown(VirtualKey key) => Win32.GetAsyncKeyState((int)key) < 0;
}
