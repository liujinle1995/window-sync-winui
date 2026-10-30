using LeXiangSync.Services;
using Microsoft.UI.Xaml;

namespace LeXiangSync;

public partial class App : Application
{
    /// <summary>全局唯一主窗口，供托盘、快捷键等组件回唤。</summary>
    public static MainWindow MainWindow { get; private set; } = null!;

    public static LogService Logs { get; private set; } = null!;
    public static DataStore Store { get; private set; } = null!;
    public static SyncEngine Sync { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Dump("AppDomain", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Dump("Task", e.Exception);
    }

    /// <summary>把未被 UI 层捕获的异常落盘，便于排查启动期崩溃。</summary>
    internal static void Dump(string tag, object exception)
    {
        try
        {
            var file = Path.Combine(Path.GetTempPath(), "LeXiangSync.crash.log");
            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {tag}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            /* 崩溃路径上不能再抛 */
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 单实例：已经有在跑的就把它叫到前台，本进程立刻退出。
        // 必须在创建任何服务之前判断 —— 两个进程各加载一份 Rust 核心，钩子会互相干扰
        if (!SingleInstanceGuard.TryAcquire())
        {
            SingleInstanceGuard.BringExistingToFront();
            Environment.Exit(0);
            return;
        }

        // 组合根：这三个服务与窗口无关，先于窗口创建
        Logs = new LogService();
        Store = new DataStore();
        Sync = new SyncEngine(Logs);

        MainWindow = new MainWindow();
        MainWindow.Activate();

        if (IsSoundCheckRequested()) SoundService.RunSelfCheck();
    }

    /// <summary>环境变量优先：命令行参数会被启动流程吞掉，环境变量一定到得了。</summary>
    private static bool IsSoundCheckRequested() =>
        Environment.GetEnvironmentVariable("LEXIANG_SOUNDCHECK") == "1" ||
        Environment.GetCommandLineArgs().Any(
            a => string.Equals(a, "--soundcheck", StringComparison.OrdinalIgnoreCase));

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 记录后标记为已处理，避免整个进程被 XAML 异常击穿（同步引擎仍在后台运行）
        e.Handled = true;
        Dump("Xaml", e.Exception);
    }
}
