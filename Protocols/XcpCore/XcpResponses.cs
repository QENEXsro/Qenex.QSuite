namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>Parsed CONNECT positive response (ASAM XCP 1.1 Part 2, section 1.6.1.1.1).</summary>
public sealed record XcpConnectResponse(
    byte Resource,
    bool IsBigEndian,
    int AddressGranularity,
    byte MaxCto,
    ushort MaxDto,
    byte ProtocolLayerVersion,
    byte TransportLayerVersion,
    bool SupportsSlaveBlockMode = false,
    bool HasOptionalCommModeInfo = false)
{
    public bool SupportsCalibration => (Resource & XcpResource.Calibration) != 0;
    public bool SupportsDaq => (Resource & XcpResource.Daq) != 0;
    public bool SupportsStim => (Resource & XcpResource.Stim) != 0;
}

/// <summary>Parsed GET_COMM_MODE_INFO positive response (ASAM XCP 1.1 Part 2, section 1.6.1.1.4):
/// the slave's optional communication modes. MAX_BS = maximum number of consecutive command
/// packets in a master block mode block, MIN_ST = minimum separation time between them in units
/// of 100 µs, QUEUE_SIZE = depth of the slave's command queue (informative).</summary>
public sealed record XcpCommModeInfo(
    byte CommModeOptional,
    byte MaxBs,
    byte MinSt,
    byte QueueSize,
    byte DriverVersion)
{
    public bool SupportsMasterBlockMode => (CommModeOptional & 0x01) != 0;
    public bool SupportsInterleavedMode => (CommModeOptional & 0x02) != 0;
}

/// <summary>Parsed GET_STATUS positive response (ASAM XCP 1.1 Part 2, section 1.6.1.1.3).</summary>
public sealed record XcpStatusResponse(
    byte SessionStatus,
    byte ResourceProtection,
    ushort SessionConfigurationId)
{
    /// <summary>Calibration commands are seed &amp; key protected and would return ERR_ACCESS_LOCKED.</summary>
    public bool IsCalibrationProtected => (ResourceProtection & XcpResource.Calibration) != 0;

    /// <summary>DAQ commands are seed &amp; key protected and would return ERR_ACCESS_LOCKED.</summary>
    public bool IsDaqProtected => (ResourceProtection & XcpResource.Daq) != 0;

    /// <summary>STIM is seed &amp; key protected and would return ERR_ACCESS_LOCKED.</summary>
    public bool IsStimProtected => (ResourceProtection & XcpResource.Stim) != 0;
}

/// <summary>Parsed GET_DAQ_PROCESSOR_INFO positive response (ASAM XCP 1.1 Part 2, 1.6.1.3.1).</summary>
public sealed record XcpDaqProcessorInfo(
    byte Properties,
    ushort MaxDaq,
    ushort MaxEventChannel,
    byte MinDaq,
    byte KeyByte)
{
    /// <summary>DAQ_PROPERTIES bit 0: lists are configured dynamically (ALLOC_*), not static.</summary>
    public bool HasDynamicLists => (Properties & 0x01) != 0;

    public bool SupportsTimestamps => (Properties & 0x10) != 0;

    /// <summary>Overload is indicated by the MSB of the DTO's PID/ODT byte.</summary>
    public bool OverloadIndicationByPid => (Properties & 0xC0) == 0x40;

    /// <summary>Overload is indicated by the EV_DAQ_OVERLOAD event packet.</summary>
    public bool OverloadIndicationByEvent => (Properties & 0xC0) == 0x80;

    public XcpDaqIdentificationType IdentificationType => (XcpDaqIdentificationType)((KeyByte >> 6) & 0x03);

    /// <summary>Bytes the identification field occupies at the start of every DTO.</summary>
    public int DtoHeaderSize => IdentificationType switch
    {
        XcpDaqIdentificationType.AbsolutePid => 1,
        XcpDaqIdentificationType.OdtWithDaqByte => 2,
        XcpDaqIdentificationType.OdtWithDaqWord => 3,
        _ => 4
    };
}

/// <summary>Parsed GET_DAQ_RESOLUTION_INFO positive response (ASAM XCP 1.1 Part 2, 1.6.1.3.2).</summary>
public sealed record XcpDaqResolutionInfo(
    byte GranularityOdtEntrySizeDaq,
    byte MaxOdtEntrySizeDaq,
    byte GranularityOdtEntrySizeStim,
    byte MaxOdtEntrySizeStim,
    byte TimestampMode,
    ushort TimestampTicks)
{
    /// <summary>Timestamp width in bytes (0/1/2/4); the TIMESTAMP_MODE size bits carry it directly.</summary>
    public int TimestampSize => TimestampMode & 0x07;

    /// <summary>The slave always sends timestamps regardless of the DAQ list mode bit.</summary>
    public bool TimestampFixed => (TimestampMode & 0x08) != 0;

    /// <summary>
    /// Duration of one slave timestamp tick in seconds: TIMESTAMP_TICKS × unit, with the unit
    /// from TIMESTAMP_MODE bits 4..7 (10^exponent ns per the ASAM table). Example (XCPlite on
    /// a 1 µs clock): unit 1 ns, ticks 1000 → 1 µs per tick.
    /// </summary>
    public double TimestampTickSeconds =>
        Math.Pow(10.0, (TimestampMode >> 4) & 0x0F) * 1e-9 * Math.Max((int)TimestampTicks, 1);
}

/// <summary>Parsed GET_DAQ_EVENT_INFO positive response (ASAM XCP 1.1 Part 2, 1.6.1.3.5);
/// the event channel name is uploaded separately via the MTA the command leaves behind.</summary>
public sealed record XcpDaqEventInfo(
    byte Properties,
    byte MaxDaqList,
    byte NameLength,
    byte TimeCycle,
    byte TimeUnit,
    byte Priority)
{
    public bool SupportsDaq => (Properties & 0x04) != 0;

    /// <summary>The event channel accepts DAQ lists in STIM direction (DAQ_EVENT_PROPERTIES bit 3).</summary>
    public bool SupportsStim => (Properties & 0x08) != 0;

    /// <summary>Nominal cycle in ms computed from TIME_CYCLE × 10^TIME_UNIT ns;
    /// null when the event is sporadic (TIME_CYCLE = 0).</summary>
    public double? CycleTimeMs => TimeCycle == 0 ? null : TimeCycle * Math.Pow(10, TimeUnit) / 1_000_000.0;
}
