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

    /// <summary>Value size in bytes; always matches the byte width of <see cref="DataType"/>.</summary>
    public int Size { get; init; }

    /// <summary>Value type used to decode/encode the raw memory bytes. Always equals the bound
    /// variable's own value type so the decoded value can be assigned to it directly.</summary>
    public ValueDataType DataType { get; init; }

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
        var dataType = ResolveDataType(settings, variable);
        var size = ResolveSize(settings, dataType);
        var binding = variableEvent == null ? null : XcpEventExtraParams.GetEventBinding(variableEvent, logger);

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
