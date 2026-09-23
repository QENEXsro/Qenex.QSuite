using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Qenex.QSuite.Controls.MatrixControl.Converters;

/// <summary>Inverzni BooleanToVisibilityConverter: true -> Collapsed.</summary>
public class TrueToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Prazdny/null text -> Collapsed (popisky os se zobrazuji jen kdyz existuji).</summary>
public class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Pozadi bunky ve write rezimu z (IsDirty, IsWriteError): cervena pri chybe zapisu,
/// zluty nadech pro rozeditovanou (dirty) bunku. V normalnim stavu vraci UnsetValue,
/// aby platilo pozadi z implicitniho theme stylu QTextBoxu (dark/light) — lokalni
/// hodnota by ho jinak trvale prebila. Polopruhledne barvy funguji nad obema tematy.
/// </summary>
/// <summary>Background of the Read button after a failed on-request read: red tint, otherwise
/// unchanged (UnsetValue keeps the theme style).</summary>
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

public class CellStateToBackgroundConverter : IMultiValueConverter
{
    private static readonly Brush DirtyBrush = CreateFrozen(0x55, 0xFF, 0xD7, 0x00);
    private static readonly Brush ErrorBrush = CreateFrozen(0x55, 0xFF, 0x00, 0x00);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isDirty = values.Length > 0 && values[0] is true;
        var isError = values.Length > 1 && values[1] is true;
        return isError ? ErrorBrush : isDirty ? DirtyBrush : DependencyProperty.UnsetValue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
