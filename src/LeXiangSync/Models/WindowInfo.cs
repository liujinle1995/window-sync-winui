namespace LeXiangSync.Models;

/// <summary>
/// 一个可见窗口的完整身份。
/// <para>
/// 字段与 Rust 侧 <c>WindowIdentity</c> 一一对应。之所以不只用 HWND，是因为句柄会被系统复用：
/// 关掉一个窗口后，新窗口完全可能拿到同一个句柄数值，<c>IsWindow</c> 对它照样返回真。
/// 所以真正用来判断「还是不是原来那个窗口」的是
/// <see cref="Pid"/> + <see cref="ProcessStartTime"/> + <see cref="ExePath"/> + <see cref="ClassName"/>。
/// </para>
/// </summary>
public sealed class WindowInfo
{
    /// <summary>当前窗口句柄。</summary>
    public long Hwnd { get; set; }

    /// <summary>进程 ID。</summary>
    public uint Pid { get; set; }

    /// <summary>
    /// 进程启动时间（FILETIME，100ns 自 1601-01-01）。
    /// 用来挡住 PID 复用：Windows 会回收 PID，光看 PID 分不出「同一个进程」还是「另一个进程」。
    /// </summary>
    public ulong ProcessStartTime { get; set; }

    /// <summary>可执行文件完整路径，用来区分同名程序（比如两个不同目录下的 <c>client.exe</c>）。</summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>窗口类名，识别窗口类型。</summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>窗口标题，辅助识别。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>父窗口句柄，0 表示顶层窗口。用于识别窗口层级（同一进程的主窗 / 工具窗）。</summary>
    public long ParentHwnd { get; set; }

    /// <summary>客户区宽度（DIP 之外的真实像素），坐标映射要用。</summary>
    public int ClientWidth { get; set; }

    /// <summary>客户区高度。</summary>
    public int ClientHeight { get; set; }

    /// <summary>窗口所在显示器的 DPI（96 为 100% 缩放），处理缩放要用。</summary>
    public uint Dpi { get; set; }

    /// <summary>当前是否可见。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>十六进制句柄，用于界面展示，如 <c>0x001A0B3C</c>。</summary>
    public string HwndHex => $"0x{Hwnd:X8}";

    /// <summary>是否顶层窗口。</summary>
    public bool IsTopLevel => ParentHwnd == 0;

    /// <summary>客户区尺寸，取不到时返回 <c>0×0</c>（不要拿它当除数）。</summary>
    public string ClientSizeText =>
        ClientWidth > 0 && ClientHeight > 0 ? $"{ClientWidth}×{ClientHeight}" : "—";

    /// <summary>缩放比例文案，如 <c>125%</c>。</summary>
    public string ScaleText => Dpi == 0 ? "—" : $"{Dpi * 100 / 96}%";

    /// <summary>EXE 文件名（不含目录），路径取不到时退回进程名。</summary>
    public string ExeName
    {
        get
        {
            if (string.IsNullOrEmpty(ExePath))
            {
                return string.Empty;
            }

            var cut = ExePath.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 && cut < ExePath.Length - 1 ? ExePath[(cut + 1)..] : ExePath;
        }
    }

    public override string ToString() => Title;
}

/// <summary>一个进程及其下属可见窗口。</summary>
public sealed class ProcessInfo
{
    public uint Pid { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 可执行文件完整路径。只有「有可见窗口」的进程才会去查（要 <c>OpenProcess</c>，
    /// 有成本），所以这里可能是空串。
    /// </summary>
    public string ExePath { get; set; } = string.Empty;

    public List<WindowInfo> Windows { get; set; } = new();

    public override string ToString() => Name;
}
