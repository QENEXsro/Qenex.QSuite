using System.Reflection;
using System.Threading.Channels;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Protocols.VirtualDataProtocol;

/// <summary>
/// Protocol for virtual (script-computed) variables. It has no transport and no timing of its
/// own: every successful script write of one of its variables is enqueued by the module
/// (IScriptWriteAwareProtocol) and the consumer loop publishes the notification, so the
/// variables show up in controls, graphs and data logs like any other communicated signal.
/// One write = one published sample, stamped with the time of the write (during replay with
/// the time of the last replayed sample, supplied by the module).
/// </summary>
public class VirtualDataProtocol : ProtocolBase<VirtualWrite>, IScriptWriteAwareProtocol, IProtocolVariableWriteProtocol
{
    // Script writes waiting for publishing; the channel keeps the consumer loop cancelable.
    private Channel<VirtualWrite>? pendingWrites;
    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public VirtualDataProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "VirtualDataProtocol",
            Label = "Virtual Variables",
            Description = "Publishes script-computed values as signal samples. Use with the Virtual Variables Host driver.",
            CreatedOn = new DateTime(2026, 7, 28),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #region Configuration

    // The protocol has no protocol-level settings; everything the variable needs is its identity.
    // Any key in the project is reported as unknown through the shared parser.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Virtual Variables protocol");
    }

    // Nothing to configure per variable: samples are born from script writes routed by identity.
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return string.Empty;
    }

    #endregion

    #region Protocol variables

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        return new ProtocolVariable
        {
            Variable = variable,
            IsCommunicated = isCommunicated,
            ProtocolVariableSpecification = VirtualDataProtocolVariableSpecification.Create(commParams)
        };
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return CreateProtocolVariable(variable, string.Empty, true);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents,
        string commParams, bool isCommunicated)
    {
        // Samples are born from script writes, so variable events are not used.
        return CreateProtocolVariable(variable, commParams, isCommunicated);
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
        pendingWrites = Channel.CreateUnbounded<VirtualWrite>();

        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: the loop must not inherit the caller's (UI) SynchronizationContext —
        // a blocked dispatcher (window drag, busy UI) would stall the whole session.
        // Capture the token now: a racing StopAsync may null runCts before the loop starts.
        var runToken = runCts.Token;
        runTask = Task.Run(() => RunLoopAsync(runToken));
        SetState(CommunicationState.Running);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        exitRequested = true;
        pendingWrites?.Writer.TryComplete();

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
                Logger?.Log(LogLevel.Warn, "Virtual data protocol did not stop before timeout.");
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

    #region Script writes

    public void OnVariableWrittenByScript(IVariableBase variable)
    {
        OnVariableWrittenByScript(variable, DateTime.UtcNow);
    }

    // The module supplies the sample time: the time of the write in a live session, the time
    // of the last replayed sample during replay.
    public void OnVariableWrittenByScript(IVariableBase variable, DateTime timestampUtc)
    {
        if (State != CommunicationState.Running)
        {
            return;
        }

        pendingWrites?.Writer.TryWrite(new VirtualWrite(variable, timestampUtc));
    }

    #endregion

    #region IProtocolVariableWriteProtocol

    /// <summary>
    /// An operator may write a virtual variable from controls when its direction is write or
    /// readWrite. Script writes do not depend on the direction.
    /// </summary>
    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable)
               && protocolVariable.ProtocolVariableSpecification is VirtualDataProtocolVariableSpecification
               {
                   Direction: CommDirection.Write or CommDirection.ReadWrite
               };
    }

    // Nothing to transfer: there is no device, the written value already lives in the variable.
    // The protocol IS the device here, so it publishes the written value like a script write
    // (own loop, fresh timestamp) - the controls show only what the "device" returns.
    public Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        if (State == CommunicationState.Running && protocolVariable.Variable is { } variable)
        {
            pendingWrites?.Writer.TryWrite(new VirtualWrite(variable, DateTime.UtcNow));
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Received data

    public override Task AddReceivedDataToQueueAsync(IEnumerable<VirtualWrite> data, CancellationToken ct = default)
    {
        foreach (var write in data)
        {
            pendingWrites?.Writer.TryWrite(write);
        }

        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var write in pendingWrites!.Reader.ReadAllAsync(ct))
            {
                if (exitRequested)
                {
                    break;
                }

                await ProcessReceivedDataAsync([write], ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Virtual data protocol loop failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
        }
    }

    protected override void ProcessReceivedData(IEnumerable<VirtualWrite> data)
    {
        foreach (var protocolVariable in Decode(data))
        {
            protocolVariable.NotifyValueChanged();
        }
    }

    protected override async Task ProcessReceivedDataAsync(IEnumerable<VirtualWrite> data, CancellationToken ct = default)
    {
        var notifyTasks = Decode(data).Select(protocolVariable => protocolVariable.NotifyValueChangedAsync());
        await Task.WhenAll(notifyTasks);
    }

    #endregion

    #region Encoding and decoding

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<VirtualWrite> data)
    {
        // The value already sits in the variable (the script wrote it); publishing only stamps
        // the write time and hands the protocol variable to the notification pipeline.
        var updated = new List<IProtocolVariable>();

        foreach (var write in data)
        {
            foreach (var protocolVariable in Variables)
            {
                if (!protocolVariable.IsCommunicated || protocolVariable.Variable.Id != write.Variable.Id)
                {
                    continue;
                }

                protocolVariable.Variable.Timestamp = write.TimestampUtc;
                updated.Add(protocolVariable);
            }
        }

        return updated;
    }

    protected override IEnumerable<VirtualWrite> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        throw new NotSupportedException("Virtual data protocol has no outgoing data.");
    }

    #endregion
}
