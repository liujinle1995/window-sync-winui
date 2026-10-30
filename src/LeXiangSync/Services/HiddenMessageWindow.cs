using System.Runtime.InteropServices;
using LeXiangSync.Native;

namespace LeXiangSync.Services;

/// <summary>
/// 一个消息专用窗口（HWND_MESSAGE），为托盘图标与全局快捷键提供消息宿主。
/// 它创建在 UI 线程上，因此直接复用 WinUI 已有的消息循环，无需额外线程。
/// </summary>
public sealed class HiddenMessageWindow : IDisposable
{
    private const string ClassName = "LeXiangSync.MessageOnly";

    private static readonly Win32.WndProc s_proc = StaticWndProc;
    private static HiddenMessageWindow? s_current;

    public IntPtr Handle { get; }
    public bool IsDisposed { get; private set; }

    /// <param name="message">窗口消息</param><param name="wParam">wParam</param><param name="lParam">lParam</param>
    public event Action<uint, IntPtr, IntPtr>? MessageReceived;

    public HiddenMessageWindow()
    {
        var hInstance = Win32.GetModuleHandleW(null);
        var wc = new Win32.WndClassExW
        {
            CbSize = (uint)Marshal.SizeOf<Win32.WndClassExW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            LpszClassName = ClassName,
            HInstance = hInstance,
        };
        Win32.RegisterClassExW(ref wc);

        s_current = this;
        Handle = Win32.CreateWindowExW(0, ClassName, string.Empty, 0, 0, 0, 0, 0,
            Win32.HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        s_current?.WndProcCore(hWnd, msg, wParam, lParam) ?? Win32.DefWindowProcW(hWnd, msg, wParam, lParam);

    private IntPtr WndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        MessageReceived?.Invoke(msg, wParam, lParam);
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (Handle != IntPtr.Zero) Win32.DestroyWindow(Handle);
        if (s_current == this) s_current = null;
        GC.SuppressFinalize(this);
    }
}
