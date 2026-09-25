using System.Buffers.Binary;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>
/// Transport-agnostic XCP packet codec. Works purely on XCP packets (PID + data bytes) — framing
/// (CAN identifiers today, LEN/CTR headers on TCP later) is the transport adapter's job. Multi-byte
/// command parameters and values are emitted in the slave's byte order (<see cref="IsBigEndian"/>,
/// learned from the CONNECT response); the CONNECT command itself has no multi-byte fields, so the
/// initial value does not matter for connecting. Only address granularity BYTE (AG=1) is supported.
/// </summary>
public sealed class XcpCodec
{
    /// <summary>Slave byte order from CONNECT COMM_MODE_BASIC bit 0: false = Intel, true = Motorola.</summary>
    public bool IsBigEndian { get; set; }

    #region Command builders

    public static byte[] BuildConnect(byte mode = 0x00) => [XcpCommand.Connect, mode];

    public static byte[] BuildDisconnect() => [XcpCommand.Disconnect];

    public static byte[] BuildGetStatus() => [XcpCommand.GetStatus];

    public static byte[] BuildSynch() => [XcpCommand.Synch];

    public static byte[] BuildGetCommModeInfo() => [XcpCommand.GetCommModeInfo];

    /// <summary>SET_MTA: 0xF6 | reserved WORD | address extension | address DWORD.</summary>
    public byte[] BuildSetMta(byte addressExtension, uint address)
    {
        var packet = new byte[8];
        packet[0] = XcpCommand.SetMta;
        packet[3] = addressExtension;
        WriteUInt32(packet.AsSpan(4), address);
        return packet;
    }

    /// <summary>SHORT_UPLOAD: 0xF4 | element count | reserved | address extension | address DWORD.</summary>
    public byte[] BuildShortUpload(byte count, byte addressExtension, uint address)
    {
        var packet = new byte[8];
        packet[0] = XcpCommand.ShortUpload;
        packet[1] = count;
        packet[3] = addressExtension;
        WriteUInt32(packet.AsSpan(4), address);
        return packet;
    }

    /// <summary>UPLOAD: 0xF5 | element count. Reads from the current MTA and post-increments it.</summary>
    public static byte[] BuildUpload(byte count) => [XcpCommand.Upload, count];

    /// <summary>DOWNLOAD: 0xF0 | element count | data. Writes to the current MTA and post-increments it.
    /// Keeping the packet within the slave's MAX_CTO (e.g. 6 data bytes on classic CAN) is the
    /// caller's contract — XcpMaster chunks by the limit learned from the CONNECT response.</summary>
    public static byte[] BuildDownload(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length,
                "A single DOWNLOAD packet carries 1..255 data bytes.");
        }

        var packet = new byte[2 + data.Length];
        packet[0] = XcpCommand.Download;
        packet[1] = (byte)data.Length;
        data.CopyTo(packet.AsSpan(2));
        return packet;
    }

    /// <summary>Master block mode: first packet of a block — 0xF0 | number of elements of the WHOLE
    /// block (1..255) | the first data bytes (at most MAX_CTO − 2). The rest follows as DOWNLOAD_NEXT.</summary>
    public static byte[] BuildDownloadBlockStart(int blockLength, ReadOnlySpan<byte> data)
    {
        if (blockLength is < 1 or > byte.MaxValue || data.Length < 1 || data.Length > blockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(blockLength), blockLength,
                "A DOWNLOAD block carries 1..255 elements and its first packet at least one of them.");
        }

        var packet = new byte[2 + data.Length];
        packet[0] = XcpCommand.Download;
        packet[1] = (byte)blockLength;
        data.CopyTo(packet.AsSpan(2));
        return packet;
    }

    /// <summary>Master block mode continuation: 0xEF | number of elements REMAINING in the block
    /// including this packet | data. The slave acknowledges the packet that brings the remainder to 0.</summary>
    public static byte[] BuildDownloadNext(int remaining, ReadOnlySpan<byte> data)
    {
        if (remaining is < 1 or > byte.MaxValue || data.Length < 1 || data.Length > remaining)
        {
            throw new ArgumentOutOfRangeException(nameof(remaining), remaining,
                "DOWNLOAD_NEXT carries 1..255 remaining elements and at least one data byte of them.");
        }

        var packet = new byte[2 + data.Length];
        packet[0] = XcpCommand.DownloadNext;
        packet[1] = (byte)remaining;
        data.CopyTo(packet.AsSpan(2));
        return packet;
    }

    #endregion

    #region DAQ command builders

    public static byte[] BuildFreeDaq() => [XcpCommand.FreeDaq];

    /// <summary>ALLOC_DAQ: 0xD5 | reserved | DAQ_COUNT (WORD).</summary>
    public byte[] BuildAllocDaq(ushort daqCount)
    {
        var packet = new byte[4];
        packet[0] = XcpCommand.AllocDaq;
        WriteUInt16(packet.AsSpan(2), daqCount);
        return packet;
    }

    /// <summary>ALLOC_ODT: 0xD4 | reserved | DAQ_LIST_NUMBER (WORD) | ODT_COUNT.</summary>
    public byte[] BuildAllocOdt(ushort daqList, byte odtCount)
    {
        var packet = new byte[5];
        packet[0] = XcpCommand.AllocOdt;
        WriteUInt16(packet.AsSpan(2), daqList);
        packet[4] = odtCount;
        return packet;
    }

    /// <summary>ALLOC_ODT_ENTRY: 0xD3 | reserved | DAQ_LIST_NUMBER (WORD) | ODT_NUMBER | ENTRY_COUNT.</summary>
    public byte[] BuildAllocOdtEntry(ushort daqList, byte odt, byte entryCount)
    {
        var packet = new byte[6];
        packet[0] = XcpCommand.AllocOdtEntry;
        WriteUInt16(packet.AsSpan(2), daqList);
        packet[4] = odt;
        packet[5] = entryCount;
        return packet;
    }

    /// <summary>SET_DAQ_PTR: 0xE2 | reserved | DAQ_LIST_NUMBER (WORD) | ODT_NUMBER | ENTRY_NUMBER.</summary>
    public byte[] BuildSetDaqPtr(ushort daqList, byte odt, byte entryIndex)
    {
        var packet = new byte[6];
        packet[0] = XcpCommand.SetDaqPtr;
        WriteUInt16(packet.AsSpan(2), daqList);
        packet[4] = odt;
        packet[5] = entryIndex;
        return packet;
    }

    /// <summary>WRITE_DAQ: 0xE1 | BIT_OFFSET (0xFF = whole element) | SIZE | ADDR_EXT | ADDRESS (DWORD).
    /// Writes the ODT entry at the current DAQ pointer and auto-increments the pointer.</summary>
    public byte[] BuildWriteDaq(byte size, byte addressExtension, uint address)
    {
        var packet = new byte[8];
        packet[0] = XcpCommand.WriteDaq;
        packet[1] = 0xFF;
        packet[2] = size;
        packet[3] = addressExtension;
        WriteUInt32(packet.AsSpan(4), address);
        return packet;
    }

    /// <summary>SET_DAQ_LIST_MODE: 0xE0 | MODE | DAQ_LIST_NUMBER (WORD) | EVENT_CHANNEL (WORD) |
    /// PRESCALER | PRIORITY.</summary>
    public byte[] BuildSetDaqListMode(byte mode, ushort daqList, ushort eventChannel, byte prescaler, byte priority)
    {
        var packet = new byte[8];
        packet[0] = XcpCommand.SetDaqListMode;
        packet[1] = mode;
        WriteUInt16(packet.AsSpan(2), daqList);
        WriteUInt16(packet.AsSpan(4), eventChannel);
        packet[6] = prescaler;
        packet[7] = priority;
        return packet;
    }

    /// <summary>START_STOP_DAQ_LIST: 0xDE | MODE (stop/start/select) | DAQ_LIST_NUMBER (WORD).</summary>
    public byte[] BuildStartStopDaqList(byte mode, ushort daqList)
    {
        var packet = new byte[4];
        packet[0] = XcpCommand.StartStopDaqList;
        packet[1] = mode;
        WriteUInt16(packet.AsSpan(2), daqList);
        return packet;
    }

    /// <summary>START_STOP_SYNCH: 0xDD | MODE (stop all / start selected / stop selected).</summary>
    public static byte[] BuildStartStopSynch(byte mode) => [XcpCommand.StartStopSynch, mode];

    public static byte[] BuildGetDaqProcessorInfo() => [XcpCommand.GetDaqProcessorInfo];

    public static byte[] BuildGetDaqResolutionInfo() => [XcpCommand.GetDaqResolutionInfo];

    /// <summary>GET_DAQ_EVENT_INFO: 0xD7 | reserved | EVENT_CHANNEL_NUMBER (WORD).</summary>
    public byte[] BuildGetDaqEventInfo(ushort eventChannel)
    {
        var packet = new byte[4];
        packet[0] = XcpCommand.GetDaqEventInfo;
        WriteUInt16(packet.AsSpan(2), eventChannel);
        return packet;
    }

    #endregion

    #region DAQ response parsers

    public XcpDaqProcessorInfo ParseDaqProcessorInfoResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed GET_DAQ_PROCESSOR_INFO response ({packet.Length} bytes).");
        }

        return new XcpDaqProcessorInfo(
            Properties: packet[1],
            MaxDaq: ReadUInt16(packet[2..4]),
            MaxEventChannel: ReadUInt16(packet[4..6]),
            MinDaq: packet[6],
            KeyByte: packet[7]);
    }

    public XcpDaqResolutionInfo ParseDaqResolutionInfoResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed GET_DAQ_RESOLUTION_INFO response ({packet.Length} bytes).");
        }

        return new XcpDaqResolutionInfo(
            GranularityOdtEntrySizeDaq: packet[1],
            MaxOdtEntrySizeDaq: packet[2],
            GranularityOdtEntrySizeStim: packet[3],
            MaxOdtEntrySizeStim: packet[4],
            TimestampMode: packet[5],
            TimestampTicks: ReadUInt16(packet[6..8]));
    }

    public static XcpDaqEventInfo ParseDaqEventInfoResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 7 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed GET_DAQ_EVENT_INFO response ({packet.Length} bytes).");
        }

        return new XcpDaqEventInfo(
            Properties: packet[1],
            MaxDaqList: packet[2],
            NameLength: packet[3],
            TimeCycle: packet[4],
            TimeUnit: packet[5],
            Priority: packet[6]);
    }

    /// <summary>FIRST_PID from the START_STOP_DAQ_LIST response; only meaningful for slaves using
    /// absolute-PID DTO identification. Optional in the spec — 0 when the slave omits it.</summary>
    public static byte ParseStartStopDaqListResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed START_STOP_DAQ_LIST response ({packet.Length} bytes).");
        }

        return packet.Length >= 2 ? packet[1] : (byte)0;
    }

    #endregion

    #region Response parsers

    /// <summary>
    /// Parses the CONNECT positive response (full packet including the leading 0xFF). Static because
    /// the slave byte order is not known until this very response: MAX_DTO is read using the byte
    /// order announced in the same packet.
    /// </summary>
    public static XcpConnectResponse ParseConnectResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed CONNECT response ({packet.Length} bytes).");
        }

        var commModeBasic = packet[2];
        var isBigEndian = (commModeBasic & 0x01) != 0;
        var addressGranularity = ((commModeBasic >> 1) & 0x03) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 0 // reserved
        };

        var maxDto = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(packet[4..6])
            : BinaryPrimitives.ReadUInt16LittleEndian(packet[4..6]);

        return new XcpConnectResponse(
            Resource: packet[1],
            IsBigEndian: isBigEndian,
            AddressGranularity: addressGranularity,
            MaxCto: packet[3],
            MaxDto: maxDto,
            ProtocolLayerVersion: packet[6],
            TransportLayerVersion: packet[7],
            SupportsSlaveBlockMode: (commModeBasic & 0x40) != 0,
            HasOptionalCommModeInfo: (commModeBasic & 0x80) != 0);
    }

    /// <summary>Parses the GET_COMM_MODE_INFO positive response: 0xFF | reserved | COMM_MODE_OPTIONAL |
    /// reserved | MAX_BS | MIN_ST | QUEUE_SIZE | XCP driver version.</summary>
    public static XcpCommModeInfo ParseCommModeInfoResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed GET_COMM_MODE_INFO response ({packet.Length} bytes).");
        }

        return new XcpCommModeInfo(
            CommModeOptional: packet[2],
            MaxBs: packet[4],
            MinSt: packet[5],
            QueueSize: packet[6],
            DriverVersion: packet[7]);
    }

    /// <summary>Parses the GET_STATUS positive response (full packet including the leading 0xFF).</summary>
    public XcpStatusResponse ParseGetStatusResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6 || packet[0] != 0xFF)
        {
            throw new XcpProtocolException($"Malformed GET_STATUS response ({packet.Length} bytes).");
        }

        var sessionConfigurationId = IsBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(packet[4..6])
            : BinaryPrimitives.ReadUInt16LittleEndian(packet[4..6]);

        return new XcpStatusResponse(
            SessionStatus: packet[1],
            ResourceProtection: packet[2],
            SessionConfigurationId: sessionConfigurationId);
    }

    #endregion

    #region Value <-> memory bytes

    /// <summary>
    /// Encodes a boxed scalar value into its ECU memory image in the slave's byte order. The boxed
    /// CLR type must match <paramref name="type"/> (the same contract as ScalarVariable.SetValue).
    /// </summary>
    public byte[] EncodeValue(object value, ValueDataType type)
    {
        var bytes = new byte[XcpVariableSpecification.SizeOf(type)];
        if (bytes.Length == 0)
        {
            throw new XcpProtocolException($"Value type '{type}' cannot be transferred over XCP.");
        }

        try
        {
            switch (type)
            {
                case ValueDataType.Byte: bytes[0] = (byte)value; break;
                case ValueDataType.SByte: bytes[0] = unchecked((byte)(sbyte)value); break;
                case ValueDataType.UShort: BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value); break;
                case ValueDataType.Short: BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)value); break;
                case ValueDataType.UInt: BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value); break;
                case ValueDataType.Int: BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)value); break;
                case ValueDataType.ULong: BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value); break;
                case ValueDataType.Long: BinaryPrimitives.WriteInt64LittleEndian(bytes, (long)value); break;
                case ValueDataType.Float: BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)value); break;
                case ValueDataType.Double: BinaryPrimitives.WriteDoubleLittleEndian(bytes, (double)value); break;
                default: throw new XcpProtocolException($"Value type '{type}' cannot be transferred over XCP.");
            }
        }
        catch (InvalidCastException)
        {
            throw new XcpProtocolException(
                $"Value of CLR type {value.GetType().Name} does not match the configured XCP type '{type}'.");
        }

        if (IsBigEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Decodes an ECU memory image (in the slave's byte order) into a boxed scalar value whose CLR
    /// type matches <paramref name="type"/>, so it can be assigned via ScalarVariable.SetValue.
    /// </summary>
    public object DecodeValue(ReadOnlySpan<byte> bytes, ValueDataType type)
    {
        var size = XcpVariableSpecification.SizeOf(type);
        if (size == 0)
        {
            throw new XcpProtocolException($"Value type '{type}' cannot be transferred over XCP.");
        }

        if (bytes.Length != size)
        {
            throw new XcpProtocolException($"Expected {size} bytes for type '{type}', got {bytes.Length}.");
        }

        Span<byte> buffer = stackalloc byte[8];
        bytes.CopyTo(buffer);
        if (IsBigEndian)
        {
            buffer[..size].Reverse();
        }

        // The first arm is cast to object so the switch cannot infer 'double' as its natural type
        // (every numeric arm converts to double implicitly, which would box all values as double).
        return type switch
        {
            ValueDataType.Byte => (object)buffer[0],
            ValueDataType.SByte => (sbyte)buffer[0],
            ValueDataType.UShort => BinaryPrimitives.ReadUInt16LittleEndian(buffer),
            ValueDataType.Short => BinaryPrimitives.ReadInt16LittleEndian(buffer),
            ValueDataType.UInt => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            ValueDataType.Int => BinaryPrimitives.ReadInt32LittleEndian(buffer),
            ValueDataType.ULong => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            ValueDataType.Long => BinaryPrimitives.ReadInt64LittleEndian(buffer),
            ValueDataType.Float => BinaryPrimitives.ReadSingleLittleEndian(buffer),
            ValueDataType.Double => BinaryPrimitives.ReadDoubleLittleEndian(buffer),
            _ => throw new XcpProtocolException($"Value type '{type}' cannot be transferred over XCP.")
        };
    }

    private void WriteUInt32(Span<byte> destination, uint value)
    {
        if (IsBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        }
    }

    private void WriteUInt16(Span<byte> destination, ushort value)
    {
        if (IsBigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        }
    }

    internal ushort ReadUInt16(ReadOnlySpan<byte> source)
    {
        return IsBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(source)
            : BinaryPrimitives.ReadUInt16LittleEndian(source);
    }

    #endregion
}
