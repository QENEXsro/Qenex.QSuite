using System.Buffers.Binary;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Protocols.XcpTcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>
/// End-to-end STIM over the XcpTcp protocol against a simulated XCP-on-Ethernet slave (S1–S7):
/// the shared DAQ+STIM configuration transaction, the DIRECTION mode bit, the master's DTO
/// stream (layout, fresh values after an operator change), the suppression of direct writes for
/// streamed variables, DAQ coexistence and the no-STIM-resource fallback to SET_MTA + DOWNLOAD.
/// Both timestamp shapes are covered: the legacy TIMESTAMP_FIXED slave (XCPlite), whose STIM
/// lists must run timestamped, and the QFW SDK slave (0.10.0+), whose STIM lists are plain
/// header + data. Also covers the engineering-value write (decision point 21 phase 2): a
/// non-identity linear conversion is inverted above the protocol layer, so both write paths —
/// the STIM stream and the fallback DOWNLOAD — must carry the raw value.
/// </summary>
internal static class TcpStimIntegrationTests
{
    internal static void Run()
    {
        StimFlow_MasterStreamsDtos_WithDaqCoexistence().GetAwaiter().GetResult();
        NoStimResource_FallsBackToDirectWrites().GetAwaiter().GetResult();
        EngConversion_StimStream_SendsInvertedRaw().GetAwaiter().GetResult();
        EngConversion_FallbackDownload_SendsInvertedRaw().GetAwaiter().GetResult();
        QfwShape_StimUntimestamped_DaqTimestamped().GetAwaiter().GetResult();
        QfwShape_MasterTimestamps_NoTimestampsAtAll().GetAwaiter().GetResult();
    }

    /// <summary>GET_DAQ_RESOLUTION_INFO timestamp mode: 4-byte timestamps, unit 1 ms; bit 3 =
    /// TIMESTAMP_FIXED (legacy XCPlite) or clear = controlled by the SET_DAQ_LIST_MODE bit (QFW SDK).</summary>
    private const byte TimestampModeFixed = 0x6C;
    private const byte TimestampModeByListBit = 0x64;

    #region Simulated DAQ+STIM slave harness

    private sealed class SimulatedStimSlave
    {
        private readonly XcpTcp protocol;
        private readonly List<byte> rxStream = [];

        public readonly List<byte[]> SentPackets = [];
        public byte ConnectResource = 0x0D; // CAL + DAQ + STIM
        public byte TimestampMode = TimestampModeFixed;

        public SimulatedStimSlave(XcpTcp xcpTcpProtocol)
        {
            protocol = xcpTcpProtocol;
            protocol.SetTransmitter(async (chunk, ct) =>
            {
                List<byte[]> packets = [];
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

                        var packet = rxStream.GetRange(4, length).ToArray();
                        rxStream.RemoveRange(0, 4 + length);
                        SentPackets.Add(packet);
                        packets.Add(packet);
                    }
                }

                foreach (var packet in packets)
                {
                    var response = Respond(packet);
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

        public byte[]? FindSent(Func<byte[], bool> predicate)
        {
            lock (SentPackets)
            {
                return SentPackets.FirstOrDefault(predicate);
            }
        }

        public List<byte[]> AllSent(Func<byte[], bool> predicate)
        {
            lock (SentPackets)
            {
                return SentPackets.Where(predicate).ToList();
            }
        }

        public async Task SendToMasterAsync(byte[] packet, CancellationToken ct = default)
        {
            var frame = new byte[4 + packet.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)packet.Length);
            packet.CopyTo(frame, 4);
            await protocol.AddReceivedDataToQueueAsync([frame], ct);
        }

        private byte[]? Respond(byte[] packet) => packet[0] switch
        {
            XcpCommand.Connect => [0xFF, ConnectResource, 0x00, 0xFA, 0xFF, 0x05, 0x01, 0x01],
            XcpCommand.GetStatus => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00],
            XcpCommand.Disconnect => [0xFF],
            XcpCommand.Synch => [0xFE, XcpErrorCode.CmdSynch],
            XcpCommand.SetMta or XcpCommand.Download => [0xFF],
            XcpCommand.ShortUpload => [0xFF, 0, 0, 0, 0, 0, 0, 0, 0],
            XcpCommand.Upload => [0xFF, (byte)'l', (byte)'o', (byte)'o', (byte)'p'],
            // XCPlite shape: dynamic lists, timestamps, overload via PID, ODT+FIL+DAQW, ext per DAQ.
            XcpCommand.GetDaqProcessorInfo => [0xFF, 0x51, 0x00, 0x00, 0x04, 0x00, 0x00, 0xF0],
            // 4-byte timestamps (unit 1 ms, 1 tick), fixed or by list bit per TimestampMode;
            // STIM granularity 1, max entry 250.
            XcpCommand.GetDaqResolutionInfo => [0xFF, 0x01, 0xFA, 0x01, 0xFA, TimestampMode, 0x01, 0x00],
            // Channel supports DAQ and STIM (0x04 | 0x08), 4-char name, nominal cycle 10 ms.
            XcpCommand.GetDaqEventInfo => [0xFF, 0x8C, 0xFF, 0x04, 0x01, 0x07, 0x00],
            XcpCommand.FreeDaq or XcpCommand.AllocDaq or XcpCommand.AllocOdt or XcpCommand.AllocOdtEntry
                or XcpCommand.SetDaqPtr or XcpCommand.WriteDaq or XcpCommand.SetDaqListMode
                or XcpCommand.StartStopSynch => [0xFF],
            XcpCommand.StartStopDaqList => [0xFF, 0x00],
            _ => null // master STIM DTOs (PID < 0xC0) land here: recorded, no response
        };
    }

    private static ScalarVariable DoubleVariable(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Values = new Values<double> { Value = 0d, ValueType = ValueDataType.Double }
    };

    /// <summary>Non-identity linear conversion: eng = raw * 5 + 50 (percent of a ±10 raw range),
    /// so 75 % inverts to raw 5.0 and 60 % to raw 2.0.</summary>
    private static void AttachPercentPresentation(ScalarVariable variable)
    {
        variable.Values.ValPresentation = new Presentation
        {
            Name = "StimPercent",
            Min = 0,
            Max = 100,
            Unit = "%",
            Conversion = new LinearValConversion { Multiplier = 5, Offset = 50 }
        };
    }

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

    private static (XcpTcp Protocol, SimulatedStimSlave Slave, ScalarVariable StimVariable, ScalarVariable DaqVariable)
        CreateRunningSetup(string rawSettings = "requestTimeoutMs=\"100\"", ILogger? logger = null)
    {
        var protocol = new XcpTcp
        {
            IsEnabled = true,
            Logger = logger,
            RawSettings = rawSettings
        };
        protocol.SetConfiguration();

        var stimEvent = new PeriodicVarEvent
        {
            Name = "stim10ms", Period = 10, Unit = TimeUnit.Milisec,
            EventExtraParams = "direction=\"STIM\";daqId=\"2\""
        };
        var daqEvent = new PeriodicVarEvent
        {
            Name = "daq10ms", Period = 10, Unit = TimeUnit.Milisec,
            EventExtraParams = "direction=\"DAQ\";daqId=\"1\""
        };
        IVarEvent[] events = [stimEvent, daqEvent];

        var stimVariable = DoubleVariable(1, "SinStim");
        var daqVariable = DoubleVariable(2, "SinRaw");
        protocol.AddVariable(protocol.CreateProtocolVariable(stimVariable, events,
            "address=\"0x3000\";direction=\"write\";eventRef=\"stim10ms\"", true)!);
        protocol.AddVariable(protocol.CreateProtocolVariable(daqVariable, events,
            "address=\"0x1000\";direction=\"read\";eventRef=\"daq10ms\"", true)!);

        var slave = new SimulatedStimSlave(protocol);
        return (protocol, slave, stimVariable, daqVariable);
    }

    /// <summary>A STIM DTO of list 1 (the second list after the DAQ list) in the simulated
    /// slave's ODT+FIL+DAQW identification: ODT 0, fill, DAQ word = 1.</summary>
    private static bool IsStimDto(byte[] packet)
    {
        return packet.Length >= 4 && packet[0] == 0x00 &&
               BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 1 &&
               packet[0] < 0xC0;
    }

    #endregion

    private static async Task StimFlow_MasterStreamsDtos_WithDaqCoexistence()
    {
        var (protocol, slave, stimVariable, daqVariable) = CreateRunningSetup();
        stimVariable.SetValue(3.25);

        await protocol.StartAsync();
        var started = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(started, "tcp stim: configuration ran through to START_STOP_SYNCH");

        // Both lists configured in one transaction: list 0 = DAQ on channel 1 (timestamp bit),
        // list 1 = STIM on channel 2 (direction + timestamp bit — a legacy TIMESTAMP_FIXED slave
        // rejects switching the timestamp off; see the QfwShape_* tests for the normal case).
        var modes = slave.AllSent(p => p[0] == XcpCommand.SetDaqListMode);
        Check(modes.Count == 2, "tcp stim: SET_DAQ_LIST_MODE sent for both lists (S4 shared transaction)");
        var daqMode = modes.FirstOrDefault(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 0);
        var stimMode = modes.FirstOrDefault(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 1);
        Check(daqMode != null && daqMode[1] == 0x10 &&
              BinaryPrimitives.ReadUInt16LittleEndian(daqMode.AsSpan(4)) == 1,
            "tcp stim: DAQ list keeps mode 0x10 on event channel 1");
        Check(stimMode != null && stimMode[1] == (XcpDaqListModeBits.Direction | XcpDaqListModeBits.Timestamp) &&
              BinaryPrimitives.ReadUInt16LittleEndian(stimMode.AsSpan(4)) == 2,
            "tcp stim: STIM list carries the DIRECTION bit (+ timestamp, TIMESTAMP_FIXED) on event channel 2");

        // The master streams DTOs: ODT+FIL+DAQW header (4 B), 4-byte timestamp, 8-byte double.
        var landed = await WaitUntilAsync(() => slave.FindSent(p =>
            IsStimDto(p) && p.Length == 4 + 4 + 8 &&
            BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(8)) == 3.25) != null);
        Check(landed, "tcp stim: STIM DTO carries the variable's value after the timestamp (S3)");

        // An operator change shows up in a subsequent cycle without any SET_MTA/DOWNLOAD.
        stimVariable.SetValue(7.5);
        var updated = await WaitUntilAsync(() => slave.FindSent(p =>
            IsStimDto(p) && p.Length == 16 &&
            BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(8)) == 7.5) != null);
        Check(updated, "tcp stim: a changed value is streamed with the next cycle (S3)");

        await protocol.WriteVariableAsync(protocol.Variables.First(v => v.Variable == stimVariable));
        Check(slave.CountSent(XcpCommand.SetMta) == 0 && slave.CountSent(XcpCommand.Download) == 0,
            "tcp stim: no SET_MTA/DOWNLOAD for a STIM-streamed variable (S3/S5)");

        // DAQ direction still works concurrently: a slave DTO lands in the DAQ variable.
        var dto = new byte[4 + 4 + 8];
        dto[0] = 0x00;
        dto[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(dto.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dto.AsSpan(4), 1000);
        BinaryPrimitives.WriteDoubleLittleEndian(dto.AsSpan(8), 42.0);
        await slave.SendToMasterAsync(dto);
        var daqLanded = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 42.0);
        await protocol.StopAsync();

        Check(daqLanded, "tcp stim: DAQ list 0 still decodes into its variable (bypass pairing, S4)");

        // WRITE_DAQ for the STIM entry carries the variable's size and address.
        var writeDaqs = slave.AllSent(p => p[0] == XcpCommand.WriteDaq);
        Check(writeDaqs.Any(p => p[2] == 8 && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == 0x3000),
            "tcp stim: WRITE_DAQ configures the STIM entry (8 B at 0x3000)");
    }

    private static async Task NoStimResource_FallsBackToDirectWrites()
    {
        var (protocol, slave, stimVariable, _) = CreateRunningSetup();
        slave.ConnectResource = 0x05; // CAL + DAQ, no STIM
        stimVariable.SetValue(1.5);

        await protocol.StartAsync();
        var daqStarted = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(daqStarted, "tcp stim fallback: DAQ still starts without the STIM resource");

        var modes = slave.AllSent(p => p[0] == XcpCommand.SetDaqListMode);
        Check(modes.Count == 1 && (modes[0][1] & XcpDaqListModeBits.Direction) == 0,
            "tcp stim fallback: only the DAQ list is configured, no STIM direction (S5)");

        // The operator write now goes out directly (SET_MTA + DOWNLOAD).
        await protocol.WriteVariableAsync(protocol.Variables.First(v => v.Variable == stimVariable));
        var written = await WaitUntilAsync(() => slave.CountSent(XcpCommand.Download) >= 1);
        await protocol.StopAsync();

        Check(written, "tcp stim fallback: the STIM variable is written via SET_MTA + DOWNLOAD (S5)");
        var setMta = slave.FindSent(p => p[0] == XcpCommand.SetMta);
        Check(setMta != null && BinaryPrimitives.ReadUInt32LittleEndian(setMta.AsSpan(4)) == 0x3000,
            "tcp stim fallback: SET_MTA targets the variable's address");
    }

    private static async Task EngConversion_StimStream_SendsInvertedRaw()
    {
        var (protocol, slave, stimVariable, _) = CreateRunningSetup();
        AttachPercentPresentation(stimVariable);

        Check(stimVariable.TrySetEngValue(75.0), "eng stim: TrySetEngValue accepts 75 %");
        Check((double)stimVariable.GetValue() == 5.0, "eng stim: 75 % inverts to raw 5.0 above the protocol");

        await protocol.StartAsync();
        var streamed = await WaitUntilAsync(() => slave.FindSent(p =>
            IsStimDto(p) && p.Length == 16 &&
            BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(8)) == 5.0) != null);
        Check(streamed, "eng stim: the STIM DTO carries the inverted raw value (5.0, not 75)");

        Check(stimVariable.TrySetEngValue(60.0), "eng stim: TrySetEngValue accepts 60 %");
        var updated = await WaitUntilAsync(() => slave.FindSent(p =>
            IsStimDto(p) && p.Length == 16 &&
            BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(8)) == 2.0) != null);
        await protocol.StopAsync();

        Check(updated, "eng stim: a changed engineering value streams as fresh raw (2.0) in the next cycle");
    }

    private static async Task EngConversion_FallbackDownload_SendsInvertedRaw()
    {
        var (protocol, slave, stimVariable, _) = CreateRunningSetup();
        slave.ConnectResource = 0x05; // CAL + DAQ, no STIM -> operator writes go out directly
        AttachPercentPresentation(stimVariable);
        Check(stimVariable.TrySetEngValue(75.0), "eng download: TrySetEngValue accepts 75 %");

        await protocol.StartAsync();
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        await protocol.WriteVariableAsync(protocol.Variables.First(v => v.Variable == stimVariable));
        var written = await WaitUntilAsync(() => slave.CountSent(XcpCommand.Download) >= 1);
        await protocol.StopAsync();

        Check(written, "eng download: the fallback write reaches DOWNLOAD");
        var download = slave.FindSent(p => p[0] == XcpCommand.Download);
        Check(download is { Length: 10 } && download[1] == 8 &&
              BinaryPrimitives.ReadDoubleLittleEndian(download.AsSpan(2)) == 5.0,
            "eng download: DOWNLOAD carries the inverted raw value (8 B, 5.0)");
    }

    /// <summary>Reads the SET_DAQ_LIST_MODE packets of the shared transaction: mode byte of the
    /// DAQ list (0) and of the STIM list (1).</summary>
    private static (byte? DaqMode, byte? StimMode) ReadListModes(SimulatedStimSlave slave)
    {
        var modes = slave.AllSent(p => p[0] == XcpCommand.SetDaqListMode);
        var daq = modes.FirstOrDefault(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 0);
        var stim = modes.FirstOrDefault(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 1);
        return (daq?[1], stim?[1]);
    }

    /// <summary>QFW SDK shape (no TIMESTAMP_FIXED), default daqTimestamps=slave: the DAQ list
    /// requests timestamps, the STIM list runs untimestamped — DIRECTION only, and the master's
    /// STIM DTO is the 4-byte header followed directly by the data.</summary>
    private static async Task QfwShape_StimUntimestamped_DaqTimestamped()
    {
        var logger = new CapturingLogger();
        var (protocol, slave, stimVariable, daqVariable) = CreateRunningSetup(logger: logger);
        slave.TimestampMode = TimestampModeByListBit;
        stimVariable.SetValue(3.25);

        await protocol.StartAsync();
        var started = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(started, "qfw stim: configuration ran through to START_STOP_SYNCH");

        var (daqMode, stimMode) = ReadListModes(slave);
        Check(daqMode == XcpDaqListModeBits.Timestamp,
            "qfw stim: DAQ list requests slave timestamps (bit set, daqTimestamps=slave)");
        Check(stimMode == XcpDaqListModeBits.Direction,
            "qfw stim: STIM list carries DIRECTION only — no timestamp bit for a non-fixed slave");

        var landed = await WaitUntilAsync(() => slave.FindSent(p =>
            IsStimDto(p) && p.Length == 4 + 8 &&
            BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(4)) == 3.25) != null);
        Check(landed, "qfw stim: STIM DTO is header + data, the value starts right after the 4-byte header");
        Check(slave.FindSent(p => IsStimDto(p) && p.Length != 4 + 8) == null,
            "qfw stim: no STIM DTO carries a timestamp");

        // The DAQ direction keeps its timestamp: a slave DTO with the DWORD timestamp lands.
        var dto = new byte[4 + 4 + 8];
        dto[0] = 0x00;
        dto[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(dto.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dto.AsSpan(4), 1000);
        BinaryPrimitives.WriteDoubleLittleEndian(dto.AsSpan(8), 42.0);
        await slave.SendToMasterAsync(dto);
        var daqLanded = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 42.0);
        await protocol.StopAsync();

        Check(daqLanded, "qfw stim: the timestamped DAQ DTO still decodes into its variable");
        Check(logger.Has(LogLevel.Info, "DAQ started") && logger.Has(LogLevel.Info, "slave timestamps on"),
            "qfw stim: 'DAQ started … slave timestamps on' is logged at INFO");
        Check(logger.Has(LogLevel.Info, "STIM started"),
            "qfw stim: 'STIM started' is logged at INFO");
    }

    /// <summary>QFW SDK shape with daqTimestamps=master: neither list carries the timestamp bit,
    /// so no timestamp travels in either direction — the STIM DTO and the DAQ DTO are both
    /// header + data.</summary>
    private static async Task QfwShape_MasterTimestamps_NoTimestampsAtAll()
    {
        var (protocol, slave, stimVariable, daqVariable) = CreateRunningSetup("requestTimeoutMs=\"100\";daqTimestamps=\"master\"");
        slave.TimestampMode = TimestampModeByListBit;
        stimVariable.SetValue(3.25);

        await protocol.StartAsync();
        var started = await WaitUntilAsync(() => slave.CountSent(XcpCommand.StartStopSynch) >= 1);
        Check(started, "qfw master: configuration ran through to START_STOP_SYNCH");

        var (daqMode, stimMode) = ReadListModes(slave);
        Check(daqMode == 0x00, "qfw master: DAQ list has the timestamp bit clear (daqTimestamps=master)");
        Check(stimMode == XcpDaqListModeBits.Direction, "qfw master: STIM list carries DIRECTION only");

        var streamed = await WaitUntilAsync(() => slave.FindSent(p =>
            IsStimDto(p) && p.Length == 4 + 8 &&
            BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(4)) == 3.25) != null);
        Check(streamed, "qfw master: STIM DTO is header + data");

        var dto = new byte[4 + 8];
        dto[0] = 0x00;
        dto[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(dto.AsSpan(2), 0);
        BinaryPrimitives.WriteDoubleLittleEndian(dto.AsSpan(4), 42.0);
        await slave.SendToMasterAsync(dto);
        var daqLanded = await WaitUntilAsync(() => (double)daqVariable.GetValue() == 42.0);
        await protocol.StopAsync();

        Check(daqLanded, "qfw master: the untimestamped DAQ DTO decodes into its variable");
    }
}
