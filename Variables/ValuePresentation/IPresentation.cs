using Qenex.QSuite.Variables.ValueConversion;

namespace Qenex.QSuite.Variables.ValuePresentation;

public interface IPresentation
{
    string Name { get; set; }
    double Min { get; set; }
    double Max { get; set; }
    string PrintFormat { get; set; }
    string Unit { get; set; }
    IValConversion Conversion { get; set; }
}