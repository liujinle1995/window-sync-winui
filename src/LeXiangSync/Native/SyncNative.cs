using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LeXiangSync.Models;

namespace LeXiangSync.Native;

/// <summary>
/// Rust 核心（window_sync_core.dll）的 P/Invoke 封装。
/// <para>
/// 两条通道：
/// <list type="bullet">
/// <item>控制面 —— UTF-8 JSON 进、堆分配的 JSON 信封出，需要 <c>wsync_free</c> 回收。
/// 启停、改配置、进程管理这些低频调用走这里，可读、好调试。</item>
/// <item>数据面 —— POD 结构体按值返回、或一段连续字节（窗口列表）、或调用方缓冲区（文本）。
/// 窗口刷新、状态轮询、大段文本走这里，没有序列化也没有解析。</item>
/// </list>
/// </para>
/// </summary>
internal static class SyncNative
{
    private const string Dll = "window_sync_core";
    private const CallingConvention Cconv = CallingConvention.Cdecl;

    /// <summary>
    /// 前端期望的 ABI 版本。和 Rust <c>abi::ABI_VERSION</c> 必须一致，
    /// 不一致时二进制块会被拒绝解析（而不是读出错位的数据）。
    /// </summary>
    public const uint ExpectedAbi = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static Utf8Callback? s_statusCb;
    private static Utf8Callback? s_logCb;
    private static RevisionCallback? s_notifyCb;

    private static ulong s_statusToken;
    private static ulong s_logToken;
    private static ulong s_notifyToken;

    private static readonly uint s_abiVersion = SafeAbiVersion();
    private static bool s_loggedAbiMismatch;

    [UnmanagedFunctionPointer(Cconv)]
    private delegate void Utf8Callback(IntPtr utf8);

    [UnmanagedFunctionPointer(Cconv)]
    private delegate void RevisionCallback(ulong revision);

    // ── 版本 / 生命周期 ───────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_free", CallingConvention = Cconv)]
    private static extern void Free(IntPtr s);

    [DllImport(Dll, EntryPoint = "wsync_free_bytes", CallingConvention = Cconv)]
    private static extern void FreeBytes(IntPtr ptr, uint len);

    [DllImport(Dll, EntryPoint = "wsync_abi_version", CallingConvention = Cconv)]
    private static extern uint AbiVersionCore();

    /// <summary>静态字符串，**不要** free。</summary>
    [DllImport(Dll, EntryPoint = "wsync_core_version", CallingConvention = Cconv)]
    private static extern IntPtr CoreVersionCore();

    [DllImport(Dll, EntryPoint = "wsync_panic_mode", CallingConvention = Cconv)]
    private static extern uint PanicModeCore();

    [DllImport(Dll, EntryPoint = "wsync_shutdown", CallingConvention = Cconv)]
    private static extern void ShutdownCore();

    // ── 回调 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_set_status_callback", CallingConvention = Cconv)]
    private static extern ulong SetStatusCallbackCore(IntPtr cb);

    [DllImport(Dll, EntryPoint = "wsync_set_status_notifier", CallingConvention = Cconv)]
    private static extern ulong SetStatusNotifierCore(IntPtr cb);

    [DllImport(Dll, EntryPoint = "wsync_set_log_callback", CallingConvention = Cconv)]
    private static extern ulong SetLogCallbackCore(IntPtr cb);

    [DllImport(Dll, EntryPoint = "wsync_clear_status_callback", CallingConvention = Cconv)]
    private static extern byte ClearStatusCallbackCore(ulong token);

    [DllImport(Dll, EntryPoint = "wsync_clear_status_notifier", CallingConvention = Cconv)]
    private static extern byte ClearStatusNotifierCore(ulong token);

    [DllImport(Dll, EntryPoint = "wsync_clear_log_callback", CallingConvention = Cconv)]
    private static extern byte ClearLogCallbackCore(ulong token);

    // ── 状态（POD） ───────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_status_snapshot", CallingConvention = Cconv)]
    private static extern StatusSnapshotNative StatusSnapshotCore();

    [DllImport(Dll, EntryPoint = "wsync_status_revision", CallingConvention = Cconv)]
    private static extern ulong StatusRevisionCore();

    // ── 查询 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_get_processes", CallingConvention = Cconv)]
    private static extern IntPtr GetProcessesCore();

    [DllImport(Dll, EntryPoint = "wsync_get_windows", CallingConvention = Cconv)]
    private static extern IntPtr GetWindowsCore();

    [DllImport(Dll, EntryPoint = "wsync_get_windows_bin", CallingConvention = Cconv)]
    private static extern IntPtr GetWindowsBinCore(out uint len);

    [DllImport(Dll, EntryPoint = "wsync_get_sync_status", CallingConvention = Cconv)]
    private static extern IntPtr GetSyncStatusCore();

    [DllImport(Dll, EntryPoint = "wsync_last_error", CallingConvention = Cconv)]
    private static extern IntPtr LastErrorCore();

    // ── 文本缓冲区 ────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_text_read", CallingConvention = Cconv)]
    private static extern int TextReadCore(long hwnd, IntPtr buf, uint cap, out uint outLen);

    [DllImport(Dll, EntryPoint = "wsync_text_write", CallingConvention = Cconv)]
    private static extern int TextWriteCore(long hwnd, IntPtr buf, uint len);

    // ── 同步控制 ──────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_start_sync", CallingConvention = Cconv)]
    private static extern IntPtr StartSyncCore(IntPtr configJson);

    [DllImport(Dll, EntryPoint = "wsync_stop_sync", CallingConvention = Cconv)]
    private static extern IntPtr StopSyncCore();

    [DllImport(Dll, EntryPoint = "wsync_update_sync_options", CallingConvention = Cconv)]
    private static extern IntPtr UpdateSyncOptionsCore(IntPtr optionsJson);

    [DllImport(Dll, EntryPoint = "wsync_set_text_mirror", CallingConvention = Cconv)]
    private static extern IntPtr SetTextMirrorCore(IntPtr argsJson);

    [DllImport(Dll, EntryPoint = "wsync_focus_window", CallingConvention = Cconv)]
    private static extern IntPtr FocusWindowCore(long hwnd);

    [DllImport(Dll, EntryPoint = "wsync_swap_master", CallingConvention = Cconv)]
    private static extern IntPtr SwapMasterCore(long slaveHwnd);

    // ── 进程 / 快捷启动 ───────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_launch_app", CallingConvention = Cconv)]
    private static extern IntPtr LaunchAppCore(IntPtr argsJson);

    [DllImport(Dll, EntryPoint = "wsync_kill_process", CallingConvention = Cconv)]
    private static extern IntPtr KillProcessCore(IntPtr argsJson);

    [DllImport(Dll, EntryPoint = "wsync_kill_process_by_name", CallingConvention = Cconv)]
    private static extern IntPtr KillProcessByNameCore(IntPtr argsJson);

    // ── 公开 API ──────────────────────────────────────────────────────────

    /// <summary>核心版本（静态字符串，零分配）。</summary>
    public static string CoreVersion
    {
        get
        {
            var p = CoreVersionCore();
            return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(p) ?? string.Empty;
        }
    }

    /// <summary>核心报告的二进制布局版本。</summary>
    public static uint AbiVersion => s_abiVersion;

    /// <summary>前端期望的 ABI 与核心是否一致。</summary>
    public static bool AbiMatches => s_abiVersion == ExpectedAbi;

    /// <summary>
    /// 核心是不是用 <c>panic = "abort"</c> 构建的。是的话一旦内部 panic，
    /// 整个进程会被直接终止，<c>catch_unwind</c> 不起作用 —— 启动时应当警告。
    /// </summary>
    public static bool UsesPanicAbort
    {
        get
        {
            try { return PanicModeCore() == 1; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>
    /// 注册状态通知（高频首选）：回调只带一个 revision，宿主自己来拉快照。
    /// 返回 token，注销时要用同一个 token。
    /// </summary>
    public static ulong SetStatusNotifier(Action<ulong>? handler)
    {
        if (handler is null)
        {
            if (s_notifyToken != 0) ClearStatusNotifierCore(s_notifyToken);
            s_notifyToken = 0;
            s_notifyCb = null;
            return 0;
        }

        s_notifyCb = rev => handler(rev);
        s_notifyToken = SetStatusNotifierCore(Marshal.GetFunctionPointerForDelegate(s_notifyCb));
        return s_notifyToken;
    }

    /// <summary>注册状态回调（JSON 载荷，兼容老宿主）。</summary>
    public static ulong SetStatusCallback(Action<string>? handler)
    {
        if (handler is null)
        {
            if (s_statusToken != 0) ClearStatusCallbackCore(s_statusToken);
            s_statusToken = 0;
            s_statusCb = null;
            return 0;
        }

        s_statusCb = p => handler(Marshal.PtrToStringUTF8(p) ?? string.Empty);
        s_statusToken = SetStatusCallbackCore(Marshal.GetFunctionPointerForDelegate(s_statusCb));
        return s_statusToken;
    }

    public static ulong SetLogCallback(Action<string>? handler)
    {
        if (handler is null)
        {
            if (s_logToken != 0) ClearLogCallbackCore(s_logToken);
            s_logToken = 0;
            s_logCb = null;
            return 0;
        }

        s_logCb = p => handler(Marshal.PtrToStringUTF8(p) ?? string.Empty);
        s_logToken = SetLogCallbackCore(Marshal.GetFunctionPointerForDelegate(s_logCb));
        return s_logToken;
    }

    /// <summary>
    /// 退出前调用：先停同步再摘回调。
    /// 顺序不能反 —— 拆同步线程的过程中还会往回调里推东西，那时宿主可能已经析构了。
    /// </summary>
    public static void Shutdown()
    {
        try { ShutdownCore(); }
        catch (EntryPointNotFoundException) { /* 老版本核心没有这个入口 */ }
        catch (DllNotFoundException) { return; }

        s_notifyToken = 0;
        s_statusToken = 0;
        s_logToken = 0;
        s_notifyCb = null;
        s_statusCb = null;
        s_logCb = null;
    }

    /// <summary>
    /// ABI 不匹配时记一次日志（只记一次，避免每次刷新都刷屏）。
    /// </summary>
    public static void WarnOnAbiMismatch(Action<string> log)
    {
        if (s_loggedAbiMismatch || AbiMatches) return;
        s_loggedAbiMismatch = true;
        log($"核心 ABI {s_abiVersion} 与前端期望的 {ExpectedAbi} 不一致，请重新构建 Rust 核心");
    }

    /// <summary>状态快照：POD 按值返回，无分配、无 JSON。</summary>
    public static SyncStatus GetStatusFast() => StatusSnapshotCore().ToStatus();

    public static ulong StatusRevision => StatusRevisionCore();

    public static IReadOnlyList<ProcessInfo> GetProcesses() =>
        Call(GetProcessesCore).Deserialize<List<ProcessInfo>>(Json) ?? new List<ProcessInfo>();

    /// <summary>窗口列表（JSON，调试用）。</summary>
    public static IReadOnlyList<WindowInfo> GetWindows() =>
        Call(GetWindowsCore).Deserialize<List<WindowInfo>>(Json) ?? new List<WindowInfo>();

    /// <summary>
    /// 窗口列表（二进制）。几百个窗口时这条路省掉「序列化 + 解析 + 一堆小字符串」，
    /// 只剩一次内存拷贝。
    /// </summary>
    public static List<WindowInfo> GetWindowsFast()
    {
        var ptr = GetWindowsBinCore(out var len);
        if (ptr == IntPtr.Zero)
        {
            throw FromLastError("枚举窗口失败");
        }

        try
        {
            // 直接在这块本机内存上切片，不做中间拷贝 —— 这条路的意义就在这里
            unsafe
            {
                var span = new ReadOnlySpan<byte>(ptr.ToPointer(), checked((int)len));
                return WindowsBlob.Parse(span, ExpectedAbi);
            }
        }
        finally
        {
            FreeBytes(ptr, len);
        }
    }

    /// <summary>当前同步状态（JSON，兼容）。新代码用 <see cref="GetStatusFast"/>。</summary>
    public static SyncStatus GetSyncStatus() =>
        Call(GetSyncStatusCore).Deserialize<SyncStatus>(Json) ?? new SyncStatus();

    public static string StartSync(SyncConfig config) =>
        Call(Serialize(config), StartSyncCore).GetString() ?? string.Empty;

    public static string StopSync() =>
        Call(StopSyncCore).GetString() ?? string.Empty;

    public static void UpdateSyncOptions(bool mouse, bool keyboard, bool clipboard) =>
        Call(Serialize(new SyncOptions(mouse, keyboard, clipboard)), UpdateSyncOptionsCore);

    /// <summary>开关文本镜像（中文 / 粘贴内容同步）。</summary>
    public static void SetTextMirror(bool enabled) =>
        Call(Serialize(new MirrorArgs(enabled)), SetTextMirrorCore);

    public static void FocusWindow(long hwnd) => Call(() => FocusWindowCore(hwnd));

    /// <summary>
    /// 主从对调：把指定从窗口提升为主窗口，原主窗口降为从窗口。
    /// <para>
    /// 同步运行中也能切，投递不中断 —— 引擎会先放掉按住的键、重建窗口身份、
    /// 作废文本基线，再从新主窗口开始采集。未运行时调用会抛
    /// <see cref="NativeError.NotRunning"/>。
    /// </para>
    /// </summary>
    public static void SwapMaster(long slaveHwnd) => Call(() => SwapMasterCore(slaveHwnd));

    public static uint LaunchApp(string path, IReadOnlyList<string> args) =>
        Call(Serialize(new LaunchArgs(path, args)), LaunchAppCore).GetUInt32();

    public static void KillProcess(uint pid) => Call(Serialize(new PidArgs(pid)), KillProcessCore);

    public static uint KillProcessByName(string exeName) =>
        Call(Serialize(new NameArgs(exeName)), KillProcessByNameCore).GetUInt32();

    // ── 文本缓冲区 ────────────────────────────────────────────────────────

    /// <summary>
    /// 读目标窗口的文本。先按 0 容量问一次长度，再按需分配 —— 大段文本不用猜大小。
    /// 读不到（自绘控件）时抛 <see cref="NativeError.Unsupported"/>。
    /// </summary>
    public static string ReadText(long hwnd)
    {
        var rc = TextReadCore(hwnd, IntPtr.Zero, 0, out var need);
        if (rc != (int)NativeError.BufferTooSmall)
        {
            if (rc != 0) throw FromCode(rc, "读取文本失败");
            return string.Empty; // 空文本
        }

        var buf = Marshal.AllocHGlobal((int)need);
        try
        {
            rc = TextReadCore(hwnd, buf, need, out var got);
            if (rc != 0) throw FromCode(rc, "读取文本失败");
            return Marshal.PtrToStringUTF8(buf, (int)got) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>把一段 UTF-8 文本整段写进目标窗口的文本控件。</summary>
    public static void WriteText(long hwnd, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buf = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            var rc = TextWriteCore(hwnd, buf, (uint)bytes.Length);
            if (rc != 0) throw FromCode(rc, "写入文本失败");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ── 内部工具 ──────────────────────────────────────────────────────────

    private static uint SafeAbiVersion()
    {
        try { return AbiVersionCore(); }
        catch (EntryPointNotFoundException) { return 0; } // 老核心没有这个入口
        catch (DllNotFoundException) { return 0; }
    }

    private static NativeException FromCode(int rc, string fallback)
    {
        var error = Enum.IsDefined(typeof(NativeError), rc) ? (NativeError)rc : NativeError.Internal;
        string message = fallback;
        string? code = null;

        try
        {
            var p = LastErrorCore();
            if (p != IntPtr.Zero)
            {
                var json = Marshal.PtrToStringUTF8(p);
                Free(p);
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("code", out var c)) code = c.GetString();
                    if (root.TryGetProperty("error", out var e)) message = e.GetString() ?? message;
                }
            }
        }
        catch (Exception)
        {
            // 拿不到细节就用手上的兜底文案
        }

        return new NativeException(message, error, code);
    }

    private static NativeException FromLastError(string fallback) => FromCode(-1, fallback);

    private static string ReadString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUTF8(ptr) ?? string.Empty; }
        finally { Free(ptr); }
    }

    /// <summary>执行无参调用，成功返回 data 节点，失败抛 <see cref="NativeException"/>。</summary>
    private static JsonElement Call(Func<IntPtr> native)
    {
        using var doc = JsonDocument.Parse(ReadString(native()));
        return Unwrap(doc.RootElement);
    }

    /// <summary>执行带 JSON 参数的调用。</summary>
    private static JsonElement Call(string argJson, Func<IntPtr, IntPtr> native)
    {
        var bytes = Encoding.UTF8.GetBytes(argJson + "\0");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            using var doc = JsonDocument.Parse(ReadString(native(handle.AddrOfPinnedObject())));
            return Unwrap(doc.RootElement);
        }
        finally { handle.Free(); }
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String) return root;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return root.GetProperty("data").Clone();

        var message = root.TryGetProperty("error", out var err) ? err.GetString() : null;
        var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
        var value = root.TryGetProperty("code_value", out var cv) && cv.TryGetInt32(out var n)
            ? (NativeError)n
            : NativeError.Internal;

        throw new NativeException(message ?? "核心引擎返回未知错误", value, code);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private sealed record SyncOptions(bool SyncMouse, bool SyncKeyboard, bool SyncClipboard);
    private sealed record MirrorArgs(bool Enabled);
    private sealed record LaunchArgs(string Path, IReadOnlyList<string> Args);
    private sealed record PidArgs(uint Pid);
    private sealed record NameArgs(string ExeName);
}
