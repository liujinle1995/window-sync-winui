namespace LeXiangSync.Models;

/// <summary>一个可见窗口。字段与 Rust 侧 <c>WindowInfo</c> 一一对应。</summary>
public sealed class WindowInfo
{
    public long Hwnd { get; set; }
    public string Title { get; set; } = string.Empty;
    public uint Pid { get; set; }
    public bool Visible { get; set; } = true;

    /// <summary>十六进制句柄，用于界面展示，如 <c>0x001A0B3C</c>。</summary>
    public string HwndHex => $"0x{Hwnd:X8}";

    public override string ToString() => Title;
}

/// <summary>一个进程及其下属可见窗口。</summary>
public sealed class ProcessInfo
{
    public uint Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<WindowInfo> Windows { get; set; } = new();
}
