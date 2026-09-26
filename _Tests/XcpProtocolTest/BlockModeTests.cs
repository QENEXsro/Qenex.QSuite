using System.Buffers.Binary;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Protocols.XcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>
/// Block mode (Pepa/Protocols/XCP/BlockMode.md) and matrix transfers: codec, master against an
/// in-process memory-backed slave with slave/master block mode, and the specification rules for
/// matrix variables.
/// </summary>
internal static class BlockModeTests
{
    internal static void Run()
    {
        Codec_BlockModeCommands_AreByteExact();
        Codec_CommModeInfo_Parses();
        Codec_ConnectFlags_Parse();
        Connect_OptionalBit_QueriesCommModeInfo().GetAwaiter().GetResult();
        Connect_CmdUnknown_NoMasterBlockMode().GetAwaiter().GetResult();
        Read_SlaveBlockMode_Can_43Bytes_OneBurst().GetAwaiter().GetResult();
        Read_SlaveBlockMode_Can_300Bytes_TwoBursts().GetAwaiter().GetResult();
        Read_SlaveBlockMode_Tcp_300Bytes().GetAwaiter().GetResult();
        Read_NoBlockMode_20Bytes_Chained().GetAwaiter().GetResult();
        Read_Scalar_StillSingleShortUpload().GetAwaiter().GetResult();
        Read_IncompleteBurst_TimesOutAfterRetries().GetAwaiter().GetResult();
        Write_MasterBlockMode_Can_20Bytes_MaxBs4().GetAwaiter().GetResult();
        Write_MasterBlockMode_Can_20Bytes_MaxBs2_TwoBlocks().GetAwaiter().GetResult();
        Write_MasterBlockMode_Tcp_300Bytes().GetAwaiter().GetResult();
        Write_MasterBlockMode_MinSt_SpacesPackets().GetAwaiter().GetResult();
        Write_SequenceError_ReissuesBlock().GetAwaiter().GetResult();
        Write_NoBlockMode_20Bytes_Chained().GetAwaiter().GetResult();
        Spec_Matrix_SizeAndUndefinedType();
        Spec_Matrix_TooLarge_Throws();
        Spec_Matrix_DaqEvent_Throws();
        Spec_Matrix_InvalidLayout_Throws();
    }

    #region Harness: memory-backed slave with block mode

    /// <summary>In-process XCP slave over the raw packet interface of XcpMaster: 4 KB of memory,
    /// SET_MTA / SHORT_UPLOAD / UPLOAD (slave block mode bursts) / DOWNLOAD + DOWNLOAD_NEXT (master
    /// block mode with sequence check), CONNECT/GET_COMM_MODE_INFO advertising both modes.</summary>
    private sealed class BlockSlave
    {
        public readonly XcpMaster Master;
        public readonly List<byte[]> Sent = [];
        public readonly List<long> SentAt = []; // Stopwatch ticks
        public readonly byte[] Memory = new byte[4096];

        public byte MaxCto = 8;
        public bool SlaveBlockMode = true;
        public bool MasterBlockMode = true;
        public byte MaxBs = 4;
        public byte MinSt;
        public bool AnswerCommModeInfo = true;
        public bool SwallowUploadBursts;
        public bool DropBurstTail; // answer only the first RES packet of a burst
        public int RejectDownloadNextOnce = -1; // 1-based index of the DOWNLOAD_NEXT packet (overall) to reject with ERR_SEQUENCE once
        public int SequenceErrorsSent;

        private uint mta;
        private int blockRemaining;
        private bool blockAborted;
        private int downloadNextSeen;

        public BlockSlave(int timeoutMs = 100, CapturingLogger? logger = null)
        {
            Master = new XcpMaster(logger) { TimeoutMs = timeoutMs };
            Master.Transmitter = (packet, ct) =>
            {
                lock (Sent)
                {
                    Sent.Add(packet);
                    SentAt.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                }

                foreach (var response in Respond(packet))
                {
                    Master.OnPacketReceived(response);
                }

                return Task.CompletedTask;
            };
        }

        public async Task<BlockSlave> ConnectedAsync()
        {
            await Master.ConnectAsync();
            lock (Sent)
            {
                Sent.Clear();
                SentAt.Clear();
            }

            return this;
        }

        public int CountSent(byte pid)
        {
            lock (Sent)
            {
                return Sent.Count(p => p[0] == pid);
            }
        }

        public void Fill(int address, int length, byte seed = 1)
        {
            for (var i = 0; i < length; i++)
            {
                Memory[address + i] = (byte)(seed + i);
            }
        }

        private int ReadDataPerPacket => MaxCto - 1;
        private int WriteDataPerPacket => MaxCto - 2;

        private IEnumerable<byte[]> Respond(byte[] cmd)
        {
            switch (cmd[0])
            {
                case XcpCommand.Connect:
                    var basic = (byte)(0x80 | (SlaveBlockMode ? 0x40 : 0x00));
                    return [[0xFF, 0x05, basic, MaxCto, MaxCto, 0x00, 0x01, 0x01]];

                case XcpCommand.GetCommModeInfo:
                    return AnswerCommModeInfo
                        ? [[0xFF, 0x00, (byte)(MasterBlockMode ? 0x01 : 0x00), 0x00, MaxBs, MinSt, 0x08, 0x10]]
                        : [[0xFE, XcpErrorCode.CmdUnknown]];

                case XcpCommand.GetStatus:
                    return [[0xFF, 0x00, 0x00, 0x00, 0x00, 0x00]];

                case XcpCommand.Disconnect:
                    return [[0xFF]];

                case XcpCommand.Synch:
                    blockRemaining = 0;
                    blockAborted = false;
                    return [[0xFE, XcpErrorCode.CmdSynch]];

                case XcpCommand.SetMta:
                    mta = BinaryPrimitives.ReadUInt32LittleEndian(cmd.AsSpan(4, 4));
                    blockRemaining = 0;
                    blockAborted = false;
                    return [[0xFF]];

                case XcpCommand.ShortUpload:
                    mta = BinaryPrimitives.ReadUInt32LittleEndian(cmd.AsSpan(4, 4));
                    return [Upload(cmd[1])];

                case XcpCommand.Upload:
                {
                    int count = cmd[1];
                    if (SwallowUploadBursts)
                    {
                        return [];
                    }

                    if (!SlaveBlockMode || count <= ReadDataPerPacket)
                    {
                        return [Upload(count)];
                    }

                    var burst = new List<byte[]>();
                    while (count > 0)
                    {
                        var chunk = Math.Min(count, ReadDataPerPacket);
                        burst.Add(Upload(chunk));
                        count -= chunk;
                        if (DropBurstTail)
                        {
                            break;
                        }
                    }

                    return burst;
                }

                case XcpCommand.Download:
                {
                    int count = cmd[1];
                    var dataLength = cmd.Length - 2;
                    if (!MasterBlockMode)
                    {
                        Store(cmd.AsSpan(2, dataLength));
                        return [[0xFF]];
                    }

                    if (dataLength > count || dataLength > WriteDataPerPacket)
                    {
                        return [[0xFE, XcpErrorCode.CmdSyntax]];
                    }

                    Store(cmd.AsSpan(2, dataLength));
                    blockRemaining = count - dataLength;
                    blockAborted = false;
                    return blockRemaining == 0 ? [[0xFF]] : [];
                }

                case XcpCommand.DownloadNext:
                {
                    if (blockAborted)
                    {
                        return []; // contract: after ERR_SEQUENCE the rest of the block is ignored silently
                    }

                    int remaining = cmd[1];
                    var dataLength = cmd.Length - 2;
                    downloadNextSeen++;
                    if (remaining != blockRemaining || blockRemaining == 0 || downloadNextSeen == RejectDownloadNextOnce)
                    {
                        RejectDownloadNextOnce = -1;
                        SequenceErrorsSent++;
                        var expected = (byte)blockRemaining;
                        blockRemaining = 0;
                        blockAborted = true;
                        return [[0xFE, XcpErrorCode.Sequence, expected]];
                    }

                    Store(cmd.AsSpan(2, dataLength));
                    blockRemaining -= dataLength;
                    return blockRemaining == 0 ? [[0xFF]] : [];
                }

                default:
                    return [[0xFE, XcpErrorCode.CmdUnknown]];
            }
        }

        private byte[] Upload(int count)
        {
            var response = new byte[1 + count];
            response[0] = 0xFF;
            Memory.AsSpan((int)mta, count).CopyTo(response.AsSpan(1));
            mta += (uint)count;
            return response;
        }

        private void Store(ReadOnlySpan<byte> data)
        {
            data.CopyTo(Memory.AsSpan((int)mta));
            mta += (uint)data.Length;
        }
    }

    private static byte[] Pattern(int length, byte seed = 100)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return data;
    }

    #endregion

    #region Codec

    private static void Codec_BlockModeCommands_AreByteExact()
    {
        Check(XcpCodec.BuildGetCommModeInfo().SequenceEqual(new byte[] { 0xFB }), "GET_COMM_MODE_INFO = FB");
        Check(XcpCodec.BuildDownloadBlockStart(20, [1, 2, 3, 4, 5, 6]).SequenceEqual(new byte[] { 0xF0, 20, 1, 2, 3, 4, 5, 6 }),
            "DOWNLOAD block start = F0 | block length | data");
        Check(XcpCodec.BuildDownloadNext(14, [7, 8, 9, 10, 11, 12]).SequenceEqual(new byte[] { 0xEF, 14, 7, 8, 9, 10, 11, 12 }),
            "DOWNLOAD_NEXT = EF | remaining | data");
        CheckThrows<ArgumentOutOfRangeException>(() => XcpCodec.BuildDownloadNext(2, [1, 2, 3]),
            "DOWNLOAD_NEXT with more data than remaining is rejected");
        CheckThrows<ArgumentOutOfRangeException>(() => XcpCodec.BuildDownloadBlockStart(256, [1]),
            "DOWNLOAD block longer than 255 elements is rejected");
    }

    private static void Codec_CommModeInfo_Parses()
    {
        var info = XcpCodec.ParseCommModeInfoResponse([0xFF, 0x00, 0x03, 0x00, 0x10, 0x05, 0x08, 0x21]);
        Check(info.SupportsMasterBlockMode && info.SupportsInterleavedMode, "COMM_MODE_INFO: optional mode bits parsed");
        Check(info is { MaxBs: 0x10, MinSt: 0x05, QueueSize: 0x08, DriverVersion: 0x21 },
            "COMM_MODE_INFO: MAX_BS, MIN_ST, QUEUE_SIZE, driver version parsed");
        CheckThrows<XcpProtocolException>(() => XcpCodec.ParseCommModeInfoResponse([0xFF, 0x00, 0x01]),
            "COMM_MODE_INFO: short response is rejected");
    }

    private static void Codec_ConnectFlags_Parse()
    {
        var plain = XcpCodec.ParseConnectResponse([0xFF, 0x05, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01]);
        var block = XcpCodec.ParseConnectResponse([0xFF, 0x05, 0xC0, 0x08, 0x08, 0x00, 0x01, 0x01]);
        Check(!plain.SupportsSlaveBlockMode && !plain.HasOptionalCommModeInfo, "CONNECT: no flags without bits 6/7");
        Check(block.SupportsSlaveBlockMode && block.HasOptionalCommModeInfo && block.AddressGranularity == 1 && !block.IsBigEndian,
            "CONNECT: SLAVE_BLOCK_MODE + OPTIONAL parsed, AG/byte order untouched");
    }

    #endregion

    #region Connect

    private static async Task Connect_OptionalBit_QueriesCommModeInfo()
    {
        var logger = new CapturingLogger();
        var slave = new BlockSlave(logger: logger) { MaxBs = 6, MinSt = 3 };
        await slave.Master.ConnectAsync();

        Check(slave.Sent.Select(p => p[0]).SequenceEqual(new byte[] { XcpCommand.Connect, XcpCommand.GetCommModeInfo, XcpCommand.GetStatus }),
            "connect: CONNECT, GET_COMM_MODE_INFO, GET_STATUS in that order");
        Check(slave.Master.SlaveBlockModeAvailable && slave.Master.MasterBlockModeAvailable, "connect: both block modes available");
        Check(slave.Master.CommModeInfo is { MaxBs: 6, MinSt: 3 }, "connect: MAX_BS / MIN_ST stored");
        Check(logger.Has(LogLevel.Info, "master yes (MAX_BS 6, MIN_ST 3"), "connect: block mode logged once");

        await slave.Master.DisconnectAsync();
        Check(slave.Master.CommModeInfo == null && !slave.Master.MasterBlockModeAvailable, "disconnect: comm mode info cleared");
    }

    private static async Task Connect_CmdUnknown_NoMasterBlockMode()
    {
        var slave = new BlockSlave { AnswerCommModeInfo = false };
        await slave.Master.ConnectAsync();

        Check(slave.Master.IsConnected, "ERR_CMD_UNKNOWN on GET_COMM_MODE_INFO: still connected");
        Check(slave.Master.CommModeInfo == null && !slave.Master.MasterBlockModeAvailable, "ERR_CMD_UNKNOWN: no master block mode");
        Check(slave.Master.SlaveBlockModeAvailable, "ERR_CMD_UNKNOWN: slave block mode from CONNECT still available");
    }

    #endregion

    #region Reads

    private static async Task Read_SlaveBlockMode_Can_43Bytes_OneBurst()
    {
        var slave = await new BlockSlave().ConnectedAsync();
        slave.Fill(0x100, 43);

        var data = await slave.Master.ReadMemoryAsync(0, 0x100, 43);

        Check(data.SequenceEqual(slave.Memory.AsSpan(0x100, 43).ToArray()), "block read 43B: payload assembled from the burst");
        Check(slave.Sent.Count == 2 && slave.Sent[0][0] == XcpCommand.SetMta && slave.Sent[1].SequenceEqual(new byte[] { 0xF5, 43 }),
            "block read 43B: SET_MTA + one UPLOAD(43), no per-packet acknowledgement");
    }

    private static async Task Read_SlaveBlockMode_Can_300Bytes_TwoBursts()
    {
        var slave = await new BlockSlave().ConnectedAsync();
        slave.Fill(0x200, 300, seed: 7);

        var data = await slave.Master.ReadMemoryAsync(0, 0x200, 300);

        Check(data.SequenceEqual(slave.Memory.AsSpan(0x200, 300).ToArray()), "block read 300B: payload assembled from two bursts");
        Check(slave.Sent.Count == 3 && slave.Sent[1][1] == 255 && slave.Sent[2][1] == 45,
            "block read 300B: SET_MTA + UPLOAD(255) + UPLOAD(45) at the auto-incremented MTA");
    }

    private static async Task Read_SlaveBlockMode_Tcp_300Bytes()
    {
        var slave = await new BlockSlave { MaxCto = 0xFF }.ConnectedAsync();
        slave.Fill(0x300, 300, seed: 9);

        var data = await slave.Master.ReadMemoryAsync(0, 0x300, 300);

        Check(data.SequenceEqual(slave.Memory.AsSpan(0x300, 300).ToArray()), "TCP block read 300B: payload assembled (254 + 1, then 45)");
        Check(slave.Sent.Count == 3, "TCP block read 300B: SET_MTA + two UPLOADs");
    }

    private static async Task Read_NoBlockMode_20Bytes_Chained()
    {
        var slave = await new BlockSlave { SlaveBlockMode = false, MasterBlockMode = false }.ConnectedAsync();
        slave.Fill(0x400, 20, seed: 50);

        var data = await slave.Master.ReadMemoryAsync(0, 0x400, 20);

        Check(data.SequenceEqual(slave.Memory.AsSpan(0x400, 20).ToArray()), "chained read 20B: payload assembled");
        Check(slave.Sent.Count == 3 && slave.Sent[0][0] == XcpCommand.ShortUpload && slave.Sent[0][1] == 7 &&
              slave.Sent[1].SequenceEqual(new byte[] { 0xF5, 7 }) && slave.Sent[2].SequenceEqual(new byte[] { 0xF5, 6 }),
            "chained read 20B: SHORT_UPLOAD(7) + UPLOAD(7) + UPLOAD(6), every packet acknowledged");
    }

    private static async Task Read_Scalar_StillSingleShortUpload()
    {
        var slave = await new BlockSlave().ConnectedAsync();
        slave.Fill(0x500, 4, seed: 0xA0);

        var data = await slave.Master.ReadMemoryAsync(0, 0x500, 4);

        Check(data.SequenceEqual(new byte[] { 0xA0, 0xA1, 0xA2, 0xA3 }), "scalar read 4B with block-capable slave: payload");
        Check(slave.Sent.Count == 1 && slave.Sent[0][0] == XcpCommand.ShortUpload, "scalar read 4B: still a single SHORT_UPLOAD");
    }

    private static async Task Read_IncompleteBurst_TimesOutAfterRetries()
    {
        var slave = await new BlockSlave(timeoutMs: 60).ConnectedAsync();
        slave.Master.MaxRetries = 1;
        slave.DropBurstTail = true;
        slave.Fill(0x600, 20);

        try
        {
            await slave.Master.ReadMemoryAsync(0, 0x600, 20);
            Check(false, "incomplete burst: no exception thrown");
        }
        catch (XcpTimeoutException)
        {
            Check(true, "incomplete burst: times out with XcpTimeoutException");
        }

        Check(slave.CountSent(XcpCommand.Upload) == 2 && slave.CountSent(XcpCommand.Synch) == 1 && slave.CountSent(XcpCommand.SetMta) == 2,
            "incomplete burst: SYNCH + full transaction retry (SET_MTA again), then failure");
    }

    #endregion

    #region Writes

    private static async Task Write_MasterBlockMode_Can_20Bytes_MaxBs4()
    {
        var slave = await new BlockSlave { MaxBs = 4 }.ConnectedAsync();
        var data = Pattern(20);

        await slave.Master.WriteMemoryAsync(0, 0x100, data);

        Check(slave.Memory.AsSpan(0x100, 20).SequenceEqual(data), "block write 20B: memory matches");
        Check(slave.Sent.Count == 5, "block write 20B: SET_MTA + 4 packets in one block");
        Check(slave.Sent[1].Take(2).SequenceEqual(new byte[] { 0xF0, 20 }) && slave.Sent[1].Length == 8,
            "block write 20B: DOWNLOAD carries block length 20 and 6 data bytes");
        Check(slave.Sent[2].Take(2).SequenceEqual(new byte[] { 0xEF, 14 }) &&
              slave.Sent[3].Take(2).SequenceEqual(new byte[] { 0xEF, 8 }) &&
              slave.Sent[4].SequenceEqual(new byte[] { 0xEF, 2, data[18], data[19] }),
            "block write 20B: DOWNLOAD_NEXT remaining counts 14, 8, 2");
    }

    private static async Task Write_MasterBlockMode_Can_20Bytes_MaxBs2_TwoBlocks()
    {
        var slave = await new BlockSlave { MaxBs = 2 }.ConnectedAsync();
        var data = Pattern(20, seed: 30);

        await slave.Master.WriteMemoryAsync(0, 0x200, data);

        Check(slave.Memory.AsSpan(0x200, 20).SequenceEqual(data), "MAX_BS 2 write 20B: memory matches");
        Check(slave.Sent.Select(p => (p[0], p[1])).SequenceEqual([(XcpCommand.SetMta, (byte)0), (XcpCommand.Download, (byte)12),
                  (XcpCommand.DownloadNext, (byte)6), (XcpCommand.Download, (byte)8), (XcpCommand.DownloadNext, (byte)2)]),
            "MAX_BS 2 write 20B: two blocks of 12 + 8 bytes, MTA continues without SET_MTA");
    }

    private static async Task Write_MasterBlockMode_Tcp_300Bytes()
    {
        var slave = await new BlockSlave { MaxCto = 0xFF, MaxBs = 0xFF }.ConnectedAsync();
        var data = Pattern(300, seed: 3);

        await slave.Master.WriteMemoryAsync(0, 0x300, data);

        Check(slave.Memory.AsSpan(0x300, 300).SequenceEqual(data), "TCP block write 300B: memory matches");
        Check(slave.Sent.Select(p => (p[0], p[1], p.Length - 2)).SequenceEqual([(XcpCommand.SetMta, (byte)0, 6), (XcpCommand.Download, (byte)255, 253),
                  (XcpCommand.DownloadNext, (byte)2, 2), (XcpCommand.Download, (byte)45, 45)]),
            "TCP block write 300B: block of 255 (253 + 2) then block of 45");
    }

    private static async Task Write_MasterBlockMode_MinSt_SpacesPackets()
    {
        var slave = await new BlockSlave { MaxBs = 4, MinSt = 50 }.ConnectedAsync(); // 5 ms
        await slave.Master.WriteMemoryAsync(0, 0x100, Pattern(20));

        var gaps = new List<double>();
        for (var i = 2; i < slave.SentAt.Count; i++)
        {
            gaps.Add((slave.SentAt[i] - slave.SentAt[i - 1]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        Check(gaps.Count == 3 && gaps.All(g => g >= 4.5), $"MIN_ST 50 (5 ms): packets of the block are spaced (gaps {string.Join("/", gaps.Select(g => g.ToString("F1")))} ms)");
    }

    private static async Task Write_SequenceError_ReissuesBlock()
    {
        var logger = new CapturingLogger();
        var slave = await new BlockSlave(logger: logger) { MaxBs = 4, RejectDownloadNextOnce = 1 }.ConnectedAsync();
        var data = Pattern(20, seed: 60);

        await slave.Master.WriteMemoryAsync(0, 0x700, data);

        Check(slave.Memory.AsSpan(0x700, 20).SequenceEqual(data), "ERR_SEQUENCE: memory matches after the re-issued block");
        Check(slave.SequenceErrorsSent == 1 && slave.CountSent(XcpCommand.SetMta) == 2 && slave.CountSent(XcpCommand.Download) == 2,
            "ERR_SEQUENCE: block re-issued once from SET_MTA");
        Check(logger.Has(LogLevel.Warn, "ERR_SEQUENCE"), "ERR_SEQUENCE: warning logged");
    }

    private static async Task Write_NoBlockMode_20Bytes_Chained()
    {
        var slave = await new BlockSlave { SlaveBlockMode = false, MasterBlockMode = false }.ConnectedAsync();
        var data = Pattern(20, seed: 80);

        await slave.Master.WriteMemoryAsync(0, 0x800, data);

        Check(slave.Memory.AsSpan(0x800, 20).SequenceEqual(data), "chained write 20B: memory matches");
        Check(slave.Sent.Count == 5 && slave.Sent.Skip(1).All(p => p[0] == XcpCommand.Download) &&
              slave.Sent.Skip(1).Select(p => (int)p[1]).SequenceEqual([6, 6, 6, 2]),
            "chained write 20B: SET_MTA + DOWNLOAD(6) × 3 + DOWNLOAD(2), every packet acknowledged");
    }

    #endregion

    #region Specification

    private static readonly IVarEvent Poll100Ms = new PeriodicVarEvent { Name = "poll100ms", Period = 100, Unit = TimeUnit.Milisec };

    private static MatrixVariable Curve(int count = 8, ValueDataType type = ValueDataType.UShort) => new()
    {
        Id = 5, Namespace = "/", Name = "TorqueCurve", Label = "Torque curve",
        DefaultDataType = type,
        XAxis = new MatrixSection { Count = count },
        Data = new MatrixSection()
    };

    private static void Spec_Matrix_SizeAndUndefinedType()
    {
        var spec = XcpVariableSpecification.Create("address=\"0x2000\";direction=\"readWrite\";eventRef=\"poll100ms\"", [Poll100Ms], Curve());

        Check(spec.Size == 32 && spec.DataType == ValueDataType.Undefined, "matrix spec: size = whole raw block (8 + 8 ushort), raw type");
        Check(spec.Direction == CommDirection.ReadWrite && !spec.IsOnRequestEvent, "matrix spec: direction and event as configured");
        Check(spec.CommParams.Contains("address=\"0x2000\"") && !spec.CommParams.Contains("size"), "matrix spec: commParams round-trip without size");

        var withSize = XcpVariableSpecification.Create("address=\"0x2000\";size=\"32\";eventRef=\"poll100ms\"", [Poll100Ms], Curve());
        Check(withSize.Size == 32, "matrix spec: matching explicit size accepted");
        CheckThrows<ArgumentException>(() => XcpVariableSpecification.Create("address=\"0x2000\";size=\"16\";eventRef=\"poll100ms\"", [Poll100Ms], Curve()),
            "matrix spec: explicit size not matching the layout is rejected");
    }

    private static void Spec_Matrix_TooLarge_Throws()
    {
        var limit = new MatrixVariable
        {
            Id = 6, Namespace = "/", Name = "BigMap", DefaultDataType = ValueDataType.Float,
            XAxis = new MatrixSection { Count = 64 }, YAxis = new MatrixSection { Count = 64 }, Data = new MatrixSection()
        };
        var tooLarge = new MatrixVariable
        {
            Id = 7, Namespace = "/", Name = "HugeMap", DefaultDataType = ValueDataType.Float,
            XAxis = new MatrixSection { Count = 65 }, YAxis = new MatrixSection { Count = 64 }, Data = new MatrixSection()
        };

        var spec = XcpVariableSpecification.Create("address=\"0x3000\";eventRef=\"poll100ms\"", [Poll100Ms], limit);
        Check(spec.Size == 64 * 64 * 4 + 64 * 4 + 64 * 4, "matrix spec: 64×64 float map (16 KB of data + axes) is within the limit");
        CheckThrows<ArgumentException>(() => XcpVariableSpecification.Create("address=\"0x3000\";eventRef=\"poll100ms\"", [Poll100Ms], tooLarge),
            "matrix spec: map larger than 64 × 64 × 4 B data is rejected");
    }

    private static void Spec_Matrix_DaqEvent_Throws()
    {
        var daqEvent = new PeriodicVarEvent { Name = "daq10ms", Period = 10, Unit = TimeUnit.Milisec, EventExtraParams = "direction=\"DAQ\";daqId=\"1\"" };
        CheckThrows<ArgumentException>(() => XcpVariableSpecification.Create("address=\"0x2000\";eventRef=\"daq10ms\"", [daqEvent], Curve()),
            "matrix spec: DAQ event channel is rejected for a matrix");
    }

    private static void Spec_Matrix_InvalidLayout_Throws()
    {
        var bad = new MatrixVariable { Id = 8, Namespace = "/", Name = "Bad", YAxis = new MatrixSection { Count = 4 }, Data = new MatrixSection() };
        CheckThrows<ArgumentException>(() => XcpVariableSpecification.Create("address=\"0x2000\";eventRef=\"poll100ms\"", [Poll100Ms], bad),
            "matrix spec: invalid layout (Y axis without X) is rejected");
    }

    #endregion
}

/// <summary>
/// Matrix variables driven through the CAN Xcp protocol against a simulated block-mode slave:
/// periodic block polling, On Request read, cell writes merged into contiguous windows, endianness
/// mismatch warning.
/// </summary>
internal static class MatrixIntegrationTests
{
    private const uint MasterId = 0x200;
    private const uint SlaveId = 0x201;

    internal static void Run()
    {
        Poll_MatrixBlock_LandsInVariable().GetAwaiter().GetResult();
        OnRequest_MatrixRead().GetAwaiter().GetResult();
        Write_EditedCells_MergedWindows().GetAwaiter().GetResult();
        EndiannessMismatch_Warns().GetAwaiter().GetResult();
    }

    /// <summary>CAN-framed block-mode slave: the same command set as BlockSlave, fed through the
    /// protocol's transmitter (8-byte padded frames) and receive queue.</summary>
    private sealed class SimulatedBlockSlave
    {
        private readonly Xcp protocol;
        public readonly List<byte[]> Sent = [];
        public readonly byte[] Memory = new byte[4096];
        public bool BigEndian;
        private uint mta;
        private int blockRemaining;
        private bool blockAborted;

        public SimulatedBlockSlave(Xcp xcpProtocol)
        {
            protocol = xcpProtocol;
            protocol.SetTransmitter(async (frame, ct) =>
            {
                lock (Sent)
                {
                    Sent.Add(frame.Data);
                }

                foreach (var response in Respond(frame.Data))
                {
                    await protocol.AddReceivedDataToQueueAsync([new CanFrame(SlaveId, response)], ct);
                }
            });
        }

        public int CountSent(byte pid)
        {
            lock (Sent)
            {
                return Sent.Count(p => p[0] == pid);
            }
        }

        private IEnumerable<byte[]> Respond(byte[] cmd)
        {
            switch (cmd[0])
            {
                case XcpCommand.Connect:
                    return [[0xFF, 0x05, (byte)(0xC0 | (BigEndian ? 0x01 : 0x00)), 0x08, 0x08, 0x00, 0x01, 0x01]];
                case XcpCommand.GetCommModeInfo:
                    return [[0xFF, 0x00, 0x01, 0x00, 0x08, 0x00, 0x08, 0x10]];
                case XcpCommand.GetStatus:
                    return [[0xFF, 0x00, 0x00, 0x00, 0x00, 0x00]];
                case XcpCommand.Disconnect:
                    return [[0xFF]];
                case XcpCommand.Synch:
                    blockRemaining = 0;
                    return [[0xFE, XcpErrorCode.CmdSynch]];
                case XcpCommand.SetMta:
                    mta = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(cmd.AsSpan(4, 4));
                    blockRemaining = 0;
                    blockAborted = false;
                    return [[0xFF]];
                case XcpCommand.ShortUpload:
                    mta = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(cmd.AsSpan(4, 4));
                    return [Upload(cmd[1])];
                case XcpCommand.Upload:
                {
                    int count = cmd[1];
                    var burst = new List<byte[]>();
                    while (count > 0)
                    {
                        var chunk = Math.Min(count, 7);
                        burst.Add(Upload(chunk));
                        count -= chunk;
                    }

                    return burst;
                }
                case XcpCommand.Download:
                {
                    int count = cmd[1];
                    var dataLength = Math.Min(count, 6); // frames are padded to DLC 8
                    Store(cmd.AsSpan(2, dataLength));
                    blockRemaining = count - dataLength;
                    blockAborted = false;
                    return blockRemaining == 0 ? [[0xFF]] : [];
                }
                case XcpCommand.DownloadNext:
                {
                    if (blockAborted)
                    {
                        return [];
                    }

                    int remaining = cmd[1];
                    if (remaining != blockRemaining || remaining == 0)
                    {
                        blockAborted = true;
                        var expected = (byte)blockRemaining;
                        blockRemaining = 0;
                        return [[0xFE, XcpErrorCode.Sequence, expected]];
                    }

                    var dataLength = Math.Min(remaining, 6);
                    Store(cmd.AsSpan(2, dataLength));
                    blockRemaining -= dataLength;
                    return blockRemaining == 0 ? [[0xFF]] : [];
                }
                default:
                    return [[0xFE, XcpErrorCode.CmdUnknown]];
            }
        }

        private byte[] Upload(int count)
        {
            var response = new byte[1 + count];
            response[0] = 0xFF;
            Memory.AsSpan((int)mta, count).CopyTo(response.AsSpan(1));
            mta += (uint)count;
            return response;
        }

        private void Store(ReadOnlySpan<byte> data)
        {
            data.CopyTo(Memory.AsSpan((int)mta));
            mta += (uint)data.Length;
        }
    }

    private static MatrixVariable Curve(string name, int id, MatrixEndianness endianness = MatrixEndianness.Little) => new()
    {
        Id = id, Namespace = "/", Name = name, Label = name,
        DefaultDataType = ValueDataType.UShort,
        Endianness = endianness,
        XAxis = new MatrixSection { Count = 8 },
        Data = new MatrixSection()
    };

    private static (Xcp Protocol, SimulatedBlockSlave Slave, MatrixVariable Polled, MatrixVariable OnRequest, CapturingLogger Logger) CreateSetup(
        MatrixEndianness endianness = MatrixEndianness.Little)
    {
        var logger = new CapturingLogger();
        var protocol = new Xcp
        {
            IsEnabled = true,
            Logger = logger,
            RawSettings = $"masterId=\"0x{MasterId:X}\";slaveId=\"0x{SlaveId:X}\";requestTimeoutMs=\"100\""
        };
        protocol.SetConfiguration();

        IVarEvent[] events = [new PeriodicVarEvent { Name = "poll20ms", Period = 20, Unit = TimeUnit.Milisec }, new OnRequestVarEvent { Name = "onRequest" }];
        var polled = Curve("TorqueCurve", 1, endianness);
        var onRequest = Curve("BoostCurve", 2, endianness);
        protocol.AddVariable(protocol.CreateProtocolVariable(polled, events, "address=\"0x100\";direction=\"readWrite\";eventRef=\"poll20ms\"", true)!);
        protocol.AddVariable(protocol.CreateProtocolVariable(onRequest, events, "address=\"0x200\";direction=\"readWrite\";eventRef=\"onRequest\"", true)!);

        var slave = new SimulatedBlockSlave(protocol);
        // 8 axis + 8 data ushort, little-endian: axis i = 1000 + i, data i = 2000 + i
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(slave.Memory.AsSpan(0x100 + i * 2), (ushort)(1000 + i));
            BinaryPrimitives.WriteUInt16LittleEndian(slave.Memory.AsSpan(0x100 + 16 + i * 2), (ushort)(2000 + i));
            BinaryPrimitives.WriteUInt16LittleEndian(slave.Memory.AsSpan(0x200 + i * 2), (ushort)(3000 + i));
            BinaryPrimitives.WriteUInt16LittleEndian(slave.Memory.AsSpan(0x200 + 16 + i * 2), (ushort)(4000 + i));
        }

        return (protocol, slave, polled, onRequest, logger);
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

    /// <summary>What the QInsight host does for an edited cell: store the value, queue its bytes.</summary>
    private static void EditCell(MatrixVariable matrix, MatrixSectionKind kind, int index, double engValue)
    {
        Check(matrix.TrySetEngValue(kind, index, engValue), $"edit cell {kind}[{index}]: value stored");
        var elementSize = matrix.GetElementSize(kind);
        var byteOffset = matrix.GetSectionOffset(kind) + index * elementSize;
        matrix.EnqueuePendingWrite(new MatrixWriteRequest(byteOffset, matrix.RawData.AsSpan(byteOffset, elementSize).ToArray()));
    }

    private static async Task Poll_MatrixBlock_LandsInVariable()
    {
        var (protocol, slave, polled, onRequest, logger) = CreateSetup();

        await protocol.StartAsync();
        var landed = await WaitUntilAsync(() => polled.GetEngValue(MatrixSectionKind.Data, 7) == 2007);
        await Task.Delay(60);
        await protocol.StopAsync();

        Check(landed, "matrix poll: the whole block (axes + data) landed in the variable");
        Check(polled.GetEngValue(MatrixSectionKind.XAxis, 3) == 1003 && polled.GetEngValue(MatrixSectionKind.Data, 0) == 2000,
            "matrix poll: axis and data cells decoded by the variable's layout");
        Check(onRequest.GetEngValue(MatrixSectionKind.Data, 0) == 0, "matrix poll: On Request matrix is not polled");
        Check(slave.CountSent(XcpCommand.Upload) >= 2 && slave.CountSent(XcpCommand.ShortUpload) == 0,
            "matrix poll: block transfers (SET_MTA + UPLOAD bursts), no SHORT_UPLOAD");
        Check(!logger.HasAny(LogLevel.Warn), "matrix poll: no warnings");
    }

    private static async Task OnRequest_MatrixRead()
    {
        var (protocol, slave, _, onRequest, _) = CreateSetup();
        var protocolVariable = protocol.Variables.Single(v => v.Variable == onRequest);
        var notified = 0;
        protocolVariable.SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref notified); return Task.CompletedTask; });

        await protocol.StartAsync();
        await WaitUntilAsync(() => protocol.State == CommunicationState.Running);
        Check(protocol.CanReadVariable(protocolVariable), "matrix on request: readable on request");

        await protocol.ReadVariableAsync(protocolVariable);
        await protocol.StopAsync();

        Check(onRequest.GetEngValue(MatrixSectionKind.XAxis, 7) == 3007 && onRequest.GetEngValue(MatrixSectionKind.Data, 5) == 4005,
            "matrix on request: block read landed (axes + data)");
        Check(notified == 1, "matrix on request: exactly one notification per read");
        var setMtaTo0x200 = slave.Sent.Count(p => p[0] == XcpCommand.SetMta && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4, 4)) == 0x200);
        Check(setMtaTo0x200 == 1, "matrix on request: exactly one block transfer of the On Request matrix (the polled one keeps polling)");
    }

    private static async Task Write_EditedCells_MergedWindows()
    {
        var (protocol, slave, polled, _, logger) = CreateSetup();
        var protocolVariable = protocol.Variables.Single(v => v.Variable == polled);

        await protocol.StartAsync();
        await WaitUntilAsync(() => polled.GetEngValue(MatrixSectionKind.Data, 7) == 2007);

        // Two adjacent data cells + one separate axis cell → two windows.
        EditCell(polled, MatrixSectionKind.Data, 2, 777);
        EditCell(polled, MatrixSectionKind.Data, 3, 888);
        EditCell(polled, MatrixSectionKind.XAxis, 6, 66);
        lock (slave.Sent)
        {
            slave.Sent.Clear();
        }

        await protocol.WriteVariableAsync(protocolVariable);
        await protocol.StopAsync();

        Check(BinaryPrimitives.ReadUInt16LittleEndian(slave.Memory.AsSpan(0x100 + 16 + 4)) == 777 &&
              BinaryPrimitives.ReadUInt16LittleEndian(slave.Memory.AsSpan(0x100 + 16 + 6)) == 888 &&
              BinaryPrimitives.ReadUInt16LittleEndian(slave.Memory.AsSpan(0x100 + 12)) == 66,
            "matrix write: edited cells arrived in the slave memory");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(slave.Memory.AsSpan(0x100 + 16 + 2)) == 2001,
            "matrix write: untouched neighbour cell not rewritten");
        var downloads = slave.Sent.Where(p => p[0] == XcpCommand.Download).ToList();
        Check(slave.CountSent(XcpCommand.SetMta) == 2 && downloads.Count == 2 && downloads.Any(p => p[1] == 4) && downloads.Any(p => p[1] == 2),
            "matrix write: two windows — 4 B (merged adjacent cells) and 2 B — each SET_MTA + one DOWNLOAD");
        Check(slave.CountSent(XcpCommand.DownloadNext) == 0, "matrix write: small windows need no DOWNLOAD_NEXT");
        Check(logger.Has(LogLevel.Info, "wrote 4 B of 'TorqueCurve'"), "matrix write: window logged");
    }

    private static async Task EndiannessMismatch_Warns()
    {
        var (protocol, _, _, _, logger) = CreateSetup(MatrixEndianness.Big);

        await protocol.StartAsync();
        var running = await WaitUntilAsync(() => protocol.State == CommunicationState.Running);
        await protocol.StopAsync();

        Check(running, "endianness mismatch: session still runs");
        Check(logger.Has(LogLevel.Warn, "configured big-endian but the slave is little-endian"),
            "endianness mismatch: warning names both sides");
    }
}
