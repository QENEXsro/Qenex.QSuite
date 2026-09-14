using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Protocols.XcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;
using static Qenex.QSuite.Tests.XcpProtocolTest.Program;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>
/// Drives the Xcp protocol purely through its public surface — SetTransmitter (as the CAN driver
/// does) and AddReceivedDataToQueueAsync — against an in-process simulated XCP slave, covering the
/// full CONNECT → poll → write flow including CAN framing, echo suppression and error paths.
/// </summary>
internal static class IntegrationTests
{
    private const uint MasterId = 0x200;
    private const uint SlaveId = 0x201;

    internal static void Run()
    {
        PollFlow_ValueLandsInVariable().GetAwaiter().GetResult();
        Framing_PaddedTo8_OnMasterId().GetAwaiter().GetResult();
        RxFilter_IgnoresForeignIds().GetAwaiter().GetResult();
        OperatorWrite_SetMtaPlusDownload().GetAwaiter().GetResult();
        EchoSuppression_PollDoesNotWriteBack().GetAwaiter().GetResult();
        ProtectedSlave_RejectsWrites().GetAwaiter().GetResult();
        UnsupportedAg_FaultsWithoutReconnect().GetAwaiter().GetResult();
        InvalidSettings_FaultsOnStart().GetAwaiter().GetResult();
    }

    #region Simulated slave harness

    private sealed class SimulatedSlave
    {
        private readonly Xcp protocol;

        public readonly List<CanFrame> SentFrames = [];
        public byte MemoryValue = 0x2A; // first payload byte served by SHORT_UPLOAD
        public bool CalibrationProtected;
        public byte CommModeBasic; // bit0 byte order, bits1-2 AG

        public SimulatedSlave(Xcp xcpProtocol)
        {
            protocol = xcpProtocol;
            // What the CAN driver does in its run loop: hand the protocol a TX path.
            protocol.SetTransmitter(async (frame, ct) =>
            {
                lock (SentFrames)
                {
                    SentFrames.Add(frame);
                }

                var response = Respond(frame.Data);
                if (response != null)
                {
                    // Loop the response back the way the driver dispatches received frames.
                    await protocol.AddReceivedDataToQueueAsync([new CanFrame(SlaveId, response)], ct);
                }
            });
        }

        public int CountSent(byte pid)
        {
            lock (SentFrames)
            {
                return SentFrames.Count(f => f.Data[0] == pid);
            }
        }

        private byte[]? Respond(byte[] command) => command[0] switch
        {
            XcpCommand.Connect => [0xFF, 0x05, CommModeBasic, 0x08, 0x08, 0x00, 0x01, 0x01],
            XcpCommand.GetStatus => [0xFF, 0x00, CalibrationProtected ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00],
            XcpCommand.Disconnect => [0xFF],
            XcpCommand.Synch => [0xFE, XcpErrorCode.CmdSynch],
            XcpCommand.SetMta => [0xFF],
            XcpCommand.Download => [0xFF],
            XcpCommand.ShortUpload => [0xFF, MemoryValue, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            XcpCommand.Upload => [0xFF, 0x00],
            _ => null
        };
    }

    private static ScalarVariable FloatVariable(string name = "EngineTemp") => new()
    {
        Id = 1,
        Name = name,
        Values = new Values<float> { Value = 0f, ValueType = ValueDataType.Float }
    };

    private static (Xcp Protocol, SimulatedSlave Slave, ScalarVariable Variable) CreateRunningSetup(
        string direction = "readWrite", bool protectedSlave = false)
    {
        var protocol = new Xcp
        {
            IsEnabled = true,
            RawSettings = $"masterId=\"0x{MasterId:X}\";slaveId=\"0x{SlaveId:X}\";requestTimeoutMs=\"100\""
        };
        protocol.SetConfiguration();

        var pollEvent = new PeriodicVarEvent { Name = "poll20ms", Period = 20, Unit = TimeUnit.Milisec };
        var variable = FloatVariable();
        var protocolVariable = protocol.CreateProtocolVariable(variable, [pollEvent],
            $"address=\"0x1000\";direction=\"{direction}\";eventRef=\"poll20ms\"", true);
        protocol.AddVariable(protocolVariable!);

        var slave = new SimulatedSlave(protocol) { CalibrationProtected = protectedSlave };
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
        slave.MemoryValue = 0x42; // float 0x00000042 (LE) = 9.2e-44, distinguishable from 0

        await protocol.StartAsync();
        var updated = await WaitUntilAsync(() => (float)variable.GetValue() != 0f);
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 2);
        await protocol.StopAsync();

        Check(updated, "poll flow: polled value landed in the variable");
        Check(protocol.State == CommunicationState.Stopped, "poll flow: protocol stopped cleanly");
        Check(slave.CountSent(XcpCommand.Connect) == 1 && slave.CountSent(XcpCommand.GetStatus) == 1,
            "poll flow: session established with CONNECT + GET_STATUS");
        Check(slave.CountSent(XcpCommand.ShortUpload) >= 2, "poll flow: repeated periodic SHORT_UPLOAD polls");
        Check(slave.CountSent(XcpCommand.Disconnect) == 1, "poll flow: DISCONNECT sent on stop");
    }

    private static async Task Framing_PaddedTo8_OnMasterId()
    {
        var (protocol, slave, _) = CreateRunningSetup();

        await protocol.StartAsync();
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 1);
        await protocol.StopAsync();

        lock (slave.SentFrames)
        {
            Check(slave.SentFrames.All(f => f.CanId == MasterId && !f.IsExtended),
                "framing: all commands transmitted on the standard master id");
            Check(slave.SentFrames.All(f => f.Data.Length == 8),
                "framing: outgoing DLC always padded to 8 bytes (even 2-byte CONNECT)");
        }
    }

    private static async Task RxFilter_IgnoresForeignIds()
    {
        var (protocol, slave, variable) = CreateRunningSetup();

        await protocol.StartAsync();
        await WaitUntilAsync(() => protocol.State == CommunicationState.Running);

        // Foreign traffic that must not disturb the session: alien id, extended variant of the
        // slave id, empty frame.
        await protocol.AddReceivedDataToQueueAsync([
            new CanFrame(0x300, [0xFF, 0x11, 0x22]),
            new CanFrame(SlaveId, [0xFF, 0x11, 0x22], isExtended: true),
            new CanFrame(SlaveId, [])
        ]);

        var stillPolling = await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 3);
        await protocol.StopAsync();

        Check(stillPolling, "rx filter: foreign frames ignored, session keeps polling");
        Check(protocol.State == CommunicationState.Stopped, "rx filter: clean stop after foreign traffic");
    }

    private static async Task OperatorWrite_SetMtaPlusDownload()
    {
        var (protocol, slave, variable) = CreateRunningSetup();
        var protocolVariable = protocol.Variables.Single();

        await protocol.StartAsync();
        await WaitUntilAsync(() => protocol.State == CommunicationState.Running);

        Check(protocol.CanWriteVariable(protocolVariable), "write: readWrite variable is writable");

        variable.SetValue(1.5f);
        await protocol.WriteVariableAsync(protocolVariable);
        await protocol.StopAsync();

        Check(slave.CountSent(XcpCommand.SetMta) == 1, "write: SET_MTA sent");
        Check(slave.CountSent(XcpCommand.Download) == 1, "write: DOWNLOAD sent");

        lock (slave.SentFrames)
        {
            var download = slave.SentFrames.First(f => f.Data[0] == XcpCommand.Download).Data;
            // 1.5f little-endian = 00 00 C0 3F; DOWNLOAD = F0 04 data, padded to DLC 8.
            Check(download.SequenceEqual(new byte[] { 0xF0, 0x04, 0x00, 0x00, 0xC0, 0x3F, 0x00, 0x00 }),
                "write: DOWNLOAD carries the raw float image, padded to 8");
            var setMta = slave.SentFrames.First(f => f.Data[0] == XcpCommand.SetMta).Data;
            Check(setMta.SequenceEqual(new byte[] { 0xF6, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00 }),
                "write: SET_MTA targets the configured address");
        }
    }

    private static async Task EchoSuppression_PollDoesNotWriteBack()
    {
        var (protocol, slave, variable) = CreateRunningSetup();
        var protocolVariable = protocol.Variables.Single();

        // Reproduce the ModuleBase wiring: value-changed notifications feed the write path.
        protocolVariable.SubscribeAsyncValueChanged(pv => protocol.WriteVariableAsync(pv));

        await protocol.StartAsync();
        await WaitUntilAsync(() => slave.CountSent(XcpCommand.ShortUpload) >= 3);

        Check(slave.CountSent(XcpCommand.Download) == 0,
            "echo suppression: poll updates never trigger a write back to the ECU");

        // A genuine operator change (outside the poll path) must reach the ECU via the same wiring.
        variable.SetValue(2.0f);
        await protocolVariable.NotifyValueChangedAsync();
        await protocol.StopAsync();

        Check(slave.CountSent(XcpCommand.Download) == 1,
            "echo suppression: operator notification does trigger the write");
    }

    private static async Task ProtectedSlave_RejectsWrites()
    {
        var (protocol, slave, variable) = CreateRunningSetup(protectedSlave: true);
        var protocolVariable = protocol.Variables.Single();

        await protocol.StartAsync();
        await WaitUntilAsync(() => protocol.State == CommunicationState.Running);

        Check(protocol.StateMessage?.Contains("seed & key") == true,
            "protected slave: state message explains seed & key");

        variable.SetValue(1.0f);
        await protocol.WriteVariableAsync(protocolVariable);
        await protocol.StopAsync();

        Check(slave.CountSent(XcpCommand.SetMta) == 0 && slave.CountSent(XcpCommand.Download) == 0,
            "protected slave: write rejected without touching the bus");
    }

    private static async Task UnsupportedAg_FaultsWithoutReconnect()
    {
        var (protocol, slave, _) = CreateRunningSetup();
        slave.CommModeBasic = 0x02; // AG = WORD

        await protocol.StartAsync();
        var faulted = await WaitUntilAsync(() => protocol.State == CommunicationState.Faulted);
        await Task.Delay(200); // would-be reconnect window

        Check(faulted, "unsupported AG: protocol faults");
        Check(protocol.StateMessage?.Contains("GRANULARITY", StringComparison.OrdinalIgnoreCase) == true,
            "unsupported AG: clear message");
        Check(slave.CountSent(XcpCommand.Connect) == 1,
            "unsupported AG: no reconnect attempts (incompatible slave)");

        await protocol.StopAsync();
    }

    private static async Task InvalidSettings_FaultsOnStart()
    {
        var protocol = new Xcp { IsEnabled = true, RawSettings = "slaveId=\"0x201\"" }; // masterId missing
        protocol.SetConfiguration();

        await protocol.StartAsync();

        Check(protocol.State == CommunicationState.Faulted, "invalid settings: protocol faults on start");
        Check(protocol.StateMessage?.Contains("masterId") == true, "invalid settings: message names the missing key");
    }
}
