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
/// Pozadi editacni bunky z (IsWriteActive, IsDirty, IsWriteError) — stejne ve vsech controls
/// (Signal, WatchTable, Matrix): oranzovy nadech = write rezim s nezmenenou hodnotou (bunka je
/// editovatelna, at je Write on Enter zapnuty nebo ne), zluty nadech = rozeditovana (dirty)
/// bunka, cervena = chyba zapisu. Mimo write rezim UnsetValue, aby platilo pozadi z implicitniho
/// theme stylu QTextBoxu (dark/light). Polopruhledne barvy funguji nad obema tematy.
/// </summary>
public class EditStateToBackgroundConverter : IMultiValueConverter
{
    private static readonly Brush WriteModeBrush = CreateFrozen(0x33, 0xFF, 0xA5, 0x00);
    private static readonly Brush DirtyBrush = CreateFrozen(0x55, 0xFF, 0xD7, 0x00);
    private static readonly Brush ErrorBrush = CreateFrozen(0x55, 0xFF, 0x00, 0x00);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isWriteActive = values.Length > 0 && values[0] is true;
        var isDirty = values.Length > 1 && values[1] is true;
        var isError = values.Length > 2 && values[2] is true;
        return isError ? ErrorBrush
            : isDirty ? DirtyBrush
            : isWriteActive ? WriteModeBrush
            : DependencyProperty.UnsetValue;
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

/// <summary>
/// Pozadi tlacitka / bunky po neuspesnem cteni nebo zapisu: cervena, jinak beze zmeny
/// (UnsetValue = zustava styl tematu).
/// </summary>
public class ErrorToBackgroundConverter : IValueConverter
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
