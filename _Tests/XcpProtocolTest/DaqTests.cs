using System.Buffers.Binary;
using Qenex.QSuite.Protocols.XcpCore;
using XcpTcpSessionSettings = Qenex.QSuite.Protocols.XcpTcpProtocol.XcpTcpSessionSettings;
using Qenex.QSuite.Variables.VariableEvents;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>DAQ phase 4 (decisions D1–D6): eventExtraParams parsing, DAQ command codec, the DTO
/// decoder and the master's configuration sequence against a scripted slave.</summary>
internal static class DaqTests
{
    internal static void Run()
    {
        ExtraParams_Validation();
        Codec_DaqCommands_ByteExact();
        Codec_DaqResponses_Parsed();
        Decoder_OdtFillDaqWord_WithTimestamp();
        Decoder_DropsAndOverload();
        Decoder_AbsolutePid();
        Mapper_SlaveSpacingWrapAndFallbacks();
        Settings_DaqTimestamps();
        Packer_FirstFitAndOdt0Filler();
        Master_ConfigureAndStartDaq_Sequence().GetAwaiter().GetResult();
        Master_GetDaqEventInfo_UploadsName().GetAwaiter().GetResult();
    }

    #region ODT packer (P4)

    /// <summary>Classic CAN geometry: MAX_DTO 8, 2 B identification field, 4 B timestamp in
    /// ODT 0 — 2 B free beside the timestamp, 6 B in every other ODT.</summary>
    private static void Packer_FirstFitAndOdt0Filler()
    {
        static XcpOdtPacker.Item Item(byte size, uint address) => new(size, 0, address);
        static int[] Ids(IReadOnlyList<XcpOdtPacker.Slot> odt) => odt.Select(s => s.ItemIndex).ToArray();

        // Three 4 B variables, slave timestamps on: none fits ODT 0 -> filler + one ODT each.
        var can = XcpOdtPacker.Pack([Item(4, 0x100), Item(4, 0x200), Item(4, 0x300)],
            maxDto: 8, headerSize: 2, timestampSizeOdt0: 4, maxEntrySize: 6, maxOdts: 0xFC, allowFiller: true);
        Check(can.Rejected.Count == 0, "packer CAN: 4 B variables are not rejected (P4)");
        Check(can.Odts.Count == 4, $"packer CAN: filler ODT 0 + 3 ODTs (got {can.Odts.Count})");
        Check(can.Odt0HasFiller && can.Odts[0] is [{ IsFiller: true, Entry: { Size: 2, Address: 0x100 } }],
            "packer CAN: ODT 0 carries a 2 B filler aliasing the first entry");
        Check(Ids(can.Odts[1]).SequenceEqual([0]) && Ids(can.Odts[2]).SequenceEqual([1]) && Ids(can.Odts[3]).SequenceEqual([2]),
            "packer CAN: one 4 B entry per ODT 1..3 in configuration order");

        // A 1 B variable configured after the 4 B ones lands in ODT 0 (first-fit), no filler.
        var small = XcpOdtPacker.Pack([Item(4, 0x100), Item(4, 0x200), Item(1, 0x300)],
            maxDto: 8, headerSize: 2, timestampSizeOdt0: 4, maxEntrySize: 6, maxOdts: 0xFC, allowFiller: true);
        Check(!small.Odt0HasFiller && Ids(small.Odts[0]).SequenceEqual([2]),
            "packer CAN: a later 1 B variable fills ODT 0 instead of a filler");
        Check(small.Odts.Count == 3 && Ids(small.Odts[1]).SequenceEqual([0]) && Ids(small.Odts[2]).SequenceEqual([1]),
            "packer CAN: first-fit keeps the 4 B entries in ODT 1 and 2");

        // Master timestamps (none in the stream): 6 B per ODT, greedy fill.
        var plain = XcpOdtPacker.Pack([Item(4, 0x100), Item(2, 0x200), Item(4, 0x300), Item(2, 0x400)],
            maxDto: 8, headerSize: 2, timestampSizeOdt0: 0, maxEntrySize: 6, maxOdts: 0xFC, allowFiller: true);
        Check(plain.Odts.Count == 2 && Ids(plain.Odts[0]).SequenceEqual([0, 1]) && Ids(plain.Odts[1]).SequenceEqual([2, 3]),
            "packer CAN/master ts: 4+2 B pairs fill 6 B ODTs");

        // STIM never gets a filler: a timestamped STIM list whose ODT 0 stays empty is not built.
        var stim = XcpOdtPacker.Pack([Item(4, 0x100), Item(4, 0x200)],
            maxDto: 8, headerSize: 2, timestampSizeOdt0: 4, maxEntrySize: 6, maxOdts: 0xC0, allowFiller: false);
        Check(stim.Odt0Unfillable && stim.Odts.Count == 0, "packer STIM: no filler, list reported unfillable");

        // Oversized entries are rejected, the rest still packs; the Ethernet geometry is unchanged.
        var eth = XcpOdtPacker.Pack([Item(4, 0x100), Item(8, 0x200), Item(200, 0x300), Item(2, 0x400)],
            maxDto: 248, headerSize: 4, timestampSizeOdt0: 4, maxEntrySize: 100, maxOdts: 0xFC, allowFiller: true);
        Check(eth.Rejected is [(2, XcpOdtPacker.RejectReason.TooLarge)], "packer ETH: 200 B entry > MAX_ODT_ENTRY_SIZE rejected");
        Check(eth.Odts.Count == 1 && Ids(eth.Odts[0]).SequenceEqual([0, 1, 3]) && !eth.Odt0HasFiller,
            "packer ETH: the remaining entries share ODT 0 beside the timestamp");

        // ODT limit: 8 B DTOs, 4 B entries, at most 2 ODTs -> the third entry has no ODT left.
        var limit = XcpOdtPacker.Pack([Item(4, 0x100), Item(4, 0x200), Item(4, 0x300)],
            maxDto: 8, headerSize: 2, timestampSizeOdt0: 0, maxEntrySize: 6, maxOdts: 2, allowFiller: true);
        Check(limit.Rejected is [(2, XcpOdtPacker.RejectReason.NoOdtLeft)] && limit.Odts.Count == 2,
            "packer: entries beyond the addressable ODTs are rejected with NoOdtLeft");
    }

    #endregion

    #region eventExtraParams

    private static void ExtraParams_Validation()
    {
        Check(Channel("") == null, "extraParams: empty string -> ordinary event");
        Check(Channel("   ") == null, "extraParams: whitespace -> ordinary event");
        Check(Channel("direction=\"DAQ\";daqId=\"3\"") == 3, "extraParams: DAQ + daqId parsed");
        Check(Binding("direction=\"DAQ\";daqId=\"3\"") is { IsStim: false }, "extraParams: DAQ direction is not STIM");
        Check(Binding("direction=\"STIM\";daqId=\"5\"") is { Channel: 5, IsStim: true },
            "extraParams: STIM + daqId parsed (S2)");
        Check(Channel("DAQID=\"7\";DIRECTION=\"daq\"") == 7, "extraParams: keys and value are case-insensitive");
        Check(Binding("direction=\"stim\";daqId=\"1\"") is { IsStim: true }, "extraParams: STIM value is case-insensitive");
        Check(Channel("foo=\"1\"") == null, "extraParams: unknown key alone -> warning only, still an ordinary event");

        CheckThrows<ArgumentException>(() => Channel("direction=\"DAQ\""),
            "extraParams: direction without daqId is rejected");
        CheckThrows<ArgumentException>(() => Channel("direction=\"STIM\""),
            "extraParams: STIM direction without daqId is rejected");
        CheckThrows<ArgumentException>(() => Channel("daqId=\"3\""),
            "extraParams: daqId without direction is rejected");
        CheckThrows<ArgumentException>(() => Channel("direction=\"BYPASS\";daqId=\"3\""),
            "extraParams: unsupported direction is rejected");
        CheckThrows<ArgumentException>(() => Channel("direction=\"DAQ\";daqId=\"70000\""),
            "extraParams: daqId out of 0..65535 is rejected");
        CheckThrows<ArgumentException>(() => Channel("DAQ"),
            "extraParams: a segment without key=value form is rejected");
    }

    private static XcpEventBinding? Binding(string extraParams)
    {
        return XcpEventExtraParams.GetEventBinding(
            new PeriodicVarEvent { Name = "e", EventExtraParams = extraParams });
    }

    private static ushort? Channel(string extraParams)
    {
        return Binding(extraParams)?.Channel;
    }

    #endregion

    #region Codec

    private static void Codec_DaqCommands_ByteExact()
    {
        var codec = new XcpCodec(); // little-endian

        Check(XcpCodec.BuildFreeDaq().SequenceEqual(new byte[] { 0xD6 }), "codec: FREE_DAQ");
        Check(codec.BuildAllocDaq(2).SequenceEqual(new byte[] { 0xD5, 0x00, 0x02, 0x00 }), "codec: ALLOC_DAQ");
        Check(codec.BuildAllocOdt(1, 3).SequenceEqual(new byte[] { 0xD4, 0x00, 0x01, 0x00, 0x03 }), "codec: ALLOC_ODT");
        Check(codec.BuildAllocOdtEntry(1, 2, 4).SequenceEqual(new byte[] { 0xD3, 0x00, 0x01, 0x00, 0x02, 0x04 }),
            "codec: ALLOC_ODT_ENTRY");
        Check(codec.BuildSetDaqPtr(1, 2, 0).SequenceEqual(new byte[] { 0xE2, 0x00, 0x01, 0x00, 0x02, 0x00 }),
            "codec: SET_DAQ_PTR");
        Check(codec.BuildWriteDaq(8, 1, 0x12345678).SequenceEqual(new byte[] { 0xE1, 0xFF, 0x08, 0x01, 0x78, 0x56, 0x34, 0x12 }),
            "codec: WRITE_DAQ (bit offset 0xFF = whole element)");
        Check(codec.BuildSetDaqListMode(0x10, 1, 2, 1, 0).SequenceEqual(new byte[] { 0xE0, 0x10, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00 }),
            "codec: SET_DAQ_LIST_MODE");
        Check(codec.BuildStartStopDaqList(XcpDaqStartStopMode.Select, 1).SequenceEqual(new byte[] { 0xDE, 0x02, 0x01, 0x00 }),
            "codec: START_STOP_DAQ_LIST");
        Check(XcpCodec.BuildStartStopSynch(XcpDaqSynchMode.StartSelected).SequenceEqual(new byte[] { 0xDD, 0x01 }),
            "codec: START_STOP_SYNCH");
        Check(codec.BuildGetDaqEventInfo(3).SequenceEqual(new byte[] { 0xD7, 0x00, 0x03, 0x00 }),
            "codec: GET_DAQ_EVENT_INFO");

        var bigEndian = new XcpCodec { IsBigEndian = true };
        Check(bigEndian.BuildAllocDaq(2).SequenceEqual(new byte[] { 0xD5, 0x00, 0x00, 0x02 }),
            "codec: WORD fields follow the slave's byte order (Motorola)");
    }

    private static void Codec_DaqResponses_Parsed()
    {
        var codec = new XcpCodec();

        // Dynamic lists, timestamps, overload via PID MSB, ODT+FIL+DAQW identification, ext per DAQ.
        var processor = codec.ParseDaqProcessorInfoResponse([0xFF, 0x51, 0x00, 0x00, 0x02, 0x00, 0x00, 0xF0]);
        Check(processor is
              {
                  HasDynamicLists: true, SupportsTimestamps: true, OverloadIndicationByPid: true,
                  MaxDaq: 0, MaxEventChannel: 2, DtoHeaderSize: 4,
                  IdentificationType: XcpDaqIdentificationType.OdtWithFillAndDaqWord
              },
            "codec: GET_DAQ_PROCESSOR_INFO parsed (properties, key byte, header size)");

        // Fixed 4-byte timestamps, unit 1 ms, 1 tick; max ODT entry 8 bytes.
        var resolution = codec.ParseDaqResolutionInfoResponse([0xFF, 0x01, 0x08, 0x01, 0x08, 0x6C, 0x01, 0x00]);
        Check(resolution is { MaxOdtEntrySizeDaq: 8, TimestampSize: 4, TimestampFixed: true, TimestampTicks: 1 },
            "codec: GET_DAQ_RESOLUTION_INFO parsed (timestamp mode decomposed)");
        Check(Math.Abs(resolution.TimestampTickSeconds - 1e-3) < 1e-12,
            "codec: tick duration = TICKS x UNIT (1 tick of 1 ms)");

        // QFW SDK shape (0.10.0+): same size and unit, bit 3 clear = the SET_DAQ_LIST_MODE
        // timestamp bit decides whether ODT 0 carries a timestamp.
        Check(codec.ParseDaqResolutionInfoResponse([0xFF, 0x01, 0x08, 0x01, 0x08, 0x64, 0x01, 0x00])
                  is { TimestampSize: 4, TimestampFixed: false },
            "codec: GET_DAQ_RESOLUTION_INFO without TIMESTAMP_FIXED = timestamps by list mode bit");

        // XCPlite on a 1 us clock reports unit 1 ns with 1000 ticks -> 1 us per tick (TICKS x UNIT,
        // never UNIT / TICKS - the inverse reading froze the DAQ time axis on live hardware).
        var xcplite = codec.ParseDaqResolutionInfoResponse([0xFF, 0x01, 0xF8, 0x01, 0xF8, 0x0C, 0xE8, 0x03]);
        Check(Math.Abs(xcplite.TimestampTickSeconds - 1e-6) < 1e-15,
            "codec: XCPlite-style resolution (1 ns unit, 1000 ticks) = 1 us per tick");

        var eventInfo = XcpCodec.ParseDaqEventInfoResponse([0xFF, 0x84, 0xFF, 0x04, 0x01, 0x06, 0x00]);
        Check(eventInfo is { SupportsDaq: true, NameLength: 4, CycleTimeMs: 1.0 },
            "codec: GET_DAQ_EVENT_INFO parsed (cycle 1×10^6 ns = 1 ms)");

        var sporadic = XcpCodec.ParseDaqEventInfoResponse([0xFF, 0x04, 0xFF, 0x00, 0x00, 0x00, 0x00]);
        Check(sporadic.CycleTimeMs == null, "codec: TIME_CYCLE 0 -> sporadic event (no nominal cycle)");

        Check(XcpCodec.ParseStartStopDaqListResponse([0xFF, 0x05]) == 5, "codec: FIRST_PID extracted");
        Check(XcpCodec.ParseStartStopDaqListResponse([0xFF]) == 0, "codec: missing FIRST_PID tolerated as 0");
    }

    #endregion

    #region DTO decoder

    private static readonly List<XcpDaqListPlan> TwoOdtPlan =
    [
        new XcpDaqListPlan(1, [
            new List<XcpDaqEntryPlan> { new(4, 0, 0x1000), new(2, 0, 0x2000) }, // ODT 0
            new List<XcpDaqEntryPlan> { new(8, 0, 0x3000) }                     // ODT 1
        ])
    ];

    private static void Decoder_OdtFillDaqWord_WithTimestamp()
    {
        var decoder = new XcpDaqDecoder(XcpDaqIdentificationType.OdtWithFillAndDaqWord, isBigEndian: false,
            timestampSizeOdt0: 4, overloadIndicationByPid: true, TwoOdtPlan);
        var entries = new List<XcpDaqDecoder.DecodedEntry>();

        // ODT 0: header(4) + timestamp(4) + 4B + 2B.
        byte[] odt0 = [0x00, 0xAA, 0x00, 0x00, 1, 2, 3, 4, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        Check(decoder.TryDecode(odt0, entries, out var ts0), "decoder: ODT 0 with timestamp decodes");
        Check(entries is [{ OdtIndex: 0, EntryIndex: 0, DataOffset: 8, Size: 4 }, { EntryIndex: 1, DataOffset: 12, Size: 2 }],
            "decoder: ODT 0 entries sit after the extracted timestamp");
        Check(ts0 == 0x04030201, "decoder: ODT 0 timestamp extracted (little-endian raw ticks)");

        // ODT 1: header(4) + 8B, no timestamp outside ODT 0.
        byte[] odt1 = [0x01, 0xAA, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];
        Check(decoder.TryDecode(odt1, entries, out var ts1), "decoder: ODT 1 decodes");
        Check(entries is [{ OdtIndex: 1, EntryIndex: 0, DataOffset: 4, Size: 8 }] && ts1 == null,
            "decoder: only ODT 0 carries the timestamp");
    }

    private static void Decoder_DropsAndOverload()
    {
        var decoder = new XcpDaqDecoder(XcpDaqIdentificationType.OdtWithFillAndDaqWord, isBigEndian: false,
            timestampSizeOdt0: 4, overloadIndicationByPid: true, TwoOdtPlan);
        var entries = new List<XcpDaqDecoder.DecodedEntry>();

        Check(!decoder.TryDecode([0x00, 0xAA], entries, out _), "decoder: packet shorter than the header is dropped");
        Check(!decoder.TryDecode([0x00, 0xAA, 0x05, 0x00, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6], entries, out _),
            "decoder: unknown DAQ list number is dropped");
        Check(!decoder.TryDecode([0x07, 0xAA, 0x00, 0x00, 1, 2, 3, 4], entries, out _),
            "decoder: unknown ODT number is dropped");
        Check(!decoder.TryDecode([0x00, 0xAA, 0x00, 0x00, 0, 0, 0, 0, 1, 2], entries, out _),
            "decoder: truncated data part is dropped");
        Check(!decoder.TryDecode([0x00, 0xAA, 0x00, 0x00, 1, 2], entries, out _),
            "decoder: packet shorter than its ODT 0 timestamp is dropped");
        Check(decoder.DroppedPackets == 5, "decoder: drops are counted");

        // Overload: MSB of the relative ODT byte set — packet still decodes as ODT 1.
        byte[] overloaded = [0x81, 0xAA, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];
        Check(decoder.TryDecode(overloaded, entries, out _) && entries is [{ OdtIndex: 1 }],
            "decoder: overload MSB is stripped and the packet still decodes");
    }

    private static void Decoder_AbsolutePid()
    {
        var decoder = new XcpDaqDecoder(XcpDaqIdentificationType.AbsolutePid, isBigEndian: false,
            timestampSizeOdt0: 0, overloadIndicationByPid: false, TwoOdtPlan);
        var entries = new List<XcpDaqDecoder.DecodedEntry>();

        Check(!decoder.TryDecode([0x10, 1, 2, 3, 4, 0x11, 0x22], entries, out _),
            "decoder: absolute PID without FIRST_PID knowledge is dropped");

        decoder.SetFirstPids([0x10]);
        Check(decoder.TryDecode([0x11, 1, 2, 3, 4, 5, 6, 7, 8], entries, out _) &&
              entries is [{ ListIndex: 0, OdtIndex: 1, DataOffset: 1, Size: 8 }],
            "decoder: absolute PID maps through FIRST_PID to list and relative ODT");
        Check(!decoder.TryDecode([0x30, 1, 2], entries, out _), "decoder: PID outside every list range is dropped");
    }

    #endregion

    #region Timestamp mapper (4b/4c)

    private static void Mapper_SlaveSpacingWrapAndFallbacks()
    {
        var t0 = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var mapper = new XcpDaqTimestampMapper(timestampSize: 4, tickSeconds: 1e-6, listCount: 2);

        // Anchor: the first timestamped sample takes its receive time; from then on the
        // spacing follows the slave clock even when TCP delivers in bursts.
        Check(mapper.Map(0, 1_000_000, t0) == t0, "mapper: first sample anchors to the receive time");
        var second = mapper.Map(0, 1_010_000, t0.AddMilliseconds(48));
        Check(CloseTo(second, t0.AddMilliseconds(10)), "mapper: spacing follows the slave clock, not the arrival burst");

        // ODTs 1..n have no timestamp: they reuse the cycle time of their list; a list that has
        // not seen any ODT 0 yet falls back to the receive time.
        Check(mapper.Map(0, null, t0.AddMilliseconds(49)) == second, "mapper: timestamp-less ODT reuses the cycle time");
        Check(mapper.Map(1, null, t0.AddMilliseconds(50)) == t0.AddMilliseconds(50),
            "mapper: a list without any ODT 0 yet uses the receive time");

        // Counter overflow: a smaller raw value continues the time axis (32-bit unwrap).
        var wrapMapper = new XcpDaqTimestampMapper(timestampSize: 4, tickSeconds: 1e-6, listCount: 1);
        Check(wrapMapper.Map(0, uint.MaxValue - 4_999, t0) == t0, "mapper: pre-wrap sample anchors");
        var afterWrap = wrapMapper.Map(0, 5_000, t0.AddMilliseconds(12));
        Check(CloseTo(afterWrap, t0.AddMilliseconds(10)), "mapper: 32-bit counter wrap is unwrapped");

        // Slave restart: the counter jump maps far away from the receive time -> re-anchor.
        var restarted = mapper.Map(0, 500, t0.AddSeconds(5));
        Check(restarted == t0.AddSeconds(5), "mapper: slave restart re-anchors to the receive time");

        Mapper_DriftIsCompensatedMonotonically();
    }

    // A slave clock running percent-fast (wrong clock source, e.g. an RC-derived HSE) must not
    // produce backward time steps: the drift is slewed away and the axis stays monotonic and
    // close to the receive time (regression for the 2026-08-10 graph-wipe incident).
    private static void Mapper_DriftIsCompensatedMonotonically()
    {
        var t0 = new DateTime(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc);
        var mapper = new XcpDaqTimestampMapper(timestampSize: 4, tickSeconds: 1e-6, listCount: 1);

        // 100 Hz for 10 simulated minutes; the slave believes in exact 10 ms cycles while
        // 1.7 % less wall time passes (measured live on the Nucleo board).
        var previous = DateTime.MinValue;
        var mapped = DateTime.MinValue;
        double maxErrorSeconds = 0;
        for (var i = 0; i < 60_000; i++)
        {
            var raw = (uint)(1_000_000 + (ulong)i * 10_000);
            var received = t0.AddSeconds(i * 0.010 / 1.017);
            mapped = mapper.Map(0, raw, received);
            Check(mapped >= previous, "mapper: drifting slave clock never moves the axis backwards");
            previous = mapped;
            maxErrorSeconds = Math.Max(maxErrorSeconds, Math.Abs((mapped - received).TotalSeconds));
        }

        Check(maxErrorSeconds < 0.5, "mapper: compensated axis stays close to the receive time");

        // Slave restart while the emitted axis is ahead of the receive time: the hard
        // re-anchor must not step backwards either (the monotonic guard clamps it).
        var restartReceive = t0.AddSeconds(60_000 * 0.010 / 1.017 + 0.005);
        var afterRestart = mapper.Map(0, 100, restartReceive);
        Check(afterRestart >= mapped, "mapper: hard re-anchor is clamped to stay monotonic");
    }

    private static bool CloseTo(DateTime actual, DateTime expected)
    {
        return Math.Abs((actual - expected).TotalMilliseconds) < 0.001;
    }

    private static void Settings_DaqTimestamps()
    {
        Check(XcpTcpSessionSettings.Parse("timeoutMs=1000").UseSlaveDaqTimestamps,
            "settings: daqTimestamps defaults to slave");
        Check(XcpTcpSessionSettings.Parse("daqTimestamps=\"SLAVE\"").UseSlaveDaqTimestamps,
            "settings: daqTimestamps=slave parsed (case-insensitive)");
        Check(!XcpTcpSessionSettings.Parse("daqTimestamps=master").UseSlaveDaqTimestamps,
            "settings: daqTimestamps=master parsed");
        CheckThrows<ArgumentException>(() => XcpTcpSessionSettings.Parse("daqTimestamps=ecu"),
            "settings: unknown daqTimestamps value is rejected");
    }

    #endregion

    #region Master DAQ sequence

    /// <summary>Scripted slave for the master-level DAQ tests (same pattern as MasterTests).</summary>
    private sealed class FakeDaqSlave
    {
        public readonly List<byte[]> Sent = [];
        public readonly XcpMaster Master = new();
        public Func<byte[], byte[]?>? Responder;

        public FakeDaqSlave()
        {
            Master.TimeoutMs = 100;
            Master.Transmitter = (packet, _) =>
            {
                lock (Sent)
                {
                    Sent.Add(packet);
                }

                var response = Responder?.Invoke(packet);
                if (response != null)
                {
                    Master.OnPacketReceived(response);
                }

                return Task.CompletedTask;
            };
        }

        public static byte[]? DefaultResponder(byte[] command) => command[0] switch
        {
            XcpCommand.Connect => [0xFF, 0x05, 0x00, 0xFA, 0xFF, 0x05, 0x01, 0x01],
            XcpCommand.GetStatus => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00],
            XcpCommand.FreeDaq or XcpCommand.AllocDaq or XcpCommand.AllocOdt or XcpCommand.AllocOdtEntry
                or XcpCommand.SetDaqPtr or XcpCommand.WriteDaq or XcpCommand.SetDaqListMode
                or XcpCommand.StartStopSynch => [0xFF],
            XcpCommand.StartStopDaqList => [0xFF, 0x05],
            _ => null
        };
    }

    private static async Task Master_ConfigureAndStartDaq_Sequence()
    {
        var slave = new FakeDaqSlave { Responder = FakeDaqSlave.DefaultResponder };
        await slave.Master.ConnectAsync();
        slave.Sent.Clear();

        List<XcpDaqListPlan> plans =
        [
            new XcpDaqListPlan(7, [
                new List<XcpDaqEntryPlan> { new(4, 0, 0x1000), new(2, 1, 0x2000) },
                new List<XcpDaqEntryPlan> { new(8, 0, 0x3000) }
            ])
        ];

        var firstPids = await slave.Master.ConfigureAndStartDaqAsync(plans, includeTimestamp: true);

        Check(firstPids is [0x05], "daq sequence: FIRST_PID collected from START_STOP_DAQ_LIST");

        var pids = slave.Sent.Select(p => p[0]).ToArray();
        byte[] expected =
        [
            XcpCommand.FreeDaq, XcpCommand.AllocDaq, XcpCommand.AllocOdt,
            XcpCommand.AllocOdtEntry, XcpCommand.AllocOdtEntry,
            XcpCommand.SetDaqPtr, XcpCommand.WriteDaq, XcpCommand.WriteDaq,
            XcpCommand.SetDaqPtr, XcpCommand.WriteDaq,
            XcpCommand.SetDaqListMode, XcpCommand.StartStopDaqList, XcpCommand.StartStopSynch
        ];
        Check(pids.SequenceEqual(expected),
            $"daq sequence: exact command order on the wire (got {string.Join(",", pids.Select(p => p.ToString("X2")))})");

        Check(slave.Sent[1].SequenceEqual(new byte[] { 0xD5, 0x00, 0x01, 0x00 }), "daq sequence: ALLOC_DAQ for 1 list");
        Check(slave.Sent[2].SequenceEqual(new byte[] { 0xD4, 0x00, 0x00, 0x00, 0x02 }), "daq sequence: ALLOC_ODT 2 ODTs");
        Check(slave.Sent[3].SequenceEqual(new byte[] { 0xD3, 0x00, 0x00, 0x00, 0x00, 0x02 }),
            "daq sequence: ALLOC_ODT_ENTRY ODT0 = 2 entries");
        Check(slave.Sent[6].SequenceEqual(new byte[] { 0xE1, 0xFF, 0x04, 0x00, 0x00, 0x10, 0x00, 0x00 }),
            "daq sequence: WRITE_DAQ carries size/ext/address");
        Check(slave.Sent[10].SequenceEqual(new byte[] { 0xE0, 0x10, 0x00, 0x00, 0x07, 0x00, 0x01, 0x00 }),
            "daq sequence: SET_DAQ_LIST_MODE with timestamp bit, channel 7, prescaler 1");
        Check(slave.Sent[11].SequenceEqual(new byte[] { 0xDE, 0x02, 0x00, 0x00 }),
            "daq sequence: START_STOP_DAQ_LIST mode=select");
        Check(slave.Sent[12].SequenceEqual(new byte[] { 0xDD, 0x01 }),
            "daq sequence: START_STOP_SYNCH starts the selected lists");
    }

    private static async Task Master_GetDaqEventInfo_UploadsName()
    {
        var slave = new FakeDaqSlave();
        slave.Responder = cmd => cmd[0] switch
        {
            XcpCommand.GetDaqEventInfo => [0xFF, 0x04, 0xFF, 0x04, 0x01, 0x06, 0x00],
            XcpCommand.Upload => [0xFF, (byte)'l', (byte)'o', (byte)'o', (byte)'p'],
            _ => FakeDaqSlave.DefaultResponder(cmd)
        };
        await slave.Master.ConnectAsync();
        slave.Sent.Clear();

        var (info, name) = await slave.Master.GetDaqEventInfoAsync(1);

        Check(name == "loop", "daq event info: channel name uploaded via the MTA left by the command");
        Check(info.CycleTimeMs == 1.0, "daq event info: nominal cycle decoded");
        Check(slave.Sent.Count == 2 && slave.Sent[0].SequenceEqual(new byte[] { 0xD7, 0x00, 0x01, 0x00 }) &&
              slave.Sent[1].SequenceEqual(new byte[] { 0xF5, 0x04 }),
            "daq event info: GET_DAQ_EVENT_INFO followed by UPLOAD(nameLength)");
    }

    #endregion
}
