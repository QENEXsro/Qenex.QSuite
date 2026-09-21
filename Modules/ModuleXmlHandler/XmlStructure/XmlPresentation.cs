using System.Xml.Serialization;

namespace Qenex.QSuite.ModuleXmlHandler.XmlStructure;

[Serializable]
[XmlRoot("presentation")]
public class XmlPresentation
{
    // Files saved by QInsight 1.0.0 also carry a "label" attribute; it is no longer used and is
    // ignored on load.
    [XmlAttribute("name")] public string Name { get; set; } = string.Empty;
    [XmlAttribute("min")] public double Min { get; set; }
    [XmlAttribute("max")] public double Max { get; set; }
    [XmlAttribute("printFormat")] public string PrintFormat { get; set; } = string.Empty;
    [XmlAttribute("unit")] public string Unit { get; set; } = string.Empty;
    
    [XmlElement("conversionReference")] public XmlConversionReference ConversionReference { get; set; }
}