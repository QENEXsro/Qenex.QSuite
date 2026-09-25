using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Protocols.Modbus;
using Qenex.QSuite.Protocols.ModbusMaster;
using Qenex.QSuite.Protocols.ModbusSlave;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;
using static Qenex.QSuite.Tests.ModbusProtocolTest.Program;

namespace Qenex.QSuite.Tests.ModbusProtocolTest;

internal static class ProtocolTests
{
    internal static void Run()
    {
        MasterSettings_NewAndLegacyNames();
        Master_PollsHoldingFloat_Tcp().GetAwaiter().GetResult();
        Master_PollsCoil_Rtu().GetAwaiter().GetResult();
        Master_OperatorWrite_WithEchoSuppression().GetAwaiter().GetResult();
        Master_OnRequestRead_ScalarAndMatrix_Tcp().GetAwaiter().GetResult();
        Master_InvalidSettings_Faults().GetAwaiter().GetResult();
        Slave_ServesAndWrites_Tcp().GetAwaiter().GetResult();
        Slave_IgnoresForeignUnit_Rtu().GetAwaiter().GetResult();
        Slave_OverlappingMap_Faults().GetAwaiter().GetResult();
        Slave_CommParams_RoundTrip();
    }

    #region Helpers

    internal static ScalarVariable FloatVariable(string name, float value = 0f) => new()
    {
        Id = 1,
        Name = name,
        Values = new Values<float> { Value = value, ValueType = ValueDataType.Float }
    };

    internal static ScalarVariable UShortVariable(string name, ushort value = 0) => new()
    {
        Id = 1,
        Name = name,
        Values = new Values<ushort> { Value = value, ValueType = ValueDataType.UShort }
    };

    internal static PeriodicVarEvent Poll20Ms { get; } = new() { Name = "poll20ms", Period = 20, Unit = TimeUnit.Milisec };

    private static ModbusMasterProtocol CreateMaster(string mode, ScalarVariable variable, string commParams)
    {
        var protocol = new ModbusMasterProtocol
        {
            IsEnabled = true,
            RawSettings = $"mode=\"{mode}\";unitId=\"1\";requestTimeoutMs=\"100\";requestRetries=\"1\""
        };
        protocol.SetConfiguration();
        var protocolVariable = protocol.CreateProtocolVariable(variable, [Poll20Ms], commParams, true);
        protocol.AddVariable(protocolVariable!);
        return protocol;
    }

    /// <summary>Backs the master's transmitter with an in-process slave engine over a dictionary store.</summary>
    private static SlaveEngineTestsAccessor AttachSimulatedSlave(ModbusMasterProtocol master, bool tcp)
    {
        var accessor = new SlaveEngineTestsAccessor(tcp);
        master.SetTransmitter(async (frame, ct) =>
        {
            var responses = await accessor.ProcessAsync(frame, ct);
            foreach (var response in responses)
            {
                await master.AddReceivedDataToQueueAsync([response], ct);
            }
        });
        return accessor;
    }

    /// <summary>Simulated wire slave reused by protocol tests: framer + engine + dictionary store.</summary>
    internal sealed class SlaveEngineTestsAccessor
    {
        public readonly Dictionary<ushort, ushort> Holding = [];
        public readonly Dictionary<ushort, bool> Coils = [];
        private readonly IModbusFramer framer;
        private readonly ModbusSlaveEngine engine;

        public SlaveEngineTestsAccessor(bool tcp)
        {
            framer = tcp ? new ModbusTcpFramer() : new ModbusRtuFramer(ModbusFramerRole.Slave);
            engine = new ModbusSlaveEngine(new Store(this)) { UnitId = 1 };
        }

        public async Task<List<byte[]>> ProcessAsync(byte[] chunk, CancellationToken ct)
        {
            var responses = new List<byte[]>();
            framer.Append(chunk);
            while (framer.TryDequeueFrame(out var request))
            {
                var response = await engine.ProcessRequestAsync(request, ct);
                if (response != null)
                {
                    responses.Add(framer.Encode(response));
                }
            }

            return responses;
        }

        private sealed class Store(SlaveEngineTestsAccessor owner) : IModbusDataStore
        {
            public bool TryReadBits(ModbusRegisterType table, ushort address, int count, out bool[] bits)
            {
                bits = new bool[count];
                for (var i = 0; i < count; i++)
                {
                    if (!owner.Coils.TryGetValue((ushort)(address + i), out bits[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public bool TryReadRegisters(ModbusRegisterType table, ushort address, int count, out ushort[] registers)
            {
                registers = new ushort[count];
                for (var i = 0; i < count; i++)
                {
                    if (!owner.Holding.TryGetValue((ushort)(address + i), out registers[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public ValueTask<bool> TryWriteBitAsync(ushort address, bool value, CancellationToken ct = default)
            {
                owner.Coils[address] = value;
                return ValueTask.FromResult(true);
            }

            public ValueTask<bool> TryWriteRegistersAsync(ushort address, ushort[] values, CancellationToken ct = default)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    owner.Holding[(ushort)(address + i)] = values[i];
                }

                return ValueTask.FromResult(true);
            }
        }
    }

    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
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

    private static async Task Master_PollsHoldingFloat_Tcp()
    {
        var variable = FloatVariable("Temperature");
        var master = CreateMaster("tcp", variable, "registerType=\"holding\";address=\"100\";eventRef=\"poll20ms\"");
        var slave = AttachSimulatedSlave(master, tcp: true);

        // 123.456f = 0x42F6E979, big word order.
        slave.Holding[100] = 0x42F6;
        slave.Holding[101] = 0xE979;

        await master.StartAsync();
        var updated = await WaitUntilAsync(() => Math.Abs((float)variable.GetValue() - 123.456f) < 0.001f);
        await master.StopAsync();

        Check(updated, "master TCP: polled float landed in the variable");
        Check(master.State == CommunicationState.Stopped, "master TCP: clean stop");
    }

    private static async Task Master_PollsCoil_Rtu()
    {
        var variable = UShortVariable("Alarm");
        var master = CreateMaster("rtu", variable, "registerType=\"coil\";address=\"7\";eventRef=\"poll20ms\"");
        var slave = AttachSimulatedSlave(master, tcp: false);
        slave.Coils[7] = true;

        await master.StartAsync();
        var updated = await WaitUntilAsync(() => (ushort)variable.GetValue() == 1);
        await master.StopAsync();

        Check(updated, "master RTU: polled coil landed in the variable as 1");
    }

    private static async Task Master_OperatorWrite_WithEchoSuppression()
    {
        var variable = FloatVariable("Setpoint");
        var master = CreateMaster("tcp", variable,
            "registerType=\"holding\";address=\"10\";direction=\"readWrite\";eventRef=\"poll20ms\"");
        var slave = AttachSimulatedSlave(master, tcp: true);
        slave.Holding[10] = 0;
        slave.Holding[11] = 0;

        var protocolVariable = master.Variables.Single();
        // Reproduce the ModuleBase wiring: value-changed notifications feed the write path.
        protocolVariable.SubscribeAsyncValueChanged(pv => master.WriteVariableAsync(pv));

        await master.StartAsync();
        await WaitUntilAsync(() => master.State == CommunicationState.Running);
        await Task.Delay(100); // several poll cycles

        var writesFromPolling = slave.Holding[10] != 0 || slave.Holding[11] != 0;
        Check(!writesFromPolling, "echo suppression: poll updates never write back to the device");

        variable.SetValue(123.456f);
        await protocolVariable.NotifyValueChangedAsync();
        await master.StopAsync();

        Check(slave.Holding[10] == 0x42F6 && slave.Holding[11] == 0xE979,
            "operator write: FC16 delivered the float to the device registers");
        Check(master.CanWriteVariable(protocolVariable), "readWrite variable reports writable");
    }

    /// <summary>
    /// On Request event: a scalar and a matrix bound to it are not polled while a periodic
    /// variable is; CanReadVariable reports them (not the polled one); ReadVariableAsync performs
    /// one read transaction each and lands the values; a read after stop throws.
    /// </summary>
    private static async Task Master_OnRequestRead_ScalarAndMatrix_Tcp()
    {
        var master = new ModbusMasterProtocol
        {
            IsEnabled = true,
            RawSettings = "mode=\"tcp\";unitId=\"1\";requestTimeoutMs=\"100\";requestRetries=\"1\""
        };
        master.SetConfiguration();

        var onRequestEvent = new OnRequestVarEvent { Name = "onRequest" };
        IVarEvent[] events = [Poll20Ms, onRequestEvent];

        var temperature = FloatVariable("Temperature");
        var polled = UShortVariable("Alarm");
        polled.Id = 2;
        var map = new MatrixVariable
        {
            Id = 3,
            Namespace = "/",
            Name = "IgnitionMap",
            Label = "Ignition map",
            DefaultDataType = ValueDataType.UShort,
            Endianness = MatrixEndianness.Big,
            XAxis = new MatrixSection { Count = 2 },
            YAxis = new MatrixSection { Count = 3 },
            Data = new MatrixSection()
        };

        var temperatureVariable = master.CreateProtocolVariable(temperature, events,
            "registerType=\"holding\";address=\"100\";direction=\"read\";eventRef=\"onRequest\"", true)!;
        var polledVariable = master.CreateProtocolVariable(polled, events,
            "registerType=\"holding\";address=\"200\";eventRef=\"poll20ms\"", true)!;
        var mapVariable = master.CreateProtocolVariable(map, events,
            "registerType=\"holding\";address=\"300\";direction=\"readWrite\";eventRef=\"onRequest\"", true)!;
        master.AddVariable(temperatureVariable);
        master.AddVariable(polledVariable);
        master.AddVariable(mapVariable);

        var slave = AttachSimulatedSlave(master, tcp: true);
        slave.Holding[100] = 0x42F6; // 123.456f, big word order
        slave.Holding[101] = 0xE979;
        slave.Holding[200] = 7;
        for (ushort i = 0; i < 11; i++) // 2 + 3 + 6 ushort cells = 22 B = 11 registers
        {
            slave.Holding[(ushort)(300 + i)] = (ushort)(1000 + i);
        }

        Check(master.CanReadVariable(temperatureVariable) && master.CanReadVariable(mapVariable),
            "on request: scalar and matrix on the On Request event are readable on request");
        Check(!master.CanReadVariable(polledVariable), "on request: periodically polled variable is NOT readable on request");

        await master.StartAsync();
        var polledLanded = await WaitUntilAsync(() => (ushort)polled.GetValue() == 7);
        await Task.Delay(60); // several poll cycles
        Check(polledLanded, "on request: periodic variable is polled");
        Check((float)temperature.GetValue() == 0f && map.GetEngValue(MatrixSectionKind.Data, 0) == 0,
            "on request: On Request variables are not polled");

        var notified = 0;
        temperatureVariable.SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref notified); return Task.CompletedTask; });

        await master.ReadVariableAsync(temperatureVariable);
        Check(Math.Abs((float)temperature.GetValue() - 123.456f) < 0.001f, "on request: scalar read landed the float");
        Check(notified == 1, "on request: exactly one notification per scalar read");

        await master.ReadVariableAsync(mapVariable);
        Check(map.GetEngValue(MatrixSectionKind.XAxis, 1) == 1001 &&
              map.GetEngValue(MatrixSectionKind.YAxis, 2) == 1004 &&
              map.GetEngValue(MatrixSectionKind.Data, 5) == 1010,
            "on request: matrix read landed the whole register block (axes + data)");

        await Task.Delay(60);
        Check(notified == 1, "on request: no further scalar update without a request");

        await master.StopAsync();
        await CheckThrowsAsync<ModbusProtocolException>(() => master.ReadVariableAsync(temperatureVariable),
            "on request: read after stop throws ModbusProtocolException");
    }

    private static async Task Master_InvalidSettings_Faults()
    {
        var protocol = new ModbusMasterProtocol { IsEnabled = true, RawSettings = "unitId=\"1\"" }; // mode missing
        protocol.SetConfiguration();

        await protocol.StartAsync();

        Check(protocol.State == CommunicationState.Faulted, "master invalid settings: protocol faults on start");
        Check(protocol.StateMessage?.Contains("mode") == true, "master invalid settings: message names the missing key");
    }

    private static async Task Slave_ServesAndWrites_Tcp()
    {
        var served = FloatVariable("Flow", 123.456f);
        var writable = UShortVariable("Mode", 5);

        var slave = new ModbusSlaveProtocol { IsEnabled = true, RawSettings = "mode=\"tcp\";unitId=\"1\"" };
        slave.SetConfiguration();
        slave.AddVariable(slave.CreateProtocolVariable(served, null!, "registerType=\"input\";address=\"0\"", true)!);
        writable.Id = 2;
        slave.AddVariable(slave.CreateProtocolVariable(writable, null!, "registerType=\"holding\";address=\"10\"", true)!);

        var sent = new List<byte[]>();
        slave.SetTransmitter((frame, _) =>
        {
            lock (sent)
            {
                sent.Add(frame);
            }

            return Task.CompletedTask;
        });

        await slave.StartAsync();
        Check(slave.State == CommunicationState.Running, "slave TCP: running");

        var framer = new ModbusTcpFramer();

        // Read the input registers (float, 2 words).
        await slave.AddReceivedDataToQueueAsync([framer.Encode(new ModbusAdu(1, ModbusPdu.BuildReadRequest(0x04, 0, 2), 1))]);
        // Write the holding register.
        await slave.AddReceivedDataToQueueAsync([framer.Encode(new ModbusAdu(1, ModbusPdu.BuildWriteSingleRegister(10, 42), 2))]);

        Check(sent.Count == 2, "slave TCP: both requests answered");

        framer.Reset();
        framer.Append(sent[0]);
        Check(framer.TryDequeueFrame(out var readResponse) &&
              ModbusPdu.ParseReadRegistersResponse(readResponse.Pdu, 2).SequenceEqual(new ushort[] { 0x42F6, 0xE979 }),
            "slave TCP: served the float variable as input registers");

        Check((ushort)writable.GetValue() == 42, "slave TCP: FC06 write assigned the variable");

        await slave.StopAsync();
    }

    private static async Task Slave_IgnoresForeignUnit_Rtu()
    {
        var variable = UShortVariable("Speed", 7);
        var slave = new ModbusSlaveProtocol { IsEnabled = true, RawSettings = "mode=\"rtu\";unitId=\"1\"" };
        slave.SetConfiguration();
        slave.AddVariable(slave.CreateProtocolVariable(variable, null!, "registerType=\"holding\";address=\"0\"", true)!);

        var sentCount = 0;
        slave.SetTransmitter((_, _) =>
        {
            Interlocked.Increment(ref sentCount);
            return Task.CompletedTask;
        });

        await slave.StartAsync();
        var framer = new ModbusRtuFramer(ModbusFramerRole.Slave);
        await slave.AddReceivedDataToQueueAsync([framer.Encode(new ModbusAdu(9, ModbusPdu.BuildReadRequest(0x03, 0, 1)))]);
        await slave.AddReceivedDataToQueueAsync([framer.Encode(new ModbusAdu(1, ModbusPdu.BuildReadRequest(0x03, 0, 1)))]);
        await slave.StopAsync();

        Check(sentCount == 1, "slave RTU: request for a foreign unit id ignored, own unit answered");
    }

    private static async Task Slave_OverlappingMap_Faults()
    {
        var first = FloatVariable("A");
        var second = UShortVariable("B");
        second.Id = 2;

        var slave = new ModbusSlaveProtocol { IsEnabled = true, RawSettings = "mode=\"tcp\"" };
        slave.SetConfiguration();
        slave.AddVariable(slave.CreateProtocolVariable(first, null!, "registerType=\"holding\";address=\"0\"", true)!);
        slave.AddVariable(slave.CreateProtocolVariable(second, null!, "registerType=\"holding\";address=\"1\"", true)!);

        await slave.StartAsync();

        Check(slave.State == CommunicationState.Faulted, "slave: overlapping register map faults on start");
        Check(slave.StateMessage?.Contains("overlaps") == true, "slave: overlap message names the conflict");
    }

    private static void Slave_CommParams_RoundTrip()
    {
        var variable = FloatVariable("X");
        var spec = ModbusVariableSpecification.Create(
            "registerType=\"holding\";address=\"0x10\";wordOrder=\"little\";direction=\"readWrite\";eventRef=\"poll20ms\"",
            [Poll20Ms], variable, requirePollEvent: true);

        var reparsed = ModbusVariableSpecification.Create(spec.CommParams, [Poll20Ms], variable, requirePollEvent: true);

        Check(reparsed.RegisterType == ModbusRegisterType.HoldingRegister &&
              reparsed.Address == 0x10 &&
              reparsed.WordOrder == ModbusWordOrder.LittleEndian &&
              reparsed.Direction == spec.Direction &&
              ReferenceEquals(reparsed.VariableEvent, Poll20Ms),
            "commParams round-trip preserves the full mapping");
    }
    private static void MasterSettings_NewAndLegacyNames()
    {
        var current = ModbusMasterSettings.Parse("mode=rtu;unitId=1;requestTimeoutMs=250;requestRetries=4");
        Check(current.TimeoutMs == 250 && current.Retries == 4, "master settings: requestTimeoutMs / requestRetries parsed");

        var former = ModbusMasterSettings.Parse("mode=rtu;unitId=1;timeoutMs=300;retries=5");
        Check(former.TimeoutMs == 1000 && former.Retries == 2, "master settings: former timeoutMs / retries are not read any more");

        var defaults = ModbusMasterSettings.Parse(new ModbusMasterProtocol().DefaultRawSettings);
        Check(defaults.TimeoutMs == 1000 && defaults.Retries == 2, "master settings: default raw settings parse");
    }
}
