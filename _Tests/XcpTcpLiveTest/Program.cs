using System.Globalization;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.TcpClientDriver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.XcpTcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

namespace Qenex.QSuite.Tests.XcpTcpLiveTest;

/// <summary>
/// Manual smoke test of the real TcpClientDriver + XcpTcp chain against a live XCP on Ethernet
/// slave (XCPlite hello_xcp on localhost). Connect-only when no variables are given; with
/// variable specs it polls (or acquires via DAQ) for a few seconds, prints the values and
/// optionally writes.
///
///   XcpTcpLiveTest &lt;ip&gt; &lt;port&gt; [ts=slave|master] [name:type:0xADDRESS[:ext][:daq=CHANNEL][:write=VALUE]] ...
///   e.g. XcpTcpLiveTest 127.0.0.1 5555 global_counter:UInt:0x1A0 heat_energy:Double:0x1A8
///        flow_rate:Float:0x10004:write=0.5
///        XcpTcpLiveTest 127.0.0.1 5555 global_counter:UInt:0x1A0:daq=0 heat_energy:Double:0x1A8:daq=1
///
/// daq=N binds the variable to ECU event channel N (measurement via a DAQ list instead of
/// polling; the channel's ECU name is validated and logged at connect). ts=slave (default) puts
/// DAQ samples onto the ECU clock (daqTimestamps=slave), ts=master stamps them with the PC
/// receive time (daqTimestamps=master, no timestamp on the wire with a QFW SDK slave). Addresses come from the
/// A2L file the slave generates (ECU_ADDRESS). Exit code 0 = session established (and every
/// acquired variable changed at least once).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: XcpTcpLiveTest <ip> <port> [ts=slave|master] [name:type:0xADDRESS[:ext][:daq=CHANNEL][:write=VALUE]] ...");
            return 2;
        }

        return Run(args[0], int.Parse(args[1], CultureInfo.InvariantCulture), args.Skip(2).ToArray())
            .GetAwaiter().GetResult();
    }

    private static async Task<int> Run(string ip, int port, string[] variableSpecs)
    {
        var logger = new ConsoleLogger();

        // ts=slave|master -> the protocol's daqTimestamps setting (DAQ time axis source).
        var timestampMode = "slave";
        var specs = new List<string>();
        foreach (var arg in variableSpecs)
        {
            if (!arg.StartsWith("ts=", StringComparison.OrdinalIgnoreCase))
            {
                specs.Add(arg);
                continue;
            }

            timestampMode = arg["ts=".Length..].ToLowerInvariant();
            if (timestampMode is not ("slave" or "master"))
            {
                Console.WriteLine($"invalid option '{arg}' (expected ts=slave or ts=master)");
                return 2;
            }
        }

        variableSpecs = specs.ToArray();

        var driver = new TcpClientDriver
        {
            Label = "XCPlite",
            IsEnabled = true,
            Logger = logger,
            // idleTimeoutMs=0: request/response protocol, a quiet line is normal.
            RawSettings = $"ip={ip};port={port};connectionTimeoutMs=3000;reconnectTimeMs=1000;numberOfReconnections=2;idleTimeoutMs=0"
        };
        driver.SetConfiguration();

        var protocol = new XcpTcp
        {
            IsEnabled = true,
            Logger = logger,
            RawSettings = $"requestTimeoutMs=1000;daqTimestamps={timestampMode}"
        };
        protocol.SetConfiguration();
        protocol.StateChanged += (_, e) => Console.WriteLine($"[state] protocol: {e.CurrentState} {e.Message}");
        driver.StateChanged += (_, e) => Console.WriteLine($"[state] driver:   {e.CurrentState} {e.Message}");
        driver.AddProtocol(protocol);

        var pollEvent = new PeriodicVarEvent { Name = "poll100ms", Period = 100, Unit = TimeUnit.Milisec };
        var daqEvents = new Dictionary<ushort, PeriodicVarEvent>();
        var polled = new List<(ScalarVariable Variable, object InitialValue)>();
        var writes = new List<(ScalarVariable Variable, double Value)>();

        var id = 0;
        var daqVariableCount = 0;
        foreach (var spec in variableSpecs)
        {
            // name:type:0xADDRESS[:ext][:daq=CHANNEL][:write=VALUE]
            var parts = spec.Split(':');
            if (parts.Length < 3)
            {
                Console.WriteLine($"invalid variable spec '{spec}'");
                return 2;
            }

            var type = Enum.Parse<ValueDataType>(parts[1], ignoreCase: true);
            var extension = parts.Skip(3).FirstOrDefault(p => byte.TryParse(p, out _)) ?? "0";
            var writePart = parts.FirstOrDefault(p => p.StartsWith("write=", StringComparison.OrdinalIgnoreCase));
            var daqPart = parts.FirstOrDefault(p => p.StartsWith("daq=", StringComparison.OrdinalIgnoreCase));
            var direction = writePart != null ? "readWrite" : "read";

            var variableEvent = pollEvent;
            if (daqPart != null)
            {
                var channel = ushort.Parse(daqPart["daq=".Length..], CultureInfo.InvariantCulture);
                if (!daqEvents.TryGetValue(channel, out var daqEvent))
                {
                    // The period is only the expected nominal cycle — the ECU's own timing drives
                    // the acquisition; a mismatch against TIMECYCLE is reported at connect.
                    daqEvents[channel] = daqEvent = new PeriodicVarEvent
                    {
                        Name = $"daq{channel}",
                        Period = 1,
                        Unit = TimeUnit.Milisec,
                        EventExtraParams = $"direction=\"DAQ\";daqId=\"{channel}\""
                    };
                }

                variableEvent = daqEvent;
            }

            var variable = CreateScalar(++id, parts[0], type);
            var protocolVariable = protocol.CreateProtocolVariable(variable, [pollEvent, .. daqEvents.Values],
                $"address=\"{parts[2]}\";addressExtension=\"{extension}\";direction=\"{direction}\";eventRef=\"{variableEvent.Name}\"", true);
            if (protocolVariable == null)
            {
                Console.WriteLine($"variable spec '{spec}' was rejected by the protocol");
                return 2;
            }

            protocol.AddVariable(protocolVariable);
            polled.Add((variable, variable.GetValue()));
            if (daqPart != null)
            {
                daqVariableCount++;
            }

            if (writePart != null)
            {
                writes.Add((variable, double.Parse(writePart["write=".Length..], CultureInfo.InvariantCulture)));
            }
        }

        Console.WriteLine($"connecting to {ip}:{port} with {polled.Count} variable(s) " +
                          $"({polled.Count - daqVariableCount} polled, {daqVariableCount} via DAQ" +
                          $"{(daqEvents.Count == 0 ? "" : $" on channel(s) {string.Join(", ", daqEvents.Keys)}")}), " +
                          $"daqTimestamps={timestampMode}...");
        await driver.StartAsync();

        var connected = await WaitUntilAsync(() => protocol.State == CommunicationState.Running, 8000);
        if (!connected)
        {
            Console.WriteLine($"FAILED: protocol did not reach Running ({protocol.State}: {protocol.StateMessage})");
            await driver.StopAsync();
            return 1;
        }

        var ok = true;
        if (polled.Count > 0)
        {
            await Task.Delay(1500);
            Console.WriteLine("--- polled values after 1.5 s ---");
            foreach (var (variable, initialValue) in polled)
            {
                var value = variable.GetValue();
                var changed = !Equals(value, initialValue);
                Console.WriteLine($"  {variable.Name} = {value} (initial {initialValue}{(changed ? "" : " — UNCHANGED")})");
            }

            foreach (var (variable, value) in writes)
            {
                var protocolVariable = protocol.Variables.First(v => v.Variable == variable);
                variable.SetValue(Convert.ChangeType(value, variable.GetValue().GetType(), CultureInfo.InvariantCulture));
                await protocol.WriteVariableAsync(protocolVariable);
                Console.WriteLine($"  wrote {variable.Name} = {value}");
            }

            await Task.Delay(1000);
            Console.WriteLine("--- polled values after write ---");
            foreach (var (variable, initialValue) in polled)
            {
                var value = variable.GetValue();
                Console.WriteLine($"  {variable.Name} = {value}");
                ok &= !Equals(value, initialValue) ||
                      writes.Any(w => w.Variable == variable); // written vars may legitimately stay at the written value
            }
        }

        await driver.StopAsync();
        Console.WriteLine(ok ? "LIVE TEST PASSED" : "LIVE TEST FAILED (a polled value never changed)");
        return ok ? 0 : 1;
    }

    private static ScalarVariable CreateScalar(int id, string name, ValueDataType type)
    {
        return new ScalarVariable
        {
            Id = id,
            Name = name,
            Values = type switch
            {
                ValueDataType.Byte => new Values<byte> { Value = 0, ValueType = type },
                ValueDataType.SByte => new Values<sbyte> { Value = 0, ValueType = type },
                ValueDataType.UShort => new Values<ushort> { Value = 0, ValueType = type },
                ValueDataType.Short => new Values<short> { Value = 0, ValueType = type },
                ValueDataType.UInt => new Values<uint> { Value = 0, ValueType = type },
                ValueDataType.Int => new Values<int> { Value = 0, ValueType = type },
                ValueDataType.ULong => new Values<ulong> { Value = 0, ValueType = type },
                ValueDataType.Long => new Values<long> { Value = 0, ValueType = type },
                ValueDataType.Float => new Values<float> { Value = 0f, ValueType = type },
                ValueDataType.Double => new Values<double> { Value = 0d, ValueType = type },
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported scalar type.")
            }
        };
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>Prints protocol/driver diagnostics straight to the console.</summary>
    private sealed class ConsoleLogger : ILogger
    {
        public void RegisterSubscriber(ILogSubscriber subscriber) { }
        public void UnRegisterSubscriber(ILogSubscriber subscriber) { }

        public void Log(ILogMessage message) => Console.WriteLine($"[log] {message}");

        public void Log(LogLevel level, string message, Exception? exception = default)
            => Console.WriteLine($"[{level.ToString().ToLowerInvariant()}] {message}{(exception == null ? "" : $" ({exception.Message})")}");

        public Task LogAsync(ILogMessage message, CancellationToken ct)
        {
            Log(message);
            return Task.CompletedTask;
        }

        public Task LogAsync(LogLevel level, string message, Exception? exception = default, CancellationToken ct = default)
        {
            Log(level, message, exception);
            return Task.CompletedTask;
        }

        public void Enable() { }
        public void Disable() { }
    }
}
