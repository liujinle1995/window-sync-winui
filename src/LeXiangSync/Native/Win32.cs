using System.Runtime.InteropServices;

namespace LeXiangSync.Native;

/// <summary>托盘、全局快捷键、图标加载所需的 Win32 API（WinUI 3 未提供托管封装的部分）。</summary>
internal static class Win32
{
    // ── 窗口消息 ──────────────────────────────────────────────────────────
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_USER = 0x0400;

    // ── 托盘 ──────────────────────────────────────────────────────────────
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    // ── 菜单 ──────────────────────────────────────────────────────────────
    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    /// <summary>菜单项置灰：托盘菜单用它显示「同步已启动」这类纯状态行。</summary>
    public const uint MF_GRAYED = 0x00000001;

    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    // ── 图标 / 快捷键 ─────────────────────────────────────────────────────
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_S = 0x53;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;

    /// <summary>消息专用窗口的父窗口句柄。</summary>
    public static readonly IntPtr HwndMessage = new(-3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassExW
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconDataW
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string SzTip;
        public uint DwState;
        public uint DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SzInfo;
        public uint UTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string SzInfoTitle;
        public uint DwInfoFlags;
        public Guid GuidItem;
        public IntPtr HBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WndClassExW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconDataW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>最高位为 1 表示该键当前物理按下（返回值是 short）。</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>系统事件音：MB_ICONASTERISK = 提示，MB_ICONEXCLAMATION = 感叹号。</summary>
    public const uint MB_ICONASTERISK = 0x00000040;
    public const uint MB_ICONEXCLAMATION = 0x00000030;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MessageBeep(uint uType);

    public const uint SND_FILENAME = 0x00020000;
    public const uint SND_ASYNC = 0x0001;
    public const uint SND_NODEFAULT = 0x0002;

    /// <summary>播放 wav 文件。SND_ASYNC = 异步播放不阻塞调用线程。</summary>
    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── 单实例 ────────────────────────────────────────────────────────────

    /// <summary>EnumWindows 的回调。返回 false 停止遍历。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>读窗口属性。没设过返回 0。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetPropW(IntPtr hWnd, string lpString);

    /// <summary>给窗口打属性标记，窗口销毁时自动清除。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPropW(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>窗口是否处于最小化状态。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>创建该窗口的线程 id。<paramref name="processId"/> 传 Zero 表示不关心进程。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>
    /// 让两个线程共用输入队列。attach 为自己的线程借用对方的前台权限时 <paramref name="attach"/> 传 true，
    /// 用完必须立刻传 false 解除，否则两个窗口的输入会互相卡住。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    // ── 屏幕取色（更新检测用）─────────────────────────────────────────────

    /// <summary>取屏幕设备上下文，<paramref name="hWnd"/> 传 Zero。拿到的句柄要用 <see cref="ReleaseDC"/> 归还。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>
    /// 读设备上下文上的一个像素，返回 0x00BBGGRR；坐标越界返回 0xFFFFFFFF。
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hDC, int x, int y);
}
