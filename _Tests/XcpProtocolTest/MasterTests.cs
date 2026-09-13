using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.XcpCore;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

internal static class MasterTests
{
    internal static void Run()
    {
        Connect_HappyPath().GetAwaiter().GetResult();
        Connect_BigEndianSlave().GetAwaiter().GetResult();
        Connect_UnsupportedAddressGranularity_Throws().GetAwaiter().GetResult();
        Connect_ProtectedCalibration_DisablesWrites().GetAwaiter().GetResult();
        Read_SingleFrame().GetAwaiter().GetResult();
        Read_EightBytes_ChainsShortUploadAndUpload().GetAwaiter().GetResult();
        LargeCto_EightBytes_SinglePacketTransfers().GetAwaiter().GetResult();
        Write_SingleFrame().GetAwaiter().GetResult();
        Write_EightBytes_ChainsDownloads().GetAwaiter().GetResult();
        Write_WhenProtected_ThrowsWithoutTransmit().GetAwaiter().GetResult();
        ErrorResponse_BecomesXcpErrorException().GetAwaiter().GetResult();
        Timeout_SynchsAndRetries_WholeTransaction().GetAwaiter().GetResult();
        Timeout_RetriesExhausted_Throws().GetAwaiter().GetResult();
        EvCmdPending_RestartsTimeout_WithoutResend().GetAwaiter().GetResult();
        UnsolicitedAndDaqPackets_AreIgnored().GetAwaiter().GetResult();
        ConcurrentReads_AreSerialized().GetAwaiter().GetResult();
        Cancellation_ReleasesTheEngine().GetAwaiter().GetResult();
        LateResponse_AfterCancelledCommand_IsConsumedInOrder().GetAwaiter().GetResult();
        Timeout_DoesNotArmLateResponseConsumption().GetAwaiter().GetResult();
    }

    #region Harness

    /// <summary>In-process scripted slave: captures transmitted packets, answers via a pluggable
    /// responder (null = swallow the command, i.e. provoke a timeout).</summary>
    private sealed class FakeSlave
    {
        public readonly List<byte[]> Sent = [];
        public readonly XcpMaster Master;
        public Func<byte[], byte[]?>? Responder;
        public int ResponseDelayMs;

        public FakeSlave(int timeoutMs = 100, CapturingLogger? logger = null)
        {
            Master = new XcpMaster(logger);
            Master.TimeoutMs = timeoutMs;
            Master.Transmitter = (packet, ct) =>
            {
                lock (Sent)
                {
                    Sent.Add(packet);
                }

                var response = Responder?.Invoke(packet);
                if (response != null)
                {
                    if (ResponseDelayMs <= 0)
                    {
                        Master.OnPacketReceived(response);
                    }
                    else
                    {
                        var delay = ResponseDelayMs;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(delay);
                            Master.OnPacketReceived(response);
                        });
                    }
                }

                return Task.CompletedTask;
            };
        }

        public int CountSent(byte pid)
        {
            lock (Sent)
            {
                return Sent.Count(p => p[0] == pid);
            }
        }

        /// <summary>Standard little-endian AG=1 slave with CAL+DAQ resources, nothing protected.</summary>
        public static byte[]? DefaultResponder(byte[] command) => command[0] switch
        {
            XcpCommand.Connect => [0xFF, 0x05, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01],
            XcpCommand.GetStatus => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00],
            XcpCommand.Disconnect => [0xFF],
            XcpCommand.Synch => [0xFE, XcpErrorCode.CmdSynch],
            XcpCommand.SetMta => [0xFF],
            XcpCommand.Download => [0xFF],
            XcpCommand.ShortUpload => [0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77],
            XcpCommand.Upload => [0xFF, 0x88],
            _ => null
        };

        public async Task<FakeSlave> ConnectedAsync()
        {
            var original = Responder;
            Responder = DefaultResponder;
            await Master.ConnectAsync();
            Responder = original ?? DefaultResponder;
            return this;
        }
    }

    #endregion

    private static async Task Connect_HappyPath()
    {
        var slave = new FakeSlave { Responder = FakeSlave.DefaultResponder };

        var status = await slave.Master.ConnectAsync();

        Check(slave.Master.IsConnected, "connect: master is connected");
        Check(!slave.Master.Codec.IsBigEndian, "connect: little-endian adopted from COMM_MODE_BASIC");
        Check(slave.Master.ConnectInfo is { MaxCto: 8, MaxDto: 8 }, "connect: MAX_CTO/MAX_DTO parsed");
        Check(slave.Master.WritesAllowed, "connect: unprotected CAL resource -> writes allowed");
        Check(!status.IsCalibrationProtected, "connect: GET_STATUS protection parsed");
        Check(slave.Sent.Count == 2 && slave.Sent[0][0] == 0xFF && slave.Sent[1][0] == 0xFD,
            "connect: exactly CONNECT then GET_STATUS on the wire");
    }

    private static async Task Connect_BigEndianSlave()
    {
        var slave = new FakeSlave
        {
            Responder = cmd => cmd[0] switch
            {
                // COMM_MODE_BASIC bit0 set -> Motorola; MAX_DTO 0x0008 as 00 08.
                XcpCommand.Connect => [0xFF, 0x01, 0x01, 0x08, 0x00, 0x08, 0x01, 0x01],
                XcpCommand.GetStatus => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00],
                _ => null
            }
        };

        await slave.Master.ConnectAsync();

        Check(slave.Master.Codec.IsBigEndian, "big-endian connect: codec switched to Motorola");
        Check(slave.Master.ConnectInfo?.MaxDto == 8, "big-endian connect: MAX_DTO read big-endian");
    }

    private static async Task Connect_UnsupportedAddressGranularity_Throws()
    {
        var slave = new FakeSlave
        {
            Responder = cmd => cmd[0] switch
            {
                XcpCommand.Connect => [0xFF, 0x01, 0x02, 0x08, 0x08, 0x00, 0x01, 0x01], // AG=WORD
                _ => null
            }
        };

        try
        {
            await slave.Master.ConnectAsync();
            Check(false, "AG=WORD slave is rejected (no exception thrown)");
        }
        catch (XcpProtocolException e)
        {
            Check(e.Message.Contains("GRANULARITY", StringComparison.OrdinalIgnoreCase),
                "AG=WORD slave is rejected with a clear message");
        }
    }

    private static async Task Connect_ProtectedCalibration_DisablesWrites()
    {
        var slave = new FakeSlave
        {
            Responder = cmd => cmd[0] switch
            {
                XcpCommand.Connect => [0xFF, 0x05, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01],
                XcpCommand.GetStatus => [0xFF, 0x00, 0x01, 0x00, 0x00, 0x00], // CAL protection bit
                _ => null
            }
        };

        var status = await slave.Master.ConnectAsync();

        Check(slave.Master.IsConnected, "protected slave: still connected (reads work)");
        Check(status.IsCalibrationProtected, "protected slave: protection reported");
        Check(!slave.Master.WritesAllowed, "protected slave: writes disabled (seed & key required)");
    }

    private static async Task Read_SingleFrame()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        slave.Responder = cmd => cmd[0] == XcpCommand.ShortUpload
            ? [0xFF, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00]
            : FakeSlave.DefaultResponder(cmd);
        slave.Sent.Clear();

        var data = await slave.Master.ReadMemoryAsync(2, 0x1000, 4);

        Check(data.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), "read 4B: payload extracted from RES");
        Check(slave.Sent.Count == 1 &&
              slave.Sent[0].SequenceEqual(new byte[] { 0xF4, 0x04, 0x00, 0x02, 0x00, 0x10, 0x00, 0x00 }),
            "read 4B: single byte-exact SHORT_UPLOAD");
    }

    private static async Task Read_EightBytes_ChainsShortUploadAndUpload()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        slave.Responder = cmd => cmd[0] switch
        {
            XcpCommand.ShortUpload => [0xFF, 1, 2, 3, 4, 5, 6, 7],
            XcpCommand.Upload => [0xFF, 8],
            _ => FakeSlave.DefaultResponder(cmd)
        };
        slave.Sent.Clear();

        var data = await slave.Master.ReadMemoryAsync(0, 0x2000, 8);

        Check(data.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "read 8B: chained payload assembled");
        Check(slave.Sent.Count == 2, "read 8B: exactly two packets");
        Check(slave.Sent[0][0] == XcpCommand.ShortUpload && slave.Sent[0][1] == 7,
            "read 8B: first packet SHORT_UPLOAD(7)");
        Check(slave.Sent[1].SequenceEqual(new byte[] { 0xF5, 0x01 }),
            "read 8B: second packet UPLOAD(1) at auto-incremented MTA");
    }

    private static async Task LargeCto_EightBytes_SinglePacketTransfers()
    {
        // XCP on Ethernet slave: MAX_CTO=250 — every supported value (max 8 bytes) fits one packet.
        var slave = new FakeSlave
        {
            Responder = cmd => cmd[0] switch
            {
                XcpCommand.Connect => [0xFF, 0x05, 0x00, 0xFA, 0xFF, 0x05, 0x01, 0x01],
                XcpCommand.ShortUpload => [0xFF, 1, 2, 3, 4, 5, 6, 7, 8],
                _ => FakeSlave.DefaultResponder(cmd)
            }
        };
        await slave.Master.ConnectAsync();
        slave.Sent.Clear();

        var data = await slave.Master.ReadMemoryAsync(0, 0x2000, 8);

        Check(data.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "large CTO read 8B: payload extracted");
        Check(slave.Sent.Count == 1 && slave.Sent[0][0] == XcpCommand.ShortUpload && slave.Sent[0][1] == 8,
            "large CTO read 8B: single SHORT_UPLOAD(8), no chaining");

        slave.Sent.Clear();
        await slave.Master.WriteMemoryAsync(0, 0x3000, [1, 2, 3, 4, 5, 6, 7, 8]);

        Check(slave.Sent.Count == 2, "large CTO write 8B: SET_MTA + one DOWNLOAD");
        Check(slave.Sent[1].SequenceEqual(new byte[] { 0xF0, 0x08, 1, 2, 3, 4, 5, 6, 7, 8 }),
            "large CTO write 8B: single DOWNLOAD carries all bytes");
    }

    private static async Task Write_SingleFrame()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        slave.Sent.Clear();

        await slave.Master.WriteMemoryAsync(1, 0xAA55, [0x11, 0x22, 0x33, 0x44]);

        Check(slave.Sent.Count == 2, "write 4B: SET_MTA + one DOWNLOAD");
        Check(slave.Sent[0].SequenceEqual(new byte[] { 0xF6, 0x00, 0x00, 0x01, 0x55, 0xAA, 0x00, 0x00 }),
            "write 4B: byte-exact SET_MTA");
        Check(slave.Sent[1].SequenceEqual(new byte[] { 0xF0, 0x04, 0x11, 0x22, 0x33, 0x44 }),
            "write 4B: byte-exact DOWNLOAD");
    }

    private static async Task Write_EightBytes_ChainsDownloads()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        slave.Sent.Clear();

        await slave.Master.WriteMemoryAsync(0, 0x3000, [1, 2, 3, 4, 5, 6, 7, 8]);

        Check(slave.Sent.Count == 3, "write 8B: SET_MTA + DOWNLOAD(6) + DOWNLOAD(2)");
        Check(slave.Sent[1].SequenceEqual(new byte[] { 0xF0, 0x06, 1, 2, 3, 4, 5, 6 }),
            "write 8B: first DOWNLOAD carries 6 bytes");
        Check(slave.Sent[2].SequenceEqual(new byte[] { 0xF0, 0x02, 7, 8 }),
            "write 8B: second DOWNLOAD carries the remaining 2 bytes");
    }

    private static async Task Write_WhenProtected_ThrowsWithoutTransmit()
    {
        var slave = new FakeSlave
        {
            Responder = cmd => cmd[0] switch
            {
                XcpCommand.Connect => [0xFF, 0x05, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01],
                XcpCommand.GetStatus => [0xFF, 0x00, 0x01, 0x00, 0x00, 0x00],
                _ => null
            }
        };
        await slave.Master.ConnectAsync();
        slave.Sent.Clear();

        try
        {
            await slave.Master.WriteMemoryAsync(0, 0x1000, [1]);
            Check(false, "write to protected slave is rejected (no exception)");
        }
        catch (XcpProtocolException e)
        {
            Check(e.Message.Contains("seed & key"), "write to protected slave mentions seed & key");
        }

        Check(slave.Sent.Count == 0, "write to protected slave transmits nothing");
    }

    private static async Task ErrorResponse_BecomesXcpErrorException()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        slave.Responder = cmd => cmd[0] == XcpCommand.ShortUpload
            ? [0xFE, XcpErrorCode.AccessDenied]
            : FakeSlave.DefaultResponder(cmd);

        try
        {
            await slave.Master.ReadMemoryAsync(0, 0x1000, 4);
            Check(false, "ERR response surfaces as exception (none thrown)");
        }
        catch (XcpErrorException e)
        {
            Check(e.ErrorCode == XcpErrorCode.AccessDenied, "ERR response carries the slave's error code");
            Check(e.Message.Contains("ERR_ACCESS_DENIED"), "ERR response message is human-readable");
        }
    }

    private static async Task Timeout_SynchsAndRetries_WholeTransaction()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        var setMtaSeen = 0;
        slave.Responder = cmd => cmd[0] switch
        {
            // First SET_MTA is swallowed -> timeout -> SYNCH -> whole transaction retried.
            XcpCommand.SetMta => ++setMtaSeen == 1 ? null : [0xFF],
            _ => FakeSlave.DefaultResponder(cmd)
        };
        slave.Sent.Clear();

        await slave.Master.WriteMemoryAsync(0, 0x4000, [0xAB, 0xCD]);

        Check(setMtaSeen == 2, "timeout recovery: SET_MTA re-issued on retry");
        Check(slave.CountSent(XcpCommand.Synch) == 1, "timeout recovery: SYNCH sent once");
        Check(slave.CountSent(XcpCommand.Download) == 1, "timeout recovery: DOWNLOAD only after successful SET_MTA");
    }

    private static async Task Timeout_RetriesExhausted_Throws()
    {
        var slave = await new FakeSlave().ConnectedAsync();
        slave.Responder = cmd => cmd[0] == XcpCommand.Synch ? [0xFE, XcpErrorCode.CmdSynch] : null;
        slave.Sent.Clear();

        try
        {
            await slave.Master.ReadMemoryAsync(0, 0x1000, 4);
            Check(false, "exhausted retries throw (no exception)");
        }
        catch (XcpTimeoutException e)
        {
            Check(e.Command == "SHORT_UPLOAD", "exhausted retries: timeout names the command");
        }

        Check(slave.CountSent(XcpCommand.ShortUpload) == 3, "exhausted retries: initial attempt + 2 retries");
        Check(slave.CountSent(XcpCommand.Synch) == 2, "exhausted retries: SYNCH before each retry");
    }

    private static async Task EvCmdPending_RestartsTimeout_WithoutResend()
    {
        var slave = await new FakeSlave(timeoutMs: 200).ConnectedAsync();
        slave.Master.MaxRetries = 0; // a genuine timeout must fail immediately
        slave.Responder = cmd => cmd[0] == XcpCommand.ShortUpload ? null : FakeSlave.DefaultResponder(cmd);
        slave.Sent.Clear();

        var readTask = slave.Master.ReadMemoryAsync(0, 0x1000, 1);
        await Task.Delay(100);
        slave.Master.OnPacketReceived([0xFD, XcpEventCode.CmdPending]); // restarts the window to ~300 ms
        await Task.Delay(150);
        slave.Master.OnPacketReceived([0xFF, 0x42, 0, 0, 0, 0, 0, 0]); // ~250 ms — after the original window

        var data = await readTask;

        Check(data.SequenceEqual(new byte[] { 0x42 }), "EV_CMD_PENDING: response after restarted timeout accepted");
        Check(slave.CountSent(XcpCommand.ShortUpload) == 1, "EV_CMD_PENDING: command was not repeated");
    }

    private static async Task UnsolicitedAndDaqPackets_AreIgnored()
    {
        var slave = await new FakeSlave().ConnectedAsync();

        // No pending command — none of these may throw or corrupt the engine.
        slave.Master.OnPacketReceived([0xFF, 0x01]);
        slave.Master.OnPacketReceived([0xFE, 0x10]);
        slave.Master.OnPacketReceived([0xFC, 0x01, 0x41]);
        slave.Master.OnPacketReceived([0x03, 0x01, 0x02]);
        slave.Master.OnPacketReceived([]);

        var data = await slave.Master.ReadMemoryAsync(0, 0x1000, 1);
        Check(data.Length == 1, "unsolicited packets: engine still fully functional afterwards");
    }

    private static async Task ConcurrentReads_AreSerialized()
    {
        var slave = await new FakeSlave(timeoutMs: 1000).ConnectedAsync();
        var inFlight = 0;
        var overlapped = false;
        slave.ResponseDelayMs = 20;
        slave.Responder = cmd =>
        {
            if (Interlocked.Increment(ref inFlight) > 1)
            {
                overlapped = true;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(15);
                Interlocked.Decrement(ref inFlight);
            });
            return cmd[0] == XcpCommand.ShortUpload ? [0xFF, 0x01, 0, 0, 0, 0, 0, 0] : FakeSlave.DefaultResponder(cmd);
        };
        slave.Sent.Clear();

        var reads = Enumerable.Range(0, 5).Select(_ => slave.Master.ReadMemoryAsync(0, 0x1000, 1));
        await Task.WhenAll(reads);

        Check(!overlapped, "concurrent reads: strictly one command in flight (1 command -> 1 response)");
        Check(slave.CountSent(XcpCommand.ShortUpload) == 5, "concurrent reads: all five executed");
    }

    private static async Task Cancellation_ReleasesTheEngine()
    {
        var slave = await new FakeSlave(timeoutMs: 5000).ConnectedAsync();
        slave.Responder = cmd => cmd[0] == XcpCommand.ShortUpload ? null : FakeSlave.DefaultResponder(cmd);

        using var cts = new CancellationTokenSource(50);
        try
        {
            await slave.Master.ReadMemoryAsync(0, 0x1000, 1, cts.Token);
            Check(false, "cancellation: read should have been cancelled");
        }
        catch (OperationCanceledException)
        {
            Check(true, "cancellation: pending read cancelled");
        }

        // The request lock must be free again — a normal read completes.
        slave.Responder = FakeSlave.DefaultResponder;
        var data = await slave.Master.ReadMemoryAsync(0, 0x1000, 1);
        Check(data.Length == 1, "cancellation: engine usable after cancellation");
    }

    /// <summary>Stop scenario: a read is cancelled while its response is still on the way, then
    /// DISCONNECT follows immediately. The late read response must be consumed silently (order
    /// on the wire), the DISCONNECT response must complete DISCONNECT, and the next CONNECT
    /// starts clean.</summary>
    private static async Task LateResponse_AfterCancelledCommand_IsConsumedInOrder()
    {
        var log = new CapturingLogger();
        var slave = await new FakeSlave(timeoutMs: 5000, logger: log).ConnectedAsync();
        slave.ResponseDelayMs = 150; // every response is late; delivery order = transmission order

        using var cts = new CancellationTokenSource(30);
        try
        {
            await slave.Master.ReadMemoryAsync(0, 0x1000, 1, cts.Token);
            Check(false, "late response: read should have been cancelled");
        }
        catch (OperationCanceledException)
        {
        }

        await slave.Master.DisconnectAsync();
        Check(!slave.Master.IsConnected, "late response: DISCONNECT completed with its own response");
        Check(log.Has(LogLevel.Debug, "late Response to a cancelled command consumed"), "late response: consumed at Debug level");
        Check(!log.Has(LogLevel.Warn, "unsolicited"), "late response: no unsolicited-packet warning");

        // A fresh session must not inherit anything.
        slave.ResponseDelayMs = 0;
        await slave.Master.ConnectAsync();
        var data = await slave.Master.ReadMemoryAsync(0, 0x1000, 1);
        Check(data.Length == 1, "late response: next session reads normally");
    }

    /// <summary>A timeout (slave silent) must not arm the late-response consumption: the following
    /// SYNCH/retry must see the slave's next packet as usual.</summary>
    private static async Task Timeout_DoesNotArmLateResponseConsumption()
    {
        var log = new CapturingLogger();
        var slave = await new FakeSlave(timeoutMs: 50, logger: log).ConnectedAsync();
        var swallowedOnce = false;
        slave.Responder = cmd =>
        {
            if (cmd[0] == XcpCommand.ShortUpload && !swallowedOnce)
            {
                swallowedOnce = true;
                return null; // provoke exactly one timeout
            }

            return FakeSlave.DefaultResponder(cmd);
        };

        var data = await slave.Master.ReadMemoryAsync(0, 0x1000, 1);
        Check(data.Length == 1, "timeout: retry after SYNCH succeeded");
        Check(!log.Has(LogLevel.Debug, "late Response to a cancelled command consumed"), "timeout: nothing consumed as a late response");
    }
}
