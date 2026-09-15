// Headless checks of JsonSignalProtocol (newline-delimited JSON in both directions):
//   T1  lines are framed and matched by name (case-insensitive), values converted to the
//       variable type, "t" rebuilds timestamps, a line split across two chunks is joined
//   T2  the first fragment after a connect is dropped silently; then dropped input is reported
//       once per cause (unparsable line, unknown name, bad value) and never again for the same cause
//   T3  commParam: only "name" is read (older aliases id/signal/signalName are ignored, the
//       variable name is the fallback), direction is parsed, ToCommParam round-trips
//   T4  writes: only write/readWrite variables are writable, WriteVariableAsync sends one
//       {"name": ..., "value": ...} line through the injected transmitter, incoming messages
//       never land in a write-only variable
//   T5  a stray protocol setting is reported as unknown
using System.Globalization;
using System.Text;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.JsonSignalProtocol;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

var failures = 0;

void Check(bool condition, string description)
{
    if (condition) return;
    failures++;
    Console.WriteLine($"FAILED: {description}");
}

static ScalarVariable DoubleVariable(int id, string name) => new()
{
    Id = id, Namespace = "/", Name = name, Label = name,
    Values = new Values<double> { Value = 0d, ValueType = ValueDataType.Double }
};

static ScalarVariable IntVariable(int id, string name) => new()
{
    Id = id, Namespace = "/", Name = name, Label = name,
    Values = new Values<int> { Value = 0, ValueType = ValueDataType.Int }
};

static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

static double Raw(ScalarVariable v) => Convert.ToDouble(v.Values.GetValue(), CultureInfo.InvariantCulture);

// ---- T1 framing, matching, conversion, timestamps
{
    var logger = new CapturingLogger();
    var protocol = new JsonSignalProtocol { Logger = logger, IsEnabled = true };
    var temp = DoubleVariable(1, "Temp");
    var rpm = IntVariable(2, "Rpm");
    protocol.AddVariable(protocol.CreateProtocolVariable(temp, "direction=\"read\";name=\"temp\"", true)!);
    protocol.AddVariable(protocol.CreateProtocolVariable(rpm, "name=\"RPM\"", true)!);

    var changed = new List<string>();
    foreach (var pv in protocol.Variables)
    {
        pv.SubscribeAsyncValueChanged(v => { changed.Add(v.Variable.Name); return Task.CompletedTask; });
    }

    await protocol.StartAsync();
    // one chunk with two lines, the second line split across two chunks, a stray "\r\n"
    await protocol.AddReceivedDataToQueueAsync([Bytes("{\"name\": \"TEMP\", \"value\": 23.4, \"t\": 10.0}\n{\"name\": \"rpm\", \"val")]);
    await protocol.AddReceivedDataToQueueAsync([Bytes("ue\": 1850.6, \"t\": 10.5}\r\n")]);
    await Task.Delay(200);

    Check(Math.Abs(Raw(temp) - 23.4) < 1e-9, "T1 temp matched case-insensitively and set");
    Check(rpm.Values.GetValue() is int i && i == 1851, $"T1 rpm converted to int with rounding (got {rpm.Values.GetValue()})");
    Check(changed.Count == 2 && changed.Contains("Temp") && changed.Contains("Rpm"), $"T1 both variables notified ({string.Join(",", changed)})");
    var dt = (rpm.Timestamp - temp.Timestamp).TotalSeconds;
    Check(Math.Abs(dt - 0.5) < 0.05, $"T1 timestamps follow t (delta {dt:F3} s, expected 0.5)");
    Check(logger.Messages.Count == 0, $"T1 no warnings for good input ({logger.Messages.Count})");
    await protocol.StopAsync();
    Console.WriteLine("T1 done");
}

// ---- T2 dropped input reported once per cause
{
    var logger = new CapturingLogger();
    var protocol = new JsonSignalProtocol { Logger = logger, IsEnabled = true };
    var temp = DoubleVariable(1, "Temp");
    protocol.AddVariable(protocol.CreateProtocolVariable(temp, "name=\"temp\"", true)!);
    await protocol.StartAsync();

    // the first fragment after connecting (tail of a message already in flight) is dropped silently
    await protocol.AddReceivedDataToQueueAsync([Bytes("\"value\": 1.5, \"t\": 3}\n")]);
    await protocol.AddReceivedDataToQueueAsync([Bytes("this is not json\n{\"foo\": 1}\n{\"name\": \"other\", \"value\": 1}\n{\"name\": \"other\", \"value\": 2}\n{\"name\": \"temp\", \"value\": \"abc\"}\n{\"name\": \"temp\", \"value\": \"def\"}\nstill not json\n")]);
    await Task.Delay(200);

    var warns = logger.Messages.Where(m => m.Level == LogLevel.Warn).Select(m => m.Text).ToList();
    Check(warns.Count == 3, $"T2 exactly three warnings, one per cause (got {warns.Count}: {string.Join(" | ", warns)})");
    Check(warns.Any(w => w.Contains("line ignored") && w.Contains("this is not json")), "T2 first unparsable line quoted");
    Check(warns.Any(w => w.Contains("name=\"other\"")), "T2 unknown name reported once");
    Check(warns.Any(w => w.Contains("cannot be converted")), "T2 bad value reported once");
    Check(Math.Abs(Raw(temp)) < 1e-9, "T2 bad values left the variable untouched");
    await protocol.StopAsync();
    Console.WriteLine("T2 done");
}

// ---- T3 commParam keys and round trip
{
    var protocol = new JsonSignalProtocol();
    var v = DoubleVariable(1, "Speed");

    var legacy = (JsonSignalProtocolVariableSpecification)protocol.CreateProtocolVariable(v, "id=\"velocity\";signal=\"x\"", true)!.ProtocolVariableSpecification;
    Check(legacy.SignalName == "Speed" && legacy.Direction == CommDirection.Read, $"T3 aliases ignored, variable name is the fallback (got '{legacy.SignalName}')");

    var named = (JsonSignalProtocolVariableSpecification)protocol.CreateProtocolVariable(v, "direction=\"readWrite\";name=\"setpoint\"", true)!.ProtocolVariableSpecification;
    Check(named.SignalName == "setpoint" && named.Direction == CommDirection.ReadWrite, "T3 name and direction parsed");
    Check(named.ToCommParam() == "direction=\"readwrite\";name=\"setpoint\"", $"T3 ToCommParam writes only direction and name (got '{named.ToCommParam()}')");

    var reread = JsonSignalProtocolVariableSpecification.Create(named.ToCommParam(), v.Name);
    Check(reread.SignalName == "setpoint" && reread.Direction == CommDirection.ReadWrite, "T3 round trip keeps name and direction");
    Check(protocol.CreateDefaultCommParam(v, []) == "direction=\"read\";name=\"Speed\"", $"T3 default template (got '{protocol.CreateDefaultCommParam(v, [])}')");
    Console.WriteLine("T3 done");
}

// ---- T4 writes
{
    var logger = new CapturingLogger();
    var protocol = new JsonSignalProtocol { Logger = logger, IsEnabled = true };
    var measured = DoubleVariable(1, "Measured");
    var setpoint = DoubleVariable(2, "Setpoint");
    var both = DoubleVariable(3, "Both");
    var pvMeasured = protocol.CreateProtocolVariable(measured, "direction=\"read\";name=\"measured\"", true)!;
    var pvSetpoint = protocol.CreateProtocolVariable(setpoint, "direction=\"write\";name=\"setpoint\"", true)!;
    var pvBoth = protocol.CreateProtocolVariable(both, "direction=\"readWrite\";name=\"both\"", true)!;
    protocol.AddVariable(pvMeasured);
    protocol.AddVariable(pvSetpoint);
    protocol.AddVariable(pvBoth);

    Check(!protocol.CanWriteVariable(pvMeasured) && protocol.CanWriteVariable(pvSetpoint) && protocol.CanWriteVariable(pvBoth),
        "T4 only write/readWrite variables are writable");

    var sent = new List<string>();
    var noTransport = false;
    try { await protocol.WriteVariableAsync(pvSetpoint); } catch (InvalidOperationException) { noTransport = true; }
    Check(noTransport, "T4 write without a transmitter throws InvalidOperationException");

    protocol.SetTransmitter((bytes, _) => { sent.Add(Encoding.UTF8.GetString(bytes)); return Task.CompletedTask; });
    setpoint.Values.SetValue(42.5);
    await protocol.WriteVariableAsync(pvSetpoint);
    Check(sent.Count == 1 && sent[0] == "{\"name\": \"setpoint\", \"value\": 42.5}\n", $"T4 one JSON line sent (got '{(sent.Count > 0 ? sent[0].TrimEnd() : "")}')");

    await protocol.StartAsync();
    await protocol.AddReceivedDataToQueueAsync([Bytes("{\"name\": \"setpoint\", \"value\": 7}\n{\"name\": \"both\", \"value\": 8}\n")]);
    await Task.Delay(200);
    Check(Math.Abs(Raw(setpoint) - 42.5) < 1e-9, "T4 incoming message does not land in a write-only variable");
    Check(Math.Abs(Raw(both) - 8) < 1e-9, "T4 incoming message lands in a readWrite variable");
    Check(logger.Messages.Any(m => m.Text.Contains("name=\"setpoint\"")), "T4 write-only name reported as not readable");
    await protocol.StopAsync();
    Console.WriteLine("T4 done");
}

// ---- T5 stray protocol setting
{
    var logger = new CapturingLogger();
    var protocol = new JsonSignalProtocol { Logger = logger, IsEnabled = true, RawSettings = "foo=1" };
    protocol.SetConfiguration();
    Check(logger.Messages.Count == 1 && logger.Messages[0].Level == LogLevel.Warn && logger.Messages[0].Text.Contains("'foo'"),
        "T5 unknown protocol setting reported");
    Console.WriteLine("T5 done");
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Text)> Messages { get; } = [];
    public void RegisterSubscriber(ILogSubscriber subscriber) { }
    public void UnRegisterSubscriber(ILogSubscriber subscriber) { }
    public void Log(ILogMessage message) => Messages.Add((message.Level, message.Message));
    public void Log(LogLevel level, string message, Exception? exception = null) => Messages.Add((level, message));
    public Task LogAsync(ILogMessage message, CancellationToken ct) { Log(message); return Task.CompletedTask; }
    public Task LogAsync(LogLevel level, string message, Exception? exception = null, CancellationToken ct = default) { Log(level, message, exception); return Task.CompletedTask; }
    public void Enable() { }
    public void Disable() { }
}
