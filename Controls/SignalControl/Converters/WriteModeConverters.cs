using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Qenex.QSuite.Controls.SignalControl.Converters;

/// <summary>Inverzni BooleanToVisibilityConverter: true -> Collapsed.</summary>
public class TrueToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Pozadi edit boxu ve write rezimu: oranzovy nadech, pri chybe zapisu cervena.
/// Polopruhledne barvy, aby fungovaly nad svetlym i tmavym tematem.
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

/// <summary>
/// Pozadi displeje po neuspesnem cteni na vyzadani (On Request): cervena, jinak beze zmeny
/// (UnsetValue = zustava styl tematu).
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
