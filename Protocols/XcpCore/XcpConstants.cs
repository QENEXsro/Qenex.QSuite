namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>XCP command packet codes (ASAM XCP 1.1 Part 2, section 1.4).</summary>
public static class XcpCommand
{
    public const byte Connect = 0xFF;
    public const byte Disconnect = 0xFE;
    public const byte GetStatus = 0xFD;
    public const byte Synch = 0xFC;
    public const byte GetCommModeInfo = 0xFB;
    public const byte SetMta = 0xF6;
    public const byte Upload = 0xF5;
    public const byte ShortUpload = 0xF4;
    public const byte Download = 0xF0;
    /// <summary>Master block mode continuation of a DOWNLOAD (ASAM XCP 1.1 Part 2, 1.6.1.2.3).</summary>
    public const byte DownloadNext = 0xEF;

    // DAQ commands (ASAM XCP 1.1 Part 2, section 1.5.3).
    public const byte SetDaqPtr = 0xE2;
    public const byte WriteDaq = 0xE1;
    public const byte SetDaqListMode = 0xE0;
    public const byte StartStopDaqList = 0xDE;
    public const byte StartStopSynch = 0xDD;
    public const byte GetDaqProcessorInfo = 0xDA;
    public const byte GetDaqResolutionInfo = 0xD9;
    public const byte GetDaqEventInfo = 0xD7;
    public const byte FreeDaq = 0xD6;
    public const byte AllocDaq = 0xD5;
    public const byte AllocOdt = 0xD4;
    public const byte AllocOdtEntry = 0xD3;
}

/// <summary>SET_DAQ_LIST_MODE mode bits (ASAM XCP 1.1 Part 2, 1.6.4.1.1.3). This master sets
/// the timestamp bit and, for STIM lists, the direction bit — alternating and PID_OFF stay
/// unsupported (PID_OFF would leave STIM DTOs without ODT identification).</summary>
public static class XcpDaqListModeBits
{
    /// <summary>Bit 1: 0 = DAQ (slave sends), 1 = STIM (master sends).</summary>
    public const byte Direction = 0x02;

    public const byte Timestamp = 0x10;
}

/// <summary>START_STOP_DAQ_LIST mode parameter.</summary>
public static class XcpDaqStartStopMode
{
    public const byte Stop = 0x00;
    public const byte Start = 0x01;
    public const byte Select = 0x02;
}

/// <summary>START_STOP_SYNCH mode parameter.</summary>
public static class XcpDaqSynchMode
{
    public const byte StopAll = 0x00;
    public const byte StartSelected = 0x01;
    public const byte StopSelected = 0x02;
}

/// <summary>DTO identification field layout, from GET_DAQ_PROCESSOR_INFO DAQ_KEY_BYTE bits 6–7.
/// Determines how a received DAQ packet names its ODT and DAQ list (and where data starts).</summary>
public enum XcpDaqIdentificationType
{
    /// <summary>Absolute ODT number as single PID byte.</summary>
    AbsolutePid = 0,

    /// <summary>Relative ODT number (byte) + absolute DAQ list number (byte).</summary>
    OdtWithDaqByte = 1,

    /// <summary>Relative ODT number (byte) + absolute DAQ list number (word, unaligned).</summary>
    OdtWithDaqWord = 2,

    /// <summary>Relative ODT number (byte) + fill byte + absolute DAQ list number (word, aligned).</summary>
    OdtWithFillAndDaqWord = 3
}

/// <summary>Kind of a packet received from the slave, classified by its PID (first byte).</summary>
public enum XcpPacketKind
{
    /// <summary>0xFF — positive response to a command.</summary>
    Response,

    /// <summary>0xFE — negative response; second byte carries the error code.</summary>
    Error,

    /// <summary>0xFD — asynchronous event; second byte carries the event code.</summary>
    Event,

    /// <summary>0xFC — asynchronous service request from the slave.</summary>
    ServiceRequest,

    /// <summary>0x00..0xFB — DAQ data transfer object (ignored until DAQ is implemented).</summary>
    DaqDto
}

public static class XcpPacket
{
    public static XcpPacketKind Classify(byte pid) => pid switch
    {
        0xFF => XcpPacketKind.Response,
        0xFE => XcpPacketKind.Error,
        0xFD => XcpPacketKind.Event,
        0xFC => XcpPacketKind.ServiceRequest,
        _ => XcpPacketKind.DaqDto
    };
}

/// <summary>XCP error codes (ASAM XCP 1.1 Part 2, section 1.7.3.1).</summary>
public static class XcpErrorCode
{
    public const byte CmdSynch = 0x00;
    public const byte CmdBusy = 0x10;
    public const byte DaqActive = 0x11;
    public const byte PgmActive = 0x12;
    public const byte CmdUnknown = 0x20;
    public const byte CmdSyntax = 0x21;
    public const byte OutOfRange = 0x22;
    public const byte WriteProtected = 0x23;
    public const byte AccessDenied = 0x24;
    public const byte AccessLocked = 0x25;
    public const byte PageNotValid = 0x26;
    public const byte ModeNotValid = 0x27;
    public const byte SegmentNotValid = 0x28;
    public const byte Sequence = 0x29;
    public const byte DaqConfig = 0x2A;
    public const byte MemoryOverflow = 0x30;
    public const byte Generic = 0x31;
    public const byte Verify = 0x32;
    public const byte ResourceTemporaryNotAccessible = 0x33;

    public static string Describe(byte code) => code switch
    {
        CmdSynch => "ERR_CMD_SYNCH (0x00): Command processor synchronization.",
        CmdBusy => "ERR_CMD_BUSY (0x10): Command was not executed.",
        DaqActive => "ERR_DAQ_ACTIVE (0x11): Command rejected because DAQ is running.",
        PgmActive => "ERR_PGM_ACTIVE (0x12): Command rejected because PGM is running.",
        CmdUnknown => "ERR_CMD_UNKNOWN (0x20): Unknown or not implemented optional command.",
        CmdSyntax => "ERR_CMD_SYNTAX (0x21): Command syntax invalid.",
        OutOfRange => "ERR_OUT_OF_RANGE (0x22): Command parameter(s) out of range.",
        WriteProtected => "ERR_WRITE_PROTECTED (0x23): The memory location is write protected.",
        AccessDenied => "ERR_ACCESS_DENIED (0x24): The memory location is not accessible.",
        AccessLocked => "ERR_ACCESS_LOCKED (0x25): Access denied, seed & key is required.",
        PageNotValid => "ERR_PAGE_NOT_VALID (0x26): Selected page not available.",
        ModeNotValid => "ERR_MODE_NOT_VALID (0x27): Selected page mode not available.",
        SegmentNotValid => "ERR_SEGMENT_NOT_VALID (0x28): Selected segment not valid.",
        Sequence => "ERR_SEQUENCE (0x29): Sequence error.",
        DaqConfig => "ERR_DAQ_CONFIG (0x2A): DAQ configuration not valid.",
        MemoryOverflow => "ERR_MEMORY_OVERFLOW (0x30): Memory overflow error.",
        Generic => "ERR_GENERIC (0x31): Generic error.",
        Verify => "ERR_VERIFY (0x32): The slave internal program verify routine detects an error.",
        ResourceTemporaryNotAccessible => "ERR_RESOURCE_TEMPORARY_NOT_ACCESSIBLE (0x33): Access to the requested resource is temporarily not possible.",
        _ => $"Unknown XCP error 0x{code:X2}."
    };
}

/// <summary>XCP event codes (ASAM XCP 1.1 Part 2, section 1.2).</summary>
public static class XcpEventCode
{
    public const byte ResumeMode = 0x00;
    public const byte ClearDaq = 0x01;
    public const byte StoreDaq = 0x02;
    public const byte StoreCal = 0x03;
    public const byte CmdPending = 0x05;
    public const byte DaqOverload = 0x06;
    public const byte SessionTerminated = 0x07;
    public const byte TimeSync = 0x08;
    public const byte StimTimeout = 0x09;
    public const byte Sleep = 0x0A;
    public const byte WakeUp = 0x0B;
    public const byte User = 0xFE;
    public const byte Transport = 0xFF;
}

/// <summary>Resource bits shared by the CONNECT RESOURCE mask and the GET_STATUS protection mask.</summary>
public static class XcpResource
{
    public const byte Calibration = 0x01;
    public const byte Daq = 0x04;
    public const byte Stim = 0x08;
    public const byte Programming = 0x10;
}
