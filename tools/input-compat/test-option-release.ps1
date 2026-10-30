# 回归用例：关闭同步选项不能留下卡键。
#
# 场景（这是 P0 缺陷的原始复现路径）：
#   1. 按住 W → 从窗口收到 KeyDown
#   2. 关掉键盘同步（此时用户还没松手）
#   3. 从窗口必须立刻收到 KeyUp —— 否则它永远认为 W 按着
# 鼠标键同理：按住左键 → 关掉鼠标同步 → 从窗口必须收到 LBUTTONUP。
#
# 断言全部基于「从窗口真实收到的窗口消息」，不读引擎内部状态。
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class OptTest
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT
    { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT
    { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION
    { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT
    { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int size);

    [DllImport("window_sync_core.dll", CharSet = CharSet.Ansi)] public static extern IntPtr wsync_start_sync(string json);
    [DllImport("window_sync_core.dll")] public static extern IntPtr wsync_stop_sync();
    [DllImport("window_sync_core.dll", CharSet = CharSet.Ansi)] public static extern IntPtr wsync_update_sync_options(string json);
    [DllImport("window_sync_core.dll")] public static extern void wsync_free(IntPtr p);
    [DllImport("window_sync_core.dll")] public static extern void wsync_shutdown();

    // 收到的消息：(时间戳 ticks, 消息号)
    static readonly List<long[]> _log = new List<long[]>();
    static readonly HashSet<IntPtr> _counting = new HashSet<IntPtr>();
    static WndProcDelegate _proc = WndProcImpl;   // 必须持有引用，否则委托会被回收
    static IntPtr _inst;

    static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (_counting.Contains(h) &&
            (m == 0x0100 || m == 0x0101 || m == 0x0201 || m == 0x0202))
        {
            lock (_log) { _log.Add(new long[] { DateTime.UtcNow.Ticks, m }); }
        }
        return DefWindowProcW(h, m, w, l);
    }

    public static IntPtr MakeWindow(string cls, string title, int x, bool count)
    {
        if (_inst == IntPtr.Zero) _inst = GetModuleHandleW(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = _inst,
            lpszClassName = cls,
        };
        RegisterClassW(ref wc);
        // WS_OVERLAPPEDWINDOW | WS_VISIBLE
        var h = CreateWindowExW(0, cls, title, 0x00CF0000u, x, 120, 420, 300,
            IntPtr.Zero, IntPtr.Zero, _inst, IntPtr.Zero);
        ShowWindow(h, 5);
        if (count) _counting.Add(h);
        return h;
    }

    /// 真正把窗口提到前台。
    ///
    /// 裸调 `SetForegroundWindow` 会被系统的前台锁拒掉（本次实测：返回 true 但前台没变），
    /// 必须先 AttachThreadInput 到当前前台线程的输入队列，改完再解绑。
    public static bool ForceForeground(IntPtr h)
    {
        var fg = GetForegroundWindow();
        var tidFg = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var tidSelf = GetCurrentThreadId();
        bool attached = tidFg != 0 && tidFg != tidSelf && AttachThreadInput(tidSelf, tidFg, true);
        try
        {
            ShowWindow(h, 5);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attached) AttachThreadInput(tidSelf, tidFg, false);
        }
        for (int i = 0; i < 20 && GetForegroundWindow() != h; i++) System.Threading.Thread.Sleep(25);
        return GetForegroundWindow() == h;
    }

    public static long Now() { return DateTime.UtcNow.Ticks; }

    public static void Clear() { lock (_log) { _log.Clear(); } }

    /// 统计某个时间点之后收到的指定消息条数
    public static int CountAfter(long since, uint msg)
    {
        int n = 0;
        lock (_log) { foreach (var e in _log) if (e[1] == msg && e[0] >= since) n++; }
        return n;
    }

    public static int CountAll(uint msg)
    {
        int n = 0;
        lock (_log) { foreach (var e in _log) if (e[1] == msg) n++; }
        return n;
    }

    /// 消息序列（调试用）
    public static string Sequence()
    {
        var sb = new System.Text.StringBuilder();
        lock (_log)
        {
            foreach (var e in _log)
            {
                string name = e[1] == 0x0100 ? "KEYDOWN" : e[1] == 0x0101 ? "KEYUP"
                            : e[1] == 0x0201 ? "LBUTTONDOWN" : "LBUTTONUP";
                sb.Append(name).Append(' ');
            }
        }
        return sb.ToString().Trim();
    }

    /// 把光标挪到窗口正中。
    ///
    /// 必须做：`SendInput` 的鼠标事件打在当前**光标所在**的窗口上。光标留在别处时，
    /// 这一次点击会把别的窗口激活 —— 主窗口一失焦，看门狗立刻按「控制权离开」补投抬起，
    /// 于是测试看到的抬起其实来自失焦兜底，而不是「关开关」这条路径。
    public static void MoveCursorToCenter(IntPtr h)
    {
        RECT r;
        if (GetWindowRect(h, out r))
            SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    static void Send(INPUT inp) { SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT))); }

    public static void KeyDown(ushort vk)
    {
        var i = new INPUT { type = 1 };
        i.u.ki = new KEYBDINPUT { wVk = vk };
        Send(i);
    }

    public static void KeyUp(ushort vk)
    {
        var i = new INPUT { type = 1 };
        i.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0002 };  // KEYEVENTF_KEYUP
        Send(i);
    }

    public static void LeftDown()
    {
        var i = new INPUT { type = 0 };
        i.u.mi = new MOUSEINPUT { dwFlags = 0x0002 };  // MOUSEEVENTF_LEFTDOWN
        Send(i);
    }

    public static void LeftUp()
    {
        var i = new INPUT { type = 0 };
        i.u.mi = new MOUSEINPUT { dwFlags = 0x0004 };  // MOUSEEVENTF_LEFTUP
        Send(i);
    }

    public static string Free(IntPtr p)
    {
        var s = Marshal.PtrToStringUTF8(p);
        wsync_free(p);
        return s;
    }
}
"@

$dllDir = "D:\app\window-sync-winui\core\target\release"
Set-Location $dllDir
$env:PATH = "$dllDir;$env:PATH"

$fail = 0
function Check($name, $cond, $detail) {
    if ($cond) { "  OK   $name  $detail" } else { "  FAIL $name  $detail"; $script:fail++ }
}
function Pump([int]$ms) {
    $deadline = [DateTime]::Now.AddMilliseconds($ms)
    while ([DateTime]::Now -lt $deadline) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 5
    }
}

"── 0. 建窗口：主窗口置前，从窗口只负责记账 ──"
$master = [OptTest]::MakeWindow("OptMasterXXXX", "选项回归 - 主窗口", 60, $false)
$slave  = [OptTest]::MakeWindow("OptSlaveXXXX", "选项回归 - 从窗口", 520, $true)
Pump 200
[void][OptTest]::ForceForeground($master)
Pump 400
Check "主窗口已在前台" ([OptTest]::GetForegroundWindow() -eq $master) "fg=$([OptTest]::GetForegroundWindow()) master=$master"

$m = $master.ToInt64(); $s = $slave.ToInt64()
$cfg = "{`"master_hwnd`":$m,`"slave_hwnds`":[$s],`"sync_mouse`":true,`"sync_keyboard`":true}"
$r = [OptTest]::Free([OptTest]::wsync_start_sync($cfg))
Check "启动同步" ($r -match '"ok":true') $r
Pump 300

"── 1. 键盘：按住 W → 关掉键盘同步 → 从窗口必须收到抬起 ──"
[void][OptTest]::ForceForeground($master)
Pump 200
Check "按键前主窗口在前台" ([OptTest]::GetForegroundWindow() -eq $master) "fg=$([OptTest]::GetForegroundWindow())"
[OptTest]::Clear()
[OptTest]::KeyDown(0x57)          # W
Pump 250
$downs = [OptTest]::CountAll(0x0100)
Check "按下已转发" ($downs -ge 1) "KEYDOWN=$downs"

# 判定用「计数增量」而不是时间戳：Windows 时钟粒度 15.6ms，事件可能落在同一拍里
$upBefore = [OptTest]::CountAll(0x0101)
$r = [OptTest]::Free([OptTest]::wsync_update_sync_options('{"sync_mouse":true,"sync_keyboard":false,"sync_clipboard":false}'))
Check "关闭键盘同步" ($r -match '"ok":true') $r
Pump 350
$ups = [OptTest]::CountAll(0x0101) - $upBefore
Check "关掉键盘同步后补投了抬起" ($ups -ge 1) "KEYUP 增量=$ups 序列=[$([OptTest]::Sequence())]"

# 物理松开：钩子此时不转发，也不该再产生额外的按下
[OptTest]::KeyUp(0x57)
Pump 200

"── 2. 鼠标：按住左键 → 关掉鼠标同步 → 从窗口必须收到抬起 ──"
# 鼠标这段天生比键盘脆：SendInput 的点击打在当前光标下的窗口上，前台锁又可能不让
# 我们的窗口上位。点了没转发就重试，别把环境抖动当成功能缺陷（也别反过来把缺陷当抖动）。
$btnDown = 0
for ($i = 1; $i -le 3 -and $btnDown -lt 1; $i++) {
    [void][OptTest]::ForceForeground($master)
    Pump 200
    [OptTest]::MoveCursorToCenter($master)
    Pump 150
    [OptTest]::Clear()
    [OptTest]::LeftDown()
    Pump 250
    $btnDown = [OptTest]::CountAll(0x0201)
    if ($btnDown -lt 1) {
        "   （第 $i 次点击没被转发：fg=$([OptTest]::GetForegroundWindow()) master=$master，重试）"
        [OptTest]::LeftUp()
        Pump 150
    }
}
Check "左键按下已转发" ($btnDown -ge 1) "LBUTTONDOWN=$btnDown"

$upBefore2 = [OptTest]::CountAll(0x0202)
$r = [OptTest]::Free([OptTest]::wsync_update_sync_options('{"sync_mouse":false,"sync_keyboard":false,"sync_clipboard":true}'))
Check "关闭鼠标同步" ($r -match '"ok":true') $r
Pump 350
$btnUp = [OptTest]::CountAll(0x0202) - $upBefore2
Check "关掉鼠标同步后补投了抬起" ($btnUp -ge 1) "LBUTTONUP 增量=$btnUp 序列=[$([OptTest]::Sequence())]"
[OptTest]::LeftUp()
Pump 200

"── 3. 重新打开开关：状态表不能留脏数据 ──"
$r = [OptTest]::Free([OptTest]::wsync_update_sync_options('{"sync_mouse":true,"sync_keyboard":true,"sync_clipboard":false}'))
Check "重新开启" ($r -match '"ok":true') $r
Pump 150
[void][OptTest]::ForceForeground($master)
Pump 250
Check "重开前主窗口在前台" ([OptTest]::GetForegroundWindow() -eq $master) "fg=$([OptTest]::GetForegroundWindow())"
[OptTest]::Clear()
[OptTest]::KeyDown(0x41)          # A
Pump 120
[OptTest]::KeyUp(0x41)
Pump 250
Check "重开后按下仍能转发" ([OptTest]::CountAll(0x0100) -ge 1) "KEYDOWN=$([OptTest]::CountAll(0x0100))"
Check "重开后抬起仍能转发" ([OptTest]::CountAll(0x0101) -ge 1) "KEYUP=$([OptTest]::CountAll(0x0101))"

"── 4. 收尾 ──"
$r = [OptTest]::Free([OptTest]::wsync_stop_sync())
Check "停止同步" ($r -match '"ok":true') $r
[OptTest]::wsync_shutdown()
[void][OptTest]::DestroyWindow($slave)
[void][OptTest]::DestroyWindow($master)

if ($fail -eq 0) { "ALL OK" } else { "$fail 项失败" }
