using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LeXiangSync.Models;

namespace LeXiangSync.Native;

/// <summary>调用方未及时释放 / 引擎返回错误时抛出。</summary>
public sealed class NativeException : Exception
{
    public NativeException(string message) : base(message) { }
}

/// <summary>
/// Rust 核心（window_sync_core.dll）的 P/Invoke 封装。
/// ABI：输入为 UTF-8 JSON，输出为堆分配的 UTF-8 JSON 信封，需 wsync_free 回收。
/// </summary>
internal static class SyncNative
{
    private const string Dll = "window_sync_core";
    private const CallingConvention Cconv = CallingConvention.Cdecl;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static Utf8Callback? s_statusCb;
    private static Utf8Callback? s_logCb;

    [UnmanagedFunctionPointer(Cconv)]
    private delegate void Utf8Callback(IntPtr utf8);

    // ── 基础 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_free", CallingConvention = Cconv)]
    private static extern void Free(IntPtr s);

    [DllImport(Dll, EntryPoint = "wsync_version", CallingConvention = Cconv)]
    private static extern IntPtr VersionCore();

    [DllImport(Dll, EntryPoint = "wsync_set_status_callback", CallingConvention = Cconv)]
    private static extern void SetStatusCallbackCore(IntPtr cb);

    [DllImport(Dll, EntryPoint = "wsync_set_log_callback", CallingConvention = Cconv)]
    private static extern void SetLogCallbackCore(IntPtr cb);

    // ── 查询 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_get_processes", CallingConvention = Cconv)]
    private static extern IntPtr GetProcessesCore();

    [DllImport(Dll, EntryPoint = "wsync_get_windows", CallingConvention = Cconv)]
    private static extern IntPtr GetWindowsCore();

    [DllImport(Dll, EntryPoint = "wsync_get_sync_status", CallingConvention = Cconv)]
    private static extern IntPtr GetSyncStatusCore();

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

    // ── 进程 / 快捷启动 ───────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "wsync_launch_app", CallingConvention = Cconv)]
    private static extern IntPtr LaunchAppCore(IntPtr argsJson);

    [DllImport(Dll, EntryPoint = "wsync_kill_process", CallingConvention = Cconv)]
    private static extern IntPtr KillProcessCore(IntPtr argsJson);

    [DllImport(Dll, EntryPoint = "wsync_kill_process_by_name", CallingConvention = Cconv)]
    private static extern IntPtr KillProcessByNameCore(IntPtr argsJson);

    // ── 公开 API ──────────────────────────────────────────────────────────

    public static string CoreVersion => ReadString(VersionCore());

    public static void SetStatusCallback(Action<string>? handler)
    {
        if (handler is null)
        {
            SetStatusCallbackCore(IntPtr.Zero);
            s_statusCb = null;
            return;
        }
        s_statusCb = p => handler(Marshal.PtrToStringUTF8(p) ?? string.Empty);
        SetStatusCallbackCore(Marshal.GetFunctionPointerForDelegate(s_statusCb));
    }

    public static void SetLogCallback(Action<string>? handler)
    {
        if (handler is null)
        {
            SetLogCallbackCore(IntPtr.Zero);
            s_logCb = null;
            return;
        }
        s_logCb = p => handler(Marshal.PtrToStringUTF8(p) ?? string.Empty);
        SetLogCallbackCore(Marshal.GetFunctionPointerForDelegate(s_logCb));
    }

    public static IReadOnlyList<ProcessInfo> GetProcesses() =>
        Call(GetProcessesCore).Deserialize<List<ProcessInfo>>(Json) ?? new List<ProcessInfo>();

    public static IReadOnlyList<WindowInfo> GetWindows() =>
        Call(GetWindowsCore).Deserialize<List<WindowInfo>>(Json) ?? new List<WindowInfo>();

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

    public static uint LaunchApp(string path, IReadOnlyList<string> args) =>
        Call(Serialize(new LaunchArgs(path, args)), LaunchAppCore).GetUInt32();

    public static void KillProcess(uint pid) => Call(Serialize(new PidArgs(pid)), KillProcessCore);

    public static uint KillProcessByName(string exeName) =>
        Call(Serialize(new NameArgs(exeName)), KillProcessByNameCore).GetUInt32();

    // ── 内部工具 ──────────────────────────────────────────────────────────

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
        throw new NativeException(message ?? "核心引擎返回未知错误");
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private sealed record SyncOptions(bool SyncMouse, bool SyncKeyboard, bool SyncClipboard);
    private sealed record MirrorArgs(bool Enabled);
    private sealed record LaunchArgs(string Path, IReadOnlyList<string> Args);
    private sealed record PidArgs(uint Pid);
    private sealed record NameArgs(string ExeName);
}
