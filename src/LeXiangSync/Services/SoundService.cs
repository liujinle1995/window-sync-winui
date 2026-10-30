using System.Runtime.InteropServices;
using LeXiangSync.Native;

namespace LeXiangSync.Services;

/// <summary>
/// 同步启停的听觉反馈。热键 / 托盘操作时看不到界面，全靠声音判断现在是开还是关。
/// 用的是 Kenney「Interface Sounds」（CC0）里的 open_002 / close_002：
/// 一个是明显的上行音（841 → 5074Hz），一个是它的镜像下行音（5074 → 829Hz），
/// 比系统事件音好分辨得多。许可见 Assets/Sounds/LICENSE.txt。
/// </summary>
public static class SoundService
{
    public const string StartFile = "sync-start.wav";
    public const string StopFile = "sync-stop.wav";

    private static string SoundDirectory => Path.Combine(AppPaths.AssetsDirectory, "Sounds");

    /// <param name="started">true = 开始同步，false = 停止同步。</param>
    public static void PlayToggle(bool started)
    {
        // 音效文件缺失 / 损坏时退回系统事件音，至少保证有声音而不是静默
        if (!TryPlay(started)) Win32.MessageBeep(started ? Win32.MB_ICONASTERISK : Win32.MB_ICONEXCLAMATION);
    }

    private static bool TryPlay(bool started)
    {
        var file = Path.Combine(SoundDirectory, started ? StartFile : StopFile);
        return File.Exists(file) &&
               Win32.PlaySound(file, IntPtr.Zero, Win32.SND_FILENAME | Win32.SND_ASYNC | Win32.SND_NODEFAULT);
    }

    /// <summary>
    /// 命令行 <c>--soundcheck</c> 触发：依次播放开、停两音，并把路径与 API 结果落盘，
    /// 用于在没有界面的情况下确认「文件读到了没有、API 返回什么」。
    /// </summary>
    public static void RunSelfCheck()
    {
        Task.Run(async () =>
        {
            await Task.Delay(500);
            _ = TryPlay(true);
            var afterStart = Marshal.GetLastWin32Error();
            await Task.Delay(1000);
            _ = TryPlay(false);
            var afterStop = Marshal.GetLastWin32Error();

            var lines = new[]
            {
                $"[{DateTime.Now:HH:mm:ss}] 自检开始",
                $"BaseDirectory = {AppContext.BaseDirectory}",
                $"AssetsDirectory = {AppPaths.AssetsDirectory}",
                SoundCheckReport(StartFile),
                SoundCheckReport(StopFile),
                $"播放 start 后 LastError = {afterStart}",
                $"播放 stop 后 LastError = {afterStop}",
            };

            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "LeXiangSync.soundcheck.txt"), lines);
        });
    }

    private static string SoundCheckReport(string name)
    {
        var path = Path.Combine(SoundDirectory, name);
        return File.Exists(path)
            ? $"{name}: 存在，{new FileInfo(path).Length} 字节，{path}"
            : $"{name}: 不存在！期望路径 {path}";
    }
}
