using LeXiangSync.Native;

namespace LeXiangSync.Services;

/// <summary>
/// 系统托盘图标。原 Tauri 版由 Rust 侧创建托盘，WinUI 3 无内置托盘控件，
/// 这里用 Shell_NotifyIconW 在 UI 线程上实现，行为保持一致。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint TrayMessage = Win32.WM_USER + 100;
    private const uint TrayUid = 1;
    private const int MenuShow = 1001;
    private const int MenuToggle = 1002;
    private const int MenuExit = 1003;

    private readonly HiddenMessageWindow _window;
    private readonly IntPtr _icon;
    private bool _visible;

    public event EventHandler? ShowRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ExitRequested;

    /// <summary>
    /// 托盘菜单里「停止同步 / 开始同步」的文案与状态行要反映引擎实况。
    /// 由 MainWindow 注入，避免托盘服务反过来依赖同步引擎。
    /// </summary>
    public Func<bool>? IsSyncActive { get; set; }

    public TrayService(HiddenMessageWindow window, string iconPath)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
        _icon = Win32.LoadImageW(IntPtr.Zero, iconPath, Win32.IMAGE_ICON, 32, 32,
            Win32.LR_LOADFROMFILE);
    }

    public bool Visible => _visible;

    public void Show(string tooltip = "乐享同步操作")
    {
        var data = NewData(tooltip);
        Win32.Shell_NotifyIconW(_visible ? Win32.NIM_MODIFY : Win32.NIM_ADD, ref data);
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible) return;
        var data = NewData(string.Empty);
        data.UFlags = 0;
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        _visible = false;
    }

    private Win32.NotifyIconDataW NewData(string tooltip) => new()
    {
        CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NotifyIconDataW>(),
        HWnd = _window.Handle,
        UId = TrayUid,
        UFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
        UCallbackMessage = TrayMessage,
        HIcon = _icon,
        SzTip = tooltip,
    };

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != TrayMessage) return;

        var mouseMsg = (uint)lParam.ToInt64();
        if (mouseMsg == Win32.WM_LBUTTONDBLCLK)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (mouseMsg is Win32.WM_RBUTTONUP or Win32.WM_CONTEXTMENU)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        var menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var active = IsSyncActive?.Invoke() ?? false;

        // 菜单顺序对齐设计稿：显示主窗口 → 停止/开始同步 → 状态说明行（不可点） → 退出
        Win32.AppendMenuW(menu, Win32.MF_STRING, MenuShow, "显示主窗口");
        Win32.AppendMenuW(menu, Win32.MF_STRING, MenuToggle, active ? "停止同步" : "开始同步");
        Win32.AppendMenuW(menu, Win32.MF_STRING | Win32.MF_GRAYED, 0,
            active ? "同步已启动" : "同步已停止");
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);
        Win32.AppendMenuW(menu, Win32.MF_STRING, MenuExit, "退出乐享同步");

        Win32.GetCursorPos(out var pt);
        // TrackPopupMenu 要求前置窗口先获得焦点，否则菜单不会自动消失
        Win32.SetForegroundWindow(_window.Handle);
        var cmd = Win32.TrackPopupMenu(menu,
            Win32.TPM_LEFTBUTTON | Win32.TPM_RIGHTALIGN | Win32.TPM_BOTTOMALIGN |
            Win32.TPM_NONOTIFY | Win32.TPM_RETURNCMD,
            pt.X, pt.Y, 0, _window.Handle, IntPtr.Zero);
        Win32.PostMessageW(_window.Handle, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Win32.DestroyMenu(menu);

        switch (cmd)
        {
            case MenuShow: ShowRequested?.Invoke(this, EventArgs.Empty); break;
            case MenuToggle: ToggleRequested?.Invoke(this, EventArgs.Empty); break;
            case MenuExit: ExitRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    public void Dispose()
    {
        Hide();
        _window.MessageReceived -= OnMessage;
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        GC.SuppressFinalize(this);
    }
}
