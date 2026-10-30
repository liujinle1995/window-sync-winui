using System.Runtime.InteropServices;
using LeXiangSync.Models;
using Microsoft.UI.Dispatching;

namespace LeXiangSync.Services;

/// <summary>
/// 同步期间的"收尾"服务：只补那些**没有同步过去的抬起消息**，其余什么都不做，同步不会因此停止。
/// <para>
/// 背景：Rust 核心只在 <c>GetForegroundWindow() == master</c> 时才转发鼠标与键盘消息。
/// 所以当控制权离开主窗口的那一刻（alt-tab、点击桌面、点击本工具界面、点击第三方窗口），
/// 正在按住的鼠标键 / 键盘键的那一次抬起都不会被转发，
/// 从窗口会一直停在"按住"状态 —— 表现为角色一直走、视角一直转、左键一直拖着。
/// </para>
/// <para>
/// 因此在控制权离开主窗口时，对仍处于按下状态的鼠标键和虚拟键，
/// 给每个从窗口补投一条对应的抬起消息（WM_*BUTTONUP / WM_KEYUP）。
/// 只补抬起、不补按下，且只发给从窗口：多余的按下等于在游戏里真点一下，会造成误动作。
/// </para>
/// <para>
/// 另外还承担「悬停同步」（<see cref="HoverSync"/>）：打开后鼠标移入主窗口自动开始同步、
/// 移出主窗口自动停止。这里复用了同一套光标位置判定，避免两处各判一次得出不同结论。
/// </para>
/// </summary>
public sealed partial class CursorWatchService : IDisposable
{
    /// <summary>边缘容差：贴着边框移动时不要来回误判。</summary>
    private const int Tolerance = 12;

    /// <summary>轮询间隔。只做几次 Win32 查询，开销可忽略；偏密是为了不错过快速划出的瞬间。</summary>
    private const int PollMs = 80;

    /// <summary>悬停同步：连续几次判定「光标在主窗口内」才启动。1 次 ≈ 80ms，移进去要立刻跟手。</summary>
    private const int EnterTicks = 1;

    /// <summary>悬停同步：连续几次判定「光标已移出」才停止，免得贴着窗口边框微抖就来回启停。</summary>
    private const int LeaveTicks = 3;

    private const uint WM_KEYUP = 0x0101;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONUP = 0x0208;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int VK_BACK = 0x08;
    private const int VK_LAST = 0xFE;

    private const int BtnLeft = 1;
    private const int BtnRight = 2;
    private const int BtnMiddle = 4;

    private readonly DispatcherQueueTimer _timer;
    private readonly SyncEngine _engine;
    private readonly LogService _log;

    /// <summary>上一轮是否还在同步，用来捕捉"停止同步"的瞬间再做一次收尾。</summary>
    private bool _wasActive;

    /// <summary>
    /// 是否正在被控制：只有"前台是主窗口且光标在其范围内"过，才允许在离开时补消息。
    /// 否则从本工具界面点「开始同步」的那一刻会被立刻误判为已经离开。
    /// </summary>
    private bool _armed;

    /// <summary>离开前最后一次采样到的按下状态。</summary>
    private int _heldMouse;

    private readonly List<int> _heldKeys = new();

    /// <summary>离开前最后一次光标屏幕坐标。</summary>
    private POINT _lastCursor;

    // ── 悬停同步 ──────────────────────────────────────────────────────────

    /// <summary>已确认的悬停状态：true = 光标当前在主窗口内。只有它翻转的那一刻才启停。</summary>
    private bool _hoverInside;

    /// <summary>「在内」的连续计数，达到 <see cref="EnterTicks"/> 才确认为进入。</summary>
    private int _pendingInside;

    /// <summary>「在外」的连续计数，达到 <see cref="LeaveTicks"/> 才确认为离开。</summary>
    private int _pendingOutside;

    private bool _hoverSync;

    /// <summary>
    /// 悬停同步：打开后鼠标移入主窗口自动开始同步，移出主窗口自动停止。
    /// <para>
    /// 只在「进出边沿」那一刻启停，所以手动点按钮 / 按热键的结果不会被下一轮轮询立刻改回去；
    /// 反过来说，手动开始时鼠标并不在主窗口上，也不会被当成「移出」而马上停掉 ——
    /// 那样用户会觉得点了开始没反应。同步会一直保持到鼠标真的进出一次为止。
    /// </para>
    /// </summary>
    public bool HoverSync
    {
        get => _hoverSync;
        set
        {
            if (_hoverSync == value) return;
            _hoverSync = value;
            ResetHover();
        }
    }

    public CursorWatchService(SyncEngine engine, LogService log)
    {
        _engine = engine;
        _log = log;

        // 读引擎状态、写日志要在 UI 线程；轮询本身开销可忽略
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PollMs);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var master = _engine.Master;

        // 悬停同步先判：它可能在这一轮把同步停掉，下面的收尾逻辑要看停之后的 IsActive。
        // point 要先给默认值：master 为 null 时不会走到 IsCursorInside，编译器不认短路后的 out
        POINT point = default;
        var inside = master is not null && IsCursorInside((IntPtr)master.Hwnd, out point);
        UpdateHover(inside);

        if (!_engine.IsActive)
        {
            if (_wasActive)
            {
                _wasActive = false;
                // 停同步那一刻同样收尾，保证从窗口不会停在按住 / 卡键状态
                if (_armed && master is { } stopped) ReleaseMissing((IntPtr)stopped.Hwnd);
                Disarm();
            }
            return;
        }

        _wasActive = true;
        if (master is null) return;

        var handle = (IntPtr)master.Hwnd;

        // 前台是主窗口 + 光标在范围内 = 控制权还在我们手上
        if (inside && GetForegroundWindow() == handle)
        {
            _armed = true;
            _lastCursor = point;
            SampleHeldInputs();
            return;
        }

        if (!_armed) return;

        ReleaseMissing(handle);
        Disarm();
    }

    /// <summary>
    /// 光标是否落在主窗口矩形内（含边缘容差）。主窗口已销毁 / 最小化时矩形退化，一律判为不在内。
    /// </summary>
    private static bool IsCursorInside(IntPtr handle, out POINT cursor)
    {
        cursor = default;

        // 主窗口已销毁 / 最小化时矩形退化，此时不做判断
        if (GetWindowRect(handle, out var rect) == 0) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
        if (GetCursorPos(out cursor) == 0) return false;

        return cursor.X >= rect.Left - Tolerance && cursor.X <= rect.Right + Tolerance
               && cursor.Y >= rect.Top - Tolerance && cursor.Y <= rect.Bottom + Tolerance;
    }

    /// <summary>
    /// 悬停同步的状态机。**只在进出边沿的那一刻启停**，并要求连续几轮判定一致，
    /// 免得贴着窗口边框微抖就来回启停（每次启停 Rust 侧都要装卸钩子，代价不小）。
    /// </summary>
    private void UpdateHover(bool inside)
    {
        if (!_hoverSync) return;

        if (inside)
        {
            _pendingOutside = 0;

            // 已经在内：不重复 Start。同步若被用户手动停了，就保持停止，等他再进出一次
            if (_hoverInside) return;
            if (++_pendingInside < EnterTicks) return;

            _pendingInside = 0;
            _hoverInside = true;

            if (_engine.IsActive || !_engine.CanStart) return;
            _engine.Start();
            _log.Add("鼠标移入主窗口，已自动开始同步");
            return;
        }

        _pendingInside = 0;
        if (!_hoverInside) return;
        if (++_pendingOutside < LeaveTicks) return;

        _pendingOutside = 0;
        _hoverInside = false;

        if (!_engine.IsActive) return;
        _engine.Stop();
        _log.Add("鼠标移出主窗口，已自动停止同步");
    }

    /// <summary>开关状态变化时复位状态机，避免残留的计数在新状态下立刻触发一次启停。</summary>
    private void ResetHover()
    {
        _hoverInside = false;
        _pendingInside = 0;
        _pendingOutside = 0;
    }

    private void Disarm()
    {
        _armed = false;
        _heldMouse = 0;
        _heldKeys.Clear();
    }

    /// <summary>采样当前仍处于按下状态的鼠标键与虚拟键，离开时用得着。</summary>
    private void SampleHeldInputs()
    {
        _heldMouse = 0;
        if (GetAsyncKeyState(VK_LBUTTON) < 0) _heldMouse |= BtnLeft;
        if (GetAsyncKeyState(VK_RBUTTON) < 0) _heldMouse |= BtnRight;
        if (GetAsyncKeyState(VK_MBUTTON) < 0) _heldMouse |= BtnMiddle;

        _heldKeys.Clear();
        for (var vk = VK_BACK; vk <= VK_LAST; vk++)
        {
            // 最高位为 1 表示当前物理按下
            if (GetAsyncKeyState(vk) < 0) _heldKeys.Add(vk);
        }
    }

    /// <summary>把缺失的抬起消息补投给从窗口；没有按下状态就什么都不做。</summary>
    private void ReleaseMissing(IntPtr master)
    {
        if (_heldMouse == 0 && _heldKeys.Count == 0) return;

        var targets = _engine.Slaves
            .Select(s => (IntPtr)s.Hwnd)
            .Where(IsWindow)
            .ToList();

        if (targets.Count == 0) return;

        // 坐标沿用核心转发鼠标时的那套：lParam 放客户区坐标（滚轮才用屏幕坐标）。
        // 这里先取主窗口客户区坐标，再按每个从窗口的客户区比例换算 —— 尺寸不一致时
        // 直接沿用主窗口的值会把抬起消息投到从窗口边缘之外。
        var client = _lastCursor;
        ScreenToClient(master, ref client);
        var (masterW, masterH) = ClientSize(master);

        var parts = new List<string>();

        foreach (var target in targets)
        {
            var point = MapPoint(client, masterW, masterH, target);
            var mouseLp = (IntPtr)(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));

            if ((_heldMouse & BtnLeft) != 0) PostMessageA(target, WM_LBUTTONUP, UIntPtr.Zero, mouseLp);
            if ((_heldMouse & BtnRight) != 0) PostMessageA(target, WM_RBUTTONUP, UIntPtr.Zero, mouseLp);
            if ((_heldMouse & BtnMiddle) != 0) PostMessageA(target, WM_MBUTTONUP, UIntPtr.Zero, mouseLp);

            foreach (var vk in _heldKeys)
            {
                // lParam 与核心一致：repeat = 1，扫描码在高 16 位
                var scan = MapVirtualKeyA((uint)vk, 0) & 0xFF;
                var lp = unchecked((IntPtr)(int)(0xC0000001u | ((uint)scan << 16)));
                PostMessageA(target, WM_KEYUP, (UIntPtr)(uint)vk, lp);
            }
        }

        if ((_heldMouse & BtnLeft) != 0) parts.Add("左键");
        if ((_heldMouse & BtnRight) != 0) parts.Add("右键");
        if ((_heldMouse & BtnMiddle) != 0) parts.Add("中键");
        parts.AddRange(_heldKeys.Select(KeyName));

        _log.Add($"控制权离开主窗口时「{string.Join('、', parts)}」仍按住，"
                 + $"已给 {targets.Count} 个从窗口补投抬起消息，同步继续运行");
    }

    private static string KeyName(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        _ => $"0x{vk:X2}",
    };

    /// <summary>客户区尺寸；取不到（窗口已销毁 / 最小化）时返回 (0, 0)。</summary>
    private static (int Width, int Height) ClientSize(IntPtr hwnd)
        => GetClientRect(hwnd, out var rect)
            ? (rect.Right - rect.Left, rect.Bottom - rect.Top)
            : (0, 0);

    /// <summary>
    /// 按客户区比例把主窗口坐标换算到目标窗口。两端尺寸一致时原值返回；
    /// 任一侧尺寸未知（0）时也原值返回，宁可偏一点也不要全部压到左上角。
    /// </summary>
    private static POINT MapPoint(POINT point, int srcWidth, int srcHeight, IntPtr target)
    {
        if (srcWidth <= 0 || srcHeight <= 0) return point;

        var (dstWidth, dstHeight) = ClientSize(target);
        if (dstWidth <= 0 || dstHeight <= 0) return point;
        if (dstWidth == srcWidth && dstHeight == srcHeight) return point;

        var x = (int)Math.Round(point.X * (double)dstWidth / srcWidth);
        var y = (int)Math.Round(point.Y * (double)dstHeight / srcHeight);
        return new POINT { X = Math.Clamp(x, 0, dstWidth - 1), Y = Math.Clamp(y, 0, dstHeight - 1) };
    }

    public void Dispose() => _timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial int ScreenToClient(IntPtr hwnd, ref POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyA(uint uCode, uint uMapType);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageA(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);
}
