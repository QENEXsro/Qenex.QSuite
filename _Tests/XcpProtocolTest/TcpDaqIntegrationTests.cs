using System.Buffers.Binary;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Protocols.XcpTcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>
/// End-to-end DAQ over the XcpTcp protocol against a simulated XCP-on-Ethernet slave
/// (ODT+FIL+DAQW identification, 32-bit timestamps) in both timestamp shapes: the legacy
/// TIMESTAMP_FIXED slave (XCPlite) and the QFW SDK slave (0.10.0+), where the master's
/// SET_DAQ_LIST_MODE timestamp bit decides whether ODT 0 carries a timestamp at all. Covers the
/// setup sequence, DTO decoding into variables with and without the timestamp, both daqTimestamps
/// settings, polling coexistence (D4) and the no-DAQ-resource fallback to polling.
/// </summary>
internal static class TcpDaqIntegrationTests
{
    internal static void Run()
    {
        DaqFlow_DtoLandsInVariable_PollingCoexists().GetAwaiter().GetResult();
        NoDaqResource_FallsBackToPolling().GetAwaiter().GetResult();
        QfwShape_MasterTimestamps_NoTimestampOnTheWire().GetAwaiter().GetResult();
        QfwShape_SlaveTimestamps_BitSetAndDtoDecoded().GetAwaiter().GetResult();
        FixedSlave_MasterTimestamps_BitStaysSetTimestampIgnored().GetAwaiter().GetResult();
        NoResolutionInfo_SlaveMode_WarnsAndRunsOnReceiveTime().GetAwaiter().GetResult();
        NoResolutionInfo_MasterMode_NoteOnly().GetAwaiter().GetResult();
    }

    /// <summary>GET_DAQ_RESOLUTION_INFO timestamp mode: 4-byte timestamps, unit 1 ms; bit 3 =
    /// TIMESTAMP_FIXED (legacy XCPlite) or clear = controlled by the SET_DAQ_LIST_MODE bit (QFW SDK).</summary>
    private const byte TimestampModeFixed = 0x6C;
    private const byte TimestampModeByListBit = 0x64;

    #region Simulated DAQ slave harness

    private sealed class SimulatedDaqSlave
    {
        private readonly XcpTcp protocol;
        private readonly List<byte> rxStream = [];

        public readonly List<byte[]> SentPackets = [];
        public byte ConnectResource = 0x05; // CAL + DAQ
        public byte TimestampMode = TimestampModeFixed;
        public bool ResolutionInfoSupported = true; // false = ERR_CMD_UNKNOWN on GET_DAQ_RESOLUTION_INFO
        public byte[] PolledMemory = new byte[8];

        public SimulatedDaqSlave(XcpTcp xcpTcpProtocol)
        {
            protocol = xcpTcpProtocol;
            protocol.SetTransmitter(async (chunk, ct) =>
            {
                List<byte[]> commands = [];
                lock (SentPackets)
                {
                    rxStream.AddRange(chunk);
                    while (rxStream.Count >= 4)
                    {
                        var length = rxStream[0] | (rxStream[1] << 8);
                        if (rxStream.Count < 4 + length)
                        {
                            break;
                        }

                        var command = rxStream.GetRange(4, length).ToArray();
                        rxStream.RemoveRange(0, 4 + length);
                        SentPackets.Add(command);
                        commands.Add(command);
                    }
                }

                foreach (var command in commands)
                {
                    var response = Respond(command);
                    if (response != null)
                    {
                        await SendToMasterAsync(response, ct);
                    }
                }
            });
        }

        public int CountSent(byte pid)
        {
            lock (SentPackets)
            {
                return SentPackets.Count(p => p[0] == pid);
            }
        }

        public byte[]? FindSent(byte pid)
        {
            lock (SentPackets)
            {
                return SentPackets.FirstOrDefault(p => p[0] == pid);
            }
        }

        /// <summary>Frames one slave packet (RES/EV/DTO) and loops it back into the protocol.</summary>
        public async Task SendToMasterAsync(byte[] packet, CancellationToken ct = default)
        {
            var frame = new byte[4 + packet.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)packet.Length);
            packet.CopyTo(frame, 4);
            await protocol.AddReceivedDataToQueueAsync([frame], ct);
        }

        private byte[]? Respond(byte[] command) => command[0] switch
        {
            XcpCommand.Connect => [0xFF, ConnectResource, 0x00, 0xFA, 0xFF, 0x05, 0x01, 0x01],
            XcpCommand.GetStatus => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00],
            XcpCommand.Disconnect => [0xFF],
            XcpCommand.Synch => [0xFE, XcpErrorCode.CmdSynch],
            XcpCommand.ShortUpload => [(byte)0xFF, .. PolledMemory.Take(command[1])],
            XcpCommand.Upload => [0xFF, (byte)'l', (byte)'o', (byte)'o', (byte)'p'],
            // XCPlite shape: dynamic lists, timestamps, overload via PID, ODT+FIL+DAQW, ext per DAQ.
            XcpCommand.GetDaqProcessorInfo => [0xFF, 0x51, 0x00, 0x00, 0x02, 0x00, 0x00, 0xF0],
            // 4-byte timestamps (unit 1 ms, 1 tick), fixed or by list bit per TimestampMode;
            // max ODT entry 250 bytes.
            XcpCommand.GetDaqResolutionInfo => ResolutionInfoSupported
                ? [0xFF, 0x01, 0xFA, 0x01, 0xFA, TimestampMode, 0x01, 0x00]
                : [0xFE, XcpErrorCode.CmdUnknown],
            // DAQ-capable channel, 4-char name ("loop" via UPLOAD), nominal cycle 1 ms.
            XcpCommand.GetDaqEventInfo => [0xFF, 0x84, 0xFF, 0x04, 0x01, 0x06, 0x00],
            XcpCommand.FreeDaq or XcpCommand.AllocDaq or XcpCommand.AllocOdt or XcpCommand.AllocOdtEntry
                or XcpCommand.SetDaqPtr or XcpCommand.WriteDaq or XcpCommand.SetDaqListMode
                or XcpCommand.StartStopSynch => [0xFF],
            XcpCommand.StartStopDaqList => [0xFF, 0x00],
            _ => null
        };
    }

    private static ScalarVariable DoubleVariable(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Values = new Values<double> { Value = 0d, ValueType = ValueDataType.Double }
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>ODT 0 of DAQ list 0 in the ODT+FIL+DAQW identification, optionally with the
    /// DWORD timestamp, followed by one 8-byte double.</summary>
    private static byte[] BuildDaqDto(uint? timestamp, double value)
    {
        var dto = new byte[4 + (timestamp.HasValue ? 4 : 0) + 8];
        dto[0] = 0x00;
        dto[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(dto.AsSpan(2), 0);
        var offset = 4;
        if (timestamp is { } ticks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dto.AsSpan(4), ticks);
            offset = 8;
        }

        BinaryPrimitives.WriteDoubleLittleEndian(dto.AsSpan(offset), value);
        return dto;
    }

    private static (XcpTcp Protocol, SimulatedDaqSlave Slave, ScalarVariable DaqVariable, ScalarVariable PolledVariable)
        CreateRunningSetup(string rawSettings = "requestTimeoutMs=\"100\"", ILogger? logger = null)
    {
        var protocol = new XcpTcp
        {
            IsEnabled = true,
            Logger = logger,
            RawSettings = rawSettings
        };
        protocol.SetConfiguration();

        var daqEvent = new PeriodicVarEvent
        {
            Name = "daq1ms", Period = 1, Unit = TimeUnit.Milisec,
            EventExtraParams = "direction=\"DAQ\";daqId=\"1\""
        };
        var pollEvent = new PeriodicVarEvent { Name = "poll20ms", Period = 20, Unit = TimeUnit.Milisec };
        IVarEvent[] events = [daqEvent, pollEvent];

        var daqVariable = DoubleVariable(1, "HeatEnergy");
        var polledVariable = DoubleVariable(2, "FlowRate");
        protocol.AddVariable(protocol.CreateProtocolVariable(daqVariable, events,
            "address=\"0x1000\";direction=\"read\";eventRef=\"daq1ms\"", true)!);
        protocol.AddVariable(protocol.CreateProtocolVariable(polledVariable, events,
            "address=\"0x2000\";direction=\"read\";eventRef=\"poll20ms\"", true)!);

        var slave = new SimulatedDaqSlave(protocol);
        return (protocol, slave, daqVariable, polledVariable);
    }

    #endregion

    private static async Task DaqFlow_DtoLandsInVariable_PollingCoexists()
    {
        var (protocol, slave, daqVariable, polledVariable) = CreateRunningSetup();
        BinaryPrimitives.WriteDoubleLittleEndian(slave.PolledMemory, 12.5);

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "tcp daq: configuration ran through to START_STOP_SYNCH");

        // One DTO from the slave: ODT 0 of DAQ list 0 (header ODT|FIL|DAQW), fixed 32-bit
        // timestamp, then the 8-byte double.
        var dto = new byte[4 + 4 + 8];
        dto[0] = 0x00;
        dto[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(dto.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dto.AsSpan(4), 123456);
        BinaryPrimitives.WriteDoubleLittleEndian(dto.AsSpan(8), 7.75);
        await slave.SendToMasterAsync(dto);

        var daqLanded = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 7.75);
        var pollLanded = await WaitUntilAsync(() => (double)polledVariable.GetValue() == 12.5);
        await protocol.StopAsync();

        Check(daqLanded, "tcp daq: DTO value landed in the DAQ variable (timestamp = receive time, 4a)");
        Check(pollLanded, "tcp daq: the polled variable still updates via SHORT_UPLOAD (D4 coexistence)");

        var setMode = slave.FindSent(XcpCommand.SetDaqListMode);
        Check(setMode != null && (setMode[1] & XcpDaqListModeBits.Timestamp) != 0,
            "tcp daq: SET_DAQ_LIST_MODE carries the timestamp bit (daqTimestamps=slave, default)");
        Check(setMode != null && BinaryPrimitives.ReadUInt16LittleEndian(setMode.AsSpan(4)) == 1,
            "tcp daq: DAQ list bound to ECU event channel 1 (daqId from eventExtraParams)");

        var writeDaq = slave.FindSent(XcpCommand.WriteDaq);
        Check(writeDaq != null && writeDaq[2] == 8 &&
              BinaryPrimitives.ReadUInt32LittleEndian(writeDaq.AsSpan(4)) == 0x1000,
            "tcp daq: WRITE_DAQ carries the variable's size and address");

        lock (slave.SentPackets)
        {
            Check(slave.SentPackets.Where(p => p[0] == XcpCommand.ShortUpload)
                    .All(p => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == 0x2000),
                "tcp daq: the DAQ variable is never polled — SHORT_UPLOAD only reads the polled one");
        }
    }

    private static async Task NoDaqResource_FallsBackToPolling()
    {
        var (protocol, slave, daqVariable, _) = CreateRunningSetup();
        slave.ConnectResource = 0x01; // CAL only, no DAQ
        BinaryPrimitives.WriteDoubleLittleEndian(slave.PolledMemory, 3.5);

        await protocol.StartAsync();
        var landed = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 3.5);
        await protocol.StopAsync();

        Check(landed, "tcp daq fallback: without a DAQ resource the DAQ variable is polled (D4)");
        Check(slave.CountSent(XcpCommand.FreeDaq) == 0, "tcp daq fallback: no DAQ configuration was attempted");
        lock (slave.SentPackets)
        {
            Check(slave.SentPackets.Any(p =>
                    p[0] == XcpCommand.ShortUpload && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == 0x1000),
                "tcp daq fallback: SHORT_UPLOAD reads the DAQ variable's address");
        }
    }

    /// <summary>QFW SDK shape with daqTimestamps=master: the master leaves the timestamp bit
    /// clear, the slave sends no timestamp and ODT 0 is pure data (full capacity).</summary>
    private static async Task QfwShape_MasterTimestamps_NoTimestampOnTheWire()
    {
        var (protocol, slave, daqVariable, _) = CreateRunningSetup("requestTimeoutMs=\"100\";daqTimestamps=\"master\"");
        slave.TimestampMode = TimestampModeByListBit;

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "qfw daq master: configuration ran through to START_STOP_SYNCH");

        await slave.SendToMasterAsync(BuildDaqDto(timestamp: null, 7.75));
        var landed = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 7.75);
        await protocol.StopAsync();

        var setMode = slave.FindSent(XcpCommand.SetDaqListMode);
        Check(setMode != null && (setMode[1] & XcpDaqListModeBits.Timestamp) == 0,
            "qfw daq master: SET_DAQ_LIST_MODE leaves the timestamp bit clear (daqTimestamps=master)");
        Check(landed, "qfw daq master: an untimestamped DTO (4 B header + data) decodes into the variable");
    }

    /// <summary>QFW SDK shape with the default daqTimestamps=slave: the master sets the bit and
    /// the slave answers with a DWORD timestamp in ODT 0 ahead of the data.</summary>
    private static async Task QfwShape_SlaveTimestamps_BitSetAndDtoDecoded()
    {
        var (protocol, slave, daqVariable, _) = CreateRunningSetup();
        slave.TimestampMode = TimestampModeByListBit;

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "qfw daq slave: configuration ran through to START_STOP_SYNCH");

        await slave.SendToMasterAsync(BuildDaqDto(timestamp: 123456, 7.75));
        var landed = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 7.75);
        await protocol.StopAsync();

        var setMode = slave.FindSent(XcpCommand.SetDaqListMode);
        Check(setMode != null && (setMode[1] & XcpDaqListModeBits.Timestamp) != 0,
            "qfw daq slave: SET_DAQ_LIST_MODE requests slave timestamps (daqTimestamps=slave)");
        Check(landed, "qfw daq slave: the DTO with the DWORD timestamp in ODT 0 decodes into the variable");
    }

    /// <summary>Legacy TIMESTAMP_FIXED slave with daqTimestamps=master: the slave sends
    /// timestamps regardless, so the master keeps the bit set for the packet layout and skips the
    /// timestamp when decoding (compatibility path, e.g. XCPlite).</summary>
    private static async Task FixedSlave_MasterTimestamps_BitStaysSetTimestampIgnored()
    {
        var (protocol, slave, daqVariable, _) = CreateRunningSetup("requestTimeoutMs=\"100\";daqTimestamps=\"master\"");
        slave.TimestampMode = TimestampModeFixed;

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "legacy fixed daq master: configuration ran through to START_STOP_SYNCH");

        await slave.SendToMasterAsync(BuildDaqDto(timestamp: 123456, 7.75));
        var landed = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 7.75);
        await protocol.StopAsync();

        var setMode = slave.FindSent(XcpCommand.SetDaqListMode);
        Check(setMode != null && (setMode[1] & XcpDaqListModeBits.Timestamp) != 0,
            "legacy fixed daq master: SET_DAQ_LIST_MODE keeps the timestamp bit (TIMESTAMP_FIXED slave sends it anyway)");
        Check(landed, "legacy fixed daq master: the timestamp in ODT 0 is skipped and the value decodes");
    }

    /// <summary>A slave without GET_DAQ_RESOLUTION_INFO: no timestamp size is known, so DAQ runs
    /// untimestamped on the PC receive time. With daqTimestamps=slave that contradicts the
    /// configuration, so the log carries a warning; the DAQ still starts and decodes.</summary>
    private static async Task NoResolutionInfo_SlaveMode_WarnsAndRunsOnReceiveTime()
    {
        var logger = new CapturingLogger();
        var (protocol, slave, daqVariable, _) = CreateRunningSetup(logger: logger);
        slave.ResolutionInfoSupported = false;

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "no resolution info: DAQ still runs through to START_STOP_SYNCH");

        await slave.SendToMasterAsync(BuildDaqDto(timestamp: null, 7.75));
        var landed = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 7.75);
        await protocol.StopAsync();

        var setMode = slave.FindSent(XcpCommand.SetDaqListMode);
        Check(setMode != null && (setMode[1] & XcpDaqListModeBits.Timestamp) == 0,
            "no resolution info: SET_DAQ_LIST_MODE cannot request timestamps (size unknown)");
        Check(landed, "no resolution info: the untimestamped DTO decodes into the variable");
        Check(logger.Has(LogLevel.Warn, "GET_DAQ_RESOLUTION_INFO"),
            "no resolution info (daqTimestamps=slave): a WARN names the missing GET_DAQ_RESOLUTION_INFO");
        Check(logger.Has(LogLevel.Warn, "daqTimestamps=slave"),
            "no resolution info (daqTimestamps=slave): the WARN says the configured ECU time axis is not honoured");
        Check(logger.Has(LogLevel.Info, "DAQ started") && logger.Has(LogLevel.Info, "slave timestamps off"),
            "no resolution info: 'DAQ started … slave timestamps off' is logged at INFO (visible without Debug)");
    }

    /// <summary>Same slave with daqTimestamps=master: the outcome matches the configuration, so
    /// the missing command is only noted at INFO — no warning.</summary>
    private static async Task NoResolutionInfo_MasterMode_NoteOnly()
    {
        var logger = new CapturingLogger();
        var (protocol, slave, daqVariable, _) = CreateRunningSetup("requestTimeoutMs=\"100\";daqTimestamps=\"master\"", logger);
        slave.ResolutionInfoSupported = false;

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "no resolution info (master): DAQ runs through to START_STOP_SYNCH");

        await slave.SendToMasterAsync(BuildDaqDto(timestamp: null, 7.75));
        var landed = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 7.75);
        await protocol.StopAsync();

        Check(landed, "no resolution info (master): the untimestamped DTO decodes into the variable");
        Check(logger.Has(LogLevel.Info, "GET_DAQ_RESOLUTION_INFO"),
            "no resolution info (master): the missing command is noted at INFO");
        Check(!logger.HasAny(LogLevel.Warn),
            "no resolution info (master): no warning — the receive-time axis is what was configured");
    }
}
