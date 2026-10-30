using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using LeXiangSync.Models;

namespace LeXiangSync.Native;

/// <summary>与 Rust 侧 <c>abi::Code</c> 一一对应的错误码。</summary>
public enum NativeError
{
    Ok = 0,
    /// <summary>参数不合法：空指针、越界、互斥项冲突、数量超限。</summary>
    InvalidArg = 1,
    /// <summary>JSON 解析失败 / 字段类型不对。</summary>
    InvalidJson = 2,
    /// <summary>同步没在运行，但该操作要求运行中。</summary>
    NotRunning = 3,
    /// <summary>没有指定主窗口。</summary>
    NoMaster = 4,
    /// <summary>目标窗口已不存在（销毁 / 句柄被复用）。</summary>
    WindowGone = 5,
    /// <summary>并发冲突（正停止时又要启动）。</summary>
    Busy = 6,
    /// <summary>平台不支持 / 该控件不支持该操作。</summary>
    Unsupported = 7,
    /// <summary>缓冲区不够，按 out 参数给出的长度重试。</summary>
    BufferTooSmall = 8,
    /// <summary>引擎内部错误（含被兜住的 panic）。</summary>
    Internal = 9,
}

/// <summary>
/// 调用方未及时释放 / 引擎返回错误时抛出。
/// <para><see cref="Code"/> 是稳定的机器可读标识，<see cref="Exception.Message"/> 是给人看的中文说明。</para>
/// </summary>
public sealed class NativeException : Exception
{
    public NativeException(string message, NativeError error = NativeError.Internal, string? code = null)
        : base(message)
    {
        Error = error;
        Code = code ?? error.ToString().ToUpperInvariant();
    }

    public NativeError Error { get; }

    /// <summary>来自 Rust 的原始码字符串（如 <c>INVALID_ARG</c>）。</summary>
    public string Code { get; }
}

/// <summary>引擎状态位。与 Rust 侧 <c>abi::status_flag</c> 对应。</summary>
[Flags]
public enum StatusFlags : uint
{
    None = 0,
    Active = 1 << 0,
    Mouse = 1 << 1,
    Keyboard = 1 << 2,
    Clipboard = 1 << 3,
    TextMirror = 1 << 4,
}

/// <summary>
/// 状态快照（POD，按值从 Rust 返回）。布局必须与 <c>abi::StatusSnapshot</c> 完全一致：
/// 4 个 u32 + i64 + u64 = 32 字节。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StatusSnapshotNative
{
    public uint Abi;
    public uint Flags;
    public uint SlaveCount;
    public uint Reserved;
    public long MasterHwnd;
    public ulong Revision;

    public readonly bool Has(StatusFlags flag) => (Flags & (uint)flag) != 0;

    public readonly SyncStatus ToStatus() => new()
    {
        Active = Has(StatusFlags.Active),
        MasterHwnd = MasterHwnd,
        SlaveCount = (int)SlaveCount,
        SyncMouse = Has(StatusFlags.Mouse),
        SyncKeyboard = Has(StatusFlags.Keyboard),
        SyncClipboard = Has(StatusFlags.Clipboard),
        TextMirror = Has(StatusFlags.TextMirror),
    };
}

/// <summary>
/// 一条窗口记录（POD）。字段顺序/宽度必须与 <c>abi::WindowRecordV1</c> 一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowRecordNative
{
    public long Hwnd;
    public ulong ProcessStartTime;
    public long ParentHwnd;
    public uint Pid;
    public uint Dpi;
    public int ClientWidth;
    public int ClientHeight;
    public uint Visible;
    public uint ExeOff;
    public uint ExeLen;
    public uint ClassOff;
    public uint ClassLen;
    public uint TitleOff;
    public uint TitleLen;
}

/// <summary>
/// 二进制窗口列表的解析。
/// <para>
/// 布局：<c>[header 20B][record × count][utf8 字符串区]</c>，头部是
/// magic / abi / count / record_size / string_bytes。
/// 解析前先校验 magic、ABI 和 record_size —— 对不上就明确报错，
/// 而不是拿错位的偏移去读出一堆垃圾。
/// </para>
/// </summary>
internal static class WindowsBlob
{
    public const uint Magic = 0x3157_5357; // 'WSW1'
    public const int HeaderLen = 20;

    public static List<WindowInfo> Parse(ReadOnlySpan<byte> blob, uint expectedAbi)
    {
        if (blob.Length < HeaderLen)
        {
            throw new NativeException("窗口列表块太短，头部都不完整", NativeError.Internal);
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(blob[..4]);
        if (magic != Magic)
        {
            throw new NativeException($"窗口列表魔数不对：0x{magic:X8}", NativeError.Internal);
        }

        var abi = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..8]);
        if (abi != expectedAbi)
        {
            throw new NativeException(
                $"前端 ABI {expectedAbi} 与核心 ABI {abi} 不一致", NativeError.Unsupported, "ABI_MISMATCH");
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[8..12]);
        var recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[12..16]);
        var stringBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[16..20]);

        var expectedRecord = Marshal.SizeOf<WindowRecordNative>();
        if (recordSize != expectedRecord)
        {
            throw new NativeException(
                $"窗口记录大小不一致：核心 {recordSize} 字节，前端 {expectedRecord} 字节",
                NativeError.Unsupported,
                "ABI_MISMATCH");
        }

        if (blob.Length < HeaderLen + count * recordSize + stringBytes)
        {
            throw new NativeException("窗口列表块长度与头部声明不符", NativeError.Internal);
        }

        var records = MemoryMarshal.Cast<byte, WindowRecordNative>(
            blob.Slice(HeaderLen, count * recordSize));
        var strings = blob.Slice(HeaderLen + count * recordSize, stringBytes);

        var list = new List<WindowInfo>(count);
        foreach (var r in records)
        {
            list.Add(new WindowInfo
            {
                Hwnd = r.Hwnd,
                ProcessStartTime = r.ProcessStartTime,
                ParentHwnd = r.ParentHwnd,
                Pid = r.Pid,
                Dpi = r.Dpi,
                ClientWidth = r.ClientWidth,
                ClientHeight = r.ClientHeight,
                Visible = r.Visible != 0,
                ExePath = Slice(strings, r.ExeOff, r.ExeLen),
                ClassName = Slice(strings, r.ClassOff, r.ClassLen),
                Title = Slice(strings, r.TitleOff, r.TitleLen),
            });
        }

        return list;
    }

    private static string Slice(ReadOnlySpan<byte> strings, uint off, uint len)
    {
        if (len == 0)
        {
            return string.Empty;
        }

        if (off + len > strings.Length)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(strings.Slice((int)off, (int)len));
    }
}
