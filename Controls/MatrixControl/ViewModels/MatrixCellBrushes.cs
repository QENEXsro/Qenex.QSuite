using System.Windows.Media;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>Colour scale of the data cells (setting of the control, persisted in the project).</summary>
public enum MatrixSpectrum
{
    Rainbow,
    BlueRed,
    GreenYellowRed,
    Viridis,
    Grayscale
}

/// <summary>
/// Shared frozen brushes of the cell backgrounds. State colours are the same semi-transparent
/// tints as in the Single-Signal and Watch Table controls (work over both themes); the colour
/// scale is quantised to a fixed number of steps so the brushes are shared by all cells.
/// </summary>
public static class MatrixCellBrushes
{
    private const int HeatSteps = 64;
    private const byte HeatAlpha = 0x90;

    public static readonly Brush None = Frozen(Colors.Transparent);
    public static readonly Brush WriteMode = Frozen(Color.FromArgb(0x33, 0xFF, 0xA5, 0x00));
    public static readonly Brush Dirty = Frozen(Color.FromArgb(0x55, 0xFF, 0xD7, 0x00));
    public static readonly Brush Error = Frozen(Color.FromArgb(0x55, 0xFF, 0x00, 0x00));

    private static readonly Dictionary<MatrixSpectrum, Brush[]> HeatCache = new();

    /// <summary>Brush of the colour scale for a normalised position 0..1 (clamped).</summary>
    public static Brush Heat(MatrixSpectrum spectrum, double position)
    {
        if (double.IsNaN(position))
        {
            return None;
        }

        position = Math.Clamp(position, 0d, 1d);
        Brush[] brushes;
        lock (HeatCache)
        {
            if (!HeatCache.TryGetValue(spectrum, out var cached))
            {
                cached = new Brush[HeatSteps];
                for (var i = 0; i < HeatSteps; i++)
                {
                    cached[i] = Frozen(Sample(spectrum, (double)i / (HeatSteps - 1)));
                }

                HeatCache[spectrum] = cached;
            }

            brushes = cached;
        }

        return brushes[(int)Math.Round(position * (HeatSteps - 1))];
    }

    private static Color Sample(MatrixSpectrum spectrum, double t)
    {
        var stops = spectrum switch
        {
            MatrixSpectrum.BlueRed => new[] { Rgb(0x3B, 0x4C, 0xC0), Rgb(0xDD, 0xDD, 0xDD), Rgb(0xB4, 0x04, 0x26) },
            MatrixSpectrum.GreenYellowRed => new[] { Rgb(0x00, 0xC0, 0x00), Rgb(0xFF, 0xFF, 0x00), Rgb(0xFF, 0x00, 0x00) },
            MatrixSpectrum.Viridis => new[] { Rgb(0x44, 0x01, 0x54), Rgb(0x3B, 0x52, 0x8B), Rgb(0x21, 0x91, 0x8C), Rgb(0x5E, 0xC9, 0x62), Rgb(0xFD, 0xE7, 0x25) },
            MatrixSpectrum.Grayscale => new[] { Rgb(0x20, 0x20, 0x20), Rgb(0xF0, 0xF0, 0xF0) },
            _ => new[] { Rgb(0x00, 0x00, 0xFF), Rgb(0x00, 0xFF, 0xFF), Rgb(0x00, 0xFF, 0x00), Rgb(0xFF, 0xFF, 0x00), Rgb(0xFF, 0x00, 0x00) }
        };

        var scaled = t * (stops.Length - 1);
        var index = Math.Min((int)Math.Floor(scaled), stops.Length - 2);
        var local = scaled - index;
        var a = stops[index];
        var b = stops[index + 1];
        return Color.FromArgb(HeatAlpha,
            (byte)Math.Round(a.R + (b.R - a.R) * local),
            (byte)Math.Round(a.G + (b.G - a.G) * local),
            (byte)Math.Round(a.B + (b.B - a.B) * local));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
