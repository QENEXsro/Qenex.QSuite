using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Qenex.QSuite.Controls.WatchTableControl.Converters;

/// <summary>
/// Background of a row's value after a failed on-request read (On Request event): red tint,
/// otherwise unchanged (UnsetValue keeps the theme style).
/// </summary>
public class ReadErrorToBackgroundConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = CreateFrozen(0x55, 0xFF, 0x00, 0x00);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ErrorBrush : DependencyProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Inverse BooleanToVisibilityConverter: true -> Collapsed.</summary>
public class TrueToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Edit box background in write mode: orange tint, red on write error.
/// Semi-transparent colors so they work on both light and dark themes.
/// </summary>
public class WriteErrorToBackgroundConverter : IValueConverter
{
    private static readonly Brush WriteBrush = CreateFrozen(0x33, 0xFF, 0xA5, 0x00);
    private static readonly Brush ErrorBrush = CreateFrozen(0x55, 0xFF, 0x00, 0x00);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ErrorBrush : WriteBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
