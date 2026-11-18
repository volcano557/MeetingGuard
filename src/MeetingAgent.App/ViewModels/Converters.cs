using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MeetingAgent.App.ViewModels;

/// <summary>
/// 将十六进制颜色字符串（如 "#1DB954"）转换为 WPF SolidColorBrush。
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ColorStringToBrushConverter : IValueConverter
{
    public static readonly ColorStringToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorStr && !string.IsNullOrWhiteSpace(colorStr))
        {
            try
            {
                var brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(colorStr));
                brush.Freeze();
                return brush;
            }
            catch
            {
                // 降级为灰色
            }
        }

        return Brushes.SlateGray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// bool 取反转换器（用于 IsEnabled 等）。
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class BoolNegConverter : IValueConverter
{
    public static readonly BoolNegConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// 当值为 null 时返回 Visible（显示占位文字），非 null 时返回 Collapsed（隐藏占位，由 Image 显示）。
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// 与 NullToVisibilityConverter 相反：值非 null 时 Visible，null 时 Collapsed。
/// 用于：Image 的可见性绑定（只有有数据时才显示）。
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public static readonly NotNullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// 字符串非空时返回 Visible，空或 null 时返回 Collapsed（用于错误消息显示）。
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public static readonly NotEmptyToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// 整数为 0 时返回 Visible（空状态占位），非 0 时返回 Collapsed。
/// </summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public static readonly ZeroToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// bool → "已连接" / "未连接" 文本转换器。
/// </summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class BoolToConnectedTextConverter : IValueConverter
{
    public static readonly BoolToConnectedTextConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "已连接" : "未连接";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
