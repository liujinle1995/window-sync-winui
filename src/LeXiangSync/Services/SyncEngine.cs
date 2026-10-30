using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LeXiangSync.Models;
using LeXiangSync.Native;
using Microsoft.UI.Dispatching;

namespace LeXiangSync.Services;

/// <summary>
/// 同步引擎门面：唯一持有"主窗口 / 从窗口 / 同步项 / 运行状态"的真相源，
/// 所有页面通过它读写状态，避免各 ViewModel 各自复制一份。
/// </summary>
public sealed partial class SyncEngine : ObservableObject
{
    private readonly LogService _log;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Rust 核心版本号。</summary>
    public string CoreVersion { get; }

    private WindowInfo? _master;
    private bool _syncMouse = true;
    private bool _syncKeyboard = true;
    private bool _syncClipboard;
    private bool _textMirror = true;
    private SyncStatus _status = new();

    /// <summary>已经渲染过的状态版本号，用来丢掉迟到的旧快照。</summary>
    private ulong _renderedRevision;

    public SyncEngine(LogService log)
    {
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Slaves.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SlaveCount));
            OnPropertyChanged(nameof(CanStart));
        };

        try
        {
            CoreVersion = SyncNative.CoreVersion;
            if (SyncNative.UsesPanicAbort)
            {
                _log.Add("核心以 panic=abort 构建：内部一旦 panic 会直接终止整个进程", LogLevel.Warning);
            }
            SyncNative.WarnOnAbiMismatch(msg => _log.Add(msg, LogLevel.Warning));
        }
        catch (Exception) { CoreVersion = "不可用（window_sync_core.dll 未加载）"; }

        // DLL 缺失时不让启动流程崩掉：所有命令都会走 Guard() 退化成日志提示
        try
        {
            // 状态走「无载荷通知 + 拉快照」：回调只带一个 revision，
            // 不再每次变化都序列化一份 JSON 再解析回来。
            SyncNative.SetStatusNotifier(OnStatusChanged);
            SyncNative.SetLogCallback(msg => _log.Add(msg));
        }
        catch (Exception) { /* CoreVersion 已给出提示 */ }
    }

    // ── 状态 ──────────────────────────────────────────────────────────────

    public ObservableCollection<WindowInfo> Slaves { get; } = new();

    public WindowInfo? Master
    {
        get => _master;
        set
        {
            if (!SetProperty(ref _master, value)) return;
            OnPropertyChanged(nameof(MasterTitle));
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public SyncStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsActive => Status.Active;

    public int SlaveCount => Slaves.Count;

    public bool CanStart => Master is not null && Slaves.Count > 0;

    public string MasterTitle => Master?.Title ?? "未选择";

    public string StatusText => IsActive ? "同步中" : "已停止";

    public bool SyncMouse
    {
        get => _syncMouse;
        set { if (SetProperty(ref _syncMouse, value)) PushOptions(); }
    }

    public bool SyncKeyboard
    {
        get => _syncKeyboard;
        set { if (SetProperty(ref _syncKeyboard, value)) PushOptions(); }
    }

    public bool SyncClipboard
    {
        get => _syncClipboard;
        set { if (SetProperty(ref _syncClipboard, value)) PushOptions(); }
    }

    /// <summary>
    /// 文本镜像：定期读主窗口焦点控件的文本做 diff，再用 WM_CHAR 投递给从窗口。
    /// 键盘钩子只能拿到虚拟键码，中文必须靠这条路才能同步。
    /// </summary>
    public bool TextMirror
    {
        get => _textMirror;
        set { if (SetProperty(ref _textMirror, value)) PushOptions(); }
    }

    /// <summary>引擎主动推送状态变更（等价于原 Tauri 版的 <c>sync-status-changed</c> 事件）。</summary>
    public event EventHandler<SyncStatus>? StatusChanged;

    // ── 窗口选择 ──────────────────────────────────────────────────────────

    public bool IsMaster(long hwnd) => Master?.Hwnd == hwnd;

    public bool IsSlave(long hwnd) => Slaves.Any(s => s.Hwnd == hwnd);

    public void SetMaster(WindowInfo? window)
    {
        if (window is null) { Master = null; return; }
        // 主窗口被选为从窗口时先移除，避免自己同步自己
        RemoveSlave(window.Hwnd);
        Master = window;
    }

    public bool ToggleSlave(WindowInfo window)
    {
        if (IsMaster(window.Hwnd)) return false;
        var existing = Slaves.FirstOrDefault(s => s.Hwnd == window.Hwnd);
        if (existing is not null) { Slaves.Remove(existing); return false; }
        Slaves.Add(window);
        return true;
    }

    public void RemoveSlave(long hwnd)
    {
        var hit = Slaves.FirstOrDefault(s => s.Hwnd == hwnd);
        if (hit is not null) Slaves.Remove(hit);
    }

    public void ClearSlaves() => Slaves.Clear();

    /// <summary>把某个从窗口与主窗口互换（原版方案面板里的槽位互换能力）。</summary>
    public void SwapMaster(long slaveHwnd)
    {
        var slave = Slaves.FirstOrDefault(s => s.Hwnd == slaveHwnd);
        if (slave is null || Master is null) return;
        Slaves.Remove(slave);
        Slaves.Add(Master);
        Master = slave;
    }

    /// <summary>刷新进程列表后剔除已消失的窗口，保持选择与现实一致。</summary>
    public int PruneStaleWindows(IReadOnlyList<WindowInfo> alive)
    {
        var aliveSet = alive.Select(w => w.Hwnd).ToHashSet();
        var removed = 0;

        if (Master is not null && !aliveSet.Contains(Master.Hwnd))
        {
            Master = null;
            removed++;
        }
        for (var i = Slaves.Count - 1; i >= 0; i--)
        {
            if (aliveSet.Contains(Slaves[i].Hwnd)) continue;
            Slaves.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动同步。必须在 UI 线程上执行：Rust 侧安装的 WH_MOUSE_LL / WH_KEYBOARD_LL
    /// 低级钩子依赖安装线程的消息循环，放到线程池线程上钩子不会触发。
    /// </summary>
    public void Start()
    {
        if (Master is null) { _log.Add("请先选择主窗口", LogLevel.Warning); return; }
        if (Slaves.Count == 0) { _log.Add("请至少选择一个从窗口", LogLevel.Warning); return; }

        var config = new SyncConfig
        {
            MasterHwnd = Master.Hwnd,
            SlaveHwnds = Slaves.Select(s => s.Hwnd).ToList(),
            SyncMouse = SyncMouse,
            SyncKeyboard = SyncKeyboard,
            SyncClipboard = SyncClipboard,
            TextMirror = TextMirror,
        };

        try
        {
            var message = SyncNative.StartSync(config);
            _log.Add(message, LogLevel.Success);
            RefreshStatus();
        }
        catch (NativeException ex)
        {
            _log.Add($"启动失败: {ex.Message}", LogLevel.Error);
            // 引擎已经整体回滚了，把界面也拉回「未运行」——
            // 否则上一次的成功状态会留在 UI 上，看起来像启动成功了一半。
            RefreshStatus();
        }
    }

    /// <summary>停止同步，同样需要在 UI 线程上执行。</summary>
    public void Stop()
    {
        try
        {
            var message = SyncNative.StopSync();
            _log.Add(message);
            RefreshStatus();
        }
        catch (NativeException ex)
        {
            _log.Add($"停止失败: {ex.Message}", LogLevel.Error);
        }
    }

    public void Toggle()
    {
        if (IsActive) Stop(); else Start();
    }

    private void PushOptions()
    {
        if (!IsActive) return;
        try
        {
            SyncNative.UpdateSyncOptions(SyncMouse, SyncKeyboard, SyncClipboard);
            SyncNative.SetTextMirror(TextMirror);
        }
        catch (NativeException ex) { _log.Add(ex.Message, LogLevel.Warning); }
    }

    public void RefreshStatus()
    {
        try
        {
            Status = SyncNative.GetSyncStatus();
            StatusChanged?.Invoke(this, Status);
        }
        catch (NativeException ex)
        {
            _log.Add(ex.Message, LogLevel.Warning);
        }
    }

    /// <summary>枚举进程（较重，放线程池执行）。</summary>
    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync() =>
        Task.Run(() => Guard(() => SyncNative.GetProcesses(), Array.Empty<ProcessInfo>()));

    public Task<IReadOnlyList<WindowInfo>> GetWindowsAsync() =>
        Task.Run(() => Guard<IReadOnlyList<WindowInfo>>(() => SyncNative.GetWindowsFast(), Array.Empty<WindowInfo>()));

    public Task FocusAsync(long hwnd) =>
        Task.Run(() => Guard(() => { SyncNative.FocusWindow(hwnd); return true; }, false));

    public Task<uint> LaunchAsync(string path, IEnumerable<string> args) =>
        Task.Run(() => Guard(() => SyncNative.LaunchApp(path, args.ToList()), 0u));

    public Task<bool> KillAsync(uint pid) =>
        Task.Run(() => Guard(() => { SyncNative.KillProcess(pid); return true; }, false));

    public Task<uint> KillByNameAsync(string exeName) =>
        Task.Run(() => Guard(() => SyncNative.KillProcessByName(exeName), 0u));

    private T Guard<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch (NativeException ex) { _log.Add(ex.Message, LogLevel.Error); return fallback; }
        catch (DllNotFoundException) { _log.Add("未找到 window_sync_core.dll，请先构建 Rust 核心", LogLevel.Error); return fallback; }
    }

    // ── 回调 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态通知：只带 revision，这里自己去拉一次 POD 快照。
    ///
    /// 两个好处：Rust 侧不必为了通知序列化 JSON；宿主侧也不必解析。
    /// 连续多次变化只会让我们多拉几次快照，而拉快照是一次按值返回的结构体读取。
    /// </summary>
    private void OnStatusChanged(ulong revision)
    {
        // 比已经渲染过的还旧就跳过（回调可能在排队的这段时间里已经过时）
        if (revision <= _renderedRevision) return;

        SyncStatus status;
        try { status = SyncNative.GetStatusFast(); }
        catch (Exception) { return; } // DLL 正在卸载之类，忽略这一拍

        if (_dispatcher.HasThreadAccess) ApplyStatus(status, revision);
        else _dispatcher.TryEnqueue(() => ApplyStatus(status, revision));
    }

    private void ApplyStatus(SyncStatus status, ulong revision)
    {
        if (revision < _renderedRevision) return; // 迟到的旧快照
        _renderedRevision = revision;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// 停同步 + 摘回调。宿主退出时必须调 —— 否则回调会打到已经析构的对象上。
    /// </summary>
    public void Dispose()
    {
        try { if (IsActive) Stop(); } catch (Exception) { /* 退出路径不抛 */ }
        SyncNative.Shutdown();
    }
}
