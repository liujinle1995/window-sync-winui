using System.Text.RegularExpressions;
using LeXiangSync.Models;

namespace LeXiangSync.Helpers;

/// <summary>
/// 方案的"指纹 ↔ 窗口句柄"换算。方案只存窗口标题（外加进程名与同标题序号），
/// 加载时回查当前窗口，因此目标程序重启后方案依然可用。
/// </summary>
public static partial class PlanResolver
{
    public static WindowFingerprint Build(WindowInfo window, string? processName = null, int index = 0) =>
        new() { Title = window.Title, ProcessName = processName, Index = index };

    /// <summary>
    /// 窗口在同进程、同标题窗口中的序号。游戏多开时标题完全一样，
    /// 没有序号就只能永远匹配到第一个实例。
    /// </summary>
    public static int IndexOf(WindowInfo window, IReadOnlyList<ProcessInfo> processes)
    {
        var name = ProcessNameOf(window.Pid, processes);
        var same = processes
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Windows)
            .Where(w => w.Title == window.Title)
            .ToList();

        var i = same.FindIndex(w => w.Hwnd == window.Hwnd);
        return i < 0 ? 0 : i;
    }

    /// <summary>
    /// 按「标题 → 进程名 → 同标题序号」三级定位窗口。
    /// 精确标题匹配不到时降级为"去掉方括号后匹配"（服务器名变化的场景）。
    /// </summary>
    public static WindowInfo? Resolve(WindowFingerprint fingerprint, IReadOnlyList<ProcessInfo> processes)
    {
        if (string.IsNullOrWhiteSpace(fingerprint.Title)) return null;

        var pool = processes
            .SelectMany(p => p.Windows.Select(w => (Window: w, Process: p.Name)))
            .ToList();

        var candidates = pool.Where(x => x.Window.Title == fingerprint.Title).ToList();
        if (candidates.Count == 0)
        {
            var stripped = StripBrackets(fingerprint.Title);
            if (string.IsNullOrEmpty(stripped)) return null;
            candidates = pool.Where(x => StripBrackets(x.Window.Title) == stripped).ToList();
        }
        if (candidates.Count == 0) return null;

        // 标题撞车时优先挑同进程的窗口
        if (!string.IsNullOrWhiteSpace(fingerprint.ProcessName))
        {
            var byProcess = candidates
                .Where(x => string.Equals(x.Process, fingerprint.ProcessName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byProcess.Count > 0) candidates = byProcess;
        }

        // 多开实例按序号取；序号越界（例如少开了几个）则退化到第一个
        return fingerprint.Index >= 0 && fingerprint.Index < candidates.Count
            ? candidates[fingerprint.Index].Window
            : candidates[0].Window;
    }

    /// <summary>把旧版（只有标题数组）方案升级为指纹结构。</summary>
    public static void Normalize(SyncPlan plan)
    {
        if (string.IsNullOrEmpty(plan.Master.Title) && !string.IsNullOrEmpty(plan.MasterTitle))
            plan.Master = new WindowFingerprint { Title = plan.MasterTitle };

        if (plan.Slaves.Count == 0 && plan.SlaveTitles is { Count: > 0 })
            plan.Slaves = plan.SlaveTitles
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new WindowFingerprint { Title = t })
                .ToList();
    }

    private static string ProcessNameOf(uint pid, IReadOnlyList<ProcessInfo> processes) =>
        processes.FirstOrDefault(p => p.Pid == pid)?.Name ?? string.Empty;

    private static string StripBrackets(string title) => BracketRegex().Replace(title, string.Empty).Trim();

    [GeneratedRegex(@"\[.*?\]", RegexOptions.Compiled)]
    private static partial Regex BracketRegex();
}
