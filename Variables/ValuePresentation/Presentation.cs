using Qenex.QSuite.Variables.ValueConversion;

namespace Qenex.QSuite.Variables.ValuePresentation;

public class Presentation : IPresentation
{
    public string Name { get; set; } = string.Empty;
    public double Min { get; set; }
    public double Max { get; set; }
    public string PrintFormat { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;

    public IValConversion Conversion { get; set; } = null!;
}