// E2E verification of FileDataLoggerDriver (write path) and
// FileDataReplayDriver + DataLogReplayProtocol (replay path).
// Exercises the same call chain QInsight uses (sink subscription -> reorder buffer ->
// writer loop -> .qilog file -> loader -> replay loop -> protocol -> variable).
//
// Scenarios:
//   T1  logger completeness: 10k sequential records, single variable
//   T2  logger under parallel producers: 4 variables x 2500 records
//   T3  reorder buffer sorts out-of-order timestamps inside the 500 ms window
//   T4  timestamps beyond the reorder window are not lost
//   T5  replay Immediate mode replays every record exactly once, in order
//   T6  replay Realtime mode replays every record with correct pacing
//   T7  a corrupted record is skipped, the REST of the file still replays,
//       the failure is logged and ReplayCompleted is raised
//   T8  records of an unknown variable are skipped WITH a warning,
//       known variables replay completely
//   T9  a not-running replay protocol drops records WITH a warning
//   T10 an out-of-order file replays without losing records
//   T11 settings: unknown keys and invalid values are reported (both drivers, both
//       protocols), re-applied settings fall back to defaults, empty comm-param templates
//   T12 logger: an unwritable log path leaves the driver Faulted with a message, no exception

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MessagePack;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.FileDataLoggerDriver;
using Qenex.QSuite.Drivers.FileDataReplayDriver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.DataLogReplayProtocol;
using Qenex.QSuite.Protocols.PassThroughProtocol;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

var results = new List<(string Name, bool Pass, string Detail)>();
var workRoot = Path.Combine(Path.GetTempPath(), "qilog-roundtrip-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);
Console.WriteLine($"Work dir: {workRoot}");

await RunTest("T1 logger: 10k sequential records, single variable", Test1_SequentialRoundTrip);
await RunTest("T2 logger: 4 variables x 2500 records, parallel producers", Test2_ParallelProducers);
await RunTest("T3 logger: out-of-order timestamps INSIDE 500ms window get sorted", Test3_ReorderInsideWindow);
await RunTest("T4 logger: out-of-order timestamps BEYOND 500ms window", Test4_ReorderBeyondWindow);
await RunTest("T5 replay: Immediate mode replays every record exactly once", Test5_ReplayImmediate);
await RunTest("T6 replay: Realtime mode replays every record, correct pacing", Test6_ReplayRealtime);
await RunTest("T7 replay: corrupted record is skipped, rest of file survives", Test7_CorruptFile);
await RunTest("T8 replay: unknown variable is skipped with a warning", Test8_UnknownVariable);
await RunTest("T9 replay: not-running protocol drops with a warning", Test9_DisabledProtocol);
await RunTest("T10 replay: out-of-order file loses nothing", Test10_OutOfOrderFile);
await RunTest("T11 settings: unknown keys, invalid values, defaults, comm-param templates", Test11_Settings);
await RunTest("T12 logger: unwritable log path -> Faulted with message", Test12_UnwritableLogPath);

Console.WriteLine();
Console.WriteLine("==== SUMMARY ====");
foreach (var (name, pass, detail) in results)
{
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
    if (!string.IsNullOrWhiteSpace(detail)) Console.WriteLine($"      {detail}");
}

return results.All(r => r.Pass) ? 0 : 1;

async Task RunTest(string name, Func<Task<(bool, string)>> test)
{
    Console.WriteLine();
    Console.WriteLine($"---- {name} ----");
    try
    {
        var (pass, detail) = await test();
        results.Add((name, pass, detail));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {detail}");
    }
    catch (Exception e)
    {
        results.Add((name, false, $"EXCEPTION: {e}"));
        Console.WriteLine($"FAIL EXCEPTION: {e}");
    }
}

// ---------------------------------------------------------------- helpers

ScalarVariable IntVariable(int id, string name) => new()
{
    Id = id,
    Namespace = "Test",
    Name = name,
    Label = name,
    Description = string.Empty,
    Values = new Values<int> { Value = 0, ValueType = ValueDataType.Int }
};

(FileDataLoggerDriver Driver, PassThroughProtocol Protocol, TestLogger Log) CreateLogger(
    string dir, string fileName, params ScalarVariable[] variables)
{
    var log = new TestLogger();
    var protocol = new PassThroughProtocol { IsEnabled = true };
    foreach (var variable in variables)
    {
        var pv = protocol.CreateProtocolVariable(variable, $"id=\"{variable.Id}\"", true)!;
        protocol.AddVariable(pv);
    }

    var driver = new FileDataLoggerDriver
    {
        IsEnabled = true,
        Logger = log,
        RawSettings = $"file=\"{Path.Combine(dir, fileName + ".qilog")}\"",
        DataLogFileName = fileName
    };
    driver.SetConfiguration();
    driver.AddProtocol(protocol);
    return (driver, protocol, log);
}

IProtocolVariable SinkVariable(PassThroughProtocol protocol, int variableId)
    => protocol.Variables.First(v => v.Variable.Id == variableId);

string SingleLogFile(string dir)
{
    var files = Directory.GetFiles(dir, "*.qilog");
    if (files.Length != 1) throw new InvalidOperationException($"Expected 1 .qilog in {dir}, found {files.Length}.");
    return files[0];
}

async Task<List<VariableLogRecord>> ParseLogFile(string filePath)
{
    var records = new List<VariableLogRecord>();
    await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    while (stream.Position < stream.Length)
    {
        records.Add(await MessagePackSerializer.DeserializeAsync<VariableLogRecord>(stream));
    }
    return records;
}

// Writes a .qilog file directly (bypassing the logger) so replay tests control the exact content.
async Task WriteLogFileDirectly(string filePath, IEnumerable<VariableLogRecord> records)
{
    await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
    foreach (var record in records)
    {
        await MessagePackSerializer.SerializeAsync(stream, record);
    }
}

VariableLogRecord IntRecord(int variableId, string name, long timestampUtcTicks, int value) => new()
{
    TimestampUtcTicks = timestampUtcTicks,
    VariableId = variableId,
    VariableNamespace = "Test",
    VariableName = name,
    VariableLabel = name,
    ValueType = "System.Int32",
    Value = value.ToString(CultureInfo.InvariantCulture)
};

(FileDataReplayDriver Driver, DataLogReplayProtocol Protocol, TestLogger Log, List<(int VarId, long Ticks, object Value)> Received)
    CreateReplay(string filePath, string mode, bool protocolEnabled, params ScalarVariable[] variables)
{
    var log = new TestLogger();
    var received = new List<(int, long, object)>();
    var receivedLock = new object();
    var protocol = new DataLogReplayProtocol { IsEnabled = protocolEnabled };
    foreach (var variable in variables)
    {
        var pv = protocol.CreateProtocolVariable(variable, $"id=\"{variable.Id}\"", true)!;
        protocol.AddVariable(pv);
        // Snapshot synchronously in the handler — the same pattern QInsight controls use
        // (WorkspaceViewModel.SubscribeControlVariable takes the snapshot before dispatching).
        pv.SubscribeAsyncValueChanged(changed =>
        {
            lock (receivedLock)
            {
                received.Add((changed.Variable.Id, changed.Variable.Timestamp.Ticks, changed.Variable.GetValue()));
            }
            return Task.CompletedTask;
        });
    }

    var driver = new FileDataReplayDriver
    {
        IsEnabled = true,
        Logger = log,
        RawSettings = $"file=\"{filePath}\";mode=\"{mode}\""
    };
    driver.SetConfiguration();
    driver.AddProtocol(protocol);
    return (driver, protocol, log, received);
}

async Task<bool> RunReplayToCompletion(FileDataReplayDriver driver, TimeSpan timeout)
{
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    driver.ReplayCompleted += (_, _) => completed.TrySetResult();
    await driver.StartAsync();
    var finished = await Task.WhenAny(completed.Task, Task.Delay(timeout)) == completed.Task;
    await driver.StopAsync();
    return finished;
}

// ---------------------------------------------------------------- tests

async Task<(bool, string)> Test1_SequentialRoundTrip()
{
    var dir = Path.Combine(workRoot, "t1");
    Directory.CreateDirectory(dir);
    const int count = 10_000;
    var variable = IntVariable(1, "Sig1");
    var (driver, protocol, log) = CreateLogger(dir, "t1", variable);
    var sink = SinkVariable(protocol, 1);
    var baseTime = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);

    await driver.StartAsync();
    for (var i = 0; i < count; i++)
    {
        variable.SetValue(i);
        variable.Timestamp = baseTime.AddMilliseconds(i * 2);
        await driver.OnProtocolVariableValueChangedAsync(sink);
    }
    await driver.StopAsync();

    var records = await ParseLogFile(SingleLogFile(dir));
    var values = records.Select(r => int.Parse(r.Value, CultureInfo.InvariantCulture)).ToList();
    var sorted = records.Zip(records.Skip(1)).All(p => p.First.TimestampUtcTicks <= p.Second.TimestampUtcTicks);
    var complete = values.Count == count && values.SequenceEqual(Enumerable.Range(0, count));

    return (complete && sorted,
        $"written={count}, in file={records.Count}, order preserved={sorted}, values intact={complete}, " +
        $"logger warnings/errors={log.Summary()}");
}

async Task<(bool, string)> Test2_ParallelProducers()
{
    var dir = Path.Combine(workRoot, "t2");
    Directory.CreateDirectory(dir);
    const int perVariable = 2_500;
    var variables = Enumerable.Range(1, 4).Select(i => IntVariable(i, $"Sig{i}")).ToArray();
    var (driver, protocol, log) = CreateLogger(dir, "t2", variables);
    var baseTime = new DateTime(2026, 7, 12, 11, 0, 0, DateTimeKind.Utc);

    await driver.StartAsync();
    await Task.WhenAll(variables.Select(variable => Task.Run(async () =>
    {
        var sink = SinkVariable(protocol, variable.Id);
        for (var i = 0; i < perVariable; i++)
        {
            variable.SetValue(i);
            variable.Timestamp = baseTime.AddMilliseconds(i * 2);
            await driver.OnProtocolVariableValueChangedAsync(sink);
        }
    })));
    await driver.StopAsync();

    var records = await ParseLogFile(SingleLogFile(dir));
    var byVar = records.GroupBy(r => r.VariableId).ToDictionary(g => g.Key, g => g.ToList());
    var countsOk = variables.All(v => byVar.TryGetValue(v.Id, out var list) && list.Count == perVariable);
    var valuesOk = countsOk && variables.All(v =>
        byVar[v.Id].Select(r => int.Parse(r.Value, CultureInfo.InvariantCulture)).OrderBy(x => x)
            .SequenceEqual(Enumerable.Range(0, perVariable)));
    var sorted = records.Zip(records.Skip(1)).All(p => p.First.TimestampUtcTicks <= p.Second.TimestampUtcTicks);

    return (countsOk && valuesOk && sorted,
        $"total={records.Count}/10000, per-variable counts ok={countsOk}, values intact={valuesOk}, " +
        $"sorted={sorted}, logger={log.Summary()}");
}

async Task<(bool, string)> Test3_ReorderInsideWindow()
{
    var dir = Path.Combine(workRoot, "t3");
    Directory.CreateDirectory(dir);
    var variable = IntVariable(1, "Sig1");
    var (driver, protocol, log) = CreateLogger(dir, "t3", variable);
    var sink = SinkVariable(protocol, 1);
    var baseTime = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    // Timestamps arrive with +-200 ms jitter (inside the 500 ms reorder window).
    var offsets = new[] { 0, 200, 100, 400, 300, 600, 500, 800, 700, 1000 };
    await driver.StartAsync();
    for (var i = 0; i < offsets.Length; i++)
    {
        variable.SetValue(i);
        variable.Timestamp = baseTime.AddMilliseconds(offsets[i]);
        await driver.OnProtocolVariableValueChangedAsync(sink);
        await Task.Delay(10); // give the writer loop a chance to run between records
    }
    await driver.StopAsync();

    var records = await ParseLogFile(SingleLogFile(dir));
    var sorted = records.Zip(records.Skip(1)).All(p => p.First.TimestampUtcTicks <= p.Second.TimestampUtcTicks);
    return (records.Count == offsets.Length && sorted,
        $"in file={records.Count}/{offsets.Length}, sorted={sorted}, logger={log.Summary()}");
}

async Task<(bool, string)> Test4_ReorderBeyondWindow()
{
    var dir = Path.Combine(workRoot, "t4");
    Directory.CreateDirectory(dir);
    var variable = IntVariable(1, "Sig1");
    var (driver, protocol, log) = CreateLogger(dir, "t4", variable);
    var sink = SinkVariable(protocol, 1);
    var baseTime = new DateTime(2026, 7, 12, 13, 0, 0, DateTimeKind.Utc);

    // One record arrives 2000 ms in the past — far beyond the 500 ms reorder window.
    // It must not be LOST; local unsortedness is acceptable (and handled by replay).
    var offsets = new[] { 0, 20, 40, 60, 2600, 2620, 600 /* late straggler */, 2640, 2660 };
    await driver.StartAsync();
    for (var i = 0; i < offsets.Length; i++)
    {
        variable.SetValue(i);
        variable.Timestamp = baseTime.AddMilliseconds(offsets[i]);
        await driver.OnProtocolVariableValueChangedAsync(sink);
        await Task.Delay(30);
    }
    await driver.StopAsync();

    var records = await ParseLogFile(SingleLogFile(dir));
    var sorted = records.Zip(records.Skip(1)).All(p => p.First.TimestampUtcTicks <= p.Second.TimestampUtcTicks);
    return (records.Count == offsets.Length,
        $"in file={records.Count}/{offsets.Length} (nothing lost={records.Count == offsets.Length}), " +
        $"file still sorted={sorted}, logger={log.Summary()}");
}

async Task<(bool, string)> Test5_ReplayImmediate()
{
    var dir = Path.Combine(workRoot, "t5");
    Directory.CreateDirectory(dir);
    var filePath = Path.Combine(dir, "t5.qilog");
    const int count = 5_000;
    var baseTicks = new DateTime(2026, 7, 12, 14, 0, 0, DateTimeKind.Utc).Ticks;
    await WriteLogFileDirectly(filePath, Enumerable.Range(0, count)
        .Select(i => IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 2, i)));

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log, received) = CreateReplay(filePath, "Immediate", true, variable);
    var finished = await RunReplayToCompletion(driver, TimeSpan.FromSeconds(60));

    var values = received.Select(r => (int)r.Value).ToList();
    var complete = values.Count == count && values.SequenceEqual(Enumerable.Range(0, count));
    return (finished && complete,
        $"records in file={count}, notified={received.Count}, values+order intact={complete}, " +
        $"completed={finished}, replay log={log.Summary()}");
}

async Task<(bool, string)> Test6_ReplayRealtime()
{
    var dir = Path.Combine(workRoot, "t6");
    Directory.CreateDirectory(dir);
    var filePath = Path.Combine(dir, "t6.qilog");
    const int count = 100; // 100 records @ 20 ms = 2 s of data
    var baseTicks = new DateTime(2026, 7, 12, 15, 0, 0, DateTimeKind.Utc).Ticks;
    await WriteLogFileDirectly(filePath, Enumerable.Range(0, count)
        .Select(i => IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 20, i)));

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log, received) = CreateReplay(filePath, "Realtime", true, variable);
    var stopwatch = Stopwatch.StartNew();
    var finished = await RunReplayToCompletion(driver, TimeSpan.FromSeconds(30));
    stopwatch.Stop();

    var values = received.Select(r => (int)r.Value).ToList();
    var complete = values.Count == count && values.SequenceEqual(Enumerable.Range(0, count));
    var pacingOk = stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5) && stopwatch.Elapsed <= TimeSpan.FromSeconds(10);
    return (finished && complete && pacingOk,
        $"records={count}, notified={received.Count}, intact={complete}, took={stopwatch.Elapsed.TotalSeconds:F1}s " +
        $"(expected ~2s), replay log={log.Summary()}");
}

async Task<(bool, string)> Test7_CorruptFile()
{
    var dir = Path.Combine(workRoot, "t7");
    Directory.CreateDirectory(dir);
    var filePath = Path.Combine(dir, "t7.qilog");
    const int count = 1_000;
    var baseTicks = new DateTime(2026, 7, 12, 16, 0, 0, DateTimeKind.Utc).Ticks;
    await WriteLogFileDirectly(filePath, Enumerable.Range(0, count)
        .Select(i => IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 2, i)));

    // Corrupt a single byte roughly in the middle of the file (hits one record's value).
    var bytes = await File.ReadAllBytesAsync(filePath);
    bytes[bytes.Length / 2] = 0xC1; // 0xC1 is a reserved/never-used MessagePack code
    await File.WriteAllBytesAsync(filePath, bytes);

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log, received) = CreateReplay(filePath, "Immediate", true, variable);
    var finished = await RunReplayToCompletion(driver, TimeSpan.FromSeconds(60));

    // One bad record may cost at most itself — everything after it must still replay,
    // the failure must be logged and ReplayCompleted must fire.
    var errorLogged = log.Messages.Any(m => m.Level is LogLevel.Error or LogLevel.Fatal);
    var restSurvived = received.Count >= count - 1
        && received.Count <= count
        && received.Any(r => (int)r.Value == count - 1);
    return (finished && restSurvived && errorLogged,
        $"records in file={count} (1 byte corrupted mid-file), replayed={received.Count} (expected >= {count - 1}), " +
        $"completed event={finished}, error logged={errorLogged}");
}

async Task<(bool, string)> Test8_UnknownVariable()
{
    var dir = Path.Combine(workRoot, "t8");
    Directory.CreateDirectory(dir);
    var filePath = Path.Combine(dir, "t8.qilog");
    var baseTicks = new DateTime(2026, 7, 12, 17, 0, 0, DateTimeKind.Utc).Ticks;
    // 100 records for variable 1 (known) interleaved with 100 for variable 99 (unknown).
    var records = new List<VariableLogRecord>();
    for (var i = 0; i < 100; i++)
    {
        records.Add(IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 2, i));
        records.Add(IntRecord(99, "Ghost", baseTicks + i * TimeSpan.TicksPerMillisecond * 2 + 1, i));
    }
    await WriteLogFileDirectly(filePath, records);

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log, received) = CreateReplay(filePath, "Immediate", true, variable);
    var finished = await RunReplayToCompletion(driver, TimeSpan.FromSeconds(60));

    var knownOk = received.Count == 100;
    var warningLogged = log.Messages.Any(m =>
        m.Level == LogLevel.Warn && m.Message.Contains("Ghost", StringComparison.Ordinal));
    return (finished && knownOk && warningLogged,
        $"known variable notified={received.Count}/100, unknown-variable warning logged={warningLogged}");
}

async Task<(bool, string)> Test9_DisabledProtocol()
{
    var dir = Path.Combine(workRoot, "t9");
    Directory.CreateDirectory(dir);
    var filePath = Path.Combine(dir, "t9.qilog");
    var baseTicks = new DateTime(2026, 7, 12, 18, 0, 0, DateTimeKind.Utc).Ticks;
    await WriteLogFileDirectly(filePath, Enumerable.Range(0, 100)
        .Select(i => IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 2, i)));

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log, received) = CreateReplay(filePath, "Immediate", protocolEnabled: false, variable);
    var finished = await RunReplayToCompletion(driver, TimeSpan.FromSeconds(60));

    var warningLogged = log.Messages.Any(m =>
        m.Level == LogLevel.Warn && m.Message.Contains("not running", StringComparison.OrdinalIgnoreCase));
    return (finished && received.Count == 0 && warningLogged,
        $"records dropped (protocol disabled), notified={received.Count} (expected 0), " +
        $"drop warning logged={warningLogged}, completed={finished}");
}

async Task<(bool, string)> Test10_OutOfOrderFile()
{
    var dir = Path.Combine(workRoot, "t10");
    Directory.CreateDirectory(dir);
    var filePath = Path.Combine(dir, "t10.qilog");
    const int count = 100;
    var baseTicks = new DateTime(2026, 7, 12, 19, 0, 0, DateTimeKind.Utc).Ticks;
    var records = Enumerable.Range(0, count)
        .Select(i => IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 20, i))
        .ToList();
    // Simulate a straggler written far out of order (beyond the logger's reorder window).
    records[50].TimestampUtcTicks = baseTicks - TimeSpan.TicksPerSecond * 2;
    await WriteLogFileDirectly(filePath, records);

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log, received) = CreateReplay(filePath, "Immediate", true, variable);
    var finished = await RunReplayToCompletion(driver, TimeSpan.FromSeconds(60));

    var values = received.Select(r => (int)r.Value).OrderBy(v => v).ToList();
    var nothingLost = values.Count == count && values.SequenceEqual(Enumerable.Range(0, count));
    return (finished && nothingLost,
        $"records={count} (1 out-of-order straggler), replayed={received.Count}, nothing lost={nothingLost}, " +
        $"replay log={log.Summary()}");
}

async Task<(bool, string)> Test11_Settings()
{
    var dir = Path.Combine(workRoot, "t11");
    Directory.CreateDirectory(dir);
    var failures = new List<string>();

    static bool HasWarn(TestLogger log, params string[] fragments) => log.Messages.Any(m =>
        m.Level == LogLevel.Warn && fragments.All(f => m.Message.Contains(f, StringComparison.OrdinalIgnoreCase)));

    // Logger driver: unknown key and invalid bool are both reported, the driver keeps working.
    var loggerLog = new TestLogger();
    var logger = new FileDataLoggerDriver
    {
        Logger = loggerLog,
        RawSettings = $"file=\"{dir}\";append=maybe;flushOnWrte=true"
    };
    logger.SetConfiguration();
    if (!HasWarn(loggerLog, "unknown setting", "flushOnWrte")) failures.Add("logger: unknown key not reported");
    if (!HasWarn(loggerLog, "invalid append")) failures.Add("logger: invalid append not reported");

    // Replay driver: unknown key and invalid speed are reported.
    var replayLog = new TestLogger();
    var replay = new FileDataReplayDriver
    {
        Logger = replayLog,
        RawSettings = $"file=\"{Path.Combine(dir, "x.qilog")}\";speed=fast;mode=immediate;loop=true;bogus=1"
    };
    replay.SetConfiguration();
    if (!HasWarn(replayLog, "unknown setting", "bogus")) failures.Add("replay: unknown key not reported");
    if (!HasWarn(replayLog, "invalid speed")) failures.Add("replay: invalid speed not reported");

    // Re-applying settings without mode/loop must fall back to the defaults (realtime, no loop),
    // not keep the previously applied values. Observed through behaviour (the plugin assemblies
    // are obfuscated, private fields are not reachable by reflection): after the reset a 1.9 s
    // recording must finish (loop=false) and must take about its recorded time (realtime).
    const int resetCount = 20;
    var resetFile = Path.Combine(dir, "reset.qilog");
    var baseTicks = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc).Ticks;
    await WriteLogFileDirectly(resetFile, Enumerable.Range(0, resetCount)
        .Select(i => IntRecord(1, "Sig1", baseTicks + i * TimeSpan.TicksPerMillisecond * 100, i)));
    var (resetDriver, _, _, resetReceived) = CreateReplay(resetFile, "immediate", true, IntVariable(1, "Sig1"));
    resetDriver.RawSettings = $"file=\"{resetFile}\";mode=immediate;loop=true";
    resetDriver.SetConfiguration();
    resetDriver.RawSettings = $"file=\"{resetFile}\"";
    resetDriver.SetConfiguration();
    var stopwatch = Stopwatch.StartNew();
    var resetFinished = await RunReplayToCompletion(resetDriver, TimeSpan.FromSeconds(30));
    stopwatch.Stop();
    if (!resetFinished) failures.Add("replay: loop did not reset to false (replay never completed)");
    if (resetReceived.Count != resetCount) failures.Add($"replay: after reset replayed {resetReceived.Count}/{resetCount}");
    if (stopwatch.Elapsed < TimeSpan.FromSeconds(1.5)) failures.Add($"replay: mode did not reset to realtime (took {stopwatch.Elapsed.TotalSeconds:F1} s)");

    // Both protocols: no settings of their own -> a stray key is reported as unknown.
    var passLog = new TestLogger();
    var pass = new PassThroughProtocol { Logger = passLog, RawSettings = "foo=1" };
    pass.SetConfiguration();
    if (!HasWarn(passLog, "unknown setting", "foo")) failures.Add("pass-through: unknown key not reported");

    var replayProtocolLog = new TestLogger();
    var replayProtocol = new DataLogReplayProtocol { Logger = replayProtocolLog, RawSettings = "foo=1" };
    replayProtocol.SetConfiguration();
    if (!HasWarn(replayProtocolLog, "unknown setting", "foo")) failures.Add("replay protocol: unknown key not reported");

    // Empty settings stay silent.
    var quietLog = new TestLogger();
    var quiet = new PassThroughProtocol { Logger = quietLog, RawSettings = string.Empty };
    quiet.SetConfiguration();
    if (quietLog.Messages.Count > 0) failures.Add("pass-through: empty settings produced a message");

    // No per-variable parameters -> empty comm-param templates.
    var variable = IntVariable(1, "Sig1");
    if (pass.CreateDefaultCommParam(variable, []) != string.Empty) failures.Add("pass-through: comm-param template not empty");
    if (replayProtocol.CreateDefaultCommParam(variable, []) != string.Empty) failures.Add("replay protocol: comm-param template not empty");

    return (failures.Count == 0,
        failures.Count == 0 ? "all settings diagnostics present, defaults restored, templates empty" : string.Join("; ", failures));
}

async Task<(bool, string)> Test12_UnwritableLogPath()
{
    var dir = Path.Combine(workRoot, "t12");
    Directory.CreateDirectory(dir);
    // A plain file where the log directory should be -> Directory.CreateDirectory fails.
    var blocker = Path.Combine(dir, "blocker");
    await File.WriteAllTextAsync(blocker, "not a directory");

    var variable = IntVariable(1, "Sig1");
    var (driver, _, log) = CreateLogger(Path.Combine(blocker, "Logs"), "t12", variable);

    Exception? thrown = null;
    try
    {
        await driver.StartAsync();
    }
    catch (Exception e)
    {
        thrown = e;
    }

    var stateAfterStart = driver.State;
    var faulted = stateAfterStart == CommunicationState.Faulted;
    var hasMessage = !string.IsNullOrWhiteSpace(driver.StateMessage);
    var errorLogged = log.Messages.Any(m => m.Level == LogLevel.Error && m.Message.Contains("could not be opened", StringComparison.OrdinalIgnoreCase));
    await driver.StopAsync();

    return (thrown == null && faulted && hasMessage && errorLogged,
        $"thrown={thrown?.GetType().Name ?? "none"}, state after start={stateAfterStart}, message={(hasMessage ? "yes" : "no")}, error logged={errorLogged}");
}

// ---------------------------------------------------------------- test logger

sealed class TestLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();

    public void RegisterSubscriber(ILogSubscriber subscriber) { }
    public void UnRegisterSubscriber(ILogSubscriber subscriber) { }
    public void Log(ILogMessage message) { }

    public void Log(LogLevel level, string message, Exception? exception = default)
    {
        Messages.Enqueue((level, message));
        Console.WriteLine($"  [{level}] {message}");
    }

    public Task LogAsync(ILogMessage message, CancellationToken ct) => Task.CompletedTask;

    public Task LogAsync(LogLevel level, string message, Exception? exception = default, CancellationToken ct = default)
    {
        Log(level, message, exception);
        return Task.CompletedTask;
    }

    public void Enable() { }
    public void Disable() { }

    public string Summary()
    {
        var counts = Messages.GroupBy(m => m.Level).Select(g => $"{g.Key}={g.Count()}").ToList();
        return counts.Count == 0 ? "clean" : string.Join(", ", counts);
    }
}
