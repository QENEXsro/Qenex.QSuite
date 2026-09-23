// Headless verification of SimulDataProtocol extensions used by the FZU seminar demo:
//   T1  writable LAOS harmonic parameters (h{n}amp/h{n}freq/h{n}phase) change the stress
//       waveform live; strain follows h1freq; phases add up / cancel
//   T2  thermal-field matrix: axes filled once (10 mm pitch), data cells in the expected
//       temperature range and changing over time; user axis edit survives regeneration
//   T3  "hold" parameter freezes the matrix data (and releasing it resumes generation)
//   T4  matrix pending element writes are re-applied over the buffer in WriteVariableAsync
//   T5  parameter keys are never generated over (scalar with signal="h2amp" keeps its value)
//   T6  Qenex_Sim_SimulData_AllSignals.qproj (the installer example, opened with the same
//       container loader as QInsight, so the encrypted .qproj works): XmlModule.xml loads through
//       the real mappers (presentations, conversions, events, variables incl. the matrix), all
//       16 Sim variables create protocol variables (7 generated scalars, 8 params, 1 matrix) and
//       the whole set runs with the init= power-on defaults applied

using Qenex.QSuite.Common.CoreComm;
using System.Xml.Serialization;
using Qenex.QSuite.Helpers.ProjectFile;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.ModuleXmlHandler;
using Qenex.QSuite.ModuleXmlHandler.XmlStructure;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Protocols.SimulDataProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

var results = new List<(string Name, bool Pass, string Detail)>();

await RunTest("T1 LAOS harmonic parameters live", Test1_HarmonicParameters);
await RunTest("T2 thermal matrix: axes, data range, changing", Test2_ThermalMatrix);
await RunTest("T3 hold freezes matrix", Test3_Hold);
await RunTest("T4 matrix pending writes re-applied", Test4_PendingWrites);
await RunTest("T5 parameters are not generated over", Test5_ParametersUntouched);
await RunTest("T6 installer example Qenex_Sim_SimulData_AllSignals loads and runs", Test6_DemoProject);
await RunTest("T7 commParam round trip keeps amp/freq/nonlin/init (no id)", Test7_CommParamRoundTrip);
await RunTest("T8 On Request event: nothing generated, one sample per read", Test8_OnRequestRead);

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

static SimulDataProtocol NewProtocol()
{
    var logger = new Logger(LogLevel.Info);
    logger.RegisterSubscriber(new ConsoleLogSubscriber());
    return new SimulDataProtocol { IsEnabled = true, Logger = logger };
}

static PeriodicVarEvent Event(string name, int periodMs) => new() { Name = name, Period = periodMs, Unit = TimeUnit.Milisec };

static ScalarVariable DoubleVariable(int id, string name) => new()
{
    Id = id,
    Namespace = "/",
    Name = name,
    Label = name,
    Values = new Values<double> { Value = 0d, ValueType = ValueDataType.Double }
};

static IPresentation Linear(string name, double multiplier, string unit = "") => new Presentation
{
    Name = name, Unit = unit, PrintFormat = "",
    Conversion = new LinearValConversion { Multiplier = multiplier, Offset = 0 }
};

static MatrixVariable ThermalMatrix(int id)
{
    var pos = Linear("posMm", 1, "mm");
    var temp = Linear("tempC", 0.1, "degC");
    return new MatrixVariable
    {
        Id = id, Namespace = "/", Name = "ThermalField", Label = "Thermal field",
        DefaultDataType = ValueDataType.UShort,
        Endianness = MatrixEndianness.Little,
        XAxis = new MatrixSection { Count = 8, Label = "X [mm]", Presentation = pos },
        YAxis = new MatrixSection { Count = 6, Label = "Y [mm]", Presentation = pos },
        Data = new MatrixSection { Presentation = temp }
    };
}

static void Add(SimulDataProtocol protocol, IVariableBase variable, IEnumerable<IVarEvent> events, string commParams)
{
    var pv = protocol.CreateProtocolVariable(variable, events, commParams, isCommunicated: true)
             ?? throw new InvalidOperationException($"protocol variable for {variable.Name} not created");
    protocol.AddVariable(pv);
}

static double Raw(ScalarVariable v) => v.GetEngValue();

static async Task<(double min, double max)> SampleRange(ScalarVariable v, int ms)
{
    double min = double.MaxValue, max = double.MinValue;
    var end = Environment.TickCount64 + ms;
    while (Environment.TickCount64 < end)
    {
        var x = Raw(v);
        min = Math.Min(min, x);
        max = Math.Max(max, x);
        await Task.Delay(5);
    }
    return (min, max);
}

static double[] DataSnapshot(MatrixVariable m)
{
    var d = new double[m.DataCount];
    for (var i = 0; i < d.Length; i++) d[i] = m.GetEngValue(MatrixSectionKind.Data, i);
    return d;
}

// ---------------------------------------------------------------- tests

async Task<(bool, string)> Test1_HarmonicParameters()
{
    var protocol = NewProtocol();
    var events = new IVarEvent[] { Event("e10", 10), Event("e100", 100) };
    var strain = DoubleVariable(1, "Strain");
    var stress = DoubleVariable(2, "Stress");
    var h1amp = DoubleVariable(3, "H1Amp");
    var h1freq = DoubleVariable(4, "H1Freq");
    var h2amp = DoubleVariable(5, "H2Amp");
    var h2freq = DoubleVariable(6, "H2Freq");
    var h1phase = DoubleVariable(7, "H1Phase");
    var h2phase = DoubleVariable(8, "H2Phase");

    Add(protocol, strain, events, "direction=\"read\";eventRef=\"e10\";id=\"Strain\";signal=\"laosstrain\"");
    Add(protocol, stress, events, "direction=\"read\";eventRef=\"e10\";id=\"Stress\";signal=\"laosstress\"");
    Add(protocol, h1amp, events, "direction=\"write\";eventRef=\"e100\";signal=\"h1amp\"");
    Add(protocol, h1freq, events, "direction=\"write\";eventRef=\"e100\";id=\"H1Freq\";signal=\"h1freq\"");
    Add(protocol, h2amp, events, "direction=\"write\";eventRef=\"e100\";id=\"H2Amp\";signal=\"h2amp\"");
    Add(protocol, h2freq, events, "direction=\"write\";eventRef=\"e100\";id=\"H2Freq\";signal=\"h2freq\"");
    Add(protocol, h1phase, events, "direction=\"write\";eventRef=\"e100\";id=\"H1Phase\";signal=\"h1phase\"");
    Add(protocol, h2phase, events, "direction=\"write\";eventRef=\"e100\";id=\"H2Phase\";signal=\"h2phase\"");

    // Defaults as init.py sets them: pure fundamental 680 @ 0.5 Hz, no 3rd harmonic.
    h1amp.TrySetEngValue(680); h1freq.TrySetEngValue(2.0); h2amp.TrySetEngValue(0); h2freq.TrySetEngValue(6.0);

    await protocol.StartAsync();
    await Task.Delay(100);
    var s1 = await SampleRange(stress, 1200);   // > 2 periods at 2 Hz
    var g1 = await SampleRange(strain, 600);
    var pureOk = s1.max > 600 && s1.max < 720 && s1.min < -600 && s1.min > -720;
    var strainOk = g1.max > 700 && g1.min < -700; // 800 amplitude, must reach both extremes within 600 ms at 2 Hz

    // Add a strong 3rd harmonic: peak must exceed the fundamental amplitude
    h2amp.TrySetEngValue(400);
    await Task.Delay(50);
    var s2 = await SampleRange(stress, 1200);
    var harmonicOk = s2.max > 760 || s2.min < -760;

    // Detune the fundamental frequency: strain must slow down (fewer extremes in the window)
    h1freq.TrySetEngValue(0.2);
    await Task.Delay(50);
    var g2 = await SampleRange(strain, 600); // at 0.2 Hz a 600 ms window cannot span both extremes
    var slowOk = !(g2.max > 700 && g2.min < -700);

    // Phase: two harmonics on the same frequency, in phase -> amplitudes add; 180 deg apart -> cancel
    h1freq.TrySetEngValue(2.0); h2freq.TrySetEngValue(2.0); h1amp.TrySetEngValue(500); h2amp.TrySetEngValue(500);
    h1phase.TrySetEngValue(0); h2phase.TrySetEngValue(0);
    await Task.Delay(50);
    var s3 = await SampleRange(stress, 1200);
    var addOk = s3.max > 950 && s3.min < -950;
    h2phase.TrySetEngValue(180);
    await Task.Delay(50);
    var s4 = await SampleRange(stress, 1200);
    var cancelOk = Math.Abs(s4.max) < 30 && Math.Abs(s4.min) < 30;

    await protocol.StopAsync();
    var detail = $"pure {s1.min:F0}..{s1.max:F0} (ok={pureOk}); strain {g1.min:F0}..{g1.max:F0} (ok={strainOk}); " +
                 $"with h2amp=400 {s2.min:F0}..{s2.max:F0} (ok={harmonicOk}); strain @0.2Hz {g2.min:F0}..{g2.max:F0} (ok={slowOk}); " +
                 $"in phase {s3.min:F0}..{s3.max:F0} (ok={addOk}); 180deg {s4.min:F0}..{s4.max:F0} (ok={cancelOk})";
    return (pureOk && strainOk && harmonicOk && slowOk && addOk && cancelOk, detail);
}

async Task<(bool, string)> Test2_ThermalMatrix()
{
    var protocol = NewProtocol();
    var events = new IVarEvent[] { Event("e50", 50) };
    var matrix = ThermalMatrix(1);
    Add(protocol, matrix, events, "direction=\"readWrite\";eventRef=\"e50\";id=\"ThermalField\";signal=\"thermal\"");

    var notified = 0;
    protocol.Variables[0].SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref notified); return Task.CompletedTask; });

    await protocol.StartAsync();
    await Task.Delay(300);
    var snap1 = DataSnapshot(matrix);
    var xAxisOk = Enumerable.Range(0, 8).All(i => Math.Abs(matrix.GetEngValue(MatrixSectionKind.XAxis, i) - 10 * i) < 1e-9);
    var yAxisOk = Enumerable.Range(0, 6).All(i => Math.Abs(matrix.GetEngValue(MatrixSectionKind.YAxis, i) - 10 * i) < 1e-9);
    var rangeOk = snap1.All(t => t >= 24.0 && t <= 66.0) && snap1.Max() > 45.0 && snap1.Min() < 30.0;

    // user edits an axis breakpoint: must survive regeneration (axes written once)
    matrix.TrySetEngValue(MatrixSectionKind.XAxis, 3, 35);
    await Task.Delay(300);
    var snap2 = DataSnapshot(matrix);
    var changing = snap1.Zip(snap2).Any(p => Math.Abs(p.First - p.Second) > 0.05);
    var axisEditKept = Math.Abs(matrix.GetEngValue(MatrixSectionKind.XAxis, 3) - 35) < 1e-9;
    var timestampOk = matrix.Timestamp > DateTime.UtcNow.AddSeconds(-5);

    await protocol.StopAsync();
    var detail = $"axes ok={xAxisOk && yAxisOk}; range {snap1.Min():F1}..{snap1.Max():F1} ok={rangeOk}; changing={changing}; " +
                 $"axis edit kept={axisEditKept}; notifications={notified}; timestamp ok={timestampOk}";
    return (xAxisOk && yAxisOk && rangeOk && changing && axisEditKept && notified > 5 && timestampOk, detail);
}

async Task<(bool, string)> Test3_Hold()
{
    var protocol = NewProtocol();
    var events = new IVarEvent[] { Event("e50", 50), Event("e100", 100) };
    var matrix = ThermalMatrix(1);
    var hold = DoubleVariable(2, "Hold");
    Add(protocol, matrix, events, "direction=\"readWrite\";eventRef=\"e50\";id=\"ThermalField\";signal=\"thermal\"");
    Add(protocol, hold, events, "direction=\"write\";eventRef=\"e100\";id=\"Hold\";signal=\"hold\"");

    await protocol.StartAsync();
    await Task.Delay(200);
    hold.TrySetEngValue(1);
    await Task.Delay(120); // let an in-flight cycle finish
    var frozen = DataSnapshot(matrix);
    var frozenStamp = matrix.Timestamp;
    matrix.TrySetEngValue(MatrixSectionKind.Data, 0, 99.9); // user edit while held
    await Task.Delay(400);
    var still = DataSnapshot(matrix);
    var heldOk = frozen.Skip(1).Zip(still.Skip(1)).All(p => Math.Abs(p.First - p.Second) < 1e-9)
                 && Math.Abs(still[0] - 99.9) < 1e-9
                 && matrix.Timestamp == frozenStamp;

    hold.TrySetEngValue(0);
    await Task.Delay(300);
    var resumed = DataSnapshot(matrix);
    var resumedOk = still.Zip(resumed).Any(p => Math.Abs(p.First - p.Second) > 0.05) && resumed[0] < 70;

    await protocol.StopAsync();
    return (heldOk && resumedOk, $"held ok={heldOk}; resumed ok={resumedOk}");
}

async Task<(bool, string)> Test4_PendingWrites()
{
    var protocol = NewProtocol();
    var events = new IVarEvent[] { Event("e50", 50) };
    var matrix = ThermalMatrix(1);
    Add(protocol, matrix, events, "direction=\"readWrite\";eventRef=\"e50\";id=\"ThermalField\";signal=\"thermal\"");
    var pv = protocol.Variables[0];

    var canWrite = ((IProtocolVariableWriteProtocol)protocol).CanWriteVariable(pv);

    // Simulate the host: set eng value, queue the element bytes, then the protocol write path
    matrix.TrySetEngValue(MatrixSectionKind.Data, 5, 42.0);
    var offset = matrix.GetSectionOffset(MatrixSectionKind.Data) + 5 * matrix.GetElementSize(MatrixSectionKind.Data);
    var bytes = matrix.RawData.AsSpan(offset, 2).ToArray();
    matrix.EnqueuePendingWrite(new MatrixWriteRequest(offset, bytes));
    // ...a generator refill in between:
    matrix.TrySetEngValue(MatrixSectionKind.Data, 5, 30.0);
    await ((IProtocolVariableWriteProtocol)protocol).WriteVariableAsync(pv);
    var reapplied = Math.Abs(matrix.GetEngValue(MatrixSectionKind.Data, 5) - 42.0) < 1e-9;
    var drained = !matrix.TryDequeuePendingWrite(out _);

    // out-of-range request is skipped, not thrown
    matrix.EnqueuePendingWrite(new MatrixWriteRequest(matrix.Size - 1, new byte[] { 1, 2 }));
    await ((IProtocolVariableWriteProtocol)protocol).WriteVariableAsync(pv);

    return (canWrite && reapplied && drained, $"canWrite={canWrite}; reapplied={reapplied}; drained={drained}");
}

// T8: a scalar signal and the thermal matrix bound to an On Request event are never generated
// periodically; CanReadVariable reports them (and not the periodic one); ReadVariableAsync
// produces exactly one sample/table per call; a read while stopped throws.
async Task<(bool, string)> Test8_OnRequestRead()
{
    var protocol = NewProtocol();
    var onRequest = new OnRequestVarEvent { Name = "onRequest" };
    var events = new IVarEvent[] { Event("e50", 50), onRequest };
    var stress = DoubleVariable(1, "Stress");
    var strain = DoubleVariable(2, "Strain");
    var matrix = ThermalMatrix(3);
    Add(protocol, stress, events, "direction=\"read\";eventRef=\"onRequest\";id=\"Stress\";signal=\"laosstress\"");
    Add(protocol, strain, events, "direction=\"read\";eventRef=\"e50\";id=\"Strain\";signal=\"laosstrain\"");
    Add(protocol, matrix, events, "direction=\"readWrite\";eventRef=\"onRequest\";id=\"ThermalField\";signal=\"thermal\"");
    var pvStress = protocol.Variables[0];
    var pvStrain = protocol.Variables[1];
    var pvMatrix = protocol.Variables[2];
    var reader = (IProtocolVariableReadProtocol)protocol;

    var canReadOk = reader.CanReadVariable(pvStress) && reader.CanReadVariable(pvMatrix) && !reader.CanReadVariable(pvStrain);

    var stoppedThrows = false;
    try { await reader.ReadVariableAsync(pvStress); } catch (InvalidOperationException) { stoppedThrows = true; }

    var stressNotified = 0;
    var matrixNotified = 0;
    pvStress.SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref stressNotified); return Task.CompletedTask; });
    pvMatrix.SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref matrixNotified); return Task.CompletedTask; });

    await protocol.StartAsync();
    var g = await SampleRange(strain, 300); // the generator loop starts on a worker task
    var running = protocol.State == CommunicationState.Running;
    var periodicRuns = g.max > 0 || g.min < 0;
    var notGenerated = stressNotified == 0 && matrixNotified == 0 && DataSnapshot(matrix).All(v => v == 0);

    await reader.ReadVariableAsync(pvStress);
    await reader.ReadVariableAsync(pvMatrix);
    var oneEach = stressNotified == 1 && matrixNotified == 1;
    var stampOk = stress.Timestamp > DateTime.UtcNow.AddSeconds(-5) && matrix.Timestamp > DateTime.UtcNow.AddSeconds(-5);
    var snap = DataSnapshot(matrix);
    var matrixFilled = snap.All(t => t >= 24.0 && t <= 66.0) && snap.Max() > 30.0
                       && Math.Abs(matrix.GetEngValue(MatrixSectionKind.XAxis, 7) - 70) < 1e-9;

    await Task.Delay(200);
    var stillOne = stressNotified == 1 && matrixNotified == 1;

    await protocol.StopAsync();
    var stoppedThrowsAgain = false;
    try { await reader.ReadVariableAsync(pvMatrix); } catch (InvalidOperationException) { stoppedThrowsAgain = true; }

    var ok = canReadOk && stoppedThrows && running && periodicRuns && notGenerated && oneEach && stampOk && matrixFilled
             && stillOne && stoppedThrowsAgain;
    var detail = $"canRead={canReadOk}; throws before start={stoppedThrows}; running={running}; periodic runs={periodicRuns}; " +
                 $"not generated={notGenerated}; one each={oneEach}; stamp={stampOk}; matrix filled={matrixFilled} " +
                 $"({snap.Min():F1}..{snap.Max():F1}); still one={stillOne}; throws after stop={stoppedThrowsAgain}";
    return (ok, detail);
}

async Task<(bool, string)> Test5_ParametersUntouched()
{
    var protocol = NewProtocol();
    var events = new IVarEvent[] { Event("e10", 10) };
    var stress = DoubleVariable(1, "Stress");
    var h2amp = DoubleVariable(2, "H2Amp");
    var readParam = DoubleVariable(3, "H3Amp");   // misconfigured as read: must not fault, stays constant
    Add(protocol, stress, events, "direction=\"read\";eventRef=\"e10\";id=\"Stress\";signal=\"laosstress\";nonlin=\"0.5\"");
    Add(protocol, h2amp, events, "direction=\"write\";eventRef=\"e10\";id=\"H2Amp\";signal=\"h2amp\"");
    Add(protocol, readParam, events, "direction=\"read\";eventRef=\"e10\";id=\"H3Amp\";signal=\"h3amp\"");
    h2amp.TrySetEngValue(123.0);

    await protocol.StartAsync();
    await Task.Delay(300);
    var state = protocol.State;
    var stateOk = state == CommunicationState.Running;
    var kept = Math.Abs(Raw(h2amp) - 123.0) < 1e-9;
    var r = await SampleRange(stress, 1100);
    var moving = r.max - r.min > 100;
    await protocol.StopAsync();
    return (stateOk && kept && moving, $"state={state} ({protocol.StateMessage}); h2amp kept={kept}; stress {r.min:F0}..{r.max:F0} moving={moving}; readParam={Raw(readParam)}");
}

// Saving a project from QInsight rewrites every commParam through ToCommParam(); the keys must
// be the ones Create() reads, otherwise generator overrides and init= defaults vanish on reload.
Task<(bool, string)> Test7_CommParamRoundTrip()
{
    var events = new List<IVarEvent> { new PeriodicVarEvent { Name = "event20ms", Period = 20, Unit = TimeUnit.Milisec } };
    var original = "direction=\"write\";eventRef=\"event20ms\";signal=\"h1amp\";amp=\"800\";freq=\"0.5\";nonlin=\"0.6\";init=\"680\"";
    var spec = SimulDataProtocolVariableSpecification.Create(events[0], original);
    var written = spec.ToCommParam();
    var reread = SimulDataProtocolVariableSpecification.Create(events[0], written);
    var ok = written == original
             && reread.Direction == CommDirection.Write && reread.VariableEvent?.Name == "event20ms"
             && reread.Signal == "h1amp"
             && reread.Amp == 800 && reread.Freq == 0.5 && reread.Nonlin == 0.6 && reread.Init == 680;

    var minimal = SimulDataProtocolVariableSpecification.Create(events[0], "direction=\"read\";eventRef=\"event20ms\";signal=\"step\"").ToCommParam();
    var minimalOk = minimal == "direction=\"read\";eventRef=\"event20ms\";signal=\"step\"";
    return Task.FromResult((ok && minimalOk, $"written='{written}'; minimal='{minimal}'"));
}

async Task<(bool, string)> Test6_DemoProject()
{
    var qproj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        @"..\..\..\..\..\QInsightSetup\Examples\Qenex_Sim_SimulData_AllSignals.qproj"));
    if (!File.Exists(qproj)) return (false, $"missing {qproj}");

    // Same loader as QInsight: handles both the plain zip and the encrypted container.
    var unzip = new ProjectZip().UnzipProject(qproj);
    if (unzip.Status != ProjectUnzipStatus.Success || !unzip.Streams.TryGetValue("XmlModule.xml", out var moduleStream))
        return (false, $"container status={unzip.Status}, entries={string.Join(", ", unzip.Streams.Keys)}");

    XmlModule module;
    using (moduleStream)
    {
        module = (XmlModule)new XmlSerializer(typeof(XmlModule)).Deserialize(moduleStream)!;
    }

    var conversions = XmlComponentMapper.FromXmlConversions(module.Conversions);
    var presentations = XmlComponentMapper.FromXmlPresentations(module.Presentations, conversions);
    var events = XmlComponentMapper.FromXmlVarEvents(module.Events);
    var variables = XmlVariableMapper.FromXmlVariables(module.Variables, presentations);

    var matrix = variables.OfType<MatrixVariable>().SingleOrDefault();
    var matrixOk = matrix is { XCount: 8, YCount: 6, DataCount: 48, Size: 124 }
                   && matrix.ValidateLayout() == null
                   && matrix.Data.Presentation?.Unit == "°C"
                   && matrix.XAxis?.Presentation?.Unit == "mm";

    var simRef = module.DriverReferences.Single(d => d.Ref == "SimulDataDriver").ProtocolReferences.Single();
    var protocol = NewProtocol();
    var created = 0;
    foreach (var vr in simRef.VariableReferences)
    {
        var variable = variables.Single(v => v.Id == vr.Ref);
        var pv = protocol.CreateProtocolVariable(variable, events, vr.CommParam, vr.IsCommunicated);
        if (pv != null) { protocol.AddVariable(pv); created++; }
    }

    var write = (IProtocolVariableWriteProtocol)protocol;
    var writableCount = protocol.Variables.Count(write.CanWriteVariable);   // 8 params + matrix = 9

    // power-on defaults come from init= in the comm params (no script involved)
    ScalarVariable S(string name) => variables.OfType<ScalarVariable>().Single(v => v.Name == name);
    var initNotified = 0;
    protocol.Variables.Single(pv => pv.Variable.Name == "H1Amp")
        .SubscribeAsyncValueChanged(_ => { Interlocked.Increment(ref initNotified); return Task.CompletedTask; });

    await protocol.StartAsync();
    await Task.Delay(1500);
    var running = protocol.State == CommunicationState.Running;
    var initOk = Math.Abs(Raw(S("H1Amp")) - 680) < 1e-9 && Math.Abs(Raw(S("H1Freq")) - 0.5) < 1e-9
                 && Math.Abs(Raw(S("Nonlin")) - 0.6) < 1e-9 && Math.Abs(Raw(S("H2Phase")) - 76) < 1e-9
                 && Math.Abs(Raw(S("Hold"))) < 1e-9 && initNotified == 1;
    var stress = await SampleRange(S("Stress"), 2100);   // a full 0.5 Hz period, so both peaks are seen
    var noisy = await SampleRange(S("NoisyStepVal"), 300);
    var walk = await SampleRange(S("Walk2Val"), 600);
    var snap = DataSnapshot(matrix!);
    await protocol.StopAsync();

    var stressOk = stress.max > 500 && stress.min < -500;   // 680 fundamental, harmonics partly cancel at the peaks
    var noisyOk = noisy.max - noisy.min > 20;   // noise +-50 on the staircase
    var walkOk = walk.max - walk.min > 0;       // the walk moved at all
    var matrixDataOk = snap.Max() > 45 && snap.Min() < 30;
    var detail = $"vars={variables.Count}/17; created={created}/16; writable={writableCount}/9; matrix ok={matrixOk}; init ok={initOk}; running={running} ({protocol.StateMessage}); " +
                 $"stress {stress.min:F0}..{stress.max:F0}; noisy {noisy.min:F0}..{noisy.max:F0}; walk2 {walk.min:F2}..{walk.max:F2}; matrix {snap.Min():F1}..{snap.Max():F1}";
    return (variables.Count == 17 && created == 16 && writableCount == 9 && matrixOk && initOk && running && stressOk && noisyOk && walkOk && matrixDataOk, detail);
}

sealed class ConsoleLogSubscriber : ILogSubscriber
{
    public void Log(ILogMessage message) => Console.WriteLine($"   [log {message.Level}] {message.Message}");
    public Task LogAsync(ILogMessage message, CancellationToken ct = default) { Log(message); return Task.CompletedTask; }
}
