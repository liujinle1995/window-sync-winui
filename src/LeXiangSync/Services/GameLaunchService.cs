using System.Diagnostics;
using LeXiangSync.Models;
using LeXiangSync.Native;

namespace LeXiangSync.Services;

/// <summary>
/// 多开启动流程：关掉旧进程 → 逐个沙箱拉起启动器 → 依次点「开始游戏 / 进入游戏 / 登录游戏」
/// → 最后把窗口按布局摆好。整个流程可中断、每一步都上报进度。
/// <para>
/// 点击一律走真实输入（SetForegroundWindow + SetCursorPos + mouse_event），
/// 不用 PostMessage：游戏客户端普遍直接轮询原始输入状态，伪造的窗口消息它不认。
/// </para>
/// </summary>
public sealed class GameLaunchService
{
    /// <summary>进度上报。回调运行在后台线程上，UI 侧需自行封送。</summary>
    public event Action<string>? Reported;

    private void Report(string message) => Reported?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>
    /// 跑一遍多开流程。<paramref name="accounts"/> 是本次要操作的沙箱集合 —— 界面上选了哪一组就传哪一组，
    /// 与 <see cref="GameLaunchConfig.Accounts"/>（全部号）区分开。
    /// </summary>
    public async Task RunAsync(GameLaunchConfig config, IReadOnlyList<GameAccount> accounts, CancellationToken token)
    {
        WindowApi.SetProcessDPIAware();

        await CloseOldProcessesAsync(config, token);

        await LaunchSandboxesAsync(config, accounts, token);

        var boxes = BoxNames(accounts);

        var launchers = await WaitForWindowsAsync(
            config.LauncherProcessName, boxes, config.LauncherWaitSeconds, "启动器", token);

        if (launchers.Count < accounts.Count)
            await RetryMissingBoxesAsync(config, accounts, launchers, token);

        Report($"启动器就绪：{launchers.Count}/{accounts.Count}");

        // 「开始游戏」只点一次：它是"启动游戏"这个动作，点两下会开出第二个客户端。
        await ClickAllAsync(launchers, config, config.StartButton, "开始游戏",
            config.StartClickDelaySeconds, attempts: 1, token);

        // 等游戏进程。等不到也不去重启沙箱 —— 试过，那样会把正在加载的客户端打断，
        // 反而更糟。照常往下点。
        var games = await WaitForWindowsAsync(
            config.GameProcessName, boxes, config.GameWaitSeconds, "游戏", token);

        Report($"游戏进程就绪：{games.Count}/{accounts.Count}");

        // 客户端刚起来时布局还没稳定，多等一会儿再点，否则会点空
        Report("等待游戏界面加载…");
        await Task.Delay(TimeSpan.FromSeconds(10), token);

        // 登录两步：先点「进入游戏」，再点「登录游戏」。
        // 位置和顺序都取自配置，客户端改版后在界面上调即可。
        await ClickAllAsync(games, config, config.EnterButton, "进入游戏",
            config.EnterClickDelaySeconds, attempts: 2, token);

        games = CollectWindows(config.GameProcessName, boxes);
        await ClickAllAsync(games, config, config.LoginButton, "登录游戏",
            config.LoginClickDelaySeconds, attempts: 2, token);

        Report($"等待游戏进入主界面（{config.SettleSeconds}s）…");
        await Task.Delay(TimeSpan.FromSeconds(config.SettleSeconds), token);

        await ArrangeAsync(config, accounts);


        Report("多开启动完成");
    }

    // ── 步骤 ──────────────────────────────────────────────────────────────

    private async Task CloseOldProcessesAsync(GameLaunchConfig config, CancellationToken token)
    {
        foreach (var name in new[] { config.GameProcessName, config.LauncherProcessName })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 已经退出、或被保护到杀不掉，跳过即可
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        Report("已关闭旧的启动器与游戏进程");
        await Task.Delay(TimeSpan.FromSeconds(2), token);
    }

    private async Task LaunchSandboxesAsync(
        GameLaunchConfig config, IReadOnlyList<GameAccount> accounts, CancellationToken token)
    {
        if (!File.Exists(config.SandboxiePath))
        {
            Report($"找不到 Sandboxie 的 Start.exe：{config.SandboxiePath}");
            return;
        }

        if (!File.Exists(config.LauncherPath))
        {
            Report($"找不到游戏启动器：{config.LauncherPath}");
            return;
        }

        foreach (var account in accounts)
        {
            token.ThrowIfCancellationRequested();
            StartInBox(config, account.Name);
            Report($"已在沙箱启动 {account.Name}");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(config.LaunchIntervalSeconds, 1)), token);
        }
    }

    private void StartInBox(GameLaunchConfig config, string box)
    {
        try
        {
            var info = new ProcessStartInfo(config.SandboxiePath) { UseShellExecute = true };
            // ArgumentList 会自己处理转义，比手工拼引号可靠
            info.ArgumentList.Add($"/box:{box}");
            info.ArgumentList.Add(config.LauncherPath);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Report($"沙箱 {box} 启动失败：{ex.Message}");
        }
    }

    /// <summary>某个沙箱没起来时补一次。</summary>
    private async Task RetryMissingBoxesAsync(
        GameLaunchConfig config, IReadOnlyList<GameAccount> accounts, List<GameWindow> started, CancellationToken token)
    {
        var missing = accounts
            .Where(a => started.All(w => w.Box != a.Name))
            .ToList();

        if (missing.Count == 0) return;

        foreach (var account in missing)
        {
            Report($"补启缺失的沙箱 {account.Name}");
            StartInBox(config, account.Name);
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }

        await Task.Delay(TimeSpan.FromSeconds(15), token);
    }

    private async Task<List<GameWindow>> WaitForWindowsAsync(
        string processName, List<string> boxes, int timeoutSeconds, string label, CancellationToken token)
    {
        if (timeoutSeconds <= 0) return CollectWindows(processName, boxes);

        for (var waited = 0; waited < timeoutSeconds; waited += 3)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            var windows = CollectWindows(processName, boxes);
            Report($"{label}就绪 {windows.Count}/{boxes.Count}（已等待 {waited + 3}s）");
            if (windows.Count >= boxes.Count) return windows;
        }

        return CollectWindows(processName, boxes);
    }

    /// <summary>
    /// 挨个点。<paramref name="attempts"/> 是每个窗口最多试几轮：点到了立刻收工，
    /// 只有"被挡住 / 窗口失效"才会再来一轮（换个时机关窗可能就解除遮挡了）。
    /// </summary>
    private async Task ClickAllAsync(
        List<GameWindow> windows, GameLaunchConfig config, RelativePoint point, string label,
        int intervalSeconds, int attempts, CancellationToken token)
    {
        if (windows.Count == 0)
        {
            Report($"没有可点击的窗口，跳过「{label}」");
            return;
        }

        foreach (var window in windows)
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!WindowApi.IsWindow(window.Handle)) break;

                if (await ClickWindowAsync(config, window, point, label, token)) break;
                if (attempt < attempts) await Task.Delay(TimeSpan.FromSeconds(3), token);
            }

            if (intervalSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token);
        }
    }

    /// <summary>
    /// 用 OCR 找按钮：认到文字就点文字中心，认不到返回 null，由调用方回落到固定坐标。
    /// <para>
    /// 窗口必须在前台且完整可见 —— 抓的是屏幕，被别的东西盖住就只会认出遮挡物的内容。
    /// </para>
    /// </summary>
    /// <summary>单次 OCR 最多等这么久。到点没结果就当没有 OCR，按固定坐标走。</summary>
    private async Task<bool> ClickWindowAsync(
        GameLaunchConfig config, GameWindow window, RelativePoint point, string label, CancellationToken token)
    {
        CloseExtraWindows(config, window.Box);

        if (!WindowApi.GetWindowRect(window.Handle, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            Report($"跳过 {window.Box}「{label}」：窗口已失效");
            return false;
        }

        WindowApi.Activate(window.Handle);
        await Task.Delay(500, token);

        var x = rect.Left + (int)(rect.Width * point.X);
        var y = rect.Top + (int)(rect.Height * point.Y);

        // 先挪鼠标再判断遮挡：判断依据是鼠标下压着的那一层，不挪过去查到的是上一个窗口
        WindowApi.SetCursorPos(x, y);
        await Task.Delay(200, token);

        var under = WindowApi.RootWindow(WindowApi.WindowFromPoint(new WindowApi.POINT { X = x, Y = y }));
        if (under != window.Handle)
        {
            Report($"{window.Box}「{label}」被别的窗口挡住（点下去会点到别人），跳过");
            return false;
        }

        WindowApi.ClickAt(rect, point.X, point.Y, out var cx, out var cy);
        Report($"{label}：{window.Box} → ({cx}, {cy})");
        return true;
    }

    /// <summary>
    /// 关掉同一沙箱下除主窗口 / 游戏窗口之外的附属窗口。
    /// 认的是标题里带 [沙箱名] 的那一圈，除了认领窗口本身剩下的就是弹窗。
    /// </summary>
    private void CloseExtraWindows(GameLaunchConfig config, string box)
    {
        var boxes = new List<string> { box };
        var main = new HashSet<IntPtr>();

        foreach (var process in SafeGetProcesses(config.GameProcessName)
                     .Concat(SafeGetProcesses(config.LauncherProcessName)))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero &&
                    MatchBox(process.MainWindowTitle, boxes).Length > 0)
                {
                    main.Add(process.MainWindowHandle);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        var extra = new List<IntPtr>();
        WindowApi.EnumWindows((hwnd, _) =>
        {
            if (main.Contains(hwnd) || !WindowApi.IsWindowVisible(hwnd)) return true;
            if (MatchBox(WindowApi.WindowText(hwnd), boxes).Length == 0) return true;
            extra.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hwnd in extra)
        {
            Win32.PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Report($"{box}：关掉一个附属窗口（公告 / 弹窗）");
        }
    }

    /// <summary>把指定沙箱的窗口都关掉。补启前必须先清干净，否则新启动器会被当成已在运行。</summary>
    private void CloseWindowsOf(GameLaunchConfig config, IReadOnlyList<GameAccount> accounts)
    {
        var boxes = BoxNames(accounts);
        var closed = 0;

        WindowApi.EnumWindows((hwnd, _) =>
        {
            if (!WindowApi.IsWindowVisible(hwnd)) return true;
            if (MatchBox(WindowApi.WindowText(hwnd), boxes).Length == 0) return true;
            Win32.PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed++;
            return true;
        }, IntPtr.Zero);

        if (closed > 0) Report($"已关闭 {closed} 个残留窗口");
    }

    /// <summary>
    /// 把某个沙箱的窗口提到最前，用来分辨屏幕上这一堆长得一样的客户端分别是谁。
    /// 游戏进程没起来时退一步找启动器窗口。
    /// </summary>
    public Task<bool> FocusAsync(GameLaunchConfig config, string box) => Task.Run(() =>
    {
        WindowApi.SetProcessDPIAware();

        var candidates = new List<string> { box };
        var hit = CollectWindows(config.GameProcessName, candidates).FirstOrDefault()
                  ?? CollectWindows(config.LauncherProcessName, candidates).FirstOrDefault();

        if (hit is null) return false;

        WindowApi.Activate(hit.Handle);
        return true;
    });

    /// <summary>关客户端前给它自己退出的时间：够存档、够走完退出流程，超时才强杀。</summary>
    public const int GraceSeconds = 3;

    private const uint WM_CLOSE = 0x0010;

    private bool _ocrWarned;

    /// <summary>
    /// 关掉这一组沙箱里的客户端：先发 WM_CLOSE 请求正常退出，等 <see cref="GraceSeconds"/> 秒，
    /// 还赖着不走的按进程强杀（连进程树一起）。返回处理掉的窗口数。
    /// <para>
    /// 认人只认窗口标题里的 [<c>沙箱名</c>]，不限进程名 ——
    /// 沙箱里的启动器、游戏本体、弹出的确认框都带这个标记，一起收掉才不留残留；
    /// 反过来说，别的组的窗口标题里没有这个标记，绝不会误伤。
    /// </para>
    /// </summary>
    public async Task<int> CloseGroupAsync(GameLaunchConfig config, IReadOnlyList<GameAccount> accounts)
    {
        WindowApi.SetProcessDPIAware();

        var boxes = BoxNames(accounts);
        if (boxes.Count == 0)
        {
            Report("这一组里还没有沙箱");
            return 0;
        }

        var windows = CollectWindowsByTitle(boxes);
        if (windows.Count == 0)
        {
            Report("没有找到这一组的客户端窗口：客户端还没启动，或窗口标题里看不到 [沙箱名]");
            return 0;
        }

        foreach (var window in windows)
            Win32.PostMessageW(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        await Task.Delay(TimeSpan.FromSeconds(GraceSeconds));

        var forced = 0;
        foreach (var window in windows)
        {
            // 窗口已经没了就是自己退干净了，不用再动
            if (!WindowApi.IsWindow(window.Handle)) continue;
            if (KillByWindow(window)) forced++;
        }

        Report($"已关闭 {windows.Count} 个窗口（其中 {forced} 个是强制结束）");
        return windows.Count;
    }

    /// <summary>按窗口找进程、连进程树一起结束。返回是否真的杀掉了。</summary>
    private bool KillByWindow(GameWindow window)
    {
        WindowApi.GetWindowThreadProcessId(window.Handle, out var pid);
        if (pid == 0) return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill(entireProcessTree: true);
            Report($"{window.Box} 未响应退出请求，已强制结束（PID {pid}）");
            return true;
        }
        catch (Exception ex)
        {
            Report($"{window.Box} 结束失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 只按标题认领窗口（不限进程名）。启动器、游戏、弹窗的标题里都带 [沙箱名]，
    /// 一个沙箱可能对应好几个窗口，所以用列表而不是字典。
    /// </summary>
    private static List<GameWindow> CollectWindowsByTitle(List<string> boxes)
    {
        var found = new List<GameWindow>();

        WindowApi.EnumWindows((hwnd, _) =>
        {
            if (!WindowApi.IsWindowVisible(hwnd)) return true;
            var box = MatchBox(WindowApi.WindowText(hwnd), boxes);
            if (box.Length == 0) return true;
            found.Add(new GameWindow(hwnd, box));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>按布局摆放游戏窗口，尺寸统一以基准窗口为准。</summary>
    public async Task ArrangeAsync(GameLaunchConfig config, IReadOnlyList<GameAccount> accounts)
    {
        var windows = CollectWindows(config.GameProcessName, BoxNames(accounts));
        if (windows.Count == 0)
        {
            Report("没有找到游戏窗口，跳过排版");
            return;
        }

        var (width, height) = ReferenceSize(windows, config);
        Report($"排版基准尺寸：{width} x {height}");

        foreach (var window in windows)
        {
            var account = accounts.FirstOrDefault(a => a.Name == window.Box);
            if (account is null || account.Slot == LayoutSlot.None)
            {
                Report($"{window.Box} 未指定位置，保持原位");
                continue;
            }

            var (x, y) = SlotPosition(account.Slot, config, width, height);

            WindowApi.ShowWindow(window.Handle, WindowApi.SW_RESTORE);
            await Task.Delay(200);
            WindowApi.SetWindowPos(window.Handle, IntPtr.Zero, x, y, width, height, WindowApi.SWP_SHOWWINDOW);
            Report($"{window.Box} → ({x}, {y}) {width} x {height}");
        }

        Report("窗口排版完成");
    }

    private static (int Width, int Height) ReferenceSize(List<GameWindow> windows, GameLaunchConfig config)
    {
        var reference = windows.FirstOrDefault(w => w.Box == config.ReferenceBox) ?? windows[0];
        return WindowApi.GetWindowRect(reference.Handle, out var rect) && rect.Width > 0
            ? (rect.Width, rect.Height)
            : (1400, 830);
    }

    private static (int X, int Y) SlotPosition(LayoutSlot slot, GameLaunchConfig config, int width, int height) =>
        slot switch
        {
            LayoutSlot.TopLeft => (0, 0),
            LayoutSlot.BottomLeft => (0, config.ScreenHeight - height - config.TaskbarHeight),
            LayoutSlot.Center => ((config.ScreenWidth - width) / 2, (config.ScreenHeight - height) / 2),
            LayoutSlot.TopRight => (config.ScreenWidth - width, 0),
            LayoutSlot.BottomRight => (config.ScreenWidth - width, config.ScreenHeight - height - config.TaskbarHeight),
            _ => (0, 0),
        };

    // ── 窗口认领 ──────────────────────────────────────────────────────────

    private static List<string> BoxNames(IReadOnlyList<GameAccount> accounts) =>
        accounts.Select(a => a.Name).Where(n => n.Length > 0).ToList();

    /// <summary>
    /// 找到各个沙箱的窗口：优先用进程的 MainWindowHandle；
    /// 跨沙箱时这个值可能取不到，再扫一遍顶层窗口的标题里的 [<c>沙箱名</c>] 兜底。
    /// </summary>
    private static List<GameWindow> CollectWindows(string processName, List<string> boxes)
    {
        var found = new Dictionary<string, GameWindow>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in SafeGetProcesses(processName))
        {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) continue;
            var box = MatchBox(process.MainWindowTitle, boxes);
            if (box.Length == 0) continue;
            found[box] = new GameWindow(handle, box);
        }

        if (found.Count < boxes.Count)
        {
            WindowApi.EnumWindows((hwnd, _) =>
            {
                if (!WindowApi.IsWindowVisible(hwnd)) return true;
                var box = MatchBox(WindowApi.WindowText(hwnd), boxes);
                if (box.Length == 0 || found.ContainsKey(box)) return true;
                found[box] = new GameWindow(hwnd, box);
                return true;
            }, IntPtr.Zero);
        }

        return found.Values.ToList();
    }

    private static Process[] SafeGetProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch (Exception) { return Array.Empty<Process>(); }
    }

    /// <summary>标题形如 <c>[#] [fc] 梦幻西游：时空 [#]</c>，取方括号里那个沙箱名。</summary>
    private static string MatchBox(string? title, IReadOnlyList<string> boxes)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        return boxes.FirstOrDefault(b => title.Contains($"[{b}]", StringComparison.OrdinalIgnoreCase))
               ?? string.Empty;
    }

    /// <summary>
    /// 更新检测：点完「开始游戏」后只探第一个号，它在 <see cref="GameLaunchConfig.UpdateProbeSeconds"/>
    /// 秒内没动静，就按"这批号都要更新"处理 —— 给所有号点「更新」，再等更新跑完。
    /// 返回是否真的做了更新。
    /// <para>
    /// 判断更新结束靠两个信号：「开始游戏」按钮位置的颜色变了（更新期间是灰的，
    /// 更新完恢复可点的样子），或者游戏进程自己起来了。都不来就等到超时，
    /// 超时也照样往下走 —— 宁可点一次空按钮，也不能卡住不动。
    /// </para>
    /// </summary>
    private sealed record GameWindow(IntPtr Handle, string Box);
}
