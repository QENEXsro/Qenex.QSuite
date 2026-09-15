using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

namespace Qenex.QSuite.Protocols.RawCanProtocol;

/// <summary>
/// Raw CAN frames mapped to variables by identifier, in both directions. A read variable is
/// updated from every received frame with its id: the raw value is decoded from the frame bytes
/// according to the variable's type (byte/sbyte, short/ushort, int/uint, long/ulong, float, double),
/// a byte offset and byte order. A write variable is sent as a frame with its id when it is written
/// from a control or a script, the value placed at the same offset and byte order. Standard 11-bit
/// and extended 29-bit identifiers are both handled; no higher-layer parsing (J1939, CANopen).
/// Sample timestamps come from the adapter's receive timestamp when the driver provides one.
/// </summary>
public class RawCanProtocol : ProtocolBase<CanFrame>, ITransportProtocol<CanFrame>, IProtocolVariableWriteProtocol
{
    private const int MaxFrameBytes = 8;

    private Func<CanFrame, CancellationToken, Task>? transmitter;

    // Adapter timestamps are relative (microseconds since the adapter/driver started); the first
    // frame aligns them with the PC clock, a jump backwards (driver reopened) aligns again.
    private DateTime? adapterTimeBaseUtc;
    private ulong lastAdapterMicroseconds;

    // Warned once per variable so a misconfigured type does not flood the log at bus speed.
    private readonly ConcurrentDictionary<string, byte> reportedUnsupportedTypes = new(StringComparer.Ordinal);

    public RawCanProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "RawCanProtocol",
            Label = "Raw CAN Frames",
            Description = "Maps CAN frames (11-bit and 29-bit ids) to variables by identifier, reading and writing. Use with the PEAK CAN Adapter driver.",
            CreatedOn = new DateTime(2026, 6, 19),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #region Configuration

    // The protocol has no settings of its own; the shared parser reports a stray key as unknown.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Raw CAN protocol");
    }

    // canId is mandatory with no sensible default; 0x100 is a placeholder the operator replaces.
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return "direction=\"read\";canId=\"0x100\";offset=\"0\";byteOrder=\"le\"";
    }

    #endregion

    #region Protocol variables

    // An unreadable commParam (missing or malformed canId, unknown byteOrder, ...) is reported and the
    // variable left out, instead of silently listening on id 0 as older versions did.
    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        try
        {
            return new RawCanProtocolVariable
            {
                Variable = variable,
                IsCommunicated = isCommunicated,
                ProtocolVariableSpecification = RawCanProtocolVariableSpecification.Create(commParams),
            };
        }
        catch (ArgumentException e)
        {
            Logger?.Log(LogLevel.Warn, $"Raw CAN protocol: variable '{variable.Name}' is not communicated — {e.Message}");
            return null;
        }
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return CreateProtocolVariable(variable, $"canId=\"{id}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents, string commParams, bool isCommunicated)
    {
        return CreateProtocolVariable(variable, commParams, isCommunicated);
    }

    #endregion

    #region Protocol control

    public override Task StartAsync(CancellationToken ct = default)
    {
        adapterTimeBaseUtc = null;
        lastAdapterMicroseconds = 0;
        reportedUnsupportedTypes.Clear();
        SetState(IsEnabled ? CommunicationState.Running : CommunicationState.Disabled);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopped);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    // The driver reopened the adapter: its timestamp counter may have restarted.
    public override void OnTransportConnectionChanged(bool connected)
    {
        if (connected)
        {
            adapterTimeBaseUtc = null;
        }
    }

    #endregion

    #region Process received data

    public override async Task AddReceivedDataToQueueAsync(IEnumerable<CanFrame> data, CancellationToken ct = default)
    {
        await ProcessReceivedDataAsync(data, ct);
    }

    protected override void ProcessReceivedData(IEnumerable<CanFrame> data)
    {
        foreach (var protocolVariable in Decode(data))
        {
            protocolVariable.NotifyValueChanged();
        }
    }

    protected override async Task ProcessReceivedDataAsync(IEnumerable<CanFrame> data, CancellationToken ct = default)
    {
        var updated = Decode(data).ToList();
        await Task.WhenAll(updated.Select(protocolVariable => protocolVariable.NotifyValueChangedAsync()));
    }

    #endregion

    #region Encode & Decode

    // Incoming frames feed the variables the bus is allowed to update: read and readWrite.
    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<CanFrame> data)
    {
        var updated = new List<IProtocolVariable>();

        foreach (var frame in data)
        {
            var timestamp = ToTimestamp(frame);

            foreach (var protocolVariable in Variables)
            {
                if (!protocolVariable.IsCommunicated ||
                    protocolVariable.ProtocolVariableSpecification is not RawCanProtocolVariableSpecification
                    {
                        Direction: CommDirection.Read or CommDirection.ReadWrite
                    } spec ||
                    spec.CanId != frame.CanId ||
                    spec.IsExtended != frame.IsExtended)
                {
                    continue;
                }

                if (protocolVariable.Variable is not ScalarVariable scalarVariable)
                {
                    if (reportedUnsupportedTypes.TryAdd(protocolVariable.Variable.Name, 0))
                    {
                        Logger?.Log(LogLevel.Warn,
                            $"Raw CAN protocol: variable '{protocolVariable.Variable.Name}' (id 0x{frame.CanId:X}) has unsupported type {protocolVariable.Variable.GetType().Name}; only numeric scalar variables are mapped.");
                    }

                    continue;
                }

                if (TrySetRawValue(scalarVariable, frame, spec, timestamp))
                {
                    updated.Add(protocolVariable);
                }
            }
        }

        return updated;
    }

    // One frame per written variable: the value's bytes at the configured offset, the rest of the
    // payload zero, DLC just long enough to carry the value.
    protected override IEnumerable<CanFrame> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        foreach (var protocolVariable in protocolVariables)
        {
            if (protocolVariable.ProtocolVariableSpecification is not RawCanProtocolVariableSpecification spec
                || protocolVariable.Variable is not ScalarVariable scalarVariable)
            {
                continue;
            }

            var valueType = scalarVariable.Values.ValueType;
            var size = SizeOf(valueType);
            if (size == 0 || spec.Offset + size > MaxFrameBytes)
            {
                Logger?.Log(LogLevel.Warn,
                    $"Raw CAN protocol: variable '{scalarVariable.Name}' cannot be sent — type {valueType} at offset {spec.Offset} does not fit into an 8-byte frame.");
                continue;
            }

            var data = new byte[spec.Offset + size];
            var slot = data.AsSpan(spec.Offset, size);
            WriteLittleEndian(slot, valueType, scalarVariable.Values.GetValue());
            if (spec.ByteOrder == RawCanByteOrder.BigEndian)
            {
                slot.Reverse();
            }

            yield return new CanFrame(spec.CanId, data, spec.IsExtended);
        }
    }

    private DateTime ToTimestamp(CanFrame frame)
    {
        if (frame.TimestampMicroseconds == 0)
        {
            return DateTime.UtcNow;
        }

        if (adapterTimeBaseUtc == null || frame.TimestampMicroseconds < lastAdapterMicroseconds)
        {
            adapterTimeBaseUtc = DateTime.UtcNow - TimeSpan.FromTicks((long)(frame.TimestampMicroseconds * 10));
        }

        lastAdapterMicroseconds = frame.TimestampMicroseconds;
        return DateTime.SpecifyKind(adapterTimeBaseUtc.Value + TimeSpan.FromTicks((long)(frame.TimestampMicroseconds * 10)), DateTimeKind.Utc);
    }

    // Builds the variable's raw value from the frame bytes according to the variable's type, the
    // configured offset and byte order. Missing bytes (short frame / offset past the end) are zero-padded.
    private bool TrySetRawValue(ScalarVariable scalarVariable, CanFrame frame, RawCanProtocolVariableSpecification spec, DateTime timestamp)
    {
        var valueType = scalarVariable.Values.ValueType;
        var size = SizeOf(valueType);
        if (size == 0)
        {
            if (reportedUnsupportedTypes.TryAdd(scalarVariable.Name, 0))
            {
                Logger?.Log(LogLevel.Warn,
                    $"Raw CAN protocol: variable '{scalarVariable.Name}' (id 0x{frame.CanId:X}) has unsupported type {valueType}; its frames are ignored.");
            }

            return false;
        }

        // Collect the value's bytes starting at the offset; normalize to little-endian for the readers below.
        Span<byte> buffer = stackalloc byte[MaxFrameBytes];
        for (var i = 0; i < size; i++)
        {
            var index = spec.Offset + i;
            buffer[i] = index < frame.Data.Length ? frame.Data[index] : (byte)0;
        }

        if (spec.ByteOrder == RawCanByteOrder.BigEndian)
        {
            buffer[..size].Reverse();
        }

        object value = valueType switch
        {
            ValueDataType.Byte => buffer[0],
            ValueDataType.SByte => (sbyte)buffer[0],
            ValueDataType.UShort => BinaryPrimitives.ReadUInt16LittleEndian(buffer),
            ValueDataType.Short => BinaryPrimitives.ReadInt16LittleEndian(buffer),
            ValueDataType.UInt => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            ValueDataType.Int => BinaryPrimitives.ReadInt32LittleEndian(buffer),
            ValueDataType.ULong => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            ValueDataType.Long => BinaryPrimitives.ReadInt64LittleEndian(buffer),
            ValueDataType.Float => BinaryPrimitives.ReadSingleLittleEndian(buffer),
            ValueDataType.Double => BinaryPrimitives.ReadDoubleLittleEndian(buffer),
            _ => null!
        };

        scalarVariable.SetValue(value);
        scalarVariable.Timestamp = timestamp;
        return true;
    }

    private static void WriteLittleEndian(Span<byte> slot, ValueDataType valueType, object value)
    {
        switch (valueType)
        {
            case ValueDataType.Byte: slot[0] = Convert.ToByte(value); break;
            case ValueDataType.SByte: slot[0] = unchecked((byte)Convert.ToSByte(value)); break;
            case ValueDataType.UShort: BinaryPrimitives.WriteUInt16LittleEndian(slot, Convert.ToUInt16(value)); break;
            case ValueDataType.Short: BinaryPrimitives.WriteInt16LittleEndian(slot, Convert.ToInt16(value)); break;
            case ValueDataType.UInt: BinaryPrimitives.WriteUInt32LittleEndian(slot, Convert.ToUInt32(value)); break;
            case ValueDataType.Int: BinaryPrimitives.WriteInt32LittleEndian(slot, Convert.ToInt32(value)); break;
            case ValueDataType.ULong: BinaryPrimitives.WriteUInt64LittleEndian(slot, Convert.ToUInt64(value)); break;
            case ValueDataType.Long: BinaryPrimitives.WriteInt64LittleEndian(slot, Convert.ToInt64(value)); break;
            case ValueDataType.Float: BinaryPrimitives.WriteSingleLittleEndian(slot, Convert.ToSingle(value)); break;
            case ValueDataType.Double: BinaryPrimitives.WriteDoubleLittleEndian(slot, Convert.ToDouble(value)); break;
        }
    }

    // Byte width of a scalar value type; 0 for types this protocol cannot map from raw CAN bytes.
    private static int SizeOf(ValueDataType valueType) => valueType switch
    {
        ValueDataType.Byte or ValueDataType.SByte => 1,
        ValueDataType.UShort or ValueDataType.Short => 2,
        ValueDataType.UInt or ValueDataType.Int or ValueDataType.Float => 4,
        ValueDataType.ULong or ValueDataType.Long or ValueDataType.Double => 8,
        _ => 0
    };

    #endregion

    #region Writes to the bus

    public void SetTransmitter(Func<CanFrame, CancellationToken, Task>? frameTransmitter)
    {
        transmitter = frameTransmitter;
    }

    /// <summary>Writable are the protocol's variables with direction write or readWrite.</summary>
    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable)
               && protocolVariable.ProtocolVariableSpecification is RawCanProtocolVariableSpecification
               {
                   Direction: CommDirection.Write or CommDirection.ReadWrite
               };
    }

    /// <summary>Sends one frame with the variable's id carrying its current raw value.</summary>
    public async Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        var currentTransmitter = transmitter
            ?? throw new InvalidOperationException("Transport is not available (no transmitter injected).");

        foreach (var frame in Encode([protocolVariable]))
        {
            await currentTransmitter(frame, ct);
        }
    }

    #endregion
}
