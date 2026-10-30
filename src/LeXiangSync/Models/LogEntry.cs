using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeXiangSync.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>一条运行日志。成员保持可 set，供 XAML 的 x:DataType 代码生成使用。</summary>
public sealed class LogEntry
{
    private static readonly SolidColorBrush InfoBrush = new(Colors.Gray);
    private static readonly SolidColorBrush SuccessBrush = new(Colors.MediumSeaGreen);
    private static readonly SolidColorBrush WarningBrush = new(Colors.DarkOrange);
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xF4, 0x62, 0x6A));

    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;

    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>
    /// 深浅主题切换时重新配色：固定的 Gray / DarkOrange 在深色底上太暗，几乎看不清。
    /// 改的是静态画刷的颜色属性，已经显示的条目会跟着刷新。
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        InfoBrush.Color = dark ? Color.FromArgb(0xFF, 0xB3, 0xB3, 0xB3) : Colors.Gray;
        SuccessBrush.Color = dark ? Color.FromArgb(0xFF, 0x5C, 0xD6, 0xA8) : Colors.MediumSeaGreen;
        WarningBrush.Color = dark ? Color.FromArgb(0xFF, 0xFF, 0xB9, 0x4D) : Colors.DarkOrange;
        ErrorBrush.Color = dark ? Color.FromArgb(0xFF, 0xFF, 0x7B, 0x82) : Color.FromArgb(0xFF, 0xF4, 0x62, 0x6A);
    }

    /// <summary>
    /// 直接给出画刷而不是让 XAML 用转换器：x:Bind 的 StaticResource 转换器
    /// 在 Window 根元素下无法生成（Window 不是 FrameworkElement）。
    /// </summary>
    public Brush LevelBrush => Level switch
    {
        LogLevel.Success => SuccessBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Error => ErrorBrush,
        _ => InfoBrush,
    };
}
