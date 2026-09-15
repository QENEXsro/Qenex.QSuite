// Headless checks of RawCanProtocol (CAN frames mapped to variables by id, both directions):
//   T1  decoding: float32 little-endian at offset 0, float32 at offset 4, big-endian int16,
//       a short frame is zero-padded, an unrelated id and a mismatching extended flag are ignored
//   T2  timestamps follow the adapter's microsecond counter; a counter restart re-aligns
//   T3  commParam: round trip of direction/canId/offset/byteOrder/extended, inferred extended id,
//       missing or malformed values throw (and CreateProtocolVariable reports and returns null)
//   T4  writes: only write/readWrite variables are writable, WriteVariableAsync sends one frame with
//       the id, offset and byte order of the variable; an incoming frame never lands in a write-only
//       variable but does in a readWrite one
//   T5  a stray protocol setting is reported as unknown; an unsupported variable type is reported once
using System.Buffers.Binary;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Protocols.RawCanProtocol;
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

static ScalarVariable FloatVariable(int id, string name) => new()
{
    Id = id, Namespace = "/", Name = name, Label = name,
    Values = new Values<float> { Value = 0f, ValueType = ValueDataType.Float }
};

static ScalarVariable ShortVariable(int id, string name) => new()
{
    Id = id, Namespace = "/", Name = name, Label = name,
    Values = new Values<short> { Value = 0, ValueType = ValueDataType.Short }
};

static byte[] FloatPair(float a, float b)
{
    var data = new byte[8];
    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0, 4), a);
    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4, 4), b);
    return data;
}

static float F(ScalarVariable v) => (float)v.Values.GetValue();

// ---- T1 decoding
{
    var logger = new CapturingLogger();
    var protocol = new RawCanProtocol { Logger = logger, IsEnabled = true };
    var value = FloatVariable(1, "Rnd1");
    var time = FloatVariable(2, "Rnd1Time");
    var be = ShortVariable(3, "BigEndian");
    var ext = FloatVariable(4, "Extended");
    protocol.AddVariable(protocol.CreateProtocolVariable(value, "canId=\"0x100\";offset=\"0\";byteOrder=\"le\"", true)!);
    protocol.AddVariable(protocol.CreateProtocolVariable(time, "canId=\"0x100\";offset=\"4\"", true)!);
    protocol.AddVariable(protocol.CreateProtocolVariable(be, "canId=\"0x101\";byteOrder=\"be\"", true)!);
    protocol.AddVariable(protocol.CreateProtocolVariable(ext, "canId=\"0x18DAF100\"", true)!);
    await protocol.StartAsync();

    await protocol.AddReceivedDataToQueueAsync([
        new CanFrame(0x100, FloatPair(1.5f, 12.25f)),
        new CanFrame(0x101, [0x01, 0x02]),                       // big-endian 0x0102 = 258
        new CanFrame(0x102, FloatPair(9f, 9f)),                  // nobody listens on 0x102
        new CanFrame(0x18DAF100, FloatPair(3.5f, 0f), isExtended: true),
        new CanFrame(0x100, FloatPair(2.5f, 0f)[..2], isExtended: true) // same id but extended: ignored
    ]);

    Check(Math.Abs(F(value) - 1.5f) < 1e-6, $"T1 float at offset 0 ({F(value)})");
    Check(Math.Abs(F(time) - 12.25f) < 1e-6, $"T1 float at offset 4 ({F(time)})");
    Check((short)be.Values.GetValue() == 258, $"T1 big-endian int16 ({be.Values.GetValue()})");
    Check(Math.Abs(F(ext) - 3.5f) < 1e-6, $"T1 extended id decoded ({F(ext)})");
    await protocol.AddReceivedDataToQueueAsync([new CanFrame(0x101, [0x7F])]);            // short frame: 0x7F00 zero-padded -> 0x7F, then reversed
    Check((short)be.Values.GetValue() == 0x7F00, $"T1 short frame zero-padded ({be.Values.GetValue():X})");
    Check(logger.Messages.Count == 0, $"T1 no warnings ({logger.Messages.Count})");
    Console.WriteLine("T1 done");
}

// ---- T2 adapter timestamps
{
    var protocol = new RawCanProtocol { IsEnabled = true };
    var v = FloatVariable(1, "V");
    protocol.AddVariable(protocol.CreateProtocolVariable(v, "canId=\"0x100\"", true)!);
    await protocol.StartAsync();
    await protocol.AddReceivedDataToQueueAsync([new CanFrame(0x100, FloatPair(1f, 0f), timestampMicroseconds: 1_000_000)]);
    var t1 = v.Timestamp;
    await protocol.AddReceivedDataToQueueAsync([new CanFrame(0x100, FloatPair(2f, 0f), timestampMicroseconds: 1_250_000)]);
    var t2 = v.Timestamp;
    Check(Math.Abs((t2 - t1).TotalMilliseconds - 250) < 1, $"T2 timestamps follow the adapter counter ({(t2 - t1).TotalMilliseconds} ms, expected 250)");
    await protocol.AddReceivedDataToQueueAsync([new CanFrame(0x100, FloatPair(3f, 0f), timestampMicroseconds: 10_000)]);
    var t3 = v.Timestamp;
    Check(Math.Abs((DateTime.UtcNow - t3).TotalSeconds) < 2, $"T2 counter restart re-aligns to the PC clock (off by {(DateTime.UtcNow - t3).TotalSeconds:F3} s)");
    Console.WriteLine("T2 done");
}

// ---- T3 commParam
{
    var logger = new CapturingLogger();
    var protocol = new RawCanProtocol { Logger = logger, IsEnabled = true };
    var v = FloatVariable(1, "V");

    var spec = RawCanProtocolVariableSpecification.Create("direction=\"readWrite\";canId=\"0x1A\";offset=\"2\";byteOrder=\"be\"");
    Check(spec.CanId == 0x1A && !spec.IsExtended && spec.Offset == 2 && spec.ByteOrder == RawCanByteOrder.BigEndian && spec.Direction == CommDirection.ReadWrite, "T3 keys parsed");
    Check(spec.ToCommParam() == "direction=\"readwrite\";canId=\"0x1A\";offset=\"2\";byteOrder=\"be\"", $"T3 round trip text (got '{spec.ToCommParam()}')");
    var reread = RawCanProtocolVariableSpecification.Create(spec.ToCommParam());
    Check(reread.CanId == 0x1A && reread.Offset == 2 && reread.ByteOrder == RawCanByteOrder.BigEndian && reread.Direction == CommDirection.ReadWrite, "T3 round trip re-read");

    var inferred = RawCanProtocolVariableSpecification.Create("canId=\"18DAF100\"");
    Check(inferred.IsExtended && inferred.CanId == 0x18DAF100 && !inferred.ToCommParam().Contains("extended"), "T3 extended inferred from a 29-bit id, not written back");
    var forced = RawCanProtocolVariableSpecification.Create("canId=\"0x100\";extended=\"true\"");
    Check(forced.IsExtended && forced.ToCommParam().EndsWith(";extended=\"true\""), "T3 extended forced for an 11-bit id is written back");
    Check(protocol.CreateDefaultCommParam(v, []) == "direction=\"read\";canId=\"0x100\";offset=\"0\";byteOrder=\"le\"", "T3 default template");

    string[] bad = ["offset=\"0\"", "canId=\"zz\"", "canId=\"0x100\";byteOrder=\"middle\"", "canId=\"0x100\";offset=\"9\"", "canId=\"0x100\";direction=\"up\"", "canId=\"0x800\";extended=\"false\""];
    var thrown = bad.Count(text => { try { RawCanProtocolVariableSpecification.Create(text); return false; } catch (ArgumentException) { return true; } });
    Check(thrown == bad.Length, $"T3 malformed commParams throw ({thrown}/{bad.Length})");
    var none = protocol.CreateProtocolVariable(v, "canId=\"zz\"", true);
    Check(none == null && logger.Messages.Count == 1 && logger.Messages[0].Level == LogLevel.Warn && logger.Messages[0].Text.Contains("'V'"),
        "T3 malformed commParam is reported and the variable left out");
    Console.WriteLine("T3 done");
}

// ---- T4 writes
{
    var logger = new CapturingLogger();
    var protocol = new RawCanProtocol { Logger = logger, IsEnabled = true };
    var measured = FloatVariable(1, "Measured");
    var setpoint = FloatVariable(2, "Setpoint");
    var both = ShortVariable(3, "Both");
    var pvMeasured = protocol.CreateProtocolVariable(measured, "direction=\"read\";canId=\"0x100\"", true)!;
    var pvSetpoint = protocol.CreateProtocolVariable(setpoint, "direction=\"write\";canId=\"0x180\";offset=\"4\"", true)!;
    var pvBoth = protocol.CreateProtocolVariable(both, "direction=\"readWrite\";canId=\"0x181\";byteOrder=\"be\"", true)!;
    protocol.AddVariable(pvMeasured);
    protocol.AddVariable(pvSetpoint);
    protocol.AddVariable(pvBoth);
    await protocol.StartAsync();

    Check(!protocol.CanWriteVariable(pvMeasured) && protocol.CanWriteVariable(pvSetpoint) && protocol.CanWriteVariable(pvBoth), "T4 only write/readWrite variables are writable");

    var sent = new List<CanFrame>();
    var noTransport = false;
    try { await protocol.WriteVariableAsync(pvSetpoint); } catch (InvalidOperationException) { noTransport = true; }
    Check(noTransport, "T4 write without a transmitter throws");

    protocol.SetTransmitter((frame, _) => { sent.Add(frame); return Task.CompletedTask; });
    setpoint.Values.SetValue(7.5f);
    await protocol.WriteVariableAsync(pvSetpoint);
    Check(sent.Count == 1 && sent[0].CanId == 0x180 && !sent[0].IsExtended && sent[0].Data.Length == 8
          && BinaryPrimitives.ReadSingleLittleEndian(sent[0].Data.AsSpan(4, 4)) == 7.5f && sent[0].Data.Take(4).All(b => b == 0),
        $"T4 float frame at offset 4 ({(sent.Count > 0 ? BitConverter.ToString(sent[0].Data) : "none")})");

    both.Values.SetValue((short)0x0102);
    await protocol.WriteVariableAsync(pvBoth);
    Check(sent.Count == 2 && sent[1].CanId == 0x181 && sent[1].Data.Length == 2 && sent[1].Data[0] == 0x01 && sent[1].Data[1] == 0x02,
        $"T4 big-endian int16 frame ({(sent.Count > 1 ? BitConverter.ToString(sent[1].Data) : "none")})");

    await protocol.AddReceivedDataToQueueAsync([new CanFrame(0x180, FloatPair(0f, 99f)), new CanFrame(0x181, [0x00, 0x2A])]);
    Check(Math.Abs(F(setpoint) - 7.5f) < 1e-6, "T4 incoming frame does not land in a write-only variable");
    Check((short)both.Values.GetValue() == 42, $"T4 incoming frame lands in a readWrite variable ({both.Values.GetValue()})");
    Console.WriteLine("T4 done");
}

// ---- T5 stray setting, unsupported type
{
    var logger = new CapturingLogger();
    var protocol = new RawCanProtocol { Logger = logger, IsEnabled = true, RawSettings = "foo=1" };
    protocol.SetConfiguration();
    Check(logger.Messages.Count == 1 && logger.Messages[0].Text.Contains("'foo'"), "T5 unknown protocol setting reported");

    var text = new StringVariable { Id = 1, Namespace = "/", Name = "Text", Label = "Text" };
    protocol.AddVariable(protocol.CreateProtocolVariable(text, "canId=\"0x100\"", true)!);
    await protocol.StartAsync();
    await protocol.AddReceivedDataToQueueAsync([new CanFrame(0x100, [1, 2]), new CanFrame(0x100, [3, 4])]);
    var typeWarnings = logger.Messages.Count(m => m.Text.Contains("unsupported type"));
    Check(typeWarnings == 1, $"T5 unsupported type reported once ({typeWarnings})");
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
