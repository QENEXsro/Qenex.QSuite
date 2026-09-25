using System.Buffers.Binary;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Protocols.XcpProtocol;
using Qenex.QSuite.Protocols.XcpTcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>
/// Drives the XcpTcp protocol purely through its public surface — SetTransmitter (as the TCP
/// client driver does) and AddReceivedDataToQueueAsync with raw byte chunks — against an
/// in-process simulated XCP-on-Ethernet slave, covering LEN+CTR framing, stream reassembly and
/// the CONNECT → poll → write flow with a large MAX_CTO.
/// </summary>
internal static class TcpIntegrationTests
{
    internal static void Run()
    {
        PollFlow_ValueLandsInVariable().GetAwaiter().GetResult();
        Framing_LenAndCtrOnEveryCommand().GetAwaiter().GetResult();
        ChunkedResponses_AreReassembled().GetAwaiter().GetResult();
        OperatorWrite_DoubleIsSingleDownload().GetAwaiter().GetResult();
        OnRequestEvent_ReadOnceNotPolled().GetAwaiter().GetResult();
        DefaultAndEmptySettings_AreValid();
        CompatibleDrivers_NarrowToTcpClient();
    }

    #region Simulated slave harness

    private sealed class SimulatedTcpSlave
    {
        private readonly XcpTcp protocol;
        private readonly List<byte> rxStream = [];

        public readonly List<byte[]> SentFrames = [];
        public readonly List<byte[]> SentPackets = [];
        public byte[] Memory = [0x2A, 0, 0, 0, 0, 0, 0, 0];
        public int ResponseChunkSize; // 0 = respond in one chunk

        public SimulatedTcpSlave(XcpTcp xcpTcpProtocol)
        {
            protocol = xcpTcpProtocol;
            // What the TCP client driver does in its run loop: hand the protocol a TX path.
            protocol.SetTransmitter(async (chunk, ct) =>
            {
                List<byte[]> commands = [];
                lock (SentFrames)
                {
                    SentFrames.Add(chunk);
                    rxStream.AddRange(chunk);

                    // The slave itself reassembles the master's stream into command packets.
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
                    if (response == null)
                    {
                        continue;
                    }

                    // Frame the response the way the ECU would and loop it back as received
                    // chunks, optionally split to exercise reassembly.
                    var frame = new byte[4 + response.Length];
                    BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)response.Length);
                    frame[2] = 0x99; // slave CTR sequence is independent and ignored by the master
                    response.CopyTo(frame, 4);

                    if (ResponseChunkSize <= 0)
                    {
                        await protocol.AddReceivedDataToQueueAsync([frame], ct);
                    }
                    else
                    {
                        for (var offset = 0; offset < frame.Length; offset += ResponseChunkSize)
                        {
                            var size = Math.Min(ResponseChunkSize, frame.Length - offset);
                            await protocol.AddReceivedDataToQueueAsync([frame[offset..(offset + size)]], ct);
                        }
                    }
                }
            });
        }

        public int CountSent(byte pid)
        {
            lock (SentFrames)
            {
                return SentPackets.Count(p => p[0] == pid);
            }
        }

        private byte[]? Respond(byte[] command) => command[0] switch
        {
            // Little-endian AG=1 slave with MAX_CTO=250 (XCP on Ethernet), CAL unprotected.
            XcpCommand.Connect => [0xFF, 0x05, 0x00, 0xFA, 0xFF, 0x05, 0x01, 0x01],
            XcpCommand.GetStatus => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00],
            XcpCommand.Disconnect => [0xFF],
            XcpCommand.Synch => [0xFE, XcpErrorCode.CmdSynch],
            XcpCommand.SetMta => [0xFF],
            XcpCommand.Download => [0xFF],
            XcpCommand.ShortUpload => [(byte)0xFF, .. Memory.Take(command[1])],
            XcpCommand.Upload => [0xFF, 0x00],
            _ => null
        };
    }

    private static ScalarVariable DoubleVariable(string name = "FuelRate") => new()
    {
        Id = 1,
        Name = name,
        Values = new Values<double> { Value = 0d, ValueType = ValueDataType.Double }
    };

    private static (XcpTcp Protocol, SimulatedTcpSlave Slave, ScalarVariable Variable) CreateRunningSetup(
        string direction = "readWrite")
    {
        var protocol = new XcpTcp
        {
            IsEnabled = true,
            RawSettings = "requestTimeoutMs=\"100\""
        };
        protocol.SetConfiguration();

        var pollEvent = new PeriodicVarEvent { Name = "poll20ms", Period = 20, Unit = TimeUnit.Milisec };
        var variable = DoubleVariable();
        var protocolVariable = protocol.CreateProtocolVariable(variable, [pollEvent],
            $"address=\"0x1000\";direction=\"{direction}\";eventRef=\"poll20ms\"", true);
        protocol.AddVariable(protocolVariable!);

        var slave = new SimulatedTcpSlave(protocol);
        return (protocol, slave, variable);
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

    #endregion

    private static async Task PollFlow_ValueLandsInVariable()
    {
        var (protocol, slave, variable) = CreateRunningSetup();
        BinaryPrimitives.WriteDoubleLittleEndian(slave.Memory, 12.5);

        await protocol.StartAsync();
        var updated = await WaitUntilAsync(() => (double)variable.GetValue() != 0d);
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 2);
        await protocol.StopAsync();

        Check(updated && (double)variable.GetValue() == 12.5, "tcp poll flow: polled double landed in the variable");
        Check(protocol.State == CommunicationState.Stopped, "tcp poll flow: protocol stopped cleanly");
        Check(slave.CountSent(XcpCommand.Connect) == 1 && slave.CountSent(XcpCommand.GetStatus) == 1,
            "tcp poll flow: session established with CONNECT + GET_STATUS");
        Check(slave.CountSent(XcpCommand.Upload) == 0,
            "tcp poll flow: 8-byte double read as a single SHORT_UPLOAD (MAX_CTO 250, no chaining)");
        Check(slave.CountSent(XcpCommand.Disconnect) == 1, "tcp poll flow: DISCONNECT sent on stop");
    }

    private static async Task Framing_LenAndCtrOnEveryCommand()
    {
        var (protocol, slave, _) = CreateRunningSetup();

        await protocol.StartAsync();
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 1);
        await protocol.StopAsync();

        lock (slave.SentFrames)
        {
            Check(slave.SentFrames.All(f =>
                    f.Length >= 5 && (f[0] | (f[1] << 8)) == f.Length - 4),
                "tcp framing: every command frame is LEN(LE) + CTR + exactly LEN packet bytes");
            Check(slave.SentFrames[0][2] == 0x00 && slave.SentFrames[0][3] == 0x00,
                "tcp framing: CTR starts at 0 for a new session");
            Check(slave.SentFrames.Count < 2 ||
                  (slave.SentFrames[1][2] | (slave.SentFrames[1][3] << 8)) == 1,
                "tcp framing: CTR increments per sent frame");
            Check(slave.SentFrames[0].Skip(4).First() == XcpCommand.Connect,
                "tcp framing: no DLC padding — CONNECT packet is sent unpadded");
        }
    }

    private static async Task ChunkedResponses_AreReassembled()
    {
        var (protocol, slave, variable) = CreateRunningSetup();
        BinaryPrimitives.WriteDoubleLittleEndian(slave.Memory, -3.25);
        slave.ResponseChunkSize = 3; // every response arrives shredded into 3-byte chunks

        await protocol.StartAsync();
        var updated = await WaitUntilAsync(() => (double)variable.GetValue() != 0d);
        await protocol.StopAsync();

        Check(updated && (double)variable.GetValue() == -3.25,
            "tcp reassembly: value decoded from responses split across arbitrary chunks");
    }

    private static async Task OperatorWrite_DoubleIsSingleDownload()
    {
        var (protocol, slave, variable) = CreateRunningSetup();
        var protocolVariable = protocol.Variables.Single();

        await protocol.StartAsync();
        await WaitUntilAsync(() => protocol.State == CommunicationState.Running);

        Check(protocol.CanWriteVariable(protocolVariable), "tcp write: readWrite variable is writable");

        variable.SetValue(1.5);
        await protocol.WriteVariableAsync(protocolVariable);
        await protocol.StopAsync();

        Check(slave.CountSent(XcpCommand.SetMta) == 1, "tcp write: SET_MTA sent");
        Check(slave.CountSent(XcpCommand.Download) == 1, "tcp write: single DOWNLOAD (8 bytes, no chaining)");

        lock (slave.SentFrames)
        {
            var download = slave.SentPackets.First(p => p[0] == XcpCommand.Download);
            var expected = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(expected, 1.5);
            Check(download.Length == 10 && download[1] == 8 && download.Skip(2).SequenceEqual(expected),
                "tcp write: DOWNLOAD carries the raw 8-byte double image");
        }
    }

    /// <summary>
    /// On Request event: the variable is excluded from polling, CanReadVariable reports it (and not
    /// the polled one), ReadVariableAsync performs exactly one SHORT_UPLOAD and lands the value,
    /// the variable's RequestReadAsync notification reaches the protocol, and a read while
    /// disconnected throws instead of failing silently.
    /// </summary>
    private static async Task OnRequestEvent_ReadOnceNotPolled()
    {
        var protocol = new XcpTcp
        {
            IsEnabled = true,
            RawSettings = "requestTimeoutMs=\"100\""
        };
        protocol.SetConfiguration();

        var pollEvent = new PeriodicVarEvent { Name = "poll20ms", Period = 20, Unit = TimeUnit.Milisec };
        var onRequestEvent = new OnRequestVarEvent { Name = "onRequest" };
        var onRequestVariable = DoubleVariable("MapCell");
        var polledVariable = DoubleVariable("Rpm");
        polledVariable.Id = 2;

        var onRequestProtocolVariable = protocol.CreateProtocolVariable(onRequestVariable, [pollEvent, onRequestEvent],
            "address=\"0x1000\";direction=\"read\";eventRef=\"onRequest\"", true)!;
        var polledProtocolVariable = protocol.CreateProtocolVariable(polledVariable, [pollEvent, onRequestEvent],
            "address=\"0x1000\";direction=\"read\";eventRef=\"poll20ms\"", true)!;
        protocol.AddVariable(onRequestProtocolVariable);
        protocol.AddVariable(polledProtocolVariable);

        var slave = new SimulatedTcpSlave(protocol);
        BinaryPrimitives.WriteDoubleLittleEndian(slave.Memory, 12.5);

        Check(protocol.CanReadVariable(onRequestProtocolVariable), "on request: variable on the On Request event is readable on request");
        Check(!protocol.CanReadVariable(polledProtocolVariable), "on request: periodically polled variable is NOT readable on request");

        await protocol.StartAsync();
        await WaitUntilAsync(() => (double)polledVariable.GetValue() == 12.5);
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 3);

        Check((double)onRequestVariable.GetValue() == 0d, "on request: variable is not polled while the poll loop runs");

        var notified = 0;
        onRequestProtocolVariable.SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref notified); return Task.CompletedTask; });

        await protocol.ReadVariableAsync(onRequestProtocolVariable);
        Check((double)onRequestVariable.GetValue() == 12.5, "on request: ReadVariableAsync landed the value in the variable");
        Check(notified == 1, "on request: exactly one value-changed notification per read");
        Check(onRequestVariable.Timestamp > DateTime.UtcNow.AddSeconds(-5), "on request: timestamp stamped by the read");

        // A new device value must not appear without another request (still not polled)...
        BinaryPrimitives.WriteDoubleLittleEndian(slave.Memory, 7.25);
        await WaitUntilAsync(() => (double)polledVariable.GetValue() == 7.25);
        await Task.Delay(60);
        Check((double)onRequestVariable.GetValue() == 12.5 && notified == 1,
            "on request: no further update without a request");

        // ...and the variable's own request notification reaches the protocol (module wiring).
        onRequestProtocolVariable.SubscribeAsyncReadRequested((pv, ct) => protocol.ReadVariableAsync(pv, ct));
        await onRequestProtocolVariable.RequestReadAsync();
        Check((double)onRequestVariable.GetValue() == 7.25 && notified == 2,
            "on request: RequestReadAsync notification performed the second read");

        await protocol.StopAsync();

        var threw = false;
        try
        {
            await protocol.ReadVariableAsync(onRequestProtocolVariable);
        }
        catch (XcpProtocolException)
        {
            threw = true;
        }

        Check(threw, "on request: read while disconnected throws XcpProtocolException");
        Check(!protocol.CanReadVariable(new XcpProtocolVariable { Variable = onRequestVariable, IsCommunicated = true }),
            "on request: a foreign protocol variable is not readable");
    }

    private static void DefaultAndEmptySettings_AreValid()
    {
        var byDefault = XcpTcpSessionSettings.Parse(new XcpTcp().DefaultRawSettings);
        Check(byDefault.TimeoutMs == 1000, "tcp settings: default raw settings parse to 1000 ms");

        var empty = XcpTcpSessionSettings.Parse(string.Empty);
        Check(empty.TimeoutMs == 1000, "tcp settings: empty settings are valid (host/port belong to the driver)");

        CheckThrows<ArgumentException>(() => XcpTcpSessionSettings.Parse("requestTimeoutMs=\"0\""),
            "tcp settings: non-positive timeout is rejected");

        Check(XcpTcpSessionSettings.Parse("requestTimeoutMs=\"250\"").TimeoutMs == 250, "tcp settings: requestTimeoutMs parsed");
        Check(XcpTcpSessionSettings.Parse(string.Empty).RequestRetries == 2, "tcp settings: requestRetries defaults to 2");
        Check(XcpTcpSessionSettings.Parse("requestRetries=\"0\"").RequestRetries == 0, "tcp settings: requestRetries 0 accepted");
        CheckThrows<ArgumentException>(() => XcpTcpSessionSettings.Parse("requestRetries=\"-1\""), "tcp settings: negative requestRetries rejected");
        Check(XcpSessionSettings.Parse("masterId=0x200;slaveId=0x201;requestRetries=\"5\"").RequestRetries == 5, "can settings: requestRetries parsed");
        Check(XcpTcpSessionSettings.Parse("timeoutMs=\"300\"").TimeoutMs == 1000, "tcp settings: former timeoutMs is not read any more");
        Check(XcpSessionSettings.Parse("masterId=0x200;slaveId=0x201;timeoutMs=\"400\"").TimeoutMs == 1000,
            "can settings: former timeoutMs is not read any more");
    }

    private static void CompatibleDrivers_NarrowToTcpClient()
    {
        Check(new XcpTcp().CompatibleDrivers is ["TcpClientDriver"],
            "tcp pairing: protocol narrows itself to the TCP Client driver");
    }
}
