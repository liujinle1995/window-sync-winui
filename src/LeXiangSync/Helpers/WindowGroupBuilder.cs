using System.Collections.ObjectModel;
using LeXiangSync.Models;

namespace LeXiangSync.Helpers;

/// <summary>
/// 把引擎枚举到的进程列表合并进「进程与窗口」面板的分组集合，尽量复用已有行对象。
/// <para>
/// 重构后只有**一个**集合：主窗口与从窗口不再分两张表，而是同一批窗口行上挂
/// <see cref="WindowRole"/>，由右侧表格的「角色」列区分。
/// </para>
/// </summary>
public static class WindowGroupBuilder
{
    /// <summary>
    /// </summary>
    /// <param name="target">面板绑定的分组集合（原地更新以避免滚动位置跳变）</param>
    /// <param name="processes">引擎返回的最新进程列表</param>
    /// <param name="roleOf">窗口当前的同步角色</param>
    /// <param name="slaveIndexOf">窗口在从窗口列表中的序号（非从窗口返回 0）</param>
    public static void Merge(
        ObservableCollection<ProcessGroupVm> target,
        IReadOnlyList<ProcessInfo> processes,
        Func<long, WindowRole> roleOf,
        Func<long, int> slaveIndexOf)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 1. 移除已消失的进程
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var pid = target[i].Pid;
            if (!processes.Any(p => p.Pid == pid)) target.RemoveAt(i);
        }

        // 2. 按软件名聚合并插入 / 更新：同名实例按 PID 排在一起，多开时顺序稳定
        var ordered = processes
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Pid)
            .ToList();

        var stats = ordered
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (Processes: g.Count(),
                      Windows: g.Sum(x => x.Windows.Count),
                      Titles: (IReadOnlyList<string>)g.SelectMany(x => x.Windows).Select(w => w.Title).ToList()),
                StringComparer.OrdinalIgnoreCase);

        var previousName = "\u0000"; // 哨兵：确保首个进程一定是组首行
        for (var i = 0; i < ordered.Count; i++)
        {
            var proc = ordered[i];
            var group = target.FirstOrDefault(g => g.Pid == proc.Pid);
            if (group is null)
            {
                group = new ProcessGroupVm { Pid = proc.Pid, Name = proc.Name };
                target.Insert(i, group);
            }
            else
            {
                // 必须按目标序号整列重排：只做插入的话，后到的同名实例会被插到组首之前，
                // 界面顺序与「谁是组首行」对不上，软件名就被挤到列表底部了。
                var currentIndex = target.IndexOf(group);
                if (currentIndex != i) target.Move(currentIndex, i);
            }

            group.Name = proc.Name;

            var stat = stats.TryGetValue(proc.Name, out var s)
                ? s
                : (Processes: 1, Windows: proc.Windows.Count, Titles: (IReadOnlyList<string>)Array.Empty<string>());

            group.DisplayName = DisplayNames.Get(proc.Name, stat.Titles);
            group.AppProcessCount = stat.Processes;
            group.AppWindowCount = stat.Windows;
            group.IsFirstOfApp = !string.Equals(previousName, proc.Name, StringComparison.OrdinalIgnoreCase);
            previousName = proc.Name;

            MergeWindows(group.Windows, proc.Windows, proc.Name, roleOf, slaveIndexOf);
        }

        // 3. 勾选态依赖窗口的角色，必须在窗口合并完成后统一重算
        foreach (var group in target) group.RefreshSelection();
    }

    private static void MergeWindows(
        ObservableCollection<WindowItem> target,
        IReadOnlyList<WindowInfo> windows,
        string processName,
        Func<long, WindowRole> roleOf,
        Func<long, int> slaveIndexOf)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var hwnd = target[i].Window.Hwnd;
            if (!windows.Any(w => w.Hwnd == hwnd)) target.RemoveAt(i);
        }

        var index = 0;
        foreach (var w in windows)
        {
            index++;

            var item = target.FirstOrDefault(x => x.Window.Hwnd == w.Hwnd);
            if (item is null)
            {
                item = new WindowItem { Window = w, ProcessName = processName, InstanceIndex = index };
                target.Add(item);
            }
            else
            {
                // 窗口对象可能被引擎重新构造，标题要以最新的一份为准
                if (!ReferenceEquals(item.Window, w)) item.Window = w;
                item.ProcessName = processName;
                item.InstanceIndex = index;
                item.OnWindowMetaChanged();
            }

            item.Role = roleOf(w.Hwnd);
            item.SlaveIndex = slaveIndexOf(w.Hwnd);
        }
    }

    /// <summary>进程 + 窗口清单的指纹，用于判断是否需要重建 UI。</summary>
    public static long Signature(IReadOnlyList<ProcessInfo> processes)
    {
        var hash = new HashCode();
        foreach (var p in processes)
        {
            hash.Add(p.Pid);
            hash.Add(p.Name);
            foreach (var w in p.Windows)
            {
                hash.Add(w.Hwnd);
                hash.Add(w.Title);
            }
        }
        return hash.ToHashCode();
    }
}
