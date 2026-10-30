using System.Runtime.InteropServices;

namespace LeXiangSync.Helpers;

/// <summary>
/// 关掉「双击窗口 = 最大化」这条系统默认行为。
/// <para>
/// 窗口用了 ExtendsContentIntoTitleBar + SetDragRectangles，标题栏那段拖拽区在系统眼里就是
/// 标准标题区，双击会被 DefWindowProc 直接翻成最大化。XAML 里的 DoubleTapped 拦不住它 ——
/// 这条消息走的是非客户区，根本到不了 XAML。所以这里挂一层窗口过程：只吃掉
/// 「非客户区双击」，其余消息原样转发给原来的过程，因此拖动标题栏、点最大化按钮、
/// Win+↑ 这些都不受影响。
/// </para>
/// </summary>
internal static class TitleBarDoubleClickGuard
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint WM_NCLBUTTONDBLCLK = 0x00A3;

    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 委托必须由静态字段持有：交给系统的只是函数指针，托管对象一旦被回收就会崩。
    private static WindowProc? _hook;
    private static IntPtr _previous;
    private static IntPtr _hwnd;

    public static void Attach(IntPtr hwnd)
    {
        if (_hwnd != IntPtr.Zero || hwnd == IntPtr.Zero) return;

        _hook = OnMessage;
        var pointer = Marshal.GetFunctionPointerForDelegate(_hook);
        _previous = SetWindowLongPtr(hwnd, GWLP_WNDPROC, pointer);
        if (_previous == IntPtr.Zero)
        {
            _hook = null;
            return;
        }

        _hwnd = hwnd;
    }

    public static void Detach()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_previous != IntPtr.Zero) SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previous);

        _hwnd = IntPtr.Zero;
        _previous = IntPtr.Zero;
        _hook = null;
    }

    private static IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 双击标题栏 / 双击边框：降级成普通的「按下」。
        // 不能直接吞掉 —— 吞掉之后系统那边的双击序列没有正常收尾，紧跟着的第一次点击
        // 会被误判成双击，用户会发现「双击过标题栏之后拖不动窗口了」。
        // 当成按下转发则刚好：拖动照旧，但永远走不到「翻成最大化」的那条分支。
        if (msg == WM_NCLBUTTONDBLCLK)
            return CallWindowProc(_previous, hWnd, WM_NCLBUTTONDOWN, wParam, lParam);

        return CallWindowProc(_previous, hWnd, msg, wParam, lParam);
    }
}
