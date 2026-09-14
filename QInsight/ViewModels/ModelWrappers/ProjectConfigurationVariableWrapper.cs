using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.ValuePresentation;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

public class ProjectConfigurationVariableWrapper : PropertyChangedBase
{
    private readonly IEnumerable<IPresentation> presentations;
    private readonly List<EditablePropertyWrapper> editableProperties = [];

    private VariableState originalState;
    private VariableState currentState;

    private EditablePropertyWrapper? presentationProperty;

    public ProjectConfigurationVariableWrapper(IVariableBase variable, IEnumerable<IPresentation> presentations, bool isNew = false)
    {
        Variable = variable;
        this.presentations = presentations;
        IsNew = isNew;
        originalState = VariableState.FromVariable(variable);
        currentState = originalState;
        Properties = CreateProperties();
    }

    public IVariableBase Variable { get; }
    public bool IsNew { get; }

    public ObservableCollection<EditablePropertyWrapper> Properties { get; }
    public int Id => currentState.Id;
    public string Name => currentState.Name;

    public string DisplayName => VariableTypeDisplay.ListName(Variable, currentState.Label, currentState.Id);

    public bool HasChanges => !currentState.Equals(originalState);

    public void ApplyChanges()
    {
        Variable.Id = currentState.Id;
        Variable.Namespace = currentState.Namespace;
        Variable.Name = currentState.Name;
        Variable.Label = currentState.Label;
        Variable.Description = currentState.Description;

        if (Variable is ScalarVariable scalarVariable && currentState.ScalarState != null)
        {
            ApplyScalarState(scalarVariable, currentState.ScalarState);
        }
        else if (Variable is MatrixVariable matrixVariable && currentState.MatrixState != null)
        {
            ApplyMatrixState(matrixVariable, currentState.MatrixState);
        }

        originalState = currentState;
        NotifyStateChanged();
    }

    public void CancelChanges()
    {
        currentState = originalState;
        RefreshProperties();
        NotifyStateChanged();
    }

    public void RefreshPresentationOptions(IEnumerable<string> presentationNames)
    {
        presentationProperty?.SetOptions(presentationNames.Prepend(string.Empty));
    }

    /// <summary>
    /// Builds a standalone variable from the wrapper's CURRENT (possibly not yet applied)
    /// state, so pending edits are included. Used for copying a variable and for XML export.
    /// </summary>
    public IVariableBase CreateVariableSnapshot(int id, string name)
    {
        IVariableBase snapshot;
        if (Variable is ScalarVariable && currentState.ScalarState != null)
        {
            var scalarSnapshot = new ScalarVariable
            {
                Values = ValuesGlobal.CreateInstance(currentState.ScalarState.ValueType)
            };
            ApplyScalarState(scalarSnapshot, currentState.ScalarState);
            snapshot = scalarSnapshot;
        }
        else if (Variable is MatrixVariable && currentState.MatrixState != null)
        {
            var matrixSnapshot = new MatrixVariable();
            ApplyMatrixState(matrixSnapshot, currentState.MatrixState);
            snapshot = matrixSnapshot;
        }
        else if (Variable is StringVariable stringVariable)
        {
            snapshot = new StringVariable { Values = stringVariable.Values ?? string.Empty };
        }
        else
        {
            throw new NotSupportedException($"Variable type {Variable.GetType().Name} cannot be copied.");
        }

        snapshot.Id = id;
        snapshot.Namespace = currentState.Namespace;
        snapshot.Name = name;
        snapshot.Label = currentState.Label;
        snapshot.Description = currentState.Description;
        return snapshot;
    }

    private ObservableCollection<EditablePropertyWrapper> CreateProperties()
    {
        var properties = new ObservableCollection<EditablePropertyWrapper>
        {
            Create("Variable", "Id", () => currentState.Id, value => UpdateState(currentState with { Id = Parse<int>(value) })),
            // The kind is fixed at creation (Scalar/Matrix combo next to +) and cannot be switched:
            // protocol commParams, controls and scripts are bound to it. Shown read-only so the
            // user can tell a matrix from a scalar without decoding the rows below.
            new EditablePropertyWrapper("Variable", "Type", () => VariableTypeDisplay.TypeName(Variable), _ => { }, isReadOnly: true),
            Create("Variable", "Namespace", () => currentState.Namespace, value => UpdateState(currentState with { Namespace = value })),
            Create("Variable", "Name", () => currentState.Name, value => UpdateState(currentState with { Name = value })),
            Create("Variable", "Label", () => currentState.Label, value => UpdateState(currentState with { Label = value })),
            Create("Variable", "Description", () => currentState.Description, value => UpdateState(currentState with { Description = value }))
        };

        if (currentState.ScalarState != null)
        {
            // Size and Value Size are not editable: they are derived from Data Type and Length on apply.
            properties.Add(Create(
                "Values",
                "Data Type",
                () => currentState.ScalarState.ValueType,
                value => UpdateScalarState(s => s with { ValueType = Parse<ValuesGlobal.ValueDataType>(value) }),
                ValuesGlobal.ValueDataTypeDict.Keys.Select(valueType => valueType.ToString())));
            properties.Add(Create("Values", "Length", () => currentState.ScalarState.Length, value => UpdateScalarState(s => s with { Length = Parse<int>(value) })));
            // Bit field (A2L BIT_MASK style): applied between the raw word and the conversion.
            // Shift = direction combo (>> right / << left) + bit count; mask is typed as hex (0x0F) or
            // decimal (15) and only validated here so a typo shows as the row error.
            properties.Add(Create(
                    "Values",
                    "Bit Shift",
                    () => currentState.ScalarState.BitShiftCount,
                    value => UpdateScalarState(s => s with { BitShiftCount = ParseBitShiftCount(value) }))
                .SetPrefix(
                    [ShiftRightText, ShiftLeftText],
                    () => currentState.ScalarState.BitShiftLeft ? ShiftLeftText : ShiftRightText,
                    value => UpdateScalarState(s => s with { BitShiftLeft = value.Trim() == ShiftLeftText })));
            properties.Add(Create(
                    "Values",
                    "Bit Mask",
                    () => currentState.ScalarState.BitMask,
                    value => UpdateScalarState(s => s with { BitMask = ValidateBitMask(value) }))
                .SetUpdateOnLostFocus());
            presentationProperty = Create(
                "Values",
                "Presentation",
                () => currentState.ScalarState.PresentationName,
                value => UpdateScalarState(s => s with { PresentationName = value }),
                presentations.Select(presentation => presentation.Name).Prepend(string.Empty));
            properties.Add(presentationProperty);
        }

        if (currentState.MatrixState != null)
        {
            // Total size in bytes is derived from the layout (value counts x element types).
            properties.Add(Create(
                "Matrix",
                "Default Data Type",
                () => currentState.MatrixState.DefaultDataType,
                value => UpdateMatrixState(s => s with { DefaultDataType = Parse<ValuesGlobal.ValueDataType>(value) }),
                ValuesGlobal.ValueDataTypeDict.Keys
                    .Where(valueType => valueType != ValuesGlobal.ValueDataType.String)
                    .Select(valueType => valueType.ToString())));
            properties.Add(Create(
                "Matrix",
                "Endianness",
                () => currentState.MatrixState.Endianness,
                value => UpdateMatrixState(s => s with { Endianness = Parse<MatrixEndianness>(value) }),
                Enum.GetNames<MatrixEndianness>()));
            properties.Add(Create(
                "Matrix",
                "Layout",
                () => currentState.MatrixState.Layout,
                value => UpdateMatrixState(s => s with { Layout = value })));
        }

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

    private void UpdateScalarState(Func<ScalarVariableState, ScalarVariableState> update)
    {
        if (currentState.ScalarState == null)
        {
            return;
        }

        UpdateState(currentState with { ScalarState = update(currentState.ScalarState) });
    }

    private void UpdateMatrixState(Func<MatrixVariableState, MatrixVariableState> update)
    {
        if (currentState.MatrixState == null)
        {
            return;
        }

        var newState = update(currentState.MatrixState);

        // Validate eagerly on a probe variable so a bad layout/type shows up as the field's
        // error tooltip right away instead of failing later on apply.
        ApplyMatrixState(new MatrixVariable(), newState);

        UpdateState(currentState with { MatrixState = newState });
    }

    private void UpdateState(VariableState state)
    {
        currentState = state;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshProperties()
    {
        foreach (var property in editableProperties)
        {
            property.RefreshValue();
        }
    }

    private void ApplyScalarState(ScalarVariable scalarVariable, ScalarVariableState state)
    {
        if (scalarVariable.Values.ValueType != state.ValueType)
        {
            var currentValue = scalarVariable.Values.GetValue();
            var newValues = ValuesGlobal.CreateInstance(state.ValueType);

            // Migrate the existing value to the new type; if it does not fit (e.g. a large uint32
            // switched to ushort) keep the new type's default rather than crashing on overflow.
            if (TryConvertTo(Convert.ToString(currentValue, CultureInfo.InvariantCulture) ?? string.Empty,
                    GetSystemType(state.ValueType), out var convertedValue))
            {
                newValues.SetValue(convertedValue);
            }

            scalarVariable.Values = newValues;
        }

        scalarVariable.Values.Length = state.Length;

        // Size and Value Size are derived (not user-entered) and stored on the variable / serialized to
        // XML from here: value size = byte width of the type, total size = value size * element count.
        scalarVariable.Values.Size = GetValueSize(state.ValueType);
        scalarVariable.Size = scalarVariable.Values.Size * Math.Max(state.Length, 1);

        scalarVariable.Values.ValPresentation = string.IsNullOrWhiteSpace(state.PresentationName)
            ? null!
            : presentations.First(presentation => presentation.Name.Equals(state.PresentationName, StringComparison.OrdinalIgnoreCase));

        ScalarVariable.TryParseBitMask(state.BitMask, out var bitMask);
        scalarVariable.BitShift = state.BitShiftLeft ? -state.BitShiftCount : state.BitShiftCount;
        scalarVariable.BitMask = bitMask;
    }

    private const string ShiftRightText = ">>";
    private const string ShiftLeftText = "<<";

    private static int ParseBitShiftCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 63)
        {
            throw new ArgumentException("Bit Shift must be a number of bits 0..63.");
        }

        return count;
    }

    // The text stays as the user typed it ("0x0F" = hex, "15" = decimal); it is only validated here.
    private static string ValidateBitMask(string value)
    {
        if (!ScalarVariable.TryParseBitMask(value, out _))
        {
            throw new ArgumentException("Bit Mask must be a hex (0x0F) or decimal (15) number, or empty.");
        }

        return value.Trim();
    }

    /// <summary>
    /// Zapise stav konfigurace do MatrixVariable: parsuje Layout string, resolvuje prezentace
    /// per sekce a validuje vysledny layout vc. Modbus limitu 246 B (123 registru, FC16).
    /// Vola se i nad sondou (probe) pri kazde editaci — vyhazuje ArgumentException s popisem.
    /// </summary>
    private void ApplyMatrixState(MatrixVariable matrixVariable, MatrixVariableState state)
    {
        var parts = MatrixLayoutText.Parse(state.Layout);

        matrixVariable.DefaultDataType = state.DefaultDataType;
        matrixVariable.Endianness = state.Endianness;
        matrixVariable.XAxis = CreateMatrixSection(parts.X);
        matrixVariable.YAxis = CreateMatrixSection(parts.Y);
        matrixVariable.Data = CreateMatrixSection(parts.Data)!;

        var layoutError = matrixVariable.ValidateLayout();
        if (layoutError != null)
        {
            throw new ArgumentException(layoutError);
        }

        if (matrixVariable.Size > MaxMatrixBytes)
        {
            throw new ArgumentException(
                $"Matrix size {matrixVariable.Size} B exceeds the {MaxMatrixBytes} B limit " +
                "(123 registers per Modbus write request).");
        }
    }

    // Modbus FC16 zapisuje max 123 registru = 246 B; cela matice se prenasi jednim requestem.
    private const int MaxMatrixBytes = 246;

    private MatrixSection? CreateMatrixSection(MatrixSectionParts? parts)
    {
        if (parts == null)
        {
            return null;
        }

        IPresentation? presentation = null;
        if (!string.IsNullOrWhiteSpace(parts.PresentationName))
        {
            presentation = presentations.FirstOrDefault(p =>
                    p.Name.Equals(parts.PresentationName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Presentation \"{parts.PresentationName}\" was not found.");
        }

        return new MatrixSection
        {
            Count = parts.Count,
            Label = parts.Label,
            DataType = parts.DataType,
            Presentation = presentation
        };
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

    // Best-effort conversion used when changing a variable's data type: returns false instead of
    // throwing when the value cannot be represented in the target type (overflow / bad format).
    private static bool TryConvertTo(string value, Type targetType, out object converted)
    {
        try
        {
            converted = ConvertTo(value, targetType);
            return true;
        }
        catch (Exception exception) when (exception is OverflowException or FormatException or InvalidCastException)
        {
            converted = null!;
            return false;
        }
    }

    private static Type GetSystemType(ValuesGlobal.ValueDataType valueType)
    {
        var typeName = ValuesGlobal.ValueDataTypeDict[valueType];
        return Type.GetType(typeName) ?? throw new InvalidOperationException($"Type \"{typeName}\" was not found.");
    }

    // Byte width of a single value of the given type, derived from the type itself (String has none).
    private static int GetValueSize(ValuesGlobal.ValueDataType valueType)
    {
        return valueType == ValuesGlobal.ValueDataType.String ? 0 : Marshal.SizeOf(GetSystemType(valueType));
    }

    private sealed record VariableState(
        int Id,
        string Namespace,
        string Name,
        string Label,
        string Description,
        ScalarVariableState? ScalarState,
        MatrixVariableState? MatrixState)
    {
        public static VariableState FromVariable(IVariableBase variable)
        {
            return new VariableState(
                variable.Id,
                variable.Namespace,
                variable.Name,
                variable.Label,
                variable.Description,
                variable is ScalarVariable scalarVariable
                    ? ScalarVariableState.FromScalarVariable(scalarVariable)
                    : null,
                variable is MatrixVariable matrixVariable
                    ? MatrixVariableState.FromMatrixVariable(matrixVariable)
                    : null);
        }
    }

    private sealed record MatrixVariableState(
        ValuesGlobal.ValueDataType DefaultDataType,
        MatrixEndianness Endianness,
        string Layout)
    {
        public static MatrixVariableState FromMatrixVariable(MatrixVariable matrixVariable)
        {
            return new MatrixVariableState(
                matrixVariable.DefaultDataType,
                matrixVariable.Endianness,
                MatrixLayoutText.Build(matrixVariable));
        }
    }

    private sealed record ScalarVariableState(
        ValuesGlobal.ValueDataType ValueType,
        int Length,
        string PresentationName,
        bool BitShiftLeft,
        int BitShiftCount,
        string BitMask)
    {
        public static ScalarVariableState FromScalarVariable(ScalarVariable scalarVariable)
        {
            return new ScalarVariableState(
                scalarVariable.Values.ValueType,
                scalarVariable.Values.Length,
                scalarVariable.Values.ValPresentation?.Name ?? string.Empty,
                scalarVariable.BitShift < 0,
                Math.Abs(scalarVariable.BitShift),
                ScalarVariable.FormatBitMask(scalarVariable.BitMask));
        }
    }
}
