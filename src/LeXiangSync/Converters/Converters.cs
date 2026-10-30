using LeXiangSync.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeXiangSync.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        return (flag ^ Invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}

/// <summary>非空 → true，用于"已选中某项才启用按钮"。</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>true → 成功提示，false → 信息提示（用于权限状态条）。</summary>
public sealed class BoolToSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success
                      : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>布尔 → 「已开启 / 已关闭」。</summary>
public sealed class BoolStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "已开启" : "已关闭";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>布尔 → 状态色（启用绿 / 停用灰）。</summary>
public sealed class BoolStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(value is true ? Colors.MediumSeaGreen : Colors.Gray);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>日志等级 → 前景色。</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = value switch
        {
            LogLevel.Success => Colors.MediumSeaGreen,
            LogLevel.Warning => Colors.DarkOrange,
            LogLevel.Error => Color.FromArgb(0xFF, 0xF4, 0x62, 0x6A),
            _ => Colors.Gray,
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
