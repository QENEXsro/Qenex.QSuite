using System.Threading.Channels;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>
/// Transport-independent core of the simplified XCP master (ASAM MCD-1 XCP 1.1). Owns everything
/// the CAN and TCP variants share: the session run loop (connect, reconnect on failure, fault on
/// incompatible slaves), polling by SHORT_UPLOAD per scalar and by block transfer per matrix
/// (address/size from the module XML, interval from the variable's periodic event), on-request
/// reads, operator writes via SET_MTA + DOWNLOAD (matrix: only the edited cells, merged into
/// contiguous windows) with echo suppression, and protocol-variable creation. A transport subclass contributes only its session
/// settings, the framing of outgoing XCP packets into <typeparamref name="TFrame"/> and the
/// extraction of received packets (its <see cref="ProtocolBase{T}.AddReceivedDataToQueueAsync"/>
/// feeds <see cref="Master"/>.<see cref="XcpMaster.OnPacketReceived"/>).
/// </summary>
public abstract class XcpProtocolBase<TFrame> : ProtocolBase<TFrame>, ITransportProtocol<TFrame>, IProtocolVariableWriteProtocol,
    IProtocolVariableReadProtocol
{
    private const int ReconnectDelayMs = 2000;
    private const int MaxConsecutiveBusFailures = 5;
    private const int SchedulerIdleMs = 50;

    private string? configurationError;

    private volatile XcpMaster? master;
    private volatile Func<TFrame, CancellationToken, Task>? transmitter;

    /// <summary>Variables currently streamed by the STIM loop (S3); their operator writes must
    /// not additionally go through SET_MTA + DOWNLOAD. Null outside an active STIM session.</summary>
    private volatile IReadOnlySet<IProtocolVariable>? stimServedVariables;

    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    // Transport connection state pushed down from the owning driver (OnTransportConnectionChanged).
    // A lost transport cancels the current session at once so the loops stop polling a dead link;
    // the run loop then waits here until the driver reports the transport back and re-CONNECTs.
    private volatile bool transportConnected = true;
    private readonly object sessionLock = new();
    private CancellationTokenSource? sessionCts;

    /// <summary>Live session engine while a session is established; null otherwise. The receive
    /// path of the transport subclass forwards extracted XCP packets to it.</summary>
    protected XcpMaster? Master => master;

    /// <summary>Parses and stores the transport's session settings; throws
    /// <see cref="ArgumentException"/> on invalid input (reported as a configuration fault).</summary>
    protected abstract void ApplyConfiguration(string rawSettings);

    /// <summary>Response timeout per command, from the parsed session settings.</summary>
    protected abstract int SessionTimeoutMs { get; }

    /// <summary>Command repetitions after a timeout, from the parsed session settings.</summary>
    protected abstract int SessionRequestRetries { get; }

    /// <summary>DAQ time axis source per the daqTimestamps setting: true = ECU timestamps
    /// (default; the SET_DAQ_LIST_MODE timestamp bit is set), false = PC receive time (the bit
    /// stays clear, so a QFW SDK slave sends no timestamps at all; a legacy TIMESTAMP_FIXED
    /// slave still sends them and they are ignored).</summary>
    protected abstract bool UseSlaveDaqTimestamps { get; }

    /// <summary>Transport name for diagnostics, e.g. "CAN" or "TCP".</summary>
    protected abstract string TransportName { get; }

    /// <summary>How the connected slave is called in the success log message.</summary>
    protected abstract string SlaveDescription { get; }

    /// <summary>
    /// Builds the per-session packet transmitter: wraps one outgoing XCP packet into the
    /// transport's framing and sends it through <paramref name="frameTransmitter"/>. Called once
    /// on every session (re)connect, so per-session framing state resets here.
    /// </summary>
    protected abstract Func<byte[], CancellationToken, Task> CreatePacketTransmitter(
        Func<TFrame, CancellationToken, Task> frameTransmitter);

    // Without events only write-only variables can be configured (polled reads need an eventRef).
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return string.Join(";",
            "address=\"0x0\"",
            "addressExtension=\"0\"",
            "direction=\"read\"",
            $"eventRef=\"{GetDefaultEventName(variableEvents)}\"");
    }

    public override void SetConfiguration()
    {
        try
        {
            ApplyConfiguration(RawSettings);
            configurationError = null;
        }
        catch (ArgumentException e)
        {
            configurationError = $"Invalid XCP session settings: {e.Message}";
            Logger?.Log(LogLevel.Error, configurationError);
        }
    }

    /// <summary>Injected by the hosting driver while its connection is usable.</summary>
    public void SetTransmitter(Func<TFrame, CancellationToken, Task>? frameTransmitter)
    {
        transmitter = frameTransmitter;
    }

    #region Protocol variables

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        return Build(variable, null, commParams, isCommunicated);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return Build(variable, [variableEvent], $"address=\"{id}\";eventRef=\"{variableEvent.Name}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents,
        string commParams, bool isCommunicated)
    {
        return Build(variable, variableEvents, commParams, isCommunicated);
    }

    private XcpProtocolVariable? Build(IVariableBase variable, IEnumerable<IVarEvent>? variableEvents, string commParams,
        bool isCommunicated)
    {
        try
        {
            return new XcpProtocolVariable
            {
                Variable = variable,
                IsCommunicated = isCommunicated,
                ProtocolVariableSpecification = XcpVariableSpecification.Create(commParams, variableEvents, variable, Logger)
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

        if (configurationError != null)
        {
            SetState(CommunicationState.Faulted, configurationError);
            return Task.CompletedTask;
        }

        if (State == CommunicationState.Running || runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        if (!SessionSealValid())
        {
            SetState(CommunicationState.Stopped);
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Starting);
        exitRequested = false;
        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: the loop must not inherit the caller's (UI) SynchronizationContext —
        // a blocked dispatcher (window drag, busy UI) would stall the whole session.
        // Capture the token now: a racing StopAsync may null runCts before the loop starts.
        var runToken = runCts.Token;
        runTask = Task.Run(() => RunLoopAsync(runToken));
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
                Logger?.Log(LogLevel.Warn, "XCP protocol did not stop before timeout.");
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

    #region Session run loop

    private async Task RunLoopAsync(CancellationToken ct)
    {
        string? stopMessage = null;
        try
        {
            while (!ct.IsCancellationRequested && !exitRequested)
            {
                // Session-scoped token: cancelled either by the run token (stop) or by
                // OnTransportConnectionChanged(false) (transport lost) so the session drops at once.
                CancellationToken sct;
                lock (sessionLock)
                {
                    sessionCts?.Dispose();
                    sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sct = sessionCts.Token;
                }

                try
                {
                    await WaitForTransportAsync(ct);
                    await ConnectSessionAsync(sct);
                    await RunSessionAsync(sct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
                {
                    throw; // real stop — leave the loop
                }
                catch (OperationCanceledException)
                {
                    // Session aborted because the transport was lost (sct cancelled, run token still
                    // live). Drop the stale session and loop; WaitForTransportAsync blocks until the
                    // driver reports the transport back, then a fresh CONNECT runs immediately.
                    Logger?.Log(LogLevel.Info, "XCP: transport lost — session dropped, reconnecting when the transport is back.");
                    SetState(CommunicationState.Faulted, "Transport lost — reconnecting.");
                    await TryDisconnectAsync();
                    master = null;
                }
                catch (XcpUnsupportedSlaveException e)
                {
                    // Reconnecting cannot fix an incompatible slave — stop with a clear error.
                    stopMessage = e.Message;
                    Logger?.Log(LogLevel.Error, $"XCP: {e.Message}");
                    SetState(CommunicationState.Faulted, e.Message);
                    return;
                }
                catch (Exception e)
                {
                    Logger?.Log(LogLevel.Warn, $"XCP session failed: {e.Message} Reconnecting in {ReconnectDelayMs} ms.");
                    SetState(CommunicationState.Faulted, $"XCP session failed: {e.Message}");
                    await Task.Delay(ReconnectDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        finally
        {
            lock (sessionLock)
            {
                sessionCts?.Dispose();
                sessionCts = null;
            }

            await TryDisconnectAsync();
            master = null;
            if (State != CommunicationState.Faulted)
            {
                SetState(CommunicationState.Stopped, stopMessage);
            }
            exitRequested = false;
        }
    }

    /// <summary>
    /// The owning driver reports transport loss/recovery here. A lost transport cancels the live
    /// session at once (the run loop then re-CONNECTs as soon as the transport is back), so the
    /// protocol no longer has to discover the loss through repeated command timeouts.
    /// </summary>
    public override void OnTransportConnectionChanged(bool connected)
    {
        transportConnected = connected;
        if (connected)
        {
            return;
        }

        lock (sessionLock)
        {
            try
            {
                sessionCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Session already torn down — nothing to cancel.
            }
        }
    }

    /// <summary>Blocks until the driver reports the transport connected, so a fresh CONNECT is
    /// only attempted once there is a live transport to run it on.</summary>
    private async Task WaitForTransportAsync(CancellationToken ct)
    {
        if (transportConnected)
        {
            return;
        }

        Logger?.Log(LogLevel.Debug, $"XCP: waiting for the {TransportName} transport to reconnect...");
        while (!transportConnected)
        {
            await Task.Delay(SchedulerIdleMs, ct);
        }
    }

    private async Task ConnectSessionAsync(CancellationToken ct)
    {
        var frameTransmitter = transmitter
                               ?? throw new XcpProtocolException(
                                   $"{TransportName} transport is not available (no transmitter injected by the driver).");

        var session = new XcpMaster(Logger)
        {
            TimeoutMs = SessionTimeoutMs,
            MaxRetries = SessionRequestRetries,
            Transmitter = CreatePacketTransmitter(frameTransmitter)
        };
        master = session;

        var status = await session.ConnectAsync(ct);
        var connectInfo = session.ConnectInfo!;
        WarnOnMatrixEndiannessMismatch(connectInfo.IsBigEndian);

        var message = session.WritesAllowed
            ? $"Connected to {SlaveDescription} ({(connectInfo.IsBigEndian ? "big" : "little")}-endian)."
            : status.IsCalibrationProtected
                ? "Connected. Calibration is protected — requires seed & key; writes are disabled."
                : "Connected. The slave has no calibration resource; writes are disabled.";

        Logger?.Log(LogLevel.Info, $"XCP: {message}");
        SetState(CommunicationState.Running, message);
    }

    /// <summary>A matrix block travels raw and the variable decodes its elements by its own
    /// endianness setting — that setting has to match the slave's byte order from CONNECT.
    /// A mismatch is a configuration error worth a warning, the data is not touched.</summary>
    private void WarnOnMatrixEndiannessMismatch(bool slaveIsBigEndian)
    {
        foreach (var protocolVariable in Variables)
        {
            if (protocolVariable is XcpProtocolVariable { Variable: MatrixVariable matrix } &&
                (matrix.Endianness == MatrixEndianness.Big) != slaveIsBigEndian)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: matrix '{matrix.Name}' is configured {(matrix.Endianness == MatrixEndianness.Big ? "big" : "little")}-endian " +
                    $"but the slave is {(slaveIsBigEndian ? "big" : "little")}-endian; its element values will decode wrong.");
            }
        }
    }

    private async Task TryDisconnectAsync()
    {
        var session = master;
        if (session is not { IsConnected: true } || transmitter == null)
        {
            return;
        }

        try
        {
            // Best effort with its own short budget — the run token is already cancelled here.
            await session.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(1500));
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"XCP: DISCONNECT on stop failed ({e.Message}).");
        }
    }

    #endregion

    #region Session body (DAQ + STIM + polling)

    /// <summary>
    /// Runs one connected session: sets up DAQ and STIM for variables bound to DAQ/STIM events
    /// (D1–D6, S1–S7; a failed setup falls back to polling and direct writes), then serves
    /// polling, the DAQ DTO stream and the STIM transmission loop concurrently until the session
    /// dies or the protocol stops.
    /// </summary>
    private async Task RunSessionAsync(CancellationToken ct)
    {
        var (daqSession, stimSession) = await TrySetupDaqStimAsync(ct);
        stimServedVariables = stimSession?.VariablesInStim;

        var consumer = daqSession != null ? ConsumeDaqDtosAsync(daqSession, ct) : Task.CompletedTask;
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var loops = new List<Task> { PollLoopAsync(daqSession?.VariablesInDaq, loopCts.Token) };
            if (stimSession != null)
            {
                loops.Add(StimLoopAsync(stimSession, loopCts.Token));
            }

            // The first loop to finish decides the session's fate (normal stop or failure); the
            // other one is cancelled and only its unexpected failures are logged.
            var first = await Task.WhenAny(loops);
            await loopCts.CancelAsync();
            foreach (var loop in loops.Where(l => l != first))
            {
                try
                {
                    await loop;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Logger?.Log(LogLevel.Warn, $"XCP: session loop failed while stopping ({e.Message}).");
                }
            }

            await first;
        }
        finally
        {
            stimServedVariables = null;
            if (master is { } session)
            {
                session.DaqDtoReceived = null;
            }

            daqSession?.Dtos.Writer.TryComplete();
            try
            {
                await consumer;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    #endregion

    #region Polling

    private sealed class PollEntry
    {
        public required XcpProtocolVariable ProtocolVariable { get; init; }
        public required XcpVariableSpecification Spec { get; init; }
        /// <summary>Exactly one of Scalar / Matrix is set.</summary>
        public ScalarVariable? Scalar { get; init; }
        public MatrixVariable? Matrix { get; init; }
        public required long IntervalMs { get; init; }
        public long NextDueMs { get; set; }
        public string Name => ProtocolVariable.Variable.Name;
    }

    private async Task PollLoopAsync(IReadOnlySet<IProtocolVariable>? daqServedVariables, CancellationToken ct)
    {
        var entries = BuildPollEntries(daqServedVariables);
        if (entries.Count == 0)
        {
            Logger?.Log(LogLevel.Debug, daqServedVariables == null
                ? "XCP: no pollable variables configured; serving writes only."
                : "XCP: all read variables are served by DAQ; polling is idle.");

            // Keep the session alive but notice a dead transport (the driver nulls the
            // transmitter on connection loss) — otherwise a DAQ-only session would never reconnect.
            while (!ct.IsCancellationRequested && !exitRequested)
            {
                if (transmitter == null)
                {
                    throw new XcpProtocolException($"{TransportName} transport is no longer available.");
                }

                await Task.Delay(500, ct);
            }

            return;
        }

        var consecutiveBusFailures = 0;
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
                try
                {
                    await PollVariableAsync(entry, ct);
                    consecutiveBusFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
                {
                    consecutiveBusFailures++;
                    Logger?.Log(LogLevel.Warn, $"XCP: poll of '{entry.Name}' failed: {e.Message}");
                    SetState(CommunicationState.Running, $"Poll of '{entry.Name}' failed: {e.Message}");

                    if (consecutiveBusFailures >= MaxConsecutiveBusFailures)
                    {
                        throw new XcpProtocolException(
                            $"{consecutiveBusFailures} consecutive poll failures (last: {e.Message}).");
                    }
                }

                // Re-anchor to now: after a stall the missed cycles are skipped instead of bursting.
                entry.NextDueMs = Environment.TickCount64 + entry.IntervalMs;
            }

            if (!anyDue)
            {
                var nextDueIn = entries.Min(e => e.NextDueMs) - Environment.TickCount64;
                var delay = (int)Math.Clamp(nextDueIn, 1, SchedulerIdleMs);
                await Task.Delay(delay, ct);
            }
        }
    }

    private List<PollEntry> BuildPollEntries(IReadOnlySet<IProtocolVariable>? daqServedVariables)
    {
        var entries = new List<PollEntry>();
        var now = Environment.TickCount64;

        foreach (var protocolVariable in Variables)
        {
            if (protocolVariable is not XcpProtocolVariable xcpVariable ||
                !xcpVariable.IsCommunicated ||
                xcpVariable.ProtocolVariableSpecification is not XcpVariableSpecification spec ||
                spec.Direction == CommDirection.Write ||
                daqServedVariables?.Contains(protocolVariable) == true)
            {
                continue;
            }

            if (spec.IsOnRequestEvent)
            {
                // Read only on request (IProtocolVariableReadProtocol) — never polled.
                Logger?.Log(LogLevel.Debug, $"XCP: variable '{xcpVariable.Variable.Name}' is read on request; not polled.");
                continue;
            }

            ScalarVariable? scalarVariable = null;
            MatrixVariable? matrixVariable = null;
            switch (xcpVariable.Variable)
            {
                case ScalarVariable scalar:
                    scalarVariable = scalar;
                    break;
                case MatrixVariable matrix:
                    // A matrix is polled as one raw block (slave block mode when the slave has it).
                    matrixVariable = matrix;
                    break;
                default:
                    Logger?.Log(LogLevel.Warn, $"XCP: variable '{xcpVariable.Variable.Name}' is neither scalar nor matrix; not polled.");
                    continue;
            }

            var name = xcpVariable.Variable.Name;
            if (spec.VariableEvent is not PeriodicVarEvent periodicEvent)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: variable '{name}' has no periodic event ('{spec.VariableEvent?.Name}'); not polled.");
                continue;
            }

            var intervalMs = (long)periodicEvent.Period * (int)periodicEvent.Unit;
            if (intervalMs <= 0)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: variable '{name}' has a non-positive poll interval; not polled.");
                continue;
            }

            entries.Add(new PollEntry
            {
                ProtocolVariable = xcpVariable,
                Spec = spec,
                Scalar = scalarVariable,
                Matrix = matrixVariable,
                IntervalMs = intervalMs,
                NextDueMs = now
            });
        }

        return entries;
    }

    private async Task PollVariableAsync(PollEntry entry, CancellationToken ct)
    {
        var session = master ?? throw new XcpProtocolException("Not connected to the XCP slave.");

        var bytes = await session.ReadMemoryAsync(entry.Spec.AddressExtension, entry.Spec.Address, entry.Spec.Size, ct);
        if (entry.Matrix != null)
        {
            await ApplyBusBlockAsync(entry.ProtocolVariable, entry.Matrix, bytes, DateTime.UtcNow);
            return;
        }

        var value = session.Codec.DecodeValue(bytes, entry.Spec.DataType);
        await ApplyBusValueAsync(entry.ProtocolVariable, entry.Scalar!, value, DateTime.UtcNow);
    }

    private static async Task ApplyBusValueAsync(XcpProtocolVariable protocolVariable, ScalarVariable scalar,
        object value, DateTime timestampUtc)
    {
        scalar.SetValue(value);
        scalar.Timestamp = timestampUtc;

        // The operator-write path listens on this notification; the flag keeps a bus-sourced update
        // from echoing back to the ECU (NotifyValueChangedAsync awaits its handlers, so the scope holds).
        protocolVariable.IsUpdatingFromBus = true;
        try
        {
            await protocolVariable.NotifyValueChangedAsync();
        }
        finally
        {
            protocolVariable.IsUpdatingFromBus = false;
        }
    }

    /// <summary>A matrix block read from the ECU replaces the variable's raw buffer as a whole
    /// (elements are decoded by the variable's own layout) and is published exactly like a polled
    /// scalar — timestamp of the data plus the bus-update flag against echoing.</summary>
    private static async Task ApplyBusBlockAsync(XcpProtocolVariable protocolVariable, MatrixVariable matrix,
        byte[] block, DateTime timestampUtc)
    {
        matrix.SetValue(block);
        matrix.Timestamp = timestampUtc;

        protocolVariable.IsUpdatingFromBus = true;
        try
        {
            await protocolVariable.NotifyValueChangedAsync();
        }
        finally
        {
            protocolVariable.IsUpdatingFromBus = false;
        }
    }

    #endregion

    #region DAQ (measurement, decisions D1-D6)

    private readonly record struct DaqDto(byte[] Packet, DateTime ReceivedUtc);

    private sealed record DaqTarget(XcpProtocolVariable ProtocolVariable, ScalarVariable Scalar, XcpVariableSpecification Spec);

    private sealed class DaqSessionState
    {
        public required XcpDaqDecoder Decoder { get; init; }
        public required XcpCodec Codec { get; init; }
        public required DaqTarget?[][][] Targets { get; init; } // [daq list][odt][entry], mirrors the combined plans; null = ODT 0 filler
        public required Channel<DaqDto> Dtos { get; init; }
        public required IReadOnlySet<IProtocolVariable> VariablesInDaq { get; init; }
        public XcpDaqTimestampMapper? TimestampMapper { get; set; } // null = stamp with receive time
    }

    private sealed class StimList
    {
        public required ushort DaqListNumber { get; init; } // global list number in the combined config
        public required DaqTarget[][] Odts { get; init; }   // [odt][entry], mirrors the list's plan
        public required long IntervalMs { get; init; }
        public byte FirstPid { get; set; }                  // slave-assigned, absolute-PID identification only
        public long NextDueMs { get; set; }
    }

    private sealed class StimSessionState
    {
        public required XcpStimEncoder Encoder { get; init; }
        public required XcpCodec Codec { get; init; }
        public required StimList[] Lists { get; init; }
        public required IReadOnlySet<IProtocolVariable> VariablesInStim { get; init; }
        public required double TimestampTickSeconds { get; init; } // 0 = untimestamped STIM lists
        public long AnchorMs { get; } = Environment.TickCount64;   // zero point of the master clock sent in ODT 0
    }

    /// <summary>
    /// Builds and starts the DAQ and STIM sides of the session for all variables bound to
    /// DAQ/STIM events, as one slave configuration (S4: FREE_DAQ wipes everything, so both
    /// directions must be replayed together; DAQ lists first, STIM lists after them). Either
    /// side degrades independently — DAQ variables fall back to polling (D4), STIM variables to
    /// direct writes on change (S5) — and every degradation is reported with its reason.
    /// </summary>
    private async Task<(DaqSessionState? Daq, StimSessionState? Stim)> TrySetupDaqStimAsync(CancellationToken ct)
    {
        var session = master;
        if (session is not { IsConnected: true })
        {
            return (null, null);
        }

        var daqChannels = CollectDaqCandidates();
        var stimChannels = CollectStimCandidates();
        if (daqChannels.Count == 0 && stimChannels.Count == 0)
        {
            return (null, null);
        }

        var connectInfo = session.ConnectInfo!;
        if (daqChannels.Count > 0)
        {
            if (!connectInfo.SupportsDaq)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: the slave has no DAQ resource; variables with DAQ events fall back to polling.");
                daqChannels.Clear();
            }
            else if (session.LastStatus?.IsDaqProtected == true)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: the slave protects DAQ with seed & key, which is not supported; variables with DAQ events fall back to polling.");
                daqChannels.Clear();
            }
        }

        if (stimChannels.Count > 0)
        {
            if (!connectInfo.SupportsStim)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: the slave has no STIM resource; variables with STIM events fall back to direct writes on change.");
                stimChannels.Clear();
            }
            else if (session.LastStatus?.IsStimProtected == true)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: the slave protects STIM with seed & key, which is not supported; variables with STIM events fall back to direct writes on change.");
                stimChannels.Clear();
            }
        }

        if (daqChannels.Count == 0 && stimChannels.Count == 0)
        {
            return (null, null);
        }

        try
        {
            var processor = await session.GetDaqProcessorInfoAsync(ct);
            if (!processor.HasDynamicLists)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: the slave only supports static DAQ lists, which is not implemented; falling back to polling and direct writes.");
                return (null, null);
            }

            XcpDaqResolutionInfo? resolution = null;
            try
            {
                resolution = await session.GetDaqResolutionInfoAsync(ct);
            }
            catch (XcpErrorException e) when (e.ErrorCode == XcpErrorCode.CmdUnknown)
            {
                // Without the resolution info no timestamp size is known: DAQ then runs on the PC
                // receive time whatever daqTimestamps says, and the ODT entry limits fall back to
                // defaults. A configured ECU time axis that is silently not delivered is worth a
                // warning; in master mode the outcome matches the setting, so a note is enough.
                Logger?.Log(UseSlaveDaqTimestamps ? LogLevel.Warn : LogLevel.Info,
                    "XCP: the slave does not implement GET_DAQ_RESOLUTION_INFO — DAQ runs without slave timestamps " +
                    "(samples carry the PC receive time" +
                    (UseSlaveDaqTimestamps ? "; daqTimestamps=slave cannot be honoured" : "") +
                    "), ODT entry limits use defaults.");
            }

            await ValidateChannelsAsync(session, processor, daqChannels, isStim: false, ct);
            await ValidateChannelsAsync(session, processor, stimChannels, isStim: true, ct);
            if (daqChannels.Count == 0 && stimChannels.Count == 0)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: no DAQ/STIM event channel survived validation; falling back to polling and direct writes.");
                return (null, null);
            }

            // The SET_DAQ_LIST_MODE timestamp bit is the master's choice: set it only with
            // daqTimestamps=slave. A QFW SDK slave (0.10.0+) then sends a DWORD timestamp in ODT 0
            // and otherwise nothing, so with daqTimestamps=master ODT 0 keeps its full data
            // capacity. Compatibility: a legacy TIMESTAMP_FIXED slave (e.g. XCPlite) sends
            // timestamps always, so the packet layout must include them even in master mode.
            var slaveTimestampSize = resolution?.TimestampSize ?? 0;
            var includeTimestamp = daqChannels.Count > 0 && slaveTimestampSize > 0 &&
                                   (UseSlaveDaqTimestamps || resolution is { TimestampFixed: true });
            var daqTimestampSize = includeTimestamp ? slaveTimestampSize : 0;
            var (daqPlans, daqTargets) = PackDaqLists(daqChannels, connectInfo, processor, resolution, daqTimestampSize);
            if (daqChannels.Count > 0 && daqPlans.Count == 0)
            {
                Logger?.Log(LogLevel.Warn, "XCP: no variable fits into a DAQ packet; DAQ variables fall back to polling.");
            }

            // S4: STIM lists run untimestamped (bit clear; a QFW SDK slave takes ODT 0 as pure
            // data). Compatibility only: a legacy TIMESTAMP_FIXED slave rejects switching the
            // timestamp off (ERR_CMD_SYNTAX), so its STIM lists run timestamped and the master
            // emits its clock in ODT 0.
            var includeStimTimestamp = stimChannels.Count > 0 && slaveTimestampSize > 0 &&
                                       resolution is { TimestampFixed: true };
            var stimTimestampSize = includeStimTimestamp ? slaveTimestampSize : 0;
            var (stimPlans, stimTargets) = PackStimLists(stimChannels, connectInfo, processor, resolution, stimTimestampSize);
            if (stimChannels.Count > 0 && stimPlans.Count == 0)
            {
                Logger?.Log(LogLevel.Warn,
                    "XCP: no variable fits into a STIM packet; STIM variables fall back to direct writes on change.");
            }

            if (daqPlans.Count == 0 && stimPlans.Count == 0)
            {
                return (null, null);
            }

            var lists = daqPlans.Concat(stimPlans).ToList();

            DaqSessionState? daqState = null;
            if (daqPlans.Count > 0)
            {
                // The decoder gets the combined plans so incoming DTOs resolve global list
                // numbers; the targets array is combined for the same reason (a conforming slave
                // never sends DTOs for STIM lists, but a misbehaving one must not crash us).
                var decoder = new XcpDaqDecoder(processor.IdentificationType, session.Codec.IsBigEndian,
                    daqTimestampSize, processor.OverloadIndicationByPid, lists, Logger);

                var variablesInDaq = daqTargets
                    .SelectMany(list => list.SelectMany(odt => odt))
                    .Where(target => target != null)
                    .Select(IProtocolVariable (target) => target!.ProtocolVariable)
                    .ToHashSet();

                var droppedDtos = 0L;
                var dtos = Channel.CreateBounded<DaqDto>(
                    new BoundedChannelOptions(8192)
                    {
                        SingleReader = true,
                        FullMode = BoundedChannelFullMode.DropOldest
                    },
                    _ =>
                    {
                        // D6: overload never faults the session; dropping the oldest keeps the view live.
                        droppedDtos++;
                        if (droppedDtos == 1 || droppedDtos % 1000 == 0)
                        {
                            Logger?.Log(LogLevel.Warn,
                                $"XCP: DAQ processing cannot keep up — {droppedDtos} packets dropped so far (oldest first).");
                        }
                    });

                daqState = new DaqSessionState
                {
                    Decoder = decoder,
                    Codec = session.Codec,
                    Targets = daqTargets.Concat(stimTargets).ToArray(),
                    Dtos = dtos,
                    VariablesInDaq = variablesInDaq
                };
            }

            if (daqState != null)
            {
                // Wired before START_STOP_SYNCH — the slave starts streaming the instant it acks,
                // and nothing may fall into the gap. (Absolute-PID slaves still drop DTOs until
                // the FIRST_PIDs arrive right below; those are counted by the decoder.)
                session.DaqDtoReceived = packet => daqState.Dtos.Writer.TryWrite(new DaqDto(packet, DateTime.UtcNow));
            }

            var firstPids = await session.ConfigureAndStartDaqAsync(lists, includeTimestamp, includeStimTimestamp, ct);
            daqState?.Decoder.SetFirstPids(firstPids);

            // 4b/4c: with timestamps in the stream, samples keep the slave-side spacing on the
            // time axis instead of clustering at the TCP receive bursts.
            var timestampMapper = daqState != null && includeTimestamp && UseSlaveDaqTimestamps
                ? new XcpDaqTimestampMapper(daqTimestampSize, resolution!.TimestampTickSeconds, lists.Count, Logger)
                : null;
            if (daqState != null)
            {
                daqState.TimestampMapper = timestampMapper;
            }

            var stimState = BuildStimState(session, processor, resolution, stimPlans, stimTargets,
                daqPlans.Count, firstPids, includeStimTimestamp ? slaveTimestampSize : 0);

            // Info, not Debug: which clock the DAQ time axis comes from is an operational fact the
            // user must be able to see in the normal log (one line per Start).
            if (daqState != null)
            {
                Logger?.Log(LogLevel.Info,
                    $"XCP: DAQ started — {daqPlans.Count} list(s) on event channel(s) " +
                    $"{string.Join(", ", daqPlans.Select(p => p.EventChannel))}, {daqState.VariablesInDaq.Count} variable(s), " +
                    (timestampMapper != null
                        ? $"slave timestamps on ({resolution!.TimestampTickSeconds * 1e6:0.###} µs/tick)."
                        : includeTimestamp
                            ? "slave timestamps ignored (daqTimestamps=master) — samples carry the receive time."
                            : "slave timestamps off — samples carry the receive time."));
            }

            if (stimState != null)
            {
                Logger?.Log(LogLevel.Info,
                    $"XCP: STIM started — {stimPlans.Count} list(s) on event channel(s) " +
                    $"{string.Join(", ", stimPlans.Select(p => p.EventChannel))}, {stimState.VariablesInStim.Count} variable(s)" +
                    (includeStimTimestamp ? ", timestamped (TIMESTAMP_FIXED slave)." : "."));
            }

            return (daqState, stimState);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
        {
            session.DaqDtoReceived = null;
            Logger?.Log(LogLevel.Warn,
                $"XCP: DAQ/STIM setup failed ({e.Message}); falling back to polling and direct writes for all variables.");
            return (null, null);
        }
    }

    /// <summary>
    /// Builds the STIM runtime state once the configuration is started. Validates the spec's
    /// STIM PID window for absolute-PID slaves (FIRST_PID + ODT count must stay ≤ 0xBF) — a
    /// violation aborts the whole setup, because the packets could not be identified.
    /// </summary>
    private StimSessionState? BuildStimState(XcpMaster session, XcpDaqProcessorInfo processor,
        XcpDaqResolutionInfo? resolution, List<XcpDaqListPlan> stimPlans, DaqTarget[][][] stimTargets,
        int firstStimListNumber, byte[] firstPids, int stimTimestampSize)
    {
        if (stimPlans.Count == 0)
        {
            return null;
        }

        var lists = new StimList[stimPlans.Count];
        for (var i = 0; i < stimPlans.Count; i++)
        {
            var listNumber = firstStimListNumber + i;
            var firstPid = firstPids[listNumber];
            if (processor.IdentificationType == XcpDaqIdentificationType.AbsolutePid &&
                firstPid + stimPlans[i].Odts.Count - 1 > 0xBF)
            {
                throw new XcpProtocolException(
                    $"The slave assigned FIRST_PID 0x{firstPid:X2} to STIM list {listNumber} with {stimPlans[i].Odts.Count} ODT(s), " +
                    "which leaves the STIM PID range 0x00..0xBF.");
            }

            // The interval was validated in CollectStimCandidates (periodic event, positive period).
            var periodicEvent = (PeriodicVarEvent)stimTargets[i][0][0].Spec.VariableEvent!;
            lists[i] = new StimList
            {
                DaqListNumber = (ushort)listNumber,
                Odts = stimTargets[i],
                IntervalMs = (long)periodicEvent.Period * (int)periodicEvent.Unit,
                FirstPid = firstPid,
                NextDueMs = Environment.TickCount64
            };
        }

        var variablesInStim = stimTargets
            .SelectMany(list => list.SelectMany(odt => odt))
            .Select(IProtocolVariable (target) => target.ProtocolVariable)
            .ToHashSet();

        return new StimSessionState
        {
            Encoder = new XcpStimEncoder(processor.IdentificationType, session.Codec.IsBigEndian, stimTimestampSize),
            Codec = session.Codec,
            Lists = lists,
            VariablesInStim = variablesInStim,
            TimestampTickSeconds = stimTimestampSize > 0 ? resolution!.TimestampTickSeconds : 0
        };
    }

    /// <summary>Read variables bound to a DAQ event, grouped by ECU event channel (stable order).</summary>
    private SortedDictionary<ushort, List<DaqTarget>> CollectDaqCandidates()
    {
        var channels = new SortedDictionary<ushort, List<DaqTarget>>();

        foreach (var protocolVariable in Variables)
        {
            if (protocolVariable is not XcpProtocolVariable xcpVariable ||
                !xcpVariable.IsCommunicated ||
                xcpVariable.ProtocolVariableSpecification is not XcpVariableSpecification spec ||
                spec.DaqEventChannel is not { } channel ||
                spec.IsStimEvent ||
                spec.IsOnRequestEvent ||
                spec.Direction == CommDirection.Write)
            {
                continue;
            }

            if (xcpVariable.Variable is not ScalarVariable scalarVariable)
            {
                Logger?.Log(LogLevel.Warn, $"XCP: variable '{xcpVariable.Variable.Name}' is not scalar; not acquired via DAQ.");
                continue;
            }

            if (!channels.TryGetValue(channel, out var targets))
            {
                channels[channel] = targets = [];
            }

            targets.Add(new DaqTarget(xcpVariable, scalarVariable, spec));
        }

        return channels;
    }

    /// <summary>Write variables bound to a STIM event (S2), grouped by ECU event channel. The
    /// bound event must be periodic — its period is the STIM transmission interval (S3).</summary>
    private SortedDictionary<ushort, List<DaqTarget>> CollectStimCandidates()
    {
        var channels = new SortedDictionary<ushort, List<DaqTarget>>();

        foreach (var protocolVariable in Variables)
        {
            if (protocolVariable is not XcpProtocolVariable xcpVariable ||
                !xcpVariable.IsCommunicated ||
                xcpVariable.ProtocolVariableSpecification is not XcpVariableSpecification spec ||
                spec.DaqEventChannel is not { } channel ||
                !spec.IsStimEvent)
            {
                continue;
            }

            if (xcpVariable.Variable is not ScalarVariable scalarVariable)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: variable '{xcpVariable.Variable.Name}' is not scalar; not stimulated via STIM.");
                continue;
            }

            if (spec.VariableEvent is not PeriodicVarEvent periodicEvent ||
                (long)periodicEvent.Period * (int)periodicEvent.Unit <= 0)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: variable '{scalarVariable.Name}' has no positive period on its STIM event " +
                    $"('{spec.VariableEvent?.Name}'); it falls back to direct writes on change.");
                continue;
            }

            if (!channels.TryGetValue(channel, out var targets))
            {
                channels[channel] = targets = [];
            }

            targets.Add(new DaqTarget(xcpVariable, scalarVariable, spec));
        }

        return channels;
    }

    /// <summary>
    /// D3/S2: every used channel is validated via GET_DAQ_EVENT_INFO — the ECU name is logged and
    /// the event's configured period is compared with the ECU's nominal TIMECYCLE (mismatch =
    /// warning with both values). Channels the slave rejects or that lack the required direction
    /// are removed and their variables fall back (DAQ → polling, STIM → direct writes on change).
    /// Slaves without GET_DAQ_EVENT_INFO skip validation with a notice.
    /// </summary>
    private async Task ValidateChannelsAsync(XcpMaster session, XcpDaqProcessorInfo processor,
        SortedDictionary<ushort, List<DaqTarget>> channels, bool isStim, CancellationToken ct)
    {
        var direction = isStim ? "STIM" : "DAQ";
        var fallback = isStim ? "fall back to direct writes on change" : "fall back to polling";

        foreach (var channel in channels.Keys.ToList())
        {
            if (processor.MaxEventChannel > 0 && channel >= processor.MaxEventChannel)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: {direction} event channel {channel} is out of the slave's range (0..{processor.MaxEventChannel - 1}); " +
                    $"its variables ({DescribeTargets(channels[channel])}) {fallback}.");
                channels.Remove(channel);
                continue;
            }

            XcpDaqEventInfo info;
            string ecuName;
            try
            {
                (info, ecuName) = await session.GetDaqEventInfoAsync(channel, ct);
            }
            catch (XcpErrorException e) when (e.ErrorCode == XcpErrorCode.CmdUnknown)
            {
                Logger?.Log(LogLevel.Debug,
                    $"XCP: GET_DAQ_EVENT_INFO not implemented by the slave; {direction} event channels are used unvalidated.");
                return;
            }
            catch (XcpErrorException e) when (e.ErrorCode == XcpErrorCode.OutOfRange)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: the slave rejected {direction} event channel {channel} (ERR_OUT_OF_RANGE); " +
                    $"its variables ({DescribeTargets(channels[channel])}) {fallback}.");
                channels.Remove(channel);
                continue;
            }

            if (isStim ? !info.SupportsStim : !info.SupportsDaq)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: event channel {channel} ('{ecuName}') does not support the {direction} direction; " +
                    $"its variables ({DescribeTargets(channels[channel])}) {fallback}.");
                channels.Remove(channel);
                continue;
            }

            Logger?.Log(LogLevel.Debug,
                $"XCP: {direction} event channel {channel} = '{ecuName}'" +
                (info.CycleTimeMs is { } cycle ? $", nominal cycle {cycle:0.###} ms." : ", sporadic (no nominal cycle)."));

            WarnOnPeriodMismatch(channels[channel], channel, ecuName, info);
        }
    }

    private void WarnOnPeriodMismatch(List<DaqTarget> targets, ushort channel, string ecuName, XcpDaqEventInfo info)
    {
        if (info.CycleTimeMs is not { } cycleMs)
        {
            return;
        }

        foreach (var target in targets)
        {
            if (target.Spec.VariableEvent is not PeriodicVarEvent periodicEvent)
            {
                continue;
            }

            var configuredMs = (double)periodicEvent.Period * (int)periodicEvent.Unit;
            if (configuredMs > 0 && Math.Abs(configuredMs - cycleMs) > cycleMs * 0.5)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: event '{periodicEvent.Name}' is configured with {configuredMs:0.###} ms but ECU channel {channel} " +
                    $"('{ecuName}') reports a nominal cycle of {cycleMs:0.###} ms — the ECU timing wins; " +
                    "adjust the event period if the difference is unintended.");
            }
        }
    }

    private static string DescribeTargets(List<DaqTarget> targets)
    {
        return string.Join(", ", targets.Select(t => $"'{t.Scalar.Name}'"));
    }

    /// <summary>
    /// Packs each channel's entries into ODTs via <see cref="XcpOdtPacker"/> (first-fit over all
    /// ODTs of the list, configuration order). ODT capacity is MAX_DTO minus the identification
    /// field (and the timestamp in ODT 0); entry size is capped by MAX_ODT_ENTRY_SIZE_DAQ.
    /// Oversized variables are skipped with a warning. When nothing fits beside the timestamp in
    /// ODT 0 (classic CAN: 8 B DTO, 2 B header, 4 B timestamp) ODT 0 carries a filler entry whose
    /// target is null — the consumer discards it.
    /// </summary>
    private (List<XcpDaqListPlan> Plans, DaqTarget?[][][] Targets) PackDaqLists(
        SortedDictionary<ushort, List<DaqTarget>> channels, XcpConnectResponse connectInfo,
        XcpDaqProcessorInfo processor, XcpDaqResolutionInfo? resolution, int timestampSize)
    {
        var maxEntrySize = resolution is { MaxOdtEntrySizeDaq: > 0 } ? resolution.MaxOdtEntrySizeDaq : byte.MaxValue;
        var headerSize = processor.DtoHeaderSize;

        var plans = new List<XcpDaqListPlan>();
        var targets = new List<DaqTarget?[][]>();

        foreach (var (channel, channelTargets) in channels)
        {
            var items = channelTargets
                .Select(t => new XcpOdtPacker.Item((byte)t.Spec.Size, t.Spec.AddressExtension, t.Spec.Address))
                .ToList();
            var packed = XcpOdtPacker.Pack(items, connectInfo.MaxDto, headerSize, timestampSize, maxEntrySize,
                maxOdts: 0xFC, allowFiller: true);

            foreach (var (itemIndex, reason) in packed.Rejected)
            {
                var target = channelTargets[itemIndex];
                Logger?.Log(LogLevel.Warn, reason == XcpOdtPacker.RejectReason.TooLarge
                    ? $"XCP: variable '{target.Scalar.Name}' ({target.Spec.Size} B) does not fit a DAQ packet " +
                      $"(MAX_ODT_ENTRY_SIZE {maxEntrySize}, ODT payload {connectInfo.MaxDto - headerSize} B); it falls back to polling."
                    : $"XCP: variable '{target.Scalar.Name}' does not fit the DAQ list of event channel {channel} " +
                      "(all 252 ODTs are used); it falls back to polling.");
            }

            if (packed.Odt0Unfillable)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: DAQ list of event channel {channel}: the {timestampSize}-byte slave timestamp leaves no room " +
                    $"for an entry in ODT 0 (MAX_DTO {connectInfo.MaxDto}); its variables fall back to polling. " +
                    "Consider daqTimestamps=master.");
                continue;
            }

            if (packed.Odts.Count == 0)
            {
                continue;
            }

            if (packed.Odt0HasFiller)
            {
                Logger?.Log(LogLevel.Debug,
                    $"XCP: DAQ list of event channel {channel}: no variable fits beside the timestamp in ODT 0; " +
                    "ODT 0 carries a filler entry that is ignored on receive.");
            }

            plans.Add(new XcpDaqListPlan(channel,
                packed.Odts.Select(IReadOnlyList<XcpDaqEntryPlan> (o) => o.Select(slot => slot.Entry).ToList()).ToList()));
            targets.Add(packed.Odts
                .Select(o => o.Select(slot => slot.IsFiller ? null : channelTargets[slot.ItemIndex]).ToArray())
                .ToArray());
        }

        return (plans, targets.ToArray());
    }

    /// <summary>
    /// Packs each STIM channel's entries into ODTs (the mirror of <see cref="PackDaqLists"/> with
    /// the STIM-side limits): ODT capacity is MAX_DTO minus the identification field (and the
    /// timestamp in ODT 0 of timestamped lists); entry size is capped by MAX_ODT_ENTRY_SIZE_STIM,
    /// and address and size must be multiples of GRANULARITY_ODT_ENTRY_SIZE_STIM. No filler is
    /// ever written to the ECU: a timestamped STIM list whose ODT 0 stays empty is not built.
    /// Non-conforming variables fall back to direct writes (S5).
    /// </summary>
    private (List<XcpDaqListPlan> Plans, DaqTarget[][][] Targets) PackStimLists(
        SortedDictionary<ushort, List<DaqTarget>> channels, XcpConnectResponse connectInfo,
        XcpDaqProcessorInfo processor, XcpDaqResolutionInfo? resolution, int timestampSize)
    {
        var maxEntrySize = resolution is { MaxOdtEntrySizeStim: > 0 } ? resolution.MaxOdtEntrySizeStim : byte.MaxValue;
        var granularity = resolution is { GranularityOdtEntrySizeStim: > 0 } ? resolution.GranularityOdtEntrySizeStim : 1;
        var headerSize = processor.DtoHeaderSize;

        var plans = new List<XcpDaqListPlan>();
        var targets = new List<DaqTarget[][]>();

        foreach (var (channel, channelTargets) in channels)
        {
            var candidates = new List<DaqTarget>();
            foreach (var target in channelTargets)
            {
                var size = target.Spec.Size;
                if (size % granularity != 0 || target.Spec.Address % granularity != 0)
                {
                    Logger?.Log(LogLevel.Warn,
                        $"XCP: variable '{target.Scalar.Name}' ({size} B at 0x{target.Spec.Address:X}) violates the slave's " +
                        $"STIM granularity ({granularity} B); it falls back to direct writes on change.");
                    continue;
                }

                candidates.Add(target);
            }

            var items = candidates
                .Select(t => new XcpOdtPacker.Item((byte)t.Spec.Size, t.Spec.AddressExtension, t.Spec.Address))
                .ToList();
            // The STIM PID window is 0x00..0xBF (narrower than DAQ), so a list is limited to
            // 192 ODTs even before the slave assigns FIRST_PIDs.
            var packed = XcpOdtPacker.Pack(items, connectInfo.MaxDto, headerSize, timestampSize, maxEntrySize,
                maxOdts: 0xC0, allowFiller: false);

            foreach (var (itemIndex, reason) in packed.Rejected)
            {
                var target = candidates[itemIndex];
                Logger?.Log(LogLevel.Warn, reason == XcpOdtPacker.RejectReason.TooLarge
                    ? $"XCP: variable '{target.Scalar.Name}' ({target.Spec.Size} B) does not fit a STIM packet " +
                      $"(MAX_ODT_ENTRY_SIZE_STIM {maxEntrySize}, ODT payload {connectInfo.MaxDto - headerSize} B); it falls back to direct writes on change."
                    : $"XCP: variable '{target.Scalar.Name}' does not fit the STIM list of event channel {channel} " +
                      "(all 192 ODTs are used); it falls back to direct writes on change.");
            }

            if (packed.Odt0Unfillable)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: STIM list of event channel {channel}: the {timestampSize}-byte timestamp leaves no room " +
                    $"for an entry in ODT 0 (MAX_DTO {connectInfo.MaxDto}); its variables fall back to direct writes on change.");
                continue;
            }

            if (packed.Odts.Count == 0)
            {
                continue;
            }

            plans.Add(new XcpDaqListPlan(channel,
                packed.Odts.Select(IReadOnlyList<XcpDaqEntryPlan> (o) => o.Select(slot => slot.Entry).ToList()).ToList(),
                IsStim: true));
            targets.Add(packed.Odts
                .Select(o => o.Select(slot => candidates[slot.ItemIndex]).ToArray())
                .ToArray());
        }

        return (plans, targets.ToArray());
    }

    /// <summary>
    /// The STIM transmission loop (S3): every list's current variable values are encoded and
    /// sent as its complete set of ODT DTOs back-to-back, at the period of the bound event.
    /// The first round goes out immediately after the configuration starts, so the slave has
    /// data before its first event. Transport failures are counted like poll failures — too
    /// many in a row kill the session (reconnect path).
    /// </summary>
    private async Task StimLoopAsync(StimSessionState stim, CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested && !exitRequested)
        {
            var now = Environment.TickCount64;
            var anyDue = false;

            foreach (var list in stim.Lists)
            {
                if (list.NextDueMs > now)
                {
                    continue;
                }

                anyDue = true;
                try
                {
                    await SendStimListAsync(stim, list, ct);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
                {
                    consecutiveFailures++;
                    Logger?.Log(LogLevel.Warn, $"XCP: STIM transmission for list {list.DaqListNumber} failed: {e.Message}");
                    SetState(CommunicationState.Running, $"STIM transmission failed: {e.Message}");

                    if (consecutiveFailures >= MaxConsecutiveBusFailures)
                    {
                        throw new XcpProtocolException(
                            $"{consecutiveFailures} consecutive STIM transmissions failed (last: {e.Message}).");
                    }
                }

                // Re-anchor to now: after a stall the missed cycles are skipped instead of bursting.
                list.NextDueMs = Environment.TickCount64 + list.IntervalMs;
            }

            if (!anyDue)
            {
                var nextDueIn = stim.Lists.Min(l => l.NextDueMs) - Environment.TickCount64;
                var delay = (int)Math.Clamp(nextDueIn, 1, SchedulerIdleMs);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Encodes and sends one complete STIM list (all ODTs back-to-back). STIM lists
    /// are normally untimestamped; only for a legacy TIMESTAMP_FIXED slave does ODT 0 carry a
    /// timestamp — the master's free-running time since session start, scaled to slave
    /// ticks.</summary>
    private async Task SendStimListAsync(StimSessionState stim, StimList list, CancellationToken ct)
    {
        var session = master ?? throw new XcpProtocolException("Not connected to the XCP slave.");

        for (var odt = 0; odt < list.Odts.Length; odt++)
        {
            var odtTargets = list.Odts[odt];
            var payload = new byte[odtTargets.Sum(t => t.Spec.Size)];
            var offset = 0;
            foreach (var target in odtTargets)
            {
                var bytes = stim.Codec.EncodeValue(target.Scalar.GetValue(), target.Spec.DataType);
                bytes.CopyTo(payload, offset);
                offset += bytes.Length;
            }

            uint? timestamp = odt == 0 && stim.TimestampTickSeconds > 0
                ? (uint)((Environment.TickCount64 - stim.AnchorMs) / 1000.0 / stim.TimestampTickSeconds)
                : null;

            var dto = stim.Encoder.BuildDto(list.DaqListNumber, (byte)odt, list.FirstPid, timestamp, payload);
            await session.SendStimDtoAsync(dto, ct);
        }
    }

    /// <summary>
    /// Consumes queued DAQ packets and pushes decoded values into their variables. With slave
    /// timestamps (D5 stages 4b/4c) the sample time comes from the ECU clock mapped onto host
    /// UTC; sessions without timestamps stamp with the PC receive time (stage 4a behaviour).
    /// </summary>
    private async Task ConsumeDaqDtosAsync(DaqSessionState daq, CancellationToken ct)
    {
        var decoded = new List<XcpDaqDecoder.DecodedEntry>(capacity: 16);
        var decodeFailures = 0L;

        await foreach (var dto in daq.Dtos.Reader.ReadAllAsync(ct))
        {
            if (!daq.Decoder.TryDecode(dto.Packet, decoded, out var timestampRaw) || decoded.Count == 0)
            {
                continue;
            }

            var sampleUtc = daq.TimestampMapper?.Map(decoded[0].ListIndex, timestampRaw, dto.ReceivedUtc)
                            ?? dto.ReceivedUtc;

            foreach (var entry in decoded)
            {
                var target = daq.Targets[entry.ListIndex][entry.OdtIndex][entry.EntryIndex];
                if (target == null)
                {
                    continue; // ODT 0 filler (timestamp carrier), see XcpOdtPacker
                }

                object value;
                try
                {
                    value = daq.Codec.DecodeValue(dto.Packet.AsSpan(entry.DataOffset, entry.Size), target.Spec.DataType);
                }
                catch (XcpProtocolException e)
                {
                    decodeFailures++;
                    if (decodeFailures == 1 || decodeFailures % 1000 == 0)
                    {
                        Logger?.Log(LogLevel.Warn,
                            $"XCP: DAQ value for '{target.Scalar.Name}' could not be decoded ({e.Message}); {decodeFailures} failures so far.");
                    }

                    continue;
                }

                await ApplyBusValueAsync(target.ProtocolVariable, target.Scalar, value, sampleUtc);
            }
        }
    }

    #endregion

    #region Operator writes

    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable) &&
               protocolVariable is XcpProtocolVariable { Variable: ScalarVariable or MatrixVariable } &&
               protocolVariable.ProtocolVariableSpecification is XcpVariableSpecification
               {
                   Direction: CommDirection.Write or CommDirection.ReadWrite
               };
    }

    public async Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        if (protocolVariable is not XcpProtocolVariable xcpVariable ||
            xcpVariable.IsUpdatingFromBus || // echo of our own poll update
            xcpVariable.ProtocolVariableSpecification is not XcpVariableSpecification spec)
        {
            return;
        }

        if (xcpVariable.Variable is MatrixVariable matrixVariable)
        {
            await WriteMatrixElementsAsync(matrixVariable, spec, ct);
            return;
        }

        if (xcpVariable.Variable is not ScalarVariable scalarVariable)
        {
            return;
        }

        // S3/S5: while the STIM loop streams this variable, its current value goes out with the
        // next cycle automatically; only outside an active STIM session (fallback) is the value
        // written directly.
        if (spec.IsStimEvent && stimServedVariables?.Contains(protocolVariable) == true)
        {
            return;
        }

        var session = master;
        if (session is not { IsConnected: true })
        {
            Logger?.Log(LogLevel.Warn, $"XCP: write of '{scalarVariable.Name}' skipped — not connected.");
            return;
        }

        if (!session.WritesAllowed)
        {
            var message = $"Write of '{scalarVariable.Name}' rejected: calibration requires seed & key or is unavailable.";
            Logger?.Log(LogLevel.Warn, $"XCP: {message}");
            SetState(CommunicationState.Running, message);
            return;
        }

        try
        {
            // The stored value is always raw — engineering input is inverted to raw above the
            // protocol layer (ScalarVariable.TrySetEngValue) — so DOWNLOAD sends it as-is,
            // the same way the STIM stream does.
            var bytes = session.Codec.EncodeValue(scalarVariable.GetValue(), spec.DataType);
            await session.WriteMemoryAsync(spec.AddressExtension, spec.Address, bytes, ct);
            Logger?.Log(LogLevel.Info,
                $"XCP: wrote '{scalarVariable.Name}' ({bytes.Length} B at 0x{spec.Address:X}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
        {
            Logger?.Log(LogLevel.Warn, $"XCP: write of '{scalarVariable.Name}' failed: {e.Message}");
            SetState(CommunicationState.Running, $"Write of '{scalarVariable.Name}' failed: {e.Message}");
        }
    }

    /// <summary>
    /// Matrix write: drains the variable's queue of edited cells (the host already stored each
    /// value in the raw buffer and queued its byte window), merges windows that are contiguous in
    /// memory into one transfer (Radek 2026-09-25) and writes every window as SET_MTA + DOWNLOAD
    /// block(s) at the cell's address. A failed window is logged and reported as protocol state,
    /// never thrown — the remaining windows are still written. Nothing is echoed to the controls:
    /// the written values show up with the next poll / Read (truth is what comes from the device).
    /// </summary>
    private async Task WriteMatrixElementsAsync(MatrixVariable matrix, XcpVariableSpecification spec, CancellationToken ct)
    {
        var windows = DequeueWriteWindows(matrix);
        if (windows.Count == 0)
        {
            return;
        }

        var session = master;
        if (session is not { IsConnected: true })
        {
            Logger?.Log(LogLevel.Warn, $"XCP: write of '{matrix.Name}' skipped — not connected ({windows.Count} window(s) dropped).");
            return;
        }

        if (!session.WritesAllowed)
        {
            var message = $"Write of '{matrix.Name}' rejected: calibration requires seed & key or is unavailable.";
            Logger?.Log(LogLevel.Warn, $"XCP: {message}");
            SetState(CommunicationState.Running, message);
            return;
        }

        foreach (var (offset, length) in windows)
        {
            try
            {
                var bytes = matrix.RawData.AsSpan(offset, length).ToArray();
                await session.WriteMemoryAsync(spec.AddressExtension, spec.Address + (uint)offset, bytes, ct);
                Logger?.Log(LogLevel.Info,
                    $"XCP: wrote {length} B of '{matrix.Name}' at 0x{spec.Address + (uint)offset:X} (byte offset {offset}).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
            {
                Logger?.Log(LogLevel.Warn, $"XCP: write of '{matrix.Name}' (byte offset {offset}, {length} B) failed: {e.Message}");
                SetState(CommunicationState.Running, $"Write of '{matrix.Name}' failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Drains the pending cell writes into (offset, length) windows sorted by offset, merging
    /// windows that touch or overlap. The queued bytes are copied back into the raw buffer first —
    /// a poll that landed in between would otherwise have overwritten the edited cell. Requests
    /// that no longer fit the layout are dropped with a warning.
    /// </summary>
    private List<(int Offset, int Length)> DequeueWriteWindows(MatrixVariable matrix)
    {
        var requests = new List<(int Offset, int Length)>();
        while (matrix.TryDequeuePendingWrite(out var request))
        {
            if (request.ByteOffset < 0 || request.Bytes.Length == 0 ||
                request.ByteOffset + request.Bytes.Length > matrix.Size)
            {
                Logger?.Log(LogLevel.Warn,
                    $"XCP: pending write of '{matrix.Name}' at byte {request.ByteOffset} no longer fits the matrix layout; skipped.");
                continue;
            }

            request.Bytes.CopyTo(matrix.RawData.AsSpan(request.ByteOffset));
            requests.Add((request.ByteOffset, request.Bytes.Length));
        }

        requests.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var windows = new List<(int Offset, int Length)>();
        foreach (var request in requests)
        {
            if (windows.Count > 0)
            {
                var last = windows[^1];
                if (request.Offset <= last.Offset + last.Length)
                {
                    var end = Math.Max(last.Offset + last.Length, request.Offset + request.Length);
                    windows[^1] = (last.Offset, end - last.Offset);
                    continue;
                }
            }

            windows.Add(request);
        }

        return windows;
    }

    #endregion

    #region On-request reads (IProtocolVariableReadProtocol)

    /// <summary>Readable on request = a read/readWrite scalar or matrix bound to an On Request event.</summary>
    public bool CanReadVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable) &&
               protocolVariable.IsCommunicated &&
               protocolVariable is XcpProtocolVariable { Variable: ScalarVariable or MatrixVariable } &&
               protocolVariable.ProtocolVariableSpecification is XcpVariableSpecification
               {
                   Direction: CommDirection.Read or CommDirection.ReadWrite,
                   IsOnRequestEvent: true
               };
    }

    /// <summary>
    /// One SHORT_UPLOAD (or chained UPLOAD) of the variable, exactly what a poll cycle does — the
    /// master's request lock serialises it with the running polling/DAQ session. The value lands
    /// in the variable through the same path as a polled value (timestamp + notification with the
    /// bus-update flag, so it does not echo back as a DOWNLOAD). Failures are logged and rethrown.
    /// </summary>
    public async Task ReadVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        if (!CanReadVariable(protocolVariable) ||
            protocolVariable is not XcpProtocolVariable xcpVariable ||
            xcpVariable.ProtocolVariableSpecification is not XcpVariableSpecification spec)
        {
            throw new InvalidOperationException(
                $"XCP: variable '{protocolVariable.Variable?.Name}' is not readable on request.");
        }

        var name = xcpVariable.Variable.Name;
        var session = master;
        if (session is not { IsConnected: true })
        {
            var message = $"Read of '{name}' skipped — not connected.";
            Logger?.Log(LogLevel.Warn, $"XCP: {message}");
            throw new XcpProtocolException(message);
        }

        try
        {
            var bytes = await session.ReadMemoryAsync(spec.AddressExtension, spec.Address, spec.Size, ct);
            switch (xcpVariable.Variable)
            {
                case MatrixVariable matrixVariable:
                    await ApplyBusBlockAsync(xcpVariable, matrixVariable, bytes, DateTime.UtcNow);
                    break;
                case ScalarVariable scalarVariable:
                    await ApplyBusValueAsync(xcpVariable, scalarVariable, session.Codec.DecodeValue(bytes, spec.DataType), DateTime.UtcNow);
                    break;
            }

            Logger?.Log(LogLevel.Debug, $"XCP: read '{name}' on request ({bytes.Length} B at 0x{spec.Address:X}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
        {
            Logger?.Log(LogLevel.Warn, $"XCP: read of '{name}' failed: {e.Message}");
            SetState(CommunicationState.Running, $"Read of '{name}' failed: {e.Message}");
            throw;
        }
    }

    #endregion

    #region Process received data

    protected override void ProcessReceivedData(IEnumerable<TFrame> data)
    {
        AddReceivedDataToQueueAsync(data).GetAwaiter().GetResult();
    }

    protected override Task ProcessReceivedDataAsync(IEnumerable<TFrame> data, CancellationToken ct = default)
    {
        return AddReceivedDataToQueueAsync(data, ct);
    }

    #endregion

    #region Encode & Decode

    protected override IEnumerable<TFrame> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        throw new NotSupportedException("XCP uses a request/response engine; bulk Encode is not supported.");
    }

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<TFrame> data)
    {
        throw new NotSupportedException("XCP uses a request/response engine; bulk Decode is not supported.");
    }

    #endregion

    // LICENSE-SEAL COPY v1 — sync z _LicenseGuard/LicenseSealTemplate.cs
    // Nezavisly licencni check (necte licenseService). true=licencovano/nejasne (fail-open),
    // false=jiste nelicencovano. Reakce (nenastartovat) je v StartAsym.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool SessionSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open
        }
    }
}
