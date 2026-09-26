using System.Globalization;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>
/// Per-variable XCP mapping: ECU memory address (+ address extension), value size/type, transfer
/// direction and the variable event whose period drives polling. Byte order and address granularity
/// are NOT configured here — they come from the slave's CONNECT response (session-wide).
/// </summary>
public class XcpVariableSpecification : ProtVariableSpecification
{
    public XcpVariableSpecification()
    {
        Name = "XcpVariableSpecification";
    }

    /// <summary>Event this variable is bound to; a periodic event defines the polling interval.
    /// Null for write-only variables that are never polled.</summary>
    public IVarEvent? VariableEvent { get; init; }

    /// <summary>32-bit ECU memory address of the value.</summary>
    public uint Address { get; init; }

    /// <summary>XCP address extension (ECU-specific address space qualifier, usually 0).</summary>
    public byte AddressExtension { get; init; }

    /// <summary>Value size in bytes: the byte width of <see cref="DataType"/> for a scalar, the
    /// whole raw block (<see cref="MatrixVariable.Size"/>) for a matrix.</summary>
    public int Size { get; init; }

    /// <summary>Value type used to decode/encode the raw memory bytes. Always equals the bound
    /// variable's own value type so the decoded value can be assigned to it directly;
    /// <see cref="ValueDataType.Undefined"/> for a matrix, whose block travels raw and is decoded
    /// by the variable's own layout.</summary>
    public ValueDataType DataType { get; init; }

    /// <summary>Largest matrix data block transferred over XCP: 64 × 64 × 4 B, axes on top (decision of Radek 2026-09-25).</summary>
    public const int MaxMatrixBytes = 64 * 64 * 4;

    /// <summary>Transfer direction: polled read, operator write, or both.</summary>
    public CommDirection Direction { get; init; } = CommDirection.Read;

    /// <summary>ECU event channel number when the bound event carries
    /// direction="DAQ"/"STIM";daqId="N" in its eventExtraParams — reads then come from a DAQ
    /// list instead of polling, writes are streamed as STIM. Null for ordinary polling events.</summary>
    public ushort? DaqEventChannel { get; init; }

    /// <summary>True when the bound event is a STIM event channel (S2): the variable's value is
    /// then streamed to the ECU as STIM DTOs instead of being written via SET_MTA + DOWNLOAD.</summary>
    public bool IsStimEvent { get; init; }

    /// <summary>True when the bound event is an On Request event: the variable is neither polled
    /// nor acquired via DAQ — it is read only on an explicit request (IProtocolVariableReadProtocol).</summary>
    public bool IsOnRequestEvent => VariableEvent is OnRequestVarEvent;

    /// <summary>Reserved for the staged engineering-value write phase; not applied yet.</summary>
    public int Multiplier { get; init; } = 1;

    /// <summary>
    /// Controlled serialization so the configuration round-trips: read back by <see cref="Create"/>.
    /// dataType and size are NOT written — they always equal the bound variable's own type (there
    /// is no type remapping in the simplified XCP), so spelling them out only suggests a choice
    /// that does not exist. Must include eventRef when an event is bound — the XML module handler
    /// routes commParams containing "eventRef" to the events-aware CreateProtocolVariable overload.
    /// </summary>
    public string CommParams
    {
        get
        {
            var direction = Direction switch
            {
                CommDirection.Write => "write",
                CommDirection.ReadWrite => "readWrite",
                _ => "read"
            };

            var commParams =
                $"address=\"0x{Address:X}\";addressExtension=\"{AddressExtension}\";" +
                $"direction=\"{direction}\";multiplier=\"{Multiplier}\"";

            if (VariableEvent != null)
            {
                commParams += $";eventRef=\"{VariableEvent.Name}\"";
            }

            return commParams;
        }
    }

    // commParam example:
    //   address="0x1A0000";addressExtension="0";direction="readWrite";multiplier="1";eventRef="poll100ms"
    // Only address is mandatory. size/dataType may still appear in hand-written or legacy XML:
    // they default to the bound variable's own value type and are validated against it — a
    // mismatch is a configuration error (the decoded value could not be assigned to the variable
    // at runtime).
    public static XcpVariableSpecification Create(string commParams, IEnumerable<IVarEvent>? variableEvents, IVariableBase variable,
        ILogger? logger = null)
    {
        var settings = Parse(commParams);

        var address = ParseAddress(settings);
        var addressExtension = ParseAddressExtension(settings);
        var direction = ParseDirection(settings);
        var multiplier = ParseMultiplier(settings);
        var variableEvent = ResolveEvent(settings, variableEvents);
        ValueDataType dataType;
        int size;
        if (variable is MatrixVariable matrix)
        {
            (dataType, size) = ResolveMatrixLayout(settings, matrix);
        }
        else
        {
            dataType = ResolveDataType(settings, variable);
            size = ResolveSize(settings, dataType);
        }

        var binding = variableEvent == null ? null : XcpEventExtraParams.GetEventBinding(variableEvent, logger);

        // A matrix block is far too large for an ODT and changes rarely: it is polled by a periodic
        // event or read on request, never acquired via DAQ or streamed as STIM.
        if (variable is MatrixVariable && binding != null)
        {
            throw new ArgumentException(
                $"Matrix variable '{variable.Name}' cannot bind to the DAQ/STIM event channel '{variableEvent!.Name}'; " +
                "a matrix is polled by a periodic event or read on request.");
        }

        if (variableEvent == null && direction != CommDirection.Write)
        {
            throw new ArgumentException(
                $"Variable '{variable.Name}' has direction '{direction}' but no eventRef; a periodic event is required for polled reads.");
        }

        // S2: a STIM event feeds values master -> slave, so only write variables may bind to it;
        // reading the stimulated value back needs a second variable on a DAQ/polling event.
        if (binding is { IsStim: true } && direction != CommDirection.Write)
        {
            throw new ArgumentException(
                $"Variable '{variable.Name}' has direction '{direction}' but its event '{variableEvent!.Name}' is a STIM event channel; " +
                "only direction=\"write\" variables can bind to a STIM event.");
        }

        return new XcpVariableSpecification
        {
            VariableEvent = variableEvent,
            Address = address,
            AddressExtension = addressExtension,
            Size = size,
            DataType = dataType,
            Direction = direction,
            DaqEventChannel = binding?.Channel,
            IsStimEvent = binding is { IsStim: true },
            Multiplier = multiplier
        };
    }

    /// <summary>Byte width of a scalar value type; 0 for types XCP cannot map to memory bytes.</summary>
    public static int SizeOf(ValueDataType valueType) => valueType switch
    {
        ValueDataType.Byte or ValueDataType.SByte => 1,
        ValueDataType.UShort or ValueDataType.Short => 2,
        ValueDataType.UInt or ValueDataType.Int or ValueDataType.Float => 4,
        ValueDataType.ULong or ValueDataType.Long or ValueDataType.Double => 8,
        _ => 0
    };

    #region Parsing

    private static Dictionary<string, string> Parse(string commParams)
    {
        return commParams
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim('"'), StringComparer.OrdinalIgnoreCase);
    }

    private static uint ParseAddress(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("address", out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Missing mandatory commParam 'address'.");
        }

        value = value.Trim();
        var isHex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (isHex)
        {
            value = value[2..];
        }

        var parsed = isHex
            ? uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
            : uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);

        return parsed ? address : throw new ArgumentException($"Invalid commParam address '{value}'.");
    }

    private static byte ParseAddressExtension(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("addressExtension", out var value))
        {
            return 0;
        }

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var extension)
            ? extension
            : throw new ArgumentException($"Invalid commParam addressExtension '{value}'.");
    }

    private static CommDirection ParseDirection(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("direction", out var value))
        {
            return CommDirection.Read;
        }

        return Enum.TryParse<CommDirection>(value, ignoreCase: true, out var direction)
            ? direction
            : throw new ArgumentException($"Invalid commParam direction '{value}'.");
    }

    private static int ParseMultiplier(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("multiplier", out var value))
        {
            return 1;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var multiplier)
            ? multiplier
            : throw new ArgumentException($"Invalid commParam multiplier '{value}'.");
    }

    private static IVarEvent? ResolveEvent(IReadOnlyDictionary<string, string> settings, IEnumerable<IVarEvent>? variableEvents)
    {
        if (!settings.TryGetValue("eventRef", out var eventName) || string.IsNullOrWhiteSpace(eventName))
        {
            return null;
        }

        return variableEvents?.FirstOrDefault(e => e.Name == eventName)
               ?? throw new ArgumentException($"Event '{eventName}' referenced by commParam eventRef was not found.");
    }

    private static ValueDataType ResolveDataType(IReadOnlyDictionary<string, string> settings, IVariableBase variable)
    {
        var variableType = (variable as ScalarVariable)?.Values.ValueType;

        if (!settings.TryGetValue("dataType", out var value))
        {
            return variableType
                   ?? throw new ArgumentException($"Variable '{variable.Name}' is not scalar and commParam dataType is missing.");
        }

        if (!Enum.TryParse<ValueDataType>(value, ignoreCase: true, out var dataType) || SizeOf(dataType) == 0)
        {
            throw new ArgumentException($"Invalid commParam dataType '{value}'.");
        }

        if (variableType.HasValue && variableType.Value != dataType)
        {
            throw new ArgumentException(
                $"commParam dataType '{dataType}' does not match the variable's own type '{variableType.Value}'.");
        }

        return dataType;
    }

    /// <summary>Matrix: the whole raw block (axes + data) is one XCP transfer; elements are decoded
    /// by the variable's own layout and endianness, so there is no scalar type. Optional commParam
    /// size must match the layout; dataType is ignored (per-section types live on the variable).</summary>
    private static (ValueDataType DataType, int Size) ResolveMatrixLayout(IReadOnlyDictionary<string, string> settings,
        MatrixVariable matrix)
    {
        var layoutError = matrix.ValidateLayout();
        if (layoutError != null)
        {
            throw new ArgumentException($"Matrix variable '{matrix.Name}' has an invalid layout: {layoutError}");
        }

        var dataBytes = matrix.DataCount * matrix.GetElementSize(MatrixSectionKind.Data);
        if (dataBytes > MaxMatrixBytes)
        {
            throw new ArgumentException(
                $"Matrix variable '{matrix.Name}' has {dataBytes} bytes of data; the XCP limit is {MaxMatrixBytes} bytes (64 × 64 × 4).");
        }

        if (settings.TryGetValue("size", out var value) &&
            (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size != matrix.Size))
        {
            throw new ArgumentException($"commParam size '{value}' does not match the {matrix.Size}-byte matrix layout.");
        }

        return (ValueDataType.Undefined, matrix.Size);
    }

    private static int ResolveSize(IReadOnlyDictionary<string, string> settings, ValueDataType dataType)
    {
        var width = SizeOf(dataType);
        if (width == 0)
        {
            throw new ArgumentException($"Value type '{dataType}' cannot be transferred over XCP.");
        }

        if (!settings.TryGetValue("size", out var value))
        {
            return width;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size != width)
        {
            throw new ArgumentException($"commParam size '{value}' does not match the {width}-byte width of type '{dataType}'.");
        }

        return size;
    }

    #endregion
}
