using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.TcpClientDriver;
using Qenex.QSuite.Drivers.TcpServerDriver;
using Qenex.QSuite.Protocols.ModbusMaster;
using Qenex.QSuite.Protocols.ModbusSlave;
using static Qenex.QSuite.Tests.ModbusProtocolTest.Program;
using static Qenex.QSuite.Tests.ModbusProtocolTest.ProtocolTests;

namespace Qenex.QSuite.Tests.ModbusProtocolTest;

/// <summary>
/// Full-stack end-to-end test over REAL localhost sockets: the TcpServer driver hosts the Modbus
/// slave protocol, the unified TcpClientDriver hosts the Modbus master protocol, and values travel
/// through actual TCP connections — exercising the drivers' connect/accept loops, transmitter
/// injection and chunk dispatch on top of everything the protocol loopback already covers.
/// </summary>
internal static class DriverLoopbackTests
{
    private const int TestPort = 15020;

    internal static void Run()
    {
        TcpDrivers_EndToEnd().GetAwaiter().GetResult();
    }

    private static async Task TcpDrivers_EndToEnd()
    {
        // Slave side: TcpServer driver + slave protocol serving a float at holding 100.
        var slaveFloat = FloatVariable("SlaveFloat", 123.456f);
        var slaveProtocol = new ModbusSlaveProtocol { IsEnabled = true, RawSettings = "mode=\"tcp\";unitId=\"1\"" };
        slaveProtocol.SetConfiguration();
        slaveProtocol.AddVariable(slaveProtocol.CreateProtocolVariable(slaveFloat, null!,
            "registerType=\"holding\";address=\"100\"", true)!);

        var server = new TcpServer { IsEnabled = true, RawSettings = $"bindAddress=\"127.0.0.1\";port=\"{TestPort}\"" };
        server.SetConfiguration();
        server.AddProtocol(slaveProtocol);

        // Master side: TcpClientDriver + master protocol polling the same register.
        var masterFloat = FloatVariable("MasterFloat");
        var masterProtocol = new ModbusMasterProtocol
        {
            IsEnabled = true,
            RawSettings = "mode=\"tcp\";unitId=\"1\";requestTimeoutMs=\"500\";requestRetries=\"2\""
        };
        masterProtocol.SetConfiguration();
        masterProtocol.AddVariable(masterProtocol.CreateProtocolVariable(masterFloat, [Poll20Ms],
            "registerType=\"holding\";address=\"100\";direction=\"readWrite\";eventRef=\"poll20ms\"", true)!);

        var client = new TcpClientDriver
        {
            IsEnabled = true,
            RawSettings = $"host=\"127.0.0.1\";port=\"{TestPort}\";reconnectTimeMs=\"200\";numberOfReconnections=\"0\""
        };
        client.SetConfiguration();
        client.AddProtocol(masterProtocol);

        try
        {
            await server.StartAsync();
            var listening = await WaitUntilAsync(() => server.State == CommunicationState.Running);
            Check(listening, "driver e2e: server listening");

            await client.StartAsync();

            // Poll direction through real sockets.
            var polled = await WaitUntilAsync(() => Math.Abs((float)masterFloat.GetValue() - 123.456f) < 0.001f, 5000);
            Check(polled, "driver e2e: master polled the slave's value over real TCP");

            // Operator write through the drivers' command path (as ModuleBase would deliver it).
            var protocolVariable = masterProtocol.Variables.Single();
            Check(client.CanSendCommand(protocolVariable), "driver e2e: client driver reports the variable writable");

            masterFloat.SetValue(-55.25f);
            await client.OnProtocolVariableCommandAsync(protocolVariable);

            var written = await WaitUntilAsync(() => Math.Abs((float)slaveFloat.GetValue() - -55.25f) < 0.001f, 5000);
            Check(written, "driver e2e: operator write reached the slave variable over real TCP");
        }
        finally
        {
            await client.StopAsync();
            await server.StopAsync();
        }

        Check(client.State == CommunicationState.Stopped && server.State == CommunicationState.Stopped,
            "driver e2e: both drivers stopped cleanly");
    }
}
