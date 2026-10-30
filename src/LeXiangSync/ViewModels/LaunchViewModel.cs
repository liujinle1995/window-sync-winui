using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeXiangSync.Models;
using LeXiangSync.Native;
using LeXiangSync.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;

namespace LeXiangSync.ViewModels;

/// <summary>
/// 「多开启动」页的视图模型：配置编辑 + 流程控制。
/// 数值属性一律用 double —— NumberBox.Value 是 double，绑 int 会在 x:Bind 编译期就类型不匹配。
/// </summary>
public sealed partial class LaunchViewModel : ObservableObject
{
    private readonly GameLaunchConfig _config;
    private readonly LogService _log;
    private readonly DataStore _store;
    private readonly Func<IntPtr> _windowHandle;
    private readonly Func<XamlRoot> _xamlRoot;
    private readonly DispatcherQueue _dispatcher;
    private readonly GameLaunchService _service = new();

    private CancellationTokenSource? _cts;

    /// <summary>本次流程里成功拉起 / 拉起失败的沙箱数，跑完拼成底部那行「上次…」。</summary>
    private int _okCount;
    private int _failCount;

    /// <summary>是否处于整组流程中：只有这时才把日志行计入进度。</summary>
    private bool _trackingRun;

    public LaunchViewModel(
        GameLaunchConfig config,
        LogService log,
        DataStore store,
        Func<IntPtr> windowHandle,
        Func<XamlRoot> xamlRoot)
    {
        _config = config;
        _log = log;
        _store = store;
        _windowHandle = windowHandle;
        _xamlRoot = xamlRoot;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        SandboxiePath = config.SandboxiePath;
        LauncherPath = config.LauncherPath;

        StartX = config.StartButton.X;
        StartY = config.StartButton.Y;
        EnterX = config.EnterButton.X;
        EnterY = config.EnterButton.Y;
        LoginX = config.LoginButton.X;
        LoginY = config.LoginButton.Y;


        ScreenWidth = config.ScreenWidth;
        ScreenHeight = config.ScreenHeight;
        TaskbarHeight = config.TaskbarHeight;
        ReferenceBox = config.ReferenceBox;

        LaunchInterval = config.LaunchIntervalSeconds;
        LauncherWait = config.LauncherWaitSeconds;
        GameWait = config.GameWaitSeconds;
        StartDelay = config.StartClickDelaySeconds;
        EnterDelay = config.EnterClickDelaySeconds;
        LoginDelay = config.LoginClickDelaySeconds;
        Settle = config.SettleSeconds;

        Accounts = new ObservableCollection<GameAccount>(config.Accounts);

        // 组别：旧配置没有组的概念，补一个默认组，把尚未归组的账号放进去
        if (config.Groups.Count == 0) config.Groups.Add(GameLaunchConfig.DefaultGroup);
        Groups = new ObservableCollection<string>(config.Groups);
        Groups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentGroupIndex));
            RebuildTabs();
        };

        foreach (var account in Accounts)
            if (string.IsNullOrWhiteSpace(account.Group)) account.Group = Groups[0];

        foreach (var account in Accounts) Attach(account);

        // 赋值即触发 RebuildVisible；再落盘一次，把补齐的组名写进配置
        CurrentGroup = Groups[0];
        RefreshPointText();
        RebuildTabs();
        Persist();
    }

    // ── 绑定属性 ──────────────────────────────────────────────────────────

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string SandboxiePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string LauncherPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReferenceBox { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewBoxName { get; set; } = string.Empty;
    [ObservableProperty] public partial GameAccount? Selected { get; set; }

    /// <summary>当前组名。切换它只改列表显示与启动范围，不动任何账号配置。</summary>
    [ObservableProperty] public partial string CurrentGroup { get; set; } = string.Empty;

    /// <summary>「新建组 / 重命名组」输入框。</summary>
    [ObservableProperty] public partial string NewGroupName { get; set; } = string.Empty;

    [ObservableProperty] public partial double StartX { get; set; }
    [ObservableProperty] public partial double StartY { get; set; }
    [ObservableProperty] public partial double EnterX { get; set; }
    [ObservableProperty] public partial double EnterY { get; set; }
    [ObservableProperty] public partial double LoginX { get; set; }
    [ObservableProperty] public partial double LoginY { get; set; }

    [ObservableProperty] public partial double ScreenWidth { get; set; }
    [ObservableProperty] public partial double ScreenHeight { get; set; }
    [ObservableProperty] public partial double TaskbarHeight { get; set; }

    [ObservableProperty] public partial double LaunchInterval { get; set; }
    [ObservableProperty] public partial double LauncherWait { get; set; }
    [ObservableProperty] public partial double GameWait { get; set; }
    [ObservableProperty] public partial double StartDelay { get; set; }
    [ObservableProperty] public partial double EnterDelay { get; set; }
    [ObservableProperty] public partial double LoginDelay { get; set; }
    [ObservableProperty] public partial double Settle { get; set; }

    public ObservableCollection<GameAccount> Accounts { get; }

    /// <summary>组清单，顺序即下拉框顺序；至少保留一个组。</summary>
    public ObservableCollection<string> Groups { get; }

    /// <summary>当前组的账号 —— 列表只显示这一组，启动 / 排版 / 窗口认领也只作用于这一组。</summary>
    public ObservableCollection<GameAccount> VisibleAccounts { get; } = new();

    /// <summary>
    /// 给 ComboBox 的 SelectedIndex 用的桥接属性，理由同 <see cref="GameAccount.SlotIndex"/>：
    /// x:Bind 不会在 string 与 ComboBoxItem 之间做转换，走 int 最省事。
    /// </summary>
    public int CurrentGroupIndex
    {
        get => Math.Max(Groups.IndexOf(CurrentGroup), 0);
        set
        {
            if (value < 0 || value >= Groups.Count) return;
            CurrentGroup = Groups[value];
        }
    }

    /// <summary>流程输出的滚动日志。</summary>
    public ObservableCollection<string> Progress { get; } = new();

    // ── 页面展示用的派生数据（都来自真实配置 / 真实窗口，不编造） ────────

    /// <summary>分组标签条的数据源：组名 + 是否当前项 + 点击命令。</summary>
    public ObservableCollection<GroupTab> Tabs { get; } = new();

    /// <summary>「时序参数与屏幕基准」卡片的四行数值。</summary>
    public string LaunchIntervalText => $"{LaunchInterval * 1000:0} ms";

    public string LauncherWaitText => $"{LauncherWait:0} s";
    public string ClickDelayText => $"{StartDelay * 1000:0} ms";
    public string ScreenSizeText => $"{ScreenWidth:0} × {ScreenHeight:0}";

    /// <summary>底部「详细参数」是否展开：设计稿里没有那些输入框，但不能因此丢掉编辑能力。</summary>
    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    public string DetailsToggleText => IsDetailsOpen
        ? "收起详细参数"
        : "详细参数（程序路径 / 按钮坐标 / 时序 / 屏幕 / 沙箱与落位）";

    partial void OnIsDetailsOpenChanged(bool value) => OnPropertyChanged(nameof(DetailsToggleText));

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    /// <summary>本次流程已拉起的沙箱数 / 总数。</summary>
    [ObservableProperty]
    public partial int ProgressDone { get; set; }

    [ObservableProperty]
    public partial int ProgressTotal { get; set; }

    /// <summary>流程最后一步的原话（去掉行首时间戳）。</summary>
    [ObservableProperty]
    public partial string ProgressStep { get; set; } = string.Empty;

    /// <summary>底部条右侧的「上次: 4 成功 · 1 失败 · 12:04:20」。</summary>
    [ObservableProperty]
    public partial string LastRunText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLastRun { get; set; }

    public double ProgressPercent =>
        ProgressTotal <= 0 ? 0 : Math.Min(100, ProgressDone * 100.0 / ProgressTotal);

    public string ProgressSummary =>
        ProgressTotal <= 0 ? "尚未开始" : $"{ProgressDone} / {ProgressTotal} · {ProgressStep}";

    partial void OnProgressDoneChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressSummary));
    }

    partial void OnProgressTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressSummary));
    }

    partial void OnProgressStepChanged(string value) => OnPropertyChanged(nameof(ProgressSummary));

    partial void OnLaunchIntervalChanged(double value) => OnPropertyChanged(nameof(LaunchIntervalText));

    partial void OnLauncherWaitChanged(double value) => OnPropertyChanged(nameof(LauncherWaitText));

    partial void OnStartDelayChanged(double value) => OnPropertyChanged(nameof(ClickDelayText));

    partial void OnScreenWidthChanged(double value) => OnPropertyChanged(nameof(ScreenSizeText));

    partial void OnScreenHeightChanged(double value) => OnPropertyChanged(nameof(ScreenSizeText));

    partial void OnStartXChanged(double value) => RefreshPointText();

    partial void OnStartYChanged(double value) => RefreshPointText();

    partial void OnEnterXChanged(double value) => RefreshPointText();

    partial void OnEnterYChanged(double value) => RefreshPointText();

    partial void OnLoginXChanged(double value) => RefreshPointText();

    partial void OnLoginYChanged(double value) => RefreshPointText();

    partial void OnIsRunningChanged(bool value)
    {
        foreach (var account in Accounts) account.IsFocusEnabled = !value;
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ArrangeCommand.NotifyCanExecuteChanged();
        FocusBoxCommand.NotifyCanExecuteChanged();
        CloseGroupCommand.NotifyCanExecuteChanged();
        StartMissingCommand.NotifyCanExecuteChanged();
        StartBoxCommand.NotifyCanExecuteChanged();
        CloseBoxCommand.NotifyCanExecuteChanged();
    }

    /// <summary>切组：只换列表显示与启动范围，账号配置原样保留，切回去还是原样。</summary>
    partial void OnCurrentGroupChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentGroupIndex));
        RebuildVisible();
        RebuildTabs();
        _ = RefreshStatusesAsync();
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    /// <summary>未在跑流程时才允许编辑和启动（x:Bind 要求成员必须是公开的）。</summary>
    public bool CanRun => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        Persist();
        var accounts = AccountsForRun();
        _cts = new CancellationTokenSource();

        void OnReport(string message) => AppendProgress(message);
        _service.Reported += OnReport;

        Ui(() =>
        {
            IsRunning = true;
            Progress.Clear();
            ProgressTotal = accounts.Count;
            ProgressDone = 0;
            ProgressStep = "正在准备…";
            _okCount = 0;
            _failCount = 0;
            _trackingRun = true;
        });

        _log.Add("多开启动开始");

        // 让自己的窗口让开：它压在游戏窗口上时，截图拍到的是自家界面

        try
        {
            await _service.RunAsync(_config, accounts, _cts.Token);
            _log.Add($"多开启动完成（组「{CurrentGroup}」）", LogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            AppendProgress("已手动停止");
            _log.Add("多开启动已停止", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            AppendProgress($"失败：{ex.Message}");
            _log.Add(ex, "多开启动失败：");
        }
        finally
        {
            _service.Reported -= OnReport;
            Ui(() => IsRunning = false);
            _cts.Dispose();
            _cts = null;
            _trackingRun = false;
            SetLastRunSummary();
        }

        // 客户端是新起来的，外面要趁这个时机刷新进程并套方案
        Finished?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            AppendProgress("正在停止…");
        }
    }

    /// <summary>游戏都已经起来了，只想重新摆一下位置时用。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ArrangeAsync()
    {
        Persist();
        var accounts = AccountsForRun();

        void OnReport(string message) => AppendProgress(message);
        _service.Reported += OnReport;

        Ui(() =>
        {
            IsRunning = true;
            Progress.Clear();
        });

        try
        {
            await _service.ArrangeAsync(_config, accounts);
            _log.Add($"窗口排版完成（组「{CurrentGroup}」）", LogLevel.Success);
        }
        catch (Exception ex)
        {
            AppendProgress($"排版失败：{ex.Message}");
            _log.Add(ex, "排版失败：");
        }
        finally
        {
            _service.Reported -= OnReport;
            Ui(() => IsRunning = false);
        }
    }

    /// <summary>把这一路客户端提到最前，方便认出它在屏幕上的位置。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FocusBoxAsync(GameAccount? account)
    {
        if (account is null) return;

        var name = account.Name.Trim();
        if (name.Length == 0) return;

        Persist();

        if (await _service.FocusAsync(_config, name))
            _log.Add($"已聚焦到「{name}」的窗口");
        else
            _log.Add($"没有找到「{name}」的窗口：客户端还没起来，或窗口标题里看不到 [{name}]", LogLevel.Warning);
    }

    /// <summary>
    /// 一键关闭当前组的客户端。只认当前组：按窗口标题里的 [沙箱名] 认人，别的组不受影响。
    /// 不可撤销，所以先弹一次确认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CloseGroupAsync()
    {
        Persist();
        var accounts = AccountsForRun();
        if (accounts.Count == 0)
        {
            _log.Add($"组「{CurrentGroup}」里还没有沙箱，没有可关闭的客户端", LogLevel.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"关闭组「{CurrentGroup}」的客户端",
            Content = $"将关闭这一组 {accounts.Count} 个沙箱里的客户端：{string.Join('、', accounts.Select(a => a.Name))}\n\n"
                      + $"先请求正常退出，{GameLaunchService.GraceSeconds} 秒后仍未退出的会强制结束进程。\n"
                      + "客户端里未保存的内容会丢失。",
            PrimaryButtonText = "关闭客户端",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _xamlRoot(),
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        void OnReport(string message) => AppendProgress(message);
        _service.Reported += OnReport;

        Ui(() =>
        {
            IsRunning = true;
            Progress.Clear();
        });

        try
        {
            var closed = await _service.CloseGroupAsync(_config, accounts);
            _log.Add(closed > 0
                ? $"已关闭组「{CurrentGroup}」的 {closed} 个客户端"
                : $"组「{CurrentGroup}」没有找到可关闭的客户端窗口",
                closed > 0 ? LogLevel.Success : LogLevel.Warning);
        }
        catch (Exception ex)
        {
            AppendProgress($"关闭失败：{ex.Message}");
            _log.Add(ex, "关闭客户端失败：");
        }
        finally
        {
            _service.Reported -= OnReport;
            Ui(() => IsRunning = false);
        }
    }

    [RelayCommand]
    private void Save() => Persist();

    [RelayCommand]
    private async Task BrowseAsync(string? target)
    {
        var picker = PickerService.CreateOpenPicker(_windowHandle(), ".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        if (target == "launcher") LauncherPath = file.Path;
        else SandboxiePath = file.Path;

        Persist();
    }

    /// <summary>新建一个空组。头部的「+ 新建组」直接弹框问组名，不用先去找输入框。</summary>
    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var name = NewGroupName.Trim();

        if (name.Length == 0)
        {
            var input = new TextBox { PlaceholderText = "组名，例如：搬砖三组" };
            var dialog = new ContentDialog
            {
                Title = "新建组",
                Content = input,
                PrimaryButtonText = "新建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _xamlRoot(),
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            name = input.Text.Trim();
            if (name.Length == 0) return;
        }

        if (Groups.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Add($"组「{name}」已经存在", LogLevel.Warning);
            return;
        }

        Groups.Add(name);
        NewGroupName = string.Empty;
        CurrentGroup = name;
        Persist();
        _log.Add($"已新建组「{name}」，下面的「添加」会把沙箱加进这一组", LogLevel.Success);
    }

    /// <summary>当前组改名，组里的账号跟着一起走。</summary>
    [RelayCommand]
    private void RenameGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0) { _log.Add("请先在右边填新组名，再点「重命名」", LogLevel.Warning); return; }

        var index = Groups.IndexOf(CurrentGroup);
        if (index < 0) return;

        if (string.Equals(Groups[index], name, StringComparison.Ordinal))
        {
            NewGroupName = string.Empty;
            return;
        }
        if (Groups.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Add($"组「{name}」已经存在", LogLevel.Warning);
            return;
        }

        var old = Groups[index];
        foreach (var account in Accounts.Where(a => string.Equals(GroupOf(a), old, StringComparison.OrdinalIgnoreCase)))
            account.Group = name;

        Groups[index] = name;
        NewGroupName = string.Empty;
        CurrentGroup = name;
        Persist();
        _log.Add($"组「{old}」已重命名为「{name}」", LogLevel.Success);
    }

    /// <summary>
    /// 删除当前组。组里的号默认**保留**（转到剩下的第一个组），也可以选择连同号一起删掉 ——
    /// 沙箱名和落位是手配出来的，直接连号删掉太容易误伤。
    /// </summary>
    [RelayCommand]
    private async Task RemoveGroupAsync()
    {
        if (Groups.Count <= 1) { _log.Add("至少要保留一个组", LogLevel.Warning); return; }

        var name = CurrentGroup;
        var members = Accounts
            .Where(a => string.Equals(GroupOf(a), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var fallback = Groups.First(g => !string.Equals(g, name, StringComparison.Ordinal));

        var dialog = new ContentDialog
        {
            Title = $"删除组「{name}」",
            Content = members.Count == 0
                ? "这一组里没有沙箱，删除后不影响其它组。"
                : $"这一组里有 {members.Count} 个沙箱：{string.Join('、', members.Select(m => m.Name))}\n\n"
                  + $"转到「{fallback}」：配置不丢，只是换个组\n"
                  + "一起删掉：从列表里彻底移除这些号",
            PrimaryButtonText = members.Count == 0 ? "删除" : $"转到「{fallback}」",
            SecondaryButtonText = members.Count == 0 ? string.Empty : "一起删掉",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _xamlRoot(),
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        var dropped = 0;
        if (result == ContentDialogResult.Secondary)
        {
            foreach (var account in members) Accounts.Remove(account);
            dropped = members.Count;
        }
        else
        {
            foreach (var account in members) account.Group = fallback;
        }

        Groups.Remove(name);
        CurrentGroup = Groups[0];
        Persist();

        if (dropped > 0) _log.Add($"已删除组「{name}」及其中 {dropped} 个沙箱", LogLevel.Warning);
        else if (members.Count > 0) _log.Add($"已删除组「{name}」，{members.Count} 个沙箱转到了「{fallback}」", LogLevel.Success);
        else _log.Add($"已删除组「{name}」");
    }

    [RelayCommand]
    private void AddBox()
    {
        var name = NewBoxName.Trim();
        if (name.Length == 0) return;

        Ui(() =>
        {
            if (Accounts.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Add($"沙箱「{name}」已经存在", LogLevel.Warning);
                return;
            }

            // 新沙箱归到当前组：添加前先切好组，就不用事后一个个改
            var account = new GameAccount { Name = name, Group = CurrentGroup, Slot = LayoutSlot.None };
            Attach(account);
            Accounts.Add(account);
            VisibleAccounts.Add(account);
            Selected = account;
            NewBoxName = string.Empty;
            RefreshPointText();
        });

        Persist();
    }

    [RelayCommand]
    private void RemoveBox(GameAccount? account)
    {
        if (account is null) return;

        Ui(() =>
        {
            Accounts.Remove(account);
            VisibleAccounts.Remove(account);
            if (Selected == account) Selected = null;
        });

        Persist();
        _log.Add($"已移除沙箱「{account.Name}」");
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    /// <summary>账号实属的组名。没填就算第一个组，免得旧配置的账号从列表里凭空消失。</summary>
    private string GroupOf(GameAccount account) =>
        string.IsNullOrWhiteSpace(account.Group)
            ? (Groups.Count > 0 ? Groups[0] : string.Empty)
            : account.Group;

    /// <summary>本次流程要操作的账号 = 当前组的全部账号（要在 Persist 之后调用）。</summary>
    private IReadOnlyList<GameAccount> AccountsForRun() =>
        _config.Accounts
            .Where(a => string.Equals(a.Group, CurrentGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>按当前组重算可见账号。切组、增删账号后都要走一次。</summary>
    private void RebuildVisible()
    {
        VisibleAccounts.Clear();

        foreach (var account in Accounts)
        {
            if (string.Equals(GroupOf(account), CurrentGroup, StringComparison.OrdinalIgnoreCase))
                VisibleAccounts.Add(account);
        }

        Selected = VisibleAccounts.FirstOrDefault();
        RefreshPointText();
    }

    /// <summary>把命令注入到行数据里，并跟踪编辑以便自动落盘。</summary>
    private void Attach(GameAccount account)
    {
        account.FocusCommand = FocusBoxCommand;
        account.StartCommand = StartBoxCommand;
        account.CloseCommand = CloseBoxCommand;
        account.IsFocusEnabled = !IsRunning;
        account.PropertyChanged += (_, e) =>
        {
            // 状态文字与坐标文本是查出来 / 推导出来的，不进配置，别为它反复落盘
            if (e.PropertyName is nameof(GameAccount.StatusText) or nameof(GameAccount.PointText)) return;
            Persist();
        };
    }

    // ── 分组标签 / 状态查询 / 行内操作 ────────────────────────────────────

    /// <summary>按 <see cref="Groups"/> 重建标签条，当前组那一条带下划线。</summary>
    private void RebuildTabs()
    {
        Tabs.Clear();
        foreach (var name in Groups)
        {
            Tabs.Add(new GroupTab(Tabs.Count, name, string.Equals(name, CurrentGroup, StringComparison.OrdinalIgnoreCase))
            {
                SelectCommand = SelectGroupCommand,
            });
        }
    }

    [RelayCommand]
    private void SelectGroup(GroupTab? tab)
    {
        if (tab is null || tab.Index < 0 || tab.Index >= Groups.Count) return;
        CurrentGroupIndex = tab.Index;
    }

    /// <summary>
    /// 按窗口标题里的 [沙箱名] 判断当前组每个沙箱现在有没有活的客户端窗口。
    /// 只有「运行中 / 未启动」两种结论 —— 查不到就是没起来，不做别的推测。
    /// </summary>
    public async Task RefreshStatusesAsync()
    {
        var boxes = VisibleAccounts
            .Select(a => a.Name.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (boxes.Count == 0) return;

        var running = await Task.Run(() => QueryRunningBoxes(boxes));

        Ui(() =>
        {
            foreach (var account in VisibleAccounts)
                account.StatusText = running.Contains(account.Name.Trim()) ? "运行中" : "未启动";
        });
    }

    private static HashSet<string> QueryRunningBoxes(List<string> boxes)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            WindowApi.EnumWindows((hwnd, _) =>
            {
                if (!WindowApi.IsWindowVisible(hwnd)) return true;
                var title = WindowApi.WindowText(hwnd);
                if (string.IsNullOrEmpty(title)) return true;

                foreach (var box in boxes)
                {
                    if (title.Contains($"[{box}]", StringComparison.OrdinalIgnoreCase))
                        running.Add(box);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 枚举失败就当成都没起来：页面打不开比状态不准严重得多
        }

        return running;
    }

    /// <summary>把三个按钮的相对坐标刷到每一行上（坐标是全局配置，每行显示同一份）。</summary>
    private void RefreshPointText()
    {
        // 构造期先给坐标赋值时 Accounts 还没建好，这一趟跳过就行
        if (Accounts is null) return;

        var text = $"{StartX:0.##},{StartY:0.##} · {EnterX:0.##},{EnterY:0.##} · {LoginX:0.##},{LoginY:0.##}";
        foreach (var account in Accounts) account.PointText = text;
    }

    /// <summary>在指定沙箱里拉起启动器，参数与流程里的 StartInBox 一致。</summary>
    private bool StartInSandbox(string box)
    {
        try
        {
            var info = new ProcessStartInfo(_config.SandboxiePath) { UseShellExecute = true };
            info.ArgumentList.Add($"/box:{box}");
            info.ArgumentList.Add(_config.LauncherPath);
            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            _log.Add(ex, $"在沙箱 {box} 启动失败：");
            return false;
        }
    }

    private bool CanRunBox(GameAccount? account) => CanRun && account is not null;

    /// <summary>行内「启动 / 重启」：正在跑的先把这一个关掉再拉起来。</summary>
    [RelayCommand(CanExecute = nameof(CanRunBox))]
    private async Task StartBoxAsync(GameAccount? account)
    {
        if (account is null) return;

        var name = account.Name.Trim();
        if (name.Length == 0) return;

        Persist();
        Ui(() => IsRunning = true);

        try
        {
            if (string.Equals(account.StatusText, "运行中", StringComparison.Ordinal))
            {
                await _service.CloseGroupAsync(_config, new[] { account });
                await Task.Delay(TimeSpan.FromSeconds(GameLaunchService.GraceSeconds));
            }

            if (StartInSandbox(name))
                AppendProgress($"已在沙箱启动 {name}");
            else
                Ui(() => account.StatusText = "启动失败");

            await Task.Delay(TimeSpan.FromSeconds(3));
            await RefreshStatusesAsync();
        }
        finally
        {
            Ui(() => IsRunning = false);
        }
    }

    /// <summary>行内「关闭」：只作用于这一路，关之前照例确认一次。</summary>
    [RelayCommand(CanExecute = nameof(CanRunBox))]
    private async Task CloseBoxAsync(GameAccount? account)
    {
        if (account is null) return;
        await CloseBoxCoreAsync(account, confirm: true);
    }

    private async Task CloseBoxCoreAsync(GameAccount account, bool confirm)
    {
        if (confirm)
        {
            var dialog = new ContentDialog
            {
                Title = $"关闭沙箱「{account.Name}」的客户端",
                Content = $"先请求正常退出，{GameLaunchService.GraceSeconds} 秒后仍未退出的会强制结束进程。\n"
                          + "客户端里未保存的内容会丢失。",
                PrimaryButtonText = "关闭客户端",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _xamlRoot(),
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        Persist();
        Ui(() => IsRunning = true);

        try
        {
            var closed = await _service.CloseGroupAsync(_config, new[] { account });
            AppendProgress(closed > 0
                ? $"已关闭 {account.Name} 的客户端"
                : $"{account.Name} 没有找到可关闭的窗口");
        }
        finally
        {
            Ui(() => IsRunning = false);
        }

        await RefreshStatusesAsync();
    }

    /// <summary>补启：只拉起当前组里还没在跑的沙箱，已经在跑的不动。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartMissingAsync()
    {
        Persist();
        await RefreshStatusesAsync();

        var missing = VisibleAccounts
            .Where(a => a.Name.Trim().Length > 0)
            .Where(a => !string.Equals(a.StatusText, "运行中", StringComparison.Ordinal))
            .ToList();

        if (missing.Count == 0)
        {
            _log.Add("当前组的沙箱都已经在运行，没有需要补启的");
            return;
        }

        Ui(() => IsRunning = true);

        try
        {
            foreach (var account in missing)
            {
                if (StartInSandbox(account.Name.Trim()))
                    AppendProgress($"补启缺失的沙箱 {account.Name}");
                else
                    Ui(() => account.StatusText = "启动失败");

                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
            await RefreshStatusesAsync();
        }
        finally
        {
            Ui(() => IsRunning = false);
        }
    }

    private void Ui(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    /// <summary>
    /// 追加一行进度。服务上报的字符串自带 [HH:mm:ss] 前缀，页面自己产生的补一个，
    /// 日志区才能统一按「时间戳 + 正文」显示。
    /// </summary>
    private void AppendProgress(string message) => Ui(() =>
    {
        if (!message.StartsWith('[')) message = $"[{DateTime.Now:HH:mm:ss}] {message}";

        Progress.Add(message);
        while (Progress.Count > 200) Progress.RemoveAt(0);

        ProgressStep = StripStamp(message);

        // 进度条只认「已经在某个沙箱里拉起启动器」这件事，别的一律不算数；
        // 单条行的启停不算进度，所以只有整组流程跑起来时才统计。
        if (_trackingRun && message.Contains("已在沙箱启动", StringComparison.Ordinal))
        {
            _okCount++;
            if (ProgressTotal > 0 && ProgressDone < ProgressTotal) ProgressDone++;
        }
        else if (_trackingRun && message.Contains("启动失败", StringComparison.Ordinal))
        {
            _failCount++;
        }
    });

    private static string StripStamp(string message) =>
        message.StartsWith('[') && message.IndexOf(']') is var end && end > 0
            ? message[(end + 1)..].Trim()
            : message;

    private void SetLastRunSummary() => Ui(() =>
    {
        LastRunText = $"上次: {_okCount} 成功 · {_failCount} 失败 · {DateTime.Now:HH:mm:ss}";
        HasLastRun = true;
    });

    /// <summary>把界面上的值写回配置并落盘。</summary>
    /// <summary>一键启动流程跑完（成功、失败、被中止都算）触发一次，供外部做收尾。</summary>
    /// <summary>
    /// 按钮定位是否走 OCR。关掉就退回纯固定坐标。
    public event Action? Finished;

    private void Persist()
    {
        _config.SandboxiePath = SandboxiePath.Trim();
        _config.LauncherPath = LauncherPath.Trim();
        _config.ReferenceBox = ReferenceBox.Trim();

        _config.StartButton = new RelativePoint { X = Clamp01(StartX), Y = Clamp01(StartY) };
        _config.EnterButton = new RelativePoint { X = Clamp01(EnterX), Y = Clamp01(EnterY) };
        _config.LoginButton = new RelativePoint { X = Clamp01(LoginX), Y = Clamp01(LoginY) };


        _config.ScreenWidth = (int)Math.Clamp(ScreenWidth, 640, 16384);
        _config.ScreenHeight = (int)Math.Clamp(ScreenHeight, 480, 16384);
        _config.TaskbarHeight = (int)Math.Clamp(TaskbarHeight, 0, 300);

        _config.LaunchIntervalSeconds = (int)Math.Clamp(LaunchInterval, 1, 30);
        _config.LauncherWaitSeconds = (int)Math.Clamp(LauncherWait, 0, 600);
        _config.GameWaitSeconds = (int)Math.Clamp(GameWait, 0, 600);
        _config.StartClickDelaySeconds = (int)Math.Clamp(StartDelay, 0, 120);
        _config.EnterClickDelaySeconds = (int)Math.Clamp(EnterDelay, 0, 120);
        _config.LoginClickDelaySeconds = (int)Math.Clamp(LoginDelay, 0, 120);
        _config.SettleSeconds = (int)Math.Clamp(Settle, 0, 300);

        _config.Groups = Groups.ToList();

        // 组名必须一起拷：漏了它，切一次组账号就全归回第一组了
        _config.Accounts = Accounts
            .Where(a => a.Name.Trim().Length > 0)
            .Select(a => new GameAccount { Name = a.Name.Trim(), Group = GroupOf(a), Slot = a.Slot })
            .ToList();

        _store.SaveLaunch();
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}

/// <summary>
/// 分组标签条的一项。模板里只能看到行数据，所以「是不是当前组」做成可通知的属性，
/// 由视图模型在切组时整条重建 —— 比去改写 ListViewItem 的选中视觉可靠得多。
/// </summary>
public sealed partial class GroupTab : ObservableObject
{
    public GroupTab(int index, string label, bool isSelected)
    {
        Index = index;
        Label = label;
        IsSelected = isSelected;
    }

    public int Index { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>点标签时回写 CurrentGroupIndex，由视图模型注入。</summary>
    public ICommand? SelectCommand { get; set; }

    /// <summary>当前项白字，其余灰字 —— 颜色写在模板里，这里只给状态。</summary>
    public bool IsUnselected => !IsSelected;

    /// <summary>当前项底部那条 2px 强调色下划线。</summary>
    public Visibility UnderlineVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUnselected));
        OnPropertyChanged(nameof(UnderlineVisibility));
    }
}
