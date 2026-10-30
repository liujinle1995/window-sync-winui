using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using LeXiangSync.Helpers;
using LeXiangSync.Models;
using LeXiangSync.Native;
using LeXiangSync.Services;
using Microsoft.UI.Dispatching;

namespace LeXiangSync.ViewModels;

/// <summary>
/// 「同步」页：运行指标 + 进程与窗口面板（左进程树 / 右窗口表）+ 方案保存。
/// <para>
/// 重构要点：原先主窗口与从窗口各有一张选择器、各自一个分组集合，用户在两张表之间来回看；
/// 现在合并成**一个**分组集合，窗口行上挂 <see cref="WindowRole"/>，
/// 右侧表格用「角色」列区分主 / 从 / 未加入。
/// </para>
/// </summary>
public sealed partial class SetupViewModel
    : ObservableObject, IDisposable, IRecipient<ValueChangedMessage<SyncPlan>>
{
    private readonly SyncEngine _engine;
    private readonly LogService _log;
    private readonly DataStore _store;
    private readonly CursorWatchService _cursorWatch;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    private IReadOnlyList<ProcessInfo> _all = Array.Empty<ProcessInfo>();
    private long _signature;

    [ObservableProperty] public partial string Filter { get; set; }

    [ObservableProperty] public partial bool IsRefreshing { get; set; }

    /// <summary>左侧进程树：按软件名聚合的进程分组。</summary>
    [ObservableProperty] public partial ObservableCollection<ProcessGroupVm> Groups { get; set; }

    /// <summary>右侧窗口表：所有可见窗口的行（与 <see cref="Groups"/> 共享同一批 WindowItem 实例）。</summary>
    [ObservableProperty] public partial ObservableCollection<WindowItem> Windows { get; set; }

    [ObservableProperty] public partial string PlanName { get; set; }

    public SetupViewModel(SyncEngine engine, LogService log, DataStore store, CursorWatchService cursorWatch)
    {
        _engine = engine;
        _log = log;
        _store = store;
        _cursorWatch = cursorWatch;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcher.CreateTimer();

        // 必须先初始化集合：给 Filter 赋值会立即触发 OnFilterChanged → Rebuild
        Groups = new ObservableCollection<ProcessGroupVm>();
        Windows = new ObservableCollection<WindowItem>();
        PlanName = string.Empty;
        Filter = store.Settings.ProcessFilter;

        _timer.Tick += (_, _) => _ = RefreshAsync();

        _engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SyncEngine.Master) or nameof(SyncEngine.SlaveCount))
            {
                RaiseSummaries();
                ApplyTimer();
            }
        };
        _engine.Slaves.CollectionChanged += (_, _) =>
        {
            RaiseSummaries();
            ApplyTimer();
        };

        // 方案页加载 / 更新方案后，同步刷新本页的角色徽章
        WeakReferenceMessenger.Default.Register(this);
    }

    // ── 展示 ──────────────────────────────────────────────────────────────

    public string MasterSummary => _engine.Master is null ? "未选择" : _engine.Master.Title;

    /// <summary>主窗口卡片副信息：`Client.exe · 同标题 #1`。</summary>
    public string MasterFingerprint
    {
        get
        {
            if (_engine.Master is not { } master) return "请在上方窗口表中选择主窗口";
            var name = ProcessNameOf(master.Pid);
            var index = PlanResolver.IndexOf(master, _all);
            return $"{name} · 同标题 #{index}";
        }
    }

    public string SlaveSummary => $"已选 {_engine.SlaveCount} 个从窗口";

    /// <summary>表格底部提示：`已选 1 主 / 4 从`。</summary>
    public string SelectionSummary
        => $"已选 {(_engine.Master is null ? 0 : 1)} 主 / {_engine.SlaveCount} 从";

    /// <summary>表格底部整行提示（选择概况 + 操作说明）。</summary>
    public string TableHint
        => $"{SelectionSummary} · 右键行可结束进程 · 双击行一键聚焦 · 已选窗口时自动暂停刷新";

    public bool CanSavePlan => _engine.Master is not null;

    /// <summary>表格表头的全选框：当前列表是否已全部加入同步（部分选中为 null）。</summary>
    public bool? HeaderSelectionState
    {
        get
        {
            if (Windows.Count == 0) return false;
            var selected = Windows.Count(w => w.IsInSync);
            return selected == 0 ? false : selected == Windows.Count ? true : null;
        }
    }

    /// <summary>进程列表自动刷新周期（秒），0 表示关闭。NumberBox.Value 为 double，故用 double 承载。</summary>
    public double AutoRefreshSeconds
    {
        get => _store.Settings.AutoRefreshSeconds;
        set
        {
            if (double.IsNaN(value)) return;
            var seconds = (int)Math.Clamp(Math.Round(value), 0, 60);
            if (_store.Settings.AutoRefreshSeconds == seconds) return;
            _store.Settings.AutoRefreshSeconds = seconds;
            _store.SaveSettings();
            ApplyTimer();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoRefreshHint));
        }
    }

    /// <summary>页面可见性：不可见时暂停自动刷新，避免无谓的枚举开销。</summary>
    public bool IsPageActive
    {
        get => _isPageActive;
        set
        {
            if (SetProperty(ref _isPageActive, value)) ApplyTimer();
        }
    }
    private bool _isPageActive;

    /// <summary>启动时已套用过方案；此时没必要再周期枚举（见原实现注释）。</summary>
    public bool HasLoadedPlan
    {
        get => _hasLoadedPlan;
        set
        {
            if (SetProperty(ref _hasLoadedPlan, value)) ApplyTimer();
        }
    }
    private bool _hasLoadedPlan;

    /// <summary>悬停同步：鼠标移入主窗口自动开始、移出自动停止。</summary>
    public bool HoverSyncEnabled
    {
        get => _store.Settings.HoverSyncEnabled;
        set
        {
            if (_store.Settings.HoverSyncEnabled == value) return;
            _store.Settings.HoverSyncEnabled = value;
            _store.SaveSettings();
            _cursorWatch.HoverSync = value;
            OnPropertyChanged();

            _log.Add(!value
                ? "悬停同步已关闭"
                : _engine.Master is null
                    ? "悬停同步已开启：先选好主窗口与从窗口，之后鼠标移入主窗口即开始同步、移出即停"
                    : "悬停同步已开启：鼠标移入主窗口自动开始同步，移出自动停止");
        }
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var processes = await _engine.GetProcessesAsync();
            var alive = processes.SelectMany(p => p.Windows).ToList();
            var pruned = _engine.PruneStaleWindows(alive);

            _all = processes;
            Rebuild(force: false);

            if (pruned > 0) _log.Add($"已清除 {pruned} 个已关闭的窗口选择", LogLevel.Warning);
            else _log.Add($"已加载 {processes.Count} 个进程");
        }
        finally { IsRefreshing = false; }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        // 主动清空 = 放弃刚套用的方案，周期刷新恢复，方便重新挑窗口
        _hasLoadedPlan = false;
        _engine.SetMaster(null);
        _engine.ClearSlaves();
        RefreshSelectionFlags();
        _log.Add("已清空选择");
    }

    /// <summary>把当前筛选结果中的全部窗口加入从窗口（批量选择）。</summary>
    [RelayCommand]
    private void SelectAllVisible()
    {
        var windows = FilteredProcesses().SelectMany(p => p.Windows).ToList();
        var added = 0;
        foreach (var w in windows)
        {
            if (_engine.IsMaster(w.Hwnd) || _engine.IsSlave(w.Hwnd)) continue;
            _engine.ToggleSlave(w);
            added++;
        }
        RefreshSelectionFlags();
        _log.Add(added > 0 ? $"已批量添加 {added} 个从窗口" : "没有新增窗口", LogLevel.Info);
    }

    /// <summary>表头全选框：已全选则清空，否则把当前列表全部加入从窗口。</summary>
    [RelayCommand]
    private void ToggleAllVisible()
    {
        if (HeaderSelectionState == true) ClearSelection();
        else SelectAllVisible();
    }

    /// <summary>
    /// 展开 / 收起进程树里的一个软件分组。
    /// 分组状态存在每行上（一行 = 一个进程实例），但视觉上按「软件名」成组，
    /// 所以必须把同名实例一起改，否则同一组里会出现一半展开一半收起。
    /// </summary>
    public void ToggleGroupExpanded(ProcessGroupVm? group)
    {
        if (group is null) return;

        var expanded = !group.IsExpanded;
        foreach (var item in Groups.Where(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            item.IsExpanded = expanded;
    }

    /// <summary>把当前的主/从窗口与同步项存成一份新方案。</summary>
    [RelayCommand]
    private void SavePlan()
    {
        if (_engine.Master is null) { _log.Add("请先选择主窗口才能保存方案", LogLevel.Warning); return; }

        var name = string.IsNullOrWhiteSpace(PlanName) ? $"方案 {DateTime.Now:MM-dd HH:mm}" : PlanName.Trim();
        var plan = TryBuildPlan(name)!;
        plan.Name = UniqueName(plan.Name);

        _store.Plans.Add(plan);
        _store.Settings.AutoLoadPlanId = plan.Id;
        _store.SavePlans();
        _store.SaveSettings();
        PlanName = string.Empty;
        _log.Add($"方案「{plan.Name}」已保存（{plan.Slaves.Count} 个从窗口）", LogLevel.Success);
    }

    /// <summary>按当前选择构造方案对象（保存与方案页「覆盖更新」共用）。未选主窗口时返回 null。</summary>
    public SyncPlan? TryBuildPlan(string? name = null)
    {
        if (_engine.Master is null) return null;

        var plan = new SyncPlan
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"方案 {DateTime.Now:MM-dd HH:mm}" : name.Trim(),
            Master = PlanResolver.Build(_engine.Master, ProcessNameOf(_engine.Master.Pid),
                PlanResolver.IndexOf(_engine.Master, _all)),
            Slaves = _engine.Slaves
                .Select(s => PlanResolver.Build(s, ProcessNameOf(s.Pid), PlanResolver.IndexOf(s, _all)))
                .ToList(),
            SyncMouse = _engine.SyncMouse,
            SyncKeyboard = _engine.SyncKeyboard,
            SyncClipboard = _engine.SyncClipboard,
            CreatedAt = DateTimeOffset.Now,
        };

        plan.Slaves.RemoveAll(s => string.IsNullOrWhiteSpace(s.Title));
        return plan;
    }

    /// <summary>方案页加载 / 更新方案后同步角色徽章。</summary>
    public void Receive(ValueChangedMessage<SyncPlan> message) => RefreshSelectionFlags();

    /// <summary>结束进程（右键菜单）。</summary>
    [RelayCommand]
    private async Task KillProcessAsync(uint pid)
    {
        var ok = await _engine.KillAsync(pid);
        if (ok) _log.Add($"已结束进程 #{pid}", LogLevel.Success);
        await RefreshAsync();
    }

    /// <summary>把某个窗口提到最前：多开时标题几乎一样，靠列表看不出谁是谁。</summary>
    [RelayCommand]
    private async Task FocusWindowAsync(WindowInfo? window)
    {
        if (window is null) return;

        await Task.Run(() => WindowApi.Activate((IntPtr)window.Hwnd));
        _log.Add($"已聚焦到「{Shorten(window.Title)}」");
    }

    /// <summary>把某行设为 / 取消主窗口。</summary>
    [RelayCommand]
    public void SetMaster(WindowItem? item)
    {
        if (item is null) return;

        if (item.IsMaster) _engine.SetMaster(null);
        else _engine.SetMaster(item.Window);

        RefreshSelectionFlags();
    }

    /// <summary>行首勾选框：主窗口保持不变，仅切换「是否为从窗口」。</summary>
    [RelayCommand]
    public void ToggleSlave(WindowItem? item)
    {
        if (item is null) return;

        if (item.IsMaster)
        {
            // 主窗口不能通过这个勾选框退出同步，否则用户会误以为同步断了
            _log.Add("主窗口请用右键「取消主窗口」或「清空选择」退出同步", LogLevel.Warning);
            RefreshSelectionFlags();
            return;
        }

        if (item.IsSlave) _engine.RemoveSlave(item.Window.Hwnd);
        else _engine.ToggleSlave(item.Window);

        RefreshSelectionFlags();
    }

    /// <summary>进程树的三态勾选：整进程一起加入 / 一起移出从窗口。</summary>
    [RelayCommand]
    public void ToggleGroup(ProcessGroupVm? group)
    {
        if (group is null) return;
        if (group.Windows.Count == 0)
        {
            _log.Add("该进程当前没有窗口", LogLevel.Warning);
            return;
        }

        var allSelected = group.Windows.All(w => w.IsInSync);
        var changed = 0;

        foreach (var item in group.Windows)
        {
            var hwnd = item.Window.Hwnd;
            if (allSelected)
            {
                if (_engine.IsSlave(hwnd)) { _engine.RemoveSlave(hwnd); changed++; }
            }
            else if (!_engine.IsSlave(hwnd) && !_engine.IsMaster(hwnd))
            {
                _engine.ToggleSlave(item.Window);
                changed++;
            }
        }

        RefreshSelectionFlags();
        _log.Add(changed > 0
                ? $"已{(allSelected ? "取消" : "加入")}「{group.Name} #{group.Pid}」的 {changed} 个窗口"
                : "该进程没有可变更的窗口",
            changed > 0 ? LogLevel.Info : LogLevel.Warning);
    }

    /// <summary>从外部（方案加载等）改变引擎选择后同步角色。</summary>
    public void RefreshSelectionFlags() => Rebuild(force: false);

    // ── 内部 ──────────────────────────────────────────────────────────────

    private string ProcessNameOf(uint pid) => _all.FirstOrDefault(p => p.Pid == pid)?.Name ?? string.Empty;

    private WindowRole RoleOf(long hwnd)
        => _engine.IsMaster(hwnd) ? WindowRole.Master
            : _engine.IsSlave(hwnd) ? WindowRole.Slave
            : WindowRole.None;

    /// <summary>从窗口序号：按引擎 Slaves 的顺序，从 1 开始。</summary>
    private int SlaveIndexOf(long hwnd)
    {
        for (var i = 0; i < _engine.Slaves.Count; i++)
            if (_engine.Slaves[i].Hwnd == hwnd) return i + 1;
        return 0;
    }

    /// <summary>重名方案自动加序号后缀，避免存出两个同名条目无法区分。</summary>
    private string UniqueName(string name)
    {
        var taken = _store.Plans.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (taken.Add(name)) return name;

        for (var i = 2; i < 999; i++)
        {
            var candidate = $"{name} ({i})";
            if (taken.Add(candidate)) return candidate;
        }
        return $"{name} {DateTime.Now:HHmmss}";
    }

    private bool HasSelection => _engine.Master is not null && _engine.Slaves.Count > 0;

    /// <summary>工具栏的补充说明：让用户知道自动刷新为什么不动了。</summary>
    public string AutoRefreshHint =>
        HasLoadedPlan && AutoRefreshSeconds > 0
            ? "秒（已加载方案，自动刷新已停止）"
            : HasSelection && AutoRefreshSeconds > 0
                ? "秒（已选定窗口，自动刷新暂停中）"
                : "秒（0 = 关闭）";

    private void ApplyTimer()
    {
        // 先刷新提示：暂停分支也要把「已暂停」显示出来
        OnPropertyChanged(nameof(AutoRefreshHint));

        var seconds = AutoRefreshSeconds;
        if (seconds <= 0 || !IsPageActive || HasSelection || HasLoadedPlan) { _timer.Stop(); return; }
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    partial void OnFilterChanged(string value)
    {
        _store.Settings.ProcessFilter = value;
        _store.SaveSettings();
        Rebuild(force: true);
    }

    private static string Shorten(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return "（无标题）";
        return value.Length <= 40 ? value : value[..40] + "…";
    }

    private IEnumerable<ProcessInfo> FilteredProcesses()
    {
        var keyword = Filter.Trim();
        if (keyword.Length == 0) return _all;

        return _all.Where(p =>
            p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            p.Windows.Any(w => w.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private void Rebuild(bool force)
    {
        var filtered = FilteredProcesses().ToList();
        var signature = WindowGroupBuilder.Signature(filtered);

        if (force || signature != _signature)
        {
            _signature = signature;
            WindowGroupBuilder.Merge(Groups, filtered, RoleOf, SlaveIndexOf);
            RebuildFlatTable();
        }
        else
        {
            ApplyRoles();
        }

        RaiseSummaries();
    }

    /// <summary>把分组里的窗口按顺序摊平成右侧表格的数据源（共享同一批行对象）。</summary>
    private void RebuildFlatTable()
    {
        // 用 Clear + 重填而不复用：签名变化意味着进程或窗口增减，整表重排最稳；
        // 角色变更不会走这里（走 ApplyRoles），所以勾选不会重置滚动位置。
        Windows.Clear();
        foreach (var group in Groups)
            foreach (var window in group.Windows)
                Windows.Add(window);
    }

    private void ApplyRoles()
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Windows)
            {
                item.Role = RoleOf(item.Window.Hwnd);
                item.SlaveIndex = SlaveIndexOf(item.Window.Hwnd);
            }
            group.RefreshSelection();
        }
    }

    private void RaiseSummaries()
    {
        OnPropertyChanged(nameof(MasterSummary));
        OnPropertyChanged(nameof(MasterFingerprint));
        OnPropertyChanged(nameof(SlaveSummary));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(TableHint));
        OnPropertyChanged(nameof(CanSavePlan));
        OnPropertyChanged(nameof(HeaderSelectionState));
    }

    public void Dispose()
    {
        _timer.Stop();
        WeakReferenceMessenger.Default.Unregister<ValueChangedMessage<SyncPlan>>(this);
    }
}
