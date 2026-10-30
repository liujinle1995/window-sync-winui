using LeXiangSync.Native;

namespace LeXiangSync.Services;

/// <summary>
/// 单实例限制：同一时间只允许一个乐享同步在跑。
/// 第二个进程启动时会把已有窗口叫到前台，然后自己退出 —— 用户不会等到第二个窗口，
/// 只会看到原来那个浮上来。
/// <para>
/// 判定用命名互斥体；找窗口用打在 HWND 上的自定义属性 ——
/// 不用窗口标题是因为标题属于 UI 文案，将来改一句就可能失效，而属性名是我们内部定的标识。
/// </para>
/// <para>
/// 为什么必须限制：两个进程会各自加载一份 Rust 核心，钩子与全局状态互相干扰；
/// 配置文件的写入也会互相打断（见 <see cref="DataStore"/> 的临时文件处理）。
/// </para>
/// </summary>
public sealed class SingleInstanceGuard
{
    /// <summary>
    /// 互斥体名。<c>Local\</c> 只对当前登录会话生效：换一个 Windows 用户仍可各跑一份，
    /// 也不会因为别人的机器上装了同名程序就起不来。
    /// </summary>
    private const string MutexName = @"Local\LeXiangSync.SingleInstance";

    /// <summary>打在主窗口 HWND 上的标记。带版本后缀，免得和其它程序撞名。</summary>
    private const string WindowProp = "LeXiangSync.MainWindow.v1";

    private const int SwShow = 5;
    private const int SwRestore = 9;

    /// <summary>
    /// 找窗口的耐心（10 × 150ms ≈ 1.5s）：点开第二个实例时，第一个可能才刚起来、窗口还没显示。
    /// </summary>
    private const int FindAttempts = 10;
    private const int FindIntervalMs = 150;

    /// <summary>互斥体必须活到进程结束：它被回收的话第二个进程就能再进来。</summary>
    private static Mutex? s_mutex;

    /// <summary>
    /// 尝试占坑。返回 true 表示本进程是第一个实例（可以照常往下建窗口）。
    /// 返回 false 时不要建窗口，先调 <see cref="BringExistingToFront"/> 再退出。
    /// </summary>
    public static bool TryAcquire()
    {
        s_mutex = new Mutex(true, MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>
    /// 把主窗口登记成「本应用的窗口」，第二个进程按它认人。窗口一销毁属性自动失效，无需清理。
    /// </summary>
    public static void Mark(IntPtr windowHandle) =>
        Win32.SetPropW(windowHandle, WindowProp, new IntPtr(1));

    /// <summary>
    /// 找到已运行的那个主窗口并把它叫到前台。返回 true 表示找到并已激活。
    /// <para>
    /// 找不到的情况只可能是老进程正在退出（窗口已销毁、互斥体还没被系统回收），
    /// 这时调用方应当照常启动，否则应用就永远起不来了。
    /// </para>
    /// </summary>
    public static bool BringExistingToFront()
    {
        IntPtr found = IntPtr.Zero;

        for (var i = 0; i < FindAttempts && found == IntPtr.Zero; i++)
        {
            if (i > 0) Thread.Sleep(FindIntervalMs);
            found = FindWindowWithProp();
        }

        if (found == IntPtr.Zero) return false;

        Activate(found);
        return true;
    }

    /// <summary>还原 / 显示 / 抢焦点，三条一条都不能省。</summary>
    private static void Activate(IntPtr hwnd)
    {
        // 最小化要先还原：否则窗口虽然被激活了却仍缩在任务栏里
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, SwRestore);

        // 最小到托盘时窗口是隐藏的（AppWindow.Hide 就是隐藏 HWND），得先显示出来才拿得到焦点
        Win32.ShowWindow(hwnd, SwShow);

        // 焦点是稀缺资源：只有前台进程（或刚收到过输入的进程）才有权把它交给别人。
        // 第二个起来的进程连窗口都没有，直接 SetForegroundWindow 常被系统忽略 ——
        // 结果就是窗口浮上来了、但输入焦点还在别处。把自己临时挂到对方输入队列上是标准解法。
        var targetThread = Win32.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        var selfThread = Win32.GetCurrentThreadId();
        var attached = targetThread != selfThread && Win32.AttachThreadInput(selfThread, targetThread, true);

        Win32.BringWindowToTop(hwnd);
        Win32.SetForegroundWindow(hwnd);

        if (attached) Win32.AttachThreadInput(selfThread, targetThread, false);
    }

    private static IntPtr FindWindowWithProp()
    {
        IntPtr found = IntPtr.Zero;

        Win32.EnumWindows((hWnd, _) =>
        {
            if (Win32.GetPropW(hWnd, WindowProp) == IntPtr.Zero) return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }
}
