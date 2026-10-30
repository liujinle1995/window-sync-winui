using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using LeXiangSync.Helpers;
using LeXiangSync.Models;
using LeXiangSync.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeXiangSync.ViewModels;

/// <summary>「方案」页：已保存方案的加载、更新、重命名、删除、导入导出。</summary>
public sealed partial class PlansViewModel : ObservableObject
{
    private readonly SyncEngine _engine;
    private readonly LogService _log;
    private readonly DataStore _store;
    private readonly Func<IntPtr> _windowHandle;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<string, SyncPlan?> _buildPlan;

    [ObservableProperty] public partial SyncPlan? Selected { get; set; }

    public PlansViewModel(
        SyncEngine engine,
        LogService log,
        DataStore store,
        Func<IntPtr> windowHandle,
        Func<XamlRoot?> xamlRoot,
        Func<string, SyncPlan?> buildPlan)
    {
        _engine = engine;
        _log = log;
        _store = store;
        _windowHandle = windowHandle;
        _xamlRoot = xamlRoot;
        _buildPlan = buildPlan;

        // 导入 / 新建 / 删除都会改集合，卡片副标题里的数量要跟着变
        Plans.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PlanCountText));
    }

    /// <summary>
    /// 直接暴露 <see cref="DataStore.Plans"/>：配置页保存的新方案会立刻出现在这里，
    /// 本页删除也会立刻反映到全局，两页不再各自持有快照。
    /// </summary>
    public ObservableCollection<SyncPlan> Plans => _store.Plans;

    // ── 展示 ──────────────────────────────────────────────────────────────

    /// <summary>卡片副标题：`共 4 个 · 单个方案含主窗口指纹、从窗口列表与同步项`。</summary>
    public string PlanCountText
        => $"共 {Plans.Count} 个 · 单个方案含主窗口指纹、从窗口列表与同步项";

    /// <summary>当前生效方案名（启动时会自动套用列表中的第一个）。</summary>
    public string ActivePlanText
    {
        get
        {
            var id = _store.Settings.AutoLoadPlanId;
            var active = id is null ? null : Plans.FirstOrDefault(p => p.Id == id);
            return active is null ? "当前生效：无" : $"当前生效：{active.Name}";
        }
    }

    /// <summary>底部栏右侧：`已选中：五开日常（1 主 / 4 从）`。</summary>
    public string SelectedSummary => Selected is null
        ? "未选中方案"
        : $"已选中：{Selected.Name}（1 主 / {Selected.Slaves.Count} 从）";

    /// <summary>顶部提示条文案：`启动时自动套用第一个方案（默认组） · 当前生效：五开日常`。</summary>
    public string AutoLoadHint => $"启动时自动套用第一个方案（默认组） · {ActivePlanText}";

    /// <summary>把生效标记同步到各行，供表格生效行淡底与次行文案使用。</summary>
    public void RefreshActiveFlags()
    {
        var id = _store.Settings.AutoLoadPlanId;
        foreach (var plan in Plans)
        {
            plan.IsActive = plan.Id == id;
            plan.MirrorEnabled = _engine.TextMirror;
        }

        OnPropertyChanged(nameof(ActivePlanText));
        OnPropertyChanged(nameof(AutoLoadHint));
        OnPropertyChanged(nameof(PlanCountText));
    }

    partial void OnSelectedChanged(SyncPlan? value) => OnPropertyChanged(nameof(SelectedSummary));

    /// <summary>用「同步」页当前的主/从选择新建一份方案。</summary>
    [RelayCommand]
    private void CreateFromCurrent()
    {
        var plan = _buildPlan($"方案 {DateTime.Now:MM-dd HH:mm}");
        if (plan is null)
        {
            _log.Add("请先在「同步」页选择主窗口，再新建方案", LogLevel.Warning);
            return;
        }

        _store.Plans.Add(plan);
        Persist();
        Selected = plan;
        RefreshActiveFlags();
        _log.Add($"已用当前选择新建方案「{plan.Name}」（{plan.Slaves.Count} 个从窗口）", LogLevel.Success);
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync(SyncPlan? plan)
    {
        await ApplyAsync(plan);
    }

    /// <summary>
    /// 启动时自动套用方案：有方案就加载列表里的第一项，一个都没有就不动。
    /// 目标程序还没启动时 ApplyAsync 只会记一条警告，不会打乱用户当前的选择。
    /// </summary>
    /// <returns>是否真的套用成功；调用方据此决定是否关掉进程列表的周期刷新。</returns>
    public async Task<bool> AutoLoadAsync()
    {
        if (Plans.Count == 0)
        {
            _log.Add("还没有保存的方案，可在「同步」页选好窗口后保存");
            return false;
        }

        var plan = Plans[0];
        Selected = plan;
        return await ApplyAsync(plan);
    }

    [RelayCommand]
    private void Delete(SyncPlan? plan)
    {
        if (plan is null) return;

        Plans.Remove(plan);
        if (Selected == plan) Selected = null;
        if (_store.Settings.AutoLoadPlanId == plan.Id)
        {
            _store.Settings.AutoLoadPlanId = null;
            _store.SaveSettings();
        }

        Persist();
        RefreshActiveFlags();
        _log.Add($"已删除方案「{plan.Name}」");
    }

    /// <summary>把选中方案的内容覆盖为配置页当前的选择。</summary>
    [RelayCommand]
    private void Update(SyncPlan? plan)
    {
        if (plan is null) return;

        var fresh = _buildPlan(plan.Name);
        if (fresh is null)
        {
            _log.Add("请先在「同步」页选择主窗口，再更新方案", LogLevel.Warning);
            return;
        }

        fresh.Id = plan.Id;
        fresh.Name = plan.Name;

        var index = Plans.IndexOf(plan);
        if (index >= 0) Plans[index] = fresh;
        Selected = fresh;

        Persist();
        WeakReferenceMessenger.Default.Send(new ValueChangedMessage<SyncPlan>(fresh));
        _log.Add($"方案「{plan.Name}」已更新为当前选择（{fresh.Slaves.Count} 个从窗口）", LogLevel.Success);
    }

    [RelayCommand]
    private async Task RenameAsync(SyncPlan? plan)
    {
        if (plan is null) return;

        var root = _xamlRoot();
        if (root is null) return;

        var box = new TextBox { Text = plan.Name, PlaceholderText = "方案名称" };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "重命名方案",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = box,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0 || name == plan.Name) return;

        plan.Name = name;
        Persist();
        _log.Add($"方案已重命名为「{name}」", LogLevel.Success);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Plans.Count == 0) { _log.Add("还没有可导出的方案", LogLevel.Warning); return; }

        var picker = PickerService.CreateSavePicker(_windowHandle(), $"乐享同步方案_{DateTime.Now:yyyyMMdd}", ".json");
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            _store.ExportPlans(file.Path, Plans);
            _log.Add($"已导出 {Plans.Count} 条方案到 {file.Path}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            _log.Add(ex, "导出失败: ");
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var picker = PickerService.CreateOpenPicker(_windowHandle(), ".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            // ImportPlans 直接往共享集合里追加，本页无需再重建
            var count = _store.ImportPlans(file.Path);
            _log.Add($"已导入 {count} 条方案", LogLevel.Success);
        }
        catch (Exception ex)
        {
            _log.Add(ex, "导入失败: ");
        }
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    /// <summary>按标题 → 进程名 → 同标题序号回查窗口句柄并套用到引擎。</summary>
    /// <returns>true = 主窗口命中并已写入引擎；false = 方案不适用（没方案 / 主窗口没开）。</returns>
    private async Task<bool> ApplyAsync(SyncPlan? plan)
    {
        if (plan is null) return false;
        PlanResolver.Normalize(plan);

        var processes = await _engine.GetProcessesAsync();

        var master = PlanResolver.Resolve(plan.Master, processes);
        if (master is null)
        {
            _log.Add($"方案「{plan.Name}」的主窗口「{plan.Master.Title}」当前未运行", LogLevel.Warning);
            return false;
        }

        // 已占用句柄去重：多开同标题时保证每个从窗口落在不同实例上
        var used = new HashSet<long> { master.Hwnd };
        var slaves = new List<WindowInfo>();
        foreach (var fingerprint in plan.Slaves)
        {
            var window = PlanResolver.Resolve(fingerprint, processes);
            if (window is null || !used.Add(window.Hwnd)) continue;
            slaves.Add(window);
        }

        _engine.SetMaster(master);
        _engine.ClearSlaves();
        foreach (var slave in slaves) _engine.ToggleSlave(slave);

        _engine.SyncMouse = plan.SyncMouse;
        _engine.SyncKeyboard = plan.SyncKeyboard;
        _engine.SyncClipboard = plan.SyncClipboard;

        _store.Settings.AutoLoadPlanId = plan.Id;
        _store.SaveSettings();
        RefreshActiveFlags();

        // 通知配置页刷新高亮
        WeakReferenceMessenger.Default.Send(new ValueChangedMessage<SyncPlan>(plan));

        var missed = plan.Slaves.Count - slaves.Count;
        _log.Add(missed > 0
            ? $"方案「{plan.Name}」已加载，{missed} 个从窗口未在运行"
            : $"方案「{plan.Name}」已加载（{slaves.Count} 个从窗口）", LogLevel.Success);
        return true;
    }

    private void Persist() => _store.SavePlans();
}
