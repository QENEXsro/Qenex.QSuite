using System.Collections.ObjectModel;
using System.Globalization;
using Qenex.QInsight.ViewModels.ModelWrappers;
using Qenex.QInsight.ViewModels.SolutionExplorerWrappers;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;

namespace Qenex.QInsight.ViewModels;

public class PresentationPropertiesViewModel : PropertyChangedBaseWithValidation, IPropertiesViewModel
{
    private readonly IPresentation presentation;
    private readonly Action refreshSource;
    private readonly IEnumerable<IValConversion> conversions;
    private readonly Func<bool> getIsEditPresentationEnabled;

    public PresentationPropertiesViewModel(
        EventAggregator ea,
        PresentationSeWrapper presentationWrapper,
        IEnumerable<IValConversion> conversions,
        Func<bool> getIsEditPresentationEnabled)
    {
        _ = ea;
        presentation = presentationWrapper.Presentation;
        refreshSource = presentationWrapper.Refresh;
        this.conversions = conversions;
        this.getIsEditPresentationEnabled = getIsEditPresentationEnabled;
        Properties = CreateProperties();
    }

    public ObservableCollection<EditablePropertyWrapper> Properties { get; }

    private ObservableCollection<EditablePropertyWrapper> CreateProperties()
    {
        return
        [
            Create("Presentation", "Name", () => presentation.Name, value => presentation.Name = value),
            Create("Presentation", "Min", () => presentation.Min, value => presentation.Min = Parse<double>(value)),
            Create("Presentation", "Max", () => presentation.Max, value => presentation.Max = Parse<double>(value)),
            Create("Presentation", "Print Format", () => presentation.PrintFormat, value => presentation.PrintFormat = value),
            Create("Presentation", "Unit", () => presentation.Unit, value => presentation.Unit = value),
            Create(
                "Presentation",
                "Conversion",
                () => presentation.Conversion?.Name ?? string.Empty,
                SetConversion,
                conversions.Select(conversion => conversion.Name))
        ];
    }

    private EditablePropertyWrapper Create(
        string group,
        string name,
        Func<object?> getValue,
        Action<string> setValue,
        IEnumerable<string>? options = null)
    {
        return new EditablePropertyWrapper(group, name, getValue, setValue, refreshSource, options, IsPropertyReadOnly);
    }

    private bool IsPropertyReadOnly()
    {
        return !getIsEditPresentationEnabled();
    }

    public void RefreshReadOnly()
    {
        foreach (var property in Properties)
        {
            property.RefreshReadOnly();
        }
    }

    public void RefreshProperties()
    {
        foreach (var property in Properties)
        {
            property.RefreshValue();
        }
    }

    private void SetConversion(string value)
    {
        var conversion = conversions.FirstOrDefault(conversion =>
            conversion.Name.Equals(value, StringComparison.OrdinalIgnoreCase));

        presentation.Conversion = conversion
            ?? throw new InvalidOperationException($"Conversion \"{value}\" was not found.");
    }

    private static T Parse<T>(string value)
    {
        return (T)ConvertTo(value, typeof(T));
    }

    private static object ConvertTo(string value, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return value;
        }

        if (targetType == typeof(bool))
        {
            return bool.Parse(value);
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value, ignoreCase: true);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }
}
