using System.Collections.ObjectModel;
using System.Globalization;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

public class ProjectConfigurationPresentationWrapper : PropertyChangedBase
{
    private readonly List<EditablePropertyWrapper> editableProperties = [];

    private List<IValConversion> conversions;
    private EditablePropertyWrapper? conversionProperty;

    private PresentationState originalState;
    private PresentationState currentState;

    public ProjectConfigurationPresentationWrapper(
        IPresentation presentation,
        IEnumerable<IValConversion> conversions,
        bool isNew = false)
    {
        Presentation = presentation;
        this.conversions = conversions.ToList();
        IsNew = isNew;
        originalState = PresentationState.FromPresentation(presentation);
        currentState = originalState;
        Properties = CreateProperties();
    }

    public IPresentation Presentation { get; }
    public bool IsNew { get; }
    public string Name => currentState.Name;
    public string DisplayName => currentState.Name;
    public ObservableCollection<EditablePropertyWrapper> Properties { get; }
    public bool HasChanges => !currentState.Equals(originalState);

    public void ApplyChanges()
    {
        Presentation.Name = currentState.Name;
        Presentation.Min = currentState.Min;
        Presentation.Max = currentState.Max;
        Presentation.PrintFormat = currentState.PrintFormat;
        Presentation.Unit = currentState.Unit;
        Presentation.Conversion = GetConversion();

        originalState = currentState;
        NotifyStateChanged();
    }

    public void CancelChanges()
    {
        currentState = originalState;
        RefreshProperties();
        NotifyStateChanged();
    }

    /// <summary>
    /// Builds a standalone presentation from the wrapper's CURRENT (possibly not yet applied)
    /// state, so pending edits are included. The conversion is shared by reference.
    /// Used for copying and for XML export.
    /// </summary>
    public IPresentation CreatePresentationSnapshot(string name)
    {
        return new Presentation
        {
            Name = name,
            Min = currentState.Min,
            Max = currentState.Max,
            PrintFormat = currentState.PrintFormat,
            Unit = currentState.Unit,
            Conversion = GetConversion()
        };
    }

    public void RefreshConverterOptions(IEnumerable<IValConversion> converters)
    {
        // Aktualizujeme i interni seznam objektu - GetConversion() podle nej resi vyber
        // pri Apply, takze po prejmenovani/odebrani converteru musi byt aktualni.
        conversions = converters.ToList();
        conversionProperty?.SetOptions(conversions.Select(conversion => conversion.Name));
    }

    private ObservableCollection<EditablePropertyWrapper> CreateProperties()
    {
        var properties = new ObservableCollection<EditablePropertyWrapper>
        {
            Create("Presentation", "Name", () => currentState.Name, value => UpdateState(currentState with { Name = value })),
            Create("Presentation", "Min", () => currentState.Min, value => UpdateState(currentState with { Min = Parse<double>(value) })),
            Create("Presentation", "Max", () => currentState.Max, value => UpdateState(currentState with { Max = Parse<double>(value) })),
            Create("Presentation", "Print Format", () => currentState.PrintFormat, value => UpdateState(currentState with { PrintFormat = value })),
            Create("Presentation", "Unit", () => currentState.Unit, value => UpdateState(currentState with { Unit = value }))
        };

        conversionProperty = Create(
            "Presentation",
            "Conversion",
            () => currentState.ConversionName,
            value => UpdateState(currentState with { ConversionName = value }),
            conversions.Select(conversion => conversion.Name));
        properties.Add(conversionProperty);

        foreach (var property in properties)
        {
            editableProperties.Add(property);
        }

        return properties;
    }

    private EditablePropertyWrapper Create(
        string group,
        string name,
        Func<object?> getValue,
        Action<string> setValue,
        IEnumerable<string>? options = null)
    {
        return new EditablePropertyWrapper(group, name, getValue, setValue, null, options);
    }

    private IValConversion GetConversion()
    {
        var conversionsList = conversions.ToList();
        var conversion = conversionsList.FirstOrDefault(conversion =>
            conversion.Name.Equals(currentState.ConversionName, StringComparison.OrdinalIgnoreCase));
        if (conversion != null)
        {
            return conversion;
        }

        if (Presentation.Conversion != null && conversionsList.Contains(Presentation.Conversion))
        {
            return Presentation.Conversion;
        }

        throw new InvalidOperationException($"Conversion \"{currentState.ConversionName}\" was not found.");
    }

    private void UpdateState(PresentationState state)
    {
        currentState = state;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshProperties()
    {
        foreach (var property in editableProperties)
        {
            property.RefreshValue();
        }
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

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value, ignoreCase: true);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private sealed record PresentationState(
        string Name,
        double Min,
        double Max,
        string PrintFormat,
        string Unit,
        string ConversionName)
    {
        public static PresentationState FromPresentation(IPresentation presentation)
        {
            return new PresentationState(
                presentation.Name,
                presentation.Min,
                presentation.Max,
                presentation.PrintFormat,
                presentation.Unit,
                presentation.Conversion?.Name ?? string.Empty);
        }
    }
}
