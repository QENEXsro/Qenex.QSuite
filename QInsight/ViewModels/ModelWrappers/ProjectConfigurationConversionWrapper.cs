using System.Collections.ObjectModel;
using System.Globalization;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.ValueConversion;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

public class ProjectConfigurationConversionWrapper : PropertyChangedBase
{
    private readonly List<EditablePropertyWrapper> editableProperties = [];
    private readonly IReadOnlyList<string> conversionTypeOptions = ConversionsGlobal.ConversionTypeDict.Keys
        .Where(type => type != ConversionsGlobal.ConversionType.Undefined)
        .Select(type => type.ToString())
        .ToList();

    private ConversionState originalState;
    private ConversionState currentState;

    public ProjectConfigurationConversionWrapper(IValConversion conversion, bool isNew = false)
    {
        Conversion = conversion;
        IsNew = isNew;
        originalState = ConversionState.FromConversion(conversion);
        currentState = originalState;
        Properties = [];
        EnumValues = [];
        AddEnumValueCommand = new RelayCommand<object>(_ => AddEnumValue(), _ => IsEnumConversion);
        RemoveEnumValueCommand = new RelayCommand<object>(_ => RemoveEnumValue(), _ => SelectedEnumValue != null);
        RebuildProperties();
        RebuildEnumValues();
    }

    public IValConversion Conversion
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public bool IsNew { get; }

    public ObservableCollection<EditablePropertyWrapper> Properties { get; }
    public ObservableCollection<ProjectConfigurationEnumConversionValueWrapper> EnumValues { get; }
    public bool IsEnumConversion => currentState.EnumState != null;
    public string Name => currentState.Name;
    public RelayCommand<object> AddEnumValueCommand { get; }
    public RelayCommand<object> RemoveEnumValueCommand { get; }

    public string DisplayName => $"{currentState.Name} ({GetConversionTypeName(currentState.ConversionType)})";

    public bool HasChanges => !currentState.Equals(originalState);

    public ProjectConfigurationEnumConversionValueWrapper? SelectedEnumValue
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
            RemoveEnumValueCommand.OnCanExecuteChanged();
        }
    }

    /// <summary>
    /// A changed type cannot be applied in place (the type is the conversion class), so a new
    /// instance is created and returned. The caller must then replace the previous instance in
    /// the project and in every presentation that references it.
    /// </summary>
    public IValConversion ApplyChanges()
    {
        if (currentState.ConversionType != ConversionState.FromConversion(Conversion).ConversionType)
        {
            Conversion = CreateConversion(currentState.ConversionType);
        }

        Conversion.Name = currentState.Name;

        if (Conversion is LinearValConversion linearConversion && currentState.LinearState != null)
        {
            linearConversion.Multiplier = currentState.LinearState.Multiplier;
            linearConversion.Offset = currentState.LinearState.Offset;
        }
        else if (Conversion is EnumValConversion enumConversion && currentState.EnumState != null)
        {
            enumConversion.Enums = currentState.EnumState.Values
                .Select(value => new EnumType { Name = value.Name, Value = value.Value })
                .ToList();
        }

        originalState = currentState;
        NotifyStateChanged();
        return Conversion;
    }

    public void CancelChanges()
    {
        currentState = originalState;
        RebuildProperties();
        RebuildEnumValues();
        NotifyStateChanged();
    }

    /// <summary>
    /// Builds a standalone conversion from the wrapper's CURRENT (possibly not yet applied)
    /// state, so pending edits are included. Used for copying and for XML export.
    /// </summary>
    public IValConversion CreateConversionSnapshot(string name)
    {
        var snapshot = CreateConversion(name, currentState.ConversionType);
        if (snapshot is LinearValConversion linearConversion && currentState.LinearState != null)
        {
            linearConversion.Multiplier = currentState.LinearState.Multiplier;
            linearConversion.Offset = currentState.LinearState.Offset;
        }
        else if (snapshot is EnumValConversion enumConversion && currentState.EnumState != null)
        {
            enumConversion.Enums = currentState.EnumState.Values
                .Select(value => new EnumType { Name = value.Name, Value = value.Value })
                .ToList();
        }

        return snapshot;
    }

    private void RebuildProperties()
    {
        editableProperties.Clear();
        Properties.Clear();

        AddProperty(Create("Conversion", "Name", () => currentState.Name, value => UpdateState(currentState with { Name = value })));
        AddProperty(Create(
            "Conversion",
            "Type",
            () => currentState.ConversionType,
            value => UpdateConversionType(Parse<ConversionsGlobal.ConversionType>(value)),
            conversionTypeOptions));

        if (currentState.LinearState != null)
        {
            AddProperty(Create(
                "Linear",
                "Multiplier",
                () => currentState.LinearState.Multiplier,
                value => UpdateLinearState(state => state with { Multiplier = Parse<double>(value) })));
            AddProperty(Create(
                "Linear",
                "Offset",
                () => currentState.LinearState.Offset,
                value => UpdateLinearState(state => state with { Offset = Parse<double>(value) })));
        }

        RefreshProperties();
    }

    private void AddProperty(EditablePropertyWrapper property)
    {
        Properties.Add(property);
        editableProperties.Add(property);
    }

    private void RebuildEnumValues()
    {
        EnumValues.Clear();

        if (currentState.EnumState == null)
        {
            return;
        }

        SelectedEnumValue = null;
        foreach (var enumValue in currentState.EnumState.Values.Select((_, index) => new ProjectConfigurationEnumConversionValueWrapper(
                () => currentState.EnumState?.Values[index].Name ?? string.Empty,
                value => UpdateEnumValue(index, enumValue => enumValue with { Name = value }),
                () => currentState.EnumState?.Values[index].Value ?? 0,
                value => UpdateEnumValue(index, enumValue => enumValue with { Value = Parse<int>(value) }))))
        {
            EnumValues.Add(enumValue);
        }
    }

    private EditablePropertyWrapper Create(
        string group,
        string name,
        Func<object?> getValue,
        Action<string> setValue,
        IEnumerable<string>? options = null,
        bool isReadOnly = false)
    {
        return new EditablePropertyWrapper(group, name, getValue, setValue, null, options, isReadOnly);
    }

    private void UpdateLinearState(Func<LinearConversionState, LinearConversionState> update)
    {
        if (currentState.LinearState == null)
        {
            return;
        }

        UpdateState(currentState with { LinearState = update(currentState.LinearState) });
    }

    private void AddEnumValue()
    {
        if (currentState.EnumState == null)
        {
            return;
        }

        var values = currentState.EnumState.Values.ToList();
        values.Add(new EnumConversionValueState(
            CreateUniqueEnumValueName(values),
            CreateNextEnumValue(values)));
        currentState = currentState with { EnumState = currentState.EnumState with { Values = values } };
        RebuildEnumValues();
        SelectedEnumValue = EnumValues.LastOrDefault();
        NotifyStateChanged();
    }

    private void RemoveEnumValue()
    {
        if (currentState.EnumState == null || SelectedEnumValue == null)
        {
            return;
        }

        var index = EnumValues.IndexOf(SelectedEnumValue);
        if (index < 0)
        {
            return;
        }

        var values = currentState.EnumState.Values.ToList();
        values.RemoveAt(index);
        currentState = currentState with { EnumState = currentState.EnumState with { Values = values } };
        RebuildEnumValues();
        SelectedEnumValue = EnumValues.Count == 0
            ? null
            : EnumValues[Math.Min(index, EnumValues.Count - 1)];
        NotifyStateChanged();
    }

    private void UpdateConversionType(ConversionsGlobal.ConversionType conversionType)
    {
        if (currentState.ConversionType == conversionType || conversionType == ConversionsGlobal.ConversionType.Undefined)
        {
            return;
        }

        currentState = CreateDefaultState(currentState.Name, conversionType);
        RebuildProperties();
        RebuildEnumValues();
        NotifyStateChanged();
    }

    private void UpdateEnumValue(int index, Func<EnumConversionValueState, EnumConversionValueState> update)
    {
        if (currentState.EnumState == null)
        {
            return;
        }

        var values = currentState.EnumState.Values.ToList();
        values[index] = update(values[index]);
        UpdateState(currentState with { EnumState = currentState.EnumState with { Values = values } });
    }

    private void UpdateState(ConversionState state)
    {
        currentState = state;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(IsEnumConversion));
        AddEnumValueCommand.OnCanExecuteChanged();
        RemoveEnumValueCommand.OnCanExecuteChanged();
    }

    private void RefreshProperties()
    {
        foreach (var property in editableProperties)
        {
            property.RefreshValue();
        }
    }

    private void RefreshEnumValues()
    {
        foreach (var enumValue in EnumValues)
        {
            enumValue.RefreshValue();
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

    private static IValConversion CreateConversion(ConversionsGlobal.ConversionType conversionType)
    {
        var conversion = ConversionsGlobal.CreateInstance(conversionType);
        if (conversion is LinearValConversion linearConversion)
        {
            linearConversion.Multiplier = 1;
            linearConversion.Offset = 0;
        }
        else if (conversion is EnumValConversion enumConversion)
        {
            enumConversion.Enums = [];
        }

        return conversion;
    }

    public static IValConversion CreateConversion(string name, ConversionsGlobal.ConversionType conversionType)
    {
        var conversion = CreateConversion(conversionType);
        conversion.Name = name;
        return conversion;
    }

    private static ConversionState CreateDefaultState(string name, ConversionsGlobal.ConversionType conversionType)
    {
        return conversionType switch
        {
            ConversionsGlobal.ConversionType.Linear => new ConversionState(
                name,
                ConversionsGlobal.ConversionType.Linear,
                new LinearConversionState(1, 0),
                null),
            ConversionsGlobal.ConversionType.Enum => new ConversionState(
                name,
                ConversionsGlobal.ConversionType.Enum,
                null,
                new EnumConversionState([])),
            _ => new ConversionState(name, conversionType, null, null)
        };
    }

    private static string GetConversionTypeName(ConversionsGlobal.ConversionType conversionType)
    {
        return conversionType.ToString().ToLowerInvariant();
    }

    private static string CreateUniqueEnumValueName(IReadOnlyCollection<EnumConversionValueState> values)
    {
        const string baseName = "value";
        var existingNames = values
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = baseName;
        var index = 1;
        while (existingNames.Contains(name))
        {
            name = $"{baseName}_{index}";
            index++;
        }

        return name;
    }

    private static int CreateNextEnumValue(IReadOnlyCollection<EnumConversionValueState> values)
    {
        return values.Count == 0 ? 0 : values.Max(value => value.Value) + 1;
    }

    private sealed record ConversionState(
        string Name,
        ConversionsGlobal.ConversionType ConversionType,
        LinearConversionState? LinearState,
        EnumConversionState? EnumState)
    {
        public static ConversionState FromConversion(IValConversion conversion)
        {
            return conversion switch
            {
                LinearValConversion linearConversion => new ConversionState(
                    linearConversion.Name,
                    ConversionsGlobal.ConversionType.Linear,
                    new LinearConversionState(linearConversion.Multiplier, linearConversion.Offset),
                    null),
                EnumValConversion enumConversion => new ConversionState(
                    enumConversion.Name,
                    ConversionsGlobal.ConversionType.Enum,
                    null,
                    new EnumConversionState((enumConversion.Enums ?? [])
                        .Select(value => new EnumConversionValueState(value.Name, value.Value))
                        .ToList())),
                _ => new ConversionState(conversion.Name, ConversionsGlobal.ConversionType.Undefined, null, null)
            };
        }
    }

    private sealed record LinearConversionState(double Multiplier, double Offset);

    private sealed record EnumConversionState(IReadOnlyList<EnumConversionValueState> Values);

    private sealed record EnumConversionValueState(string Name, int Value);
}

public class ProjectConfigurationEnumConversionValueWrapper(
    Func<string> getName,
    Action<string> setName,
    Func<int> getValue,
    Action<string> setValue) : PropertyChangedBase
{
    public string Name
    {
        get => getName();
        set
        {
            try
            {
                setName(value);
                Error = string.Empty;
                OnPropertyChanged();
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
        }
    }

    public string Value
    {
        get => Convert.ToString(getValue(), CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            try
            {
                setValue(value);
                Error = string.Empty;
                OnPropertyChanged();
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
        }
    }

    public string Error
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public void RefreshValue()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Value));
    }
}
