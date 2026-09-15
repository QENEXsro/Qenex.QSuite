using System.Diagnostics;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Protocols.SimulDataProtocol;

/// <summary>
/// Simulation source: generates the signal shapes from SimulSignalCatalog (sines, random
/// walks, LAOS strain/stress pair) directly into its variables. Each variable is generated
/// in the period of its periodic event, mirroring the poll scheduling of the Modbus/XCP
/// protocols; the hosting SimDataDriver only starts and stops the protocol. A scalar with
/// direction="write" is a live parameter, not a generated signal: "nonlin" drives the harmonic
/// amplitudes of "laosstress", "h1amp".."h4amp"/"h1freq".."h4freq"/"h1phase".."h4phase" tune single harmonics and
/// "hold" freezes the matrix generators while the simulation runs. A MatrixVariable with the
/// "thermal" signal is generated as a whole table (axes + data) and, with direction="readWrite",
/// its cells can be edited from the Matrix control (the edit lands in the raw buffer directly).
/// </summary>
public class SimulDataProtocol : ProtocolBase<int>, IProtocolVariableWriteProtocol
{
    private const int SchedulerIdleMs = 50;

    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    #region Constructors

    public SimulDataProtocol()
    {
        Specification = new SpecificationBase()
        {
            Name = "SimulDataProtocol",
            Label = "Simulation Signals",
            Description = "Generates test signal shapes (sines, walks, LAOS strain/stress pair with writable nonlinearity and per-harmonic amplitude/frequency/phase parameters, thermal-field matrix with a hold parameter); each variable in the period of its event. Use with the Simulation driver.",
            CreatedOn = new DateTime(2021, 11, 23),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #endregion

    #region Configuration

    // The protocol has no settings (everything lives in the variables' commParam); any key in
    // the project is reported as unknown through the shared parser.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Simulation Signals protocol");
    }

    // "signal" picks one of the catalog generators (step, noisystep, walk1-3, laosstrain,
    // laosstress; thermal for a matrix) or a writable parameter (nonlin, hold, h1amp..h4amp,
    // h1freq..h4freq, h1phase..h4phase; optional init= power-on default); optional
    // amp=/freq=/nonlin= tune the generator, the generation rate comes from the referenced event.
    // There is no id: the variable identifies itself, nothing on the "device" side needs a name.
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return string.Join(";",
            "direction=\"read\"",
            $"eventRef=\"{GetDefaultEventName(variableEvents)}\"",
            $"signal=\"{SimulSignalCatalog.StepKey}\"");
    }

    #endregion

    #region Protocol variables

    // The project loader uses this overload when the commParam carries no eventRef (which can
    // happen after a source move in the Project Configurator). Throwing here made such a
    // project impossible to open; instead create the variable without an event — the protocol
    // logs a "not generated" warning at start.
    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        return CreateProtocolVariable(variable, [], commParams, isCommunicated);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent varEvent, string id)
    {
        try
        {
            return new SimulDataProtocolVariable
            {
                Variable = variable,
                IsCommunicated = true,
                ProtocolVariableSpecification = SimulDataProtocolVariableSpecification.CreateDefault(varEvent)
            };
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"Protocol variable specification for variable {variable.Name} could not be created ({e.Message}).");
            return null;
        }
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents,
        string commParams, bool isCommunicated)
    {
        try
        {
            var eventRefName = commParams.Split(';').FirstOrDefault(e => e.Contains("eventRef"));
            eventRefName = eventRefName?.Split('=')[1].Trim('"');

            var varEvent = variableEvents.FirstOrDefault(e => e.Name == eventRefName);

            return new SimulDataProtocolVariable
            {
                Variable = variable,
                IsCommunicated = isCommunicated,
                ProtocolVariableSpecification = SimulDataProtocolVariableSpecification.Create(varEvent, commParams)
            };
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"Protocol variable specification for variable {variable.Name} could not be created ({e.Message}).");
            return null;
        }
    }

    #endregion

    #region Protocol control

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return Task.CompletedTask;
        }

        if (State == CommunicationState.Running || runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Starting);
        exitRequested = false;

        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: the loop must not inherit the caller's (UI) SynchronizationContext —
        // a blocked dispatcher (window drag, busy UI) would stall the whole session.
        // Capture the token now: a racing StopAsync may null runCts before the loop starts.
        var runToken = runCts.Token;
        runTask = Task.Run(() => GenerateLoopAsync(runToken));
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        exitRequested = true;

        if (runCts != null)
        {
            await runCts.CancelAsync();
        }

        if (runTask != null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                Logger?.Log(LogLevel.Warn, "Simulation protocol did not stop before timeout.");
            }
        }

        runCts?.Dispose();
        runCts = null;
        runTask = null;
        SetState(CommunicationState.Stopped);
    }

    public override void Dispose()
    {
        runCts?.Dispose();
    }

    #endregion

    #region Signal generation

    private sealed class PollEntry
    {
        public required SimulDataProtocolVariable ProtocolVariable { get; init; }
        public required IVariableBase Variable { get; init; }

        /// <summary>Writes the next sample (scalar) or table (matrix) into the variable for time t [s].</summary>
        public required Action<double> Generate { get; init; }

        /// <summary>Optional live gate: true = skip this cycle (matrix "hold" parameter).</summary>
        public Func<bool>? IsHeld { get; init; }

        public required long IntervalMs { get; init; }
        public long NextDueMs { get; set; }
    }

    private async Task GenerateLoopAsync(CancellationToken ct)
    {
        try
        {
            await ApplyParameterInitialValuesAsync();
            var entries = BuildPollEntries();
            SetState(CommunicationState.Running, $"Simulation: generating {entries.Count} signal(s).");

            if (entries.Count == 0)
            {
                Logger?.Log(LogLevel.Warn, "Simulation: no variables with a periodic event; nothing to generate.");
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }

            var clock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested && !exitRequested)
            {
                var now = Environment.TickCount64;
                var anyDue = false;

                foreach (var entry in entries)
                {
                    if (entry.NextDueMs > now)
                    {
                        continue;
                    }

                    anyDue = true;
                    if (entry.IsHeld?.Invoke() != true)
                    {
                        entry.Generate(clock.Elapsed.TotalSeconds);
                        entry.Variable.Timestamp = DateTime.UtcNow;
                        await entry.ProtocolVariable.NotifyValueChangedAsync();
                    }

                    // Absolute anchoring keeps the average rate exactly on the event period even
                    // though Task.Delay wakes late (Windows timer granularity); a variable that
                    // fell a whole interval behind skips the missed cycles instead of bursting.
                    entry.NextDueMs += entry.IntervalMs;
                    if (entry.NextDueMs <= Environment.TickCount64)
                    {
                        entry.NextDueMs = Environment.TickCount64 + entry.IntervalMs;
                    }
                }

                if (!anyDue)
                {
                    var nextDueIn = entries.Min(e => e.NextDueMs) - Environment.TickCount64;
                    var delay = (int)Math.Clamp(nextDueIn, 1, SchedulerIdleMs);
                    await Task.Delay(delay, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Simulation loop failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
        }
    }

    private List<PollEntry> BuildPollEntries()
    {
        var entries = new List<PollEntry>();
        var now = Environment.TickCount64;
        var parameterProvider = BuildParameterProvider();

        foreach (var protocolVariable in Variables)
        {
            if (protocolVariable is not SimulDataProtocolVariable simulVariable ||
                !simulVariable.IsCommunicated ||
                simulVariable.ProtocolVariableSpecification is not SimulDataProtocolVariableSpecification spec)
            {
                continue;
            }

            // Parameter variables (nonlin, hold, h{n}amp/h{n}freq/h{n}phase — or any scalar with a
            // write/readwrite direction) are written by the user from controls and read by the
            // generators — never generated over, whatever direction the configurator saved.
            var isMatrix = simulVariable.Variable is MatrixVariable;
            if (SimulSignalCatalog.IsParameterKey(spec.Signal) || (!isMatrix && spec.Direction != CommDirection.Read))
            {
                continue;
            }

            // A matrix is generated when readable (read/readWrite); write-only means user data only.
            if (isMatrix && spec.Direction == CommDirection.Write)
            {
                continue;
            }

            if (spec.VariableEvent is not PeriodicVarEvent periodicEvent)
            {
                Logger?.Log(LogLevel.Warn,
                    $"Simulation: variable '{simulVariable.Variable.Name}' has no periodic event ('{spec.VariableEvent?.Name}'); not generated.");
                continue;
            }

            var intervalMs = (long)periodicEvent.Period * (int)periodicEvent.Unit;
            if (intervalMs <= 0)
            {
                Logger?.Log(LogLevel.Warn,
                    $"Simulation: variable '{simulVariable.Variable.Name}' has a non-positive period; not generated.");
                continue;
            }

            var settings = new SimulSignalSettings(spec.Amp, spec.Freq, spec.Nonlin, parameterProvider);

            switch (simulVariable.Variable)
            {
                case ScalarVariable scalarVariable:
                {
                    if (!SupportsValues(scalarVariable))
                    {
                        Logger?.Log(LogLevel.Warn,
                            $"Simulation: variable '{scalarVariable.Name}' has an unsupported value type; not generated.");
                        continue;
                    }

                    var signal = SimulSignalCatalog.Create(ResolveSignalKey(spec, scalarVariable), settings);
                    entries.Add(new PollEntry
                    {
                        ProtocolVariable = simulVariable,
                        Variable = scalarVariable,
                        Generate = t => SetSampleValue(scalarVariable, signal.Next(t)),
                        IntervalMs = intervalMs,
                        NextDueMs = now
                    });
                    break;
                }

                case MatrixVariable matrixVariable:
                {
                    if (!SimulSignalCatalog.IsMatrixKey(spec.Signal))
                    {
                        Logger?.Log(LogLevel.Warn,
                            $"Simulation: matrix variable '{matrixVariable.Name}' needs a matrix signal " +
                            $"('{SimulSignalCatalog.ThermalMatrixKey}'), got '{spec.Signal}'; not generated.");
                        continue;
                    }

                    var layoutError = matrixVariable.ValidateLayout();
                    if (layoutError != null)
                    {
                        Logger?.Log(LogLevel.Warn,
                            $"Simulation: matrix variable '{matrixVariable.Name}' has an invalid layout ({layoutError}); not generated.");
                        continue;
                    }

                    var matrixSignal = SimulSignalCatalog.CreateMatrix(spec.Signal, settings);
                    entries.Add(new PollEntry
                    {
                        ProtocolVariable = simulVariable,
                        Variable = matrixVariable,
                        Generate = t => matrixSignal.Fill(matrixVariable, t),
                        IsHeld = () => (parameterProvider?.Invoke(SimulSignalCatalog.HoldParamKey) ?? 0.0) != 0.0,
                        IntervalMs = intervalMs,
                        NextDueMs = now
                    });
                    break;
                }

                default:
                    Logger?.Log(LogLevel.Warn,
                        $"Simulation: variable '{simulVariable.Variable.Name}' is neither scalar nor matrix; not generated.");
                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// Power-on defaults of the simulated device: every parameter variable with init= in its
    /// commParam gets that value written at start and a value-changed notification, so the
    /// watch table and other controls show the default without any script.
    /// </summary>
    private async Task ApplyParameterInitialValuesAsync()
    {
        foreach (var simulVariable in Variables.OfType<SimulDataProtocolVariable>())
        {
            if (simulVariable.ProtocolVariableSpecification is not SimulDataProtocolVariableSpecification
                {
                    Init: { } initialValue
                } spec
                || !SimulSignalCatalog.IsParameterKey(spec.Signal)
                || simulVariable.Variable is not ScalarVariable scalar
                || !SupportsValues(scalar))
            {
                continue;
            }

            SetSampleValue(scalar, initialValue);
            scalar.Timestamp = DateTime.UtcNow;
            await simulVariable.NotifyValueChangedAsync();
        }
    }

    /// <summary>
    /// Live source of the writable parameters (nonlin, hold, h{n}amp, h{n}freq, h{n}phase): looks up the
    /// scalar parameter variable by its signal key and returns its current raw value, or null
    /// when the project has no such parameter (generators then use their commParam/default).
    /// Returns null when the project has no parameter variables at all.
    /// </summary>
    private Func<string, double?>? BuildParameterProvider()
    {
        var parameters = new Dictionary<string, ScalarVariable>(StringComparer.Ordinal);
        foreach (var simulVariable in Variables.OfType<SimulDataProtocolVariable>())
        {
            if (simulVariable.ProtocolVariableSpecification is SimulDataProtocolVariableSpecification spec
                && SimulSignalCatalog.IsParameterKey(spec.Signal)
                && simulVariable.Variable is ScalarVariable scalar)
            {
                parameters.TryAdd(spec.Signal, scalar);
            }
        }

        if (parameters.Count == 0)
        {
            return null;
        }

        return key => parameters.TryGetValue(key, out var scalar) ? GetSampleValue(scalar) : null;
    }

    private string ResolveSignalKey(SimulDataProtocolVariableSpecification spec, ScalarVariable scalarVariable)
    {
        if (string.IsNullOrWhiteSpace(spec.Signal))
        {
            return SimulSignalCatalog.FallbackKey(scalarVariable);
        }

        if (!SimulSignalCatalog.IsKnown(spec.Signal))
        {
            var fallback = SimulSignalCatalog.FallbackKey(scalarVariable);
            Logger?.Log(LogLevel.Warn,
                $"Simulation: variable '{scalarVariable.Name}' has an unknown signal '{spec.Signal}'; using '{fallback}'.");
            return fallback;
        }

        return spec.Signal;
    }

    private static bool SupportsValues(ScalarVariable scalarVariable)
    {
        return scalarVariable.Values is Values<double> or Values<float> or Values<long> or Values<ulong>
            or Values<int> or Values<uint> or Values<short> or Values<ushort> or Values<byte> or Values<sbyte>;
    }

    // Inverse of SetSampleValue: reads the scalar's current raw value as double (used for
    // live parameter variables).
    private static double GetSampleValue(ScalarVariable scalarVariable)
    {
        return scalarVariable.Values switch
        {
            Values<double> doubleValues => doubleValues.Value,
            Values<float> floatValues => floatValues.Value,
            Values<long> longValues => longValues.Value,
            Values<ulong> ulongValues => ulongValues.Value,
            Values<int> intValues => intValues.Value,
            Values<uint> uintValues => uintValues.Value,
            Values<short> shortValues => shortValues.Value,
            Values<ushort> ushortValues => ushortValues.Value,
            Values<byte> byteValues => byteValues.Value,
            Values<sbyte> sbyteValues => sbyteValues.Value,
            _ => 0.0
        };
    }

    // Values<T>.SetValue does not convert, so the generated double is rounded and clamped
    // to the variable's raw type here.
    private static void SetSampleValue(ScalarVariable scalarVariable, double sample)
    {
        switch (scalarVariable.Values)
        {
            case Values<double> doubleValues:
                doubleValues.Value = sample;
                break;
            case Values<float> floatValues:
                floatValues.Value = (float)sample;
                break;
            case Values<long> longValues:
                longValues.Value = (long)Math.Clamp(Math.Round(sample), long.MinValue, long.MaxValue);
                break;
            case Values<ulong> ulongValues:
                ulongValues.Value = (ulong)Math.Clamp(Math.Round(sample), ulong.MinValue, ulong.MaxValue);
                break;
            case Values<int> intValues:
                intValues.Value = (int)Math.Clamp(Math.Round(sample), int.MinValue, int.MaxValue);
                break;
            case Values<uint> uintValues:
                uintValues.Value = (uint)Math.Clamp(Math.Round(sample), uint.MinValue, uint.MaxValue);
                break;
            case Values<short> shortValues:
                shortValues.Value = (short)Math.Clamp(Math.Round(sample), short.MinValue, short.MaxValue);
                break;
            case Values<ushort> ushortValues:
                ushortValues.Value = (ushort)Math.Clamp(Math.Round(sample), ushort.MinValue, ushort.MaxValue);
                break;
            case Values<byte> byteValues:
                byteValues.Value = (byte)Math.Clamp(Math.Round(sample), byte.MinValue, byte.MaxValue);
                break;
            case Values<sbyte> sbyteValues:
                sbyteValues.Value = (sbyte)Math.Clamp(Math.Round(sample), sbyte.MinValue, sbyte.MaxValue);
                break;
        }
    }

    #endregion

    #region IProtocolVariableWriteProtocol

    /// <summary>Writable are only this protocol's parameter variables (write or readwrite direction).</summary>
    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable)
               && protocolVariable is SimulDataProtocolVariable
               {
                   ProtocolVariableSpecification: SimulDataProtocolVariableSpecification
                   {
                       Direction: CommDirection.Write or CommDirection.ReadWrite
                   }
               };
    }

    /// <summary>
    /// Nothing to transfer for a scalar: the written value already lives in the variable and the
    /// generators read it from there on the next sample. For a matrix the queued element writes
    /// are re-applied over the raw buffer — the generator may have refilled the table between
    /// the edit and this call, and the edit must win (with "hold" set the generator is idle).
    /// </summary>
    public Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        if (protocolVariable.Variable is MatrixVariable matrixVariable)
        {
            while (matrixVariable.TryDequeuePendingWrite(out var request))
            {
                if (request.ByteOffset < 0 || request.Bytes.Length == 0 ||
                    request.ByteOffset + request.Bytes.Length > matrixVariable.Size)
                {
                    Logger?.Log(LogLevel.Warn,
                        $"Simulation: pending write of '{matrixVariable.Name}' at byte {request.ByteOffset} " +
                        "does not fit the matrix layout; skipped.");
                    continue;
                }

                request.Bytes.CopyTo(matrixVariable.RawData.AsSpan(request.ByteOffset));
            }
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Received data & encoding (not used - the protocol is a data source)

    public override Task AddReceivedDataToQueueAsync(IEnumerable<int> data, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    protected override void ProcessReceivedData(IEnumerable<int> data)
    {
    }

    protected override Task ProcessReceivedDataAsync(IEnumerable<int> data, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    protected override IEnumerable<int> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        throw new NotImplementedException();
    }

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<int> data)
    {
        throw new NotImplementedException();
    }

    #endregion
}
