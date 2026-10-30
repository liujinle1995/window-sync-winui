using System.Runtime.InteropServices;
using System.Text;

namespace LeXiangSync.Native;

/// <summary>
/// 多开启动要用到的窗口操作 API。
/// 这些调用必须发真实输入（SetForegroundWindow + SetCursorPos + mouse_event），
/// 不能用 PostMessage 代替：游戏客户端通常轮询原始输入状态，不会理会伪造的窗口消息。
/// </summary>
public static partial class WindowApi
{
    public const int SW_RESTORE = 9;
    public const uint SWP_SHOWWINDOW = 0x0040;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>在多显示器且缩放不一致时，保证读到的坐标与现实像素一致。</summary>
    [LibraryImport("user32.dll")]
    public static partial int SetProcessDPIAware();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    // StringBuilder 不被源生成的 P/Invoke 支持（SYSLIB1051），这里用常规 DllImport
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    /// <summary>合成一次真实的按键 / 抬起，走的是系统输入队列。</summary>
    [LibraryImport("user32.dll")]
    public static partial void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hwnd, int cmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint GA_ROOT = 2;

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>
    /// 取顶层窗口。点之前用它确认「鼠标下压着的那一层」就是目标窗口本身 ——
    /// 五个客户端同屏排着，点歪一下就变成替别人操作了。
    /// </summary>
    public static IntPtr RootWindow(IntPtr hwnd)
    {
        var root = GetAncestor(hwnd, GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    /// <summary>
    /// 唤醒窗口：先还原最小化，再激活。
    /// 系统有前台锁定时单次 SetForegroundWindow 会失败（只闪任务栏），
    /// 这时把输入队列临时挂到前台线程上再试一次，成功率明显更高。
    /// </summary>
    public static void Activate(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd)) return;

        var current = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        if (current == 0 || foregroundThread == 0 || targetThread == 0 || targetThread == foregroundThread) return;

        if (AttachThreadInput(current, foregroundThread, true))
        {
            SetForegroundWindow(hwnd);
            AttachThreadInput(current, foregroundThread, false);
        }
    }

    /// <summary>在窗口的某个相对位置合成一次左键单击。</summary>
    public static bool ClickAt(RECT rect, double ratioX, double ratioY, out int x, out int y)
    {
        x = rect.Left + (int)(rect.Width * ratioX);
        y = rect.Top + (int)(rect.Height * ratioY);
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, x, y, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, x, y, 0, UIntPtr.Zero);
        return true;
    }

    public static string WindowText(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        return GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }
}
