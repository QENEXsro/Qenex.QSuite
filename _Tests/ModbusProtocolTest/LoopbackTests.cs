using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Protocols.ModbusMaster;
using Qenex.QSuite.Protocols.ModbusSlave;
using Qenex.QSuite.Variables.QVariables;
using static Qenex.QSuite.Tests.ModbusProtocolTest.Program;
using static Qenex.QSuite.Tests.ModbusProtocolTest.ProtocolTests;

namespace Qenex.QSuite.Tests.ModbusProtocolTest;

/// <summary>
/// Wires the real ModbusMasterProtocol and ModbusSlaveProtocol back to back — each protocol's
/// transmitter feeds the other's AddReceivedDataToQueueAsync, exactly like two drivers on a shared
/// wire. Proves the full stack (spec parsing, framing, engines, register codec, variable plumbing)
/// end to end in both RTU and TCP modes.
/// </summary>
internal static class LoopbackTests
{
    internal static void Run()
    {
        Loopback_PollAndWrite("rtu").GetAwaiter().GetResult();
        Loopback_PollAndWrite("tcp").GetAwaiter().GetResult();
    }

    private static async Task Loopback_PollAndWrite(string mode)
    {
        // Slave side: a float served at holding 100 and a coil at 5.
        var slaveFloat = FloatVariable("SlaveFloat", 123.456f);
        var slaveCoil = UShortVariable("SlaveCoil");
        slaveCoil.Id = 2;

        var slave = new ModbusSlaveProtocol { IsEnabled = true, RawSettings = $"mode=\"{mode}\";unitId=\"1\"" };
        slave.SetConfiguration();
        slave.AddVariable(slave.CreateProtocolVariable(slaveFloat, null!, "registerType=\"holding\";address=\"100\"", true)!);
        slave.AddVariable(slave.CreateProtocolVariable(slaveCoil, null!, "registerType=\"coil\";address=\"5\"", true)!);

        // Master side: mirrors of the same registers, polled every 20 ms.
        var masterFloat = FloatVariable("MasterFloat");
        var masterCoil = UShortVariable("MasterCoil");
        masterCoil.Id = 2;

        var master = new ModbusMasterProtocol
        {
            IsEnabled = true,
            RawSettings = $"mode=\"{mode}\";unitId=\"1\";requestTimeoutMs=\"200\";requestRetries=\"1\""
        };
        master.SetConfiguration();
        master.AddVariable(master.CreateProtocolVariable(masterFloat, [Poll20Ms],
            "registerType=\"holding\";address=\"100\";direction=\"readWrite\";eventRef=\"poll20ms\"", true)!);
        master.AddVariable(master.CreateProtocolVariable(masterCoil, [Poll20Ms],
            "registerType=\"coil\";address=\"5\";direction=\"readWrite\";eventRef=\"poll20ms\"", true)!);

        // The shared wire: each side's TX is the other side's RX.
        master.SetTransmitter((frame, ct) => slave.AddReceivedDataToQueueAsync([frame], ct));
        slave.SetTransmitter((frame, ct) => master.AddReceivedDataToQueueAsync([frame], ct));

        await slave.StartAsync();
        await master.StartAsync();

        // Poll direction: the slave's value appears in the master's variable.
        var polled = await WaitUntilAsync(() => Math.Abs((float)masterFloat.GetValue() - 123.456f) < 0.001f);
        Check(polled, $"loopback {mode}: master polled the slave's float");

        // Write direction: an operator write on the master lands in the slave's variable.
        var masterFloatVariable = master.Variables.First();
        masterFloat.SetValue(-7.5f);
        await master.WriteVariableAsync(masterFloatVariable);
        Check(Math.Abs((float)slaveFloat.GetValue() - -7.5f) < 0.001f,
            $"loopback {mode}: master write reached the slave's float variable");

        // Coil write + poll back.
        var masterCoilVariable = master.Variables.Last();
        masterCoil.SetValue((ushort)1);
        await master.WriteVariableAsync(masterCoilVariable);
        Check((ushort)slaveCoil.GetValue() == 1, $"loopback {mode}: coil write reached the slave");

        // And the next poll confirms the written value back into the master's variable.
        slaveFloat.SetValue(42.0f);
        var repolled = await WaitUntilAsync(() => Math.Abs((float)masterFloat.GetValue() - 42.0f) < 0.001f);
        Check(repolled, $"loopback {mode}: subsequent poll picked up the slave-side change");

        await master.StopAsync();
        await slave.StopAsync();

        Check(master.State == CommunicationState.Stopped && slave.State == CommunicationState.Stopped,
            $"loopback {mode}: both protocols stopped cleanly");
    }
}
