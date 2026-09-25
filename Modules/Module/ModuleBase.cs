using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Specifications.ComponentSpecification;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Scripting.Script;
using Qenex.QSuite.Scripting.ScriptingEngine;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Modules.Module;

public abstract class ModuleBase : IModuleBase
{
    private readonly List<(IProtocolVariable ProtocolVariable, Func<IProtocolVariable, Task> Handler)> onValueChangedScriptSubscriptions = [];
    private readonly List<(IProtocolVariable ProtocolVariable, Func<IProtocolVariable, Task> Handler)> protocolVariableSinkSubscriptions = [];
    private readonly List<(IProtocolVariable ProtocolVariable, Func<IProtocolVariable, Task> Handler)> protocolVariableCommandSubscriptions = [];
    private readonly List<(IProtocolVariable ProtocolVariable, Func<IProtocolVariable, CancellationToken, Task> Handler)> protocolVariableReadRequestSubscriptions = [];
    private readonly List<(IProtocolVariable ProtocolVariable, Func<IProtocolVariable, Task> Handler)> replaySampleTimeSubscriptions = [];

    // UTC ticks of the last replayed sample; 0 outside replay or before the first sample.
    private long replaySampleTimestampUtcTicks;

    #region Constructors

    protected ModuleBase(ScriptEngineSettings scriptEngineSettings, ILogger? logger = null)
    {
        Logger = logger;
        Variables = new List<IVariableBase>();
        Drivers = new List<IDriverBase>();
        Presentations = new List<IPresentation>();
        Conversions = new List<IValConversion>();
        VarEvents = new List<IVarEvent>();
        Scripting = new ScriptingContext(scriptEngineSettings, logger);
    }

    #endregion
    
    #region Properties

    // ReSharper disable once MemberCanBePrivate.Global
    public  ILogger? Logger { get; set; }
    
    public bool ExitRequested { get; set; } = false;
    
    public bool IsEnabled { get; set; } = false;
    
    public CommunicationState State { get; private set; } = CommunicationState.Stopped;
    public string? StateMessage { get; private set; }
    public event EventHandler<CommunicationStateChangedEventArgs>? StateChanged;
    
    public ISpecification Specification { get; protected init; }
    
    public IList<IVariableBase> Variables { get; set; }
    
    public IList<IDriverBase> Drivers { get; set;  }
    
    public IList<IPresentation> Presentations { get; set; }

    public IList<IValConversion> Conversions { get; set; }
    
    public IList<IVarEvent> VarEvents { get; set; }

    public ScriptingContext Scripting { get; set; }

    #endregion

    #region Variables
    public virtual void AddVariable(IVariableBase variable)
    {
        if (Variables.FirstOrDefault(v => v.Id == variable.Id) != null)
        {
            Logger?.Log(LogLevel.Warn, $"Variable with Id {variable.Id} already exists.");
            return;
        }
        
        var highestId = Variables.Count > 0 ? Variables.Max(x => x.Id) : 0;
        if (variable.Id == 0)
        {
            highestId++;
            variable.Id = highestId;
        }
        
        Variables.Add(variable);
    }

    public virtual void AddVariables(IList<IVariableBase> variables)
    {
        var highestId = Variables.Count > 0 ? Variables.Max(x => x.Id) : 0;
        foreach (var variable in variables)
        {
            if (Variables.FirstOrDefault(v => v.Id == variable.Id) != null)
            {
                Logger?.Log(LogLevel.Warn, $"Variable with Id {variable.Id} already exists.");
                continue;
            }
            
            if (variable.Id == 0)
            {
                highestId++;
                variable.Id = highestId;
            }
            
            Variables.Add(variable);
        }
    }
    
    public virtual void RemoveVariable(IVariableBase variable)
    {
        Variables.Remove(variable);
    }

    #endregion

    #region Presentations
    
    public virtual void AddPresentation(IPresentation presentation)
    {
        if (Presentations.FirstOrDefault(v => v.Name == presentation.Name) != null)
        {
            Logger?.Log(LogLevel.Warn, $"Presentation with Name {presentation.Name} already exists.");
            return;
        }
        
        Presentations.Add(presentation);
    }
    
    public virtual void AddPresentations(IList<IPresentation> presentations)
    {
        foreach (var presentation in presentations)
        {
            AddPresentation(presentation);
        }
    }
    
    public virtual void RemovePresentation(IPresentation presentation)
    {
        Presentations.Remove(presentation);
    }
    
    #endregion

    #region Conversions

    public virtual void AddConversion(IValConversion conversion)
    {
        if (Conversions.FirstOrDefault(v => v.Name == conversion.Name) != null)
        {
            Logger?.Log(LogLevel.Warn, $"Conversion with Name {conversion.Name} already exists.");
            return;
        }
        
        Conversions.Add(conversion);
    }

    public virtual void AddConversions(IList<IValConversion> conversions)
    {
        foreach (var conversion in conversions)
        {
            AddConversion(conversion);
        }
    }
    
    public virtual void RemoveConversion(IValConversion conversion)
    {
        Conversions.Remove(conversion);
    }

    #endregion
    
    #region VarEvents

    public void AddVarEvent(IVarEvent varEvent)
    {
        if (VarEvents.FirstOrDefault(v => v.Name == varEvent.Name) != null)
        {
            Logger?.Log(LogLevel.Warn, $"VarEvent with Name {varEvent.Name} already exists.");
            return;
        }
        VarEvents.Add(varEvent);
    }

    public void AddVarEvents(IList<IVarEvent> varEvents)
    {
        foreach (var varEvent in varEvents)
        {
            AddVarEvent(varEvent);
        }
    }

    public void RemoveVarEvent(IVarEvent varEvent)
    {
        VarEvents.Remove(varEvent);
    }

    #endregion

    #region Driver

    public virtual void AddDriver(IDriverBase driver)
    {
        if (Drivers.FirstOrDefault(v => v.Id == driver.Id) != null)
        {
            Logger?.Log(LogLevel.Warn, $"Driver with Id {driver.Id} already exists.");
            return;
        }
        
        var highestId = Drivers.Count > 0 ? Drivers.Max(x => x.Id) : 0;
        if (driver.Id == 0)
        {
            highestId++;
            driver.Id = highestId;
        }
        Drivers.Add(driver);
    }
    
    public virtual void AddDrivers(IEnumerable<IDriverBase> drivers)
    {
        var highestId = Drivers.Count > 0 ? Drivers.Max(x => x.Id) : 0;
        foreach (var driver in drivers)
        {
            if (Drivers.FirstOrDefault(v => v.Id == driver.Id) != null)
            {
                Logger?.Log(LogLevel.Warn, $"Driver with Id {driver.Id} already exists.");
                return;
            }
            
            if (driver.Id == 0)
            {
                highestId++;
                driver.Id = highestId;
            }
            Drivers.Add(driver);
        }
    }

    public virtual void RemoveDriver(IDriverBase driver)
    {
        Drivers.Remove(driver);
    }

    #endregion

    #region Scripting

    

    #endregion

    #region Module control

    public virtual async Task StartAsync(CancellationToken ct = default)
    {
        // Python is optional (see ScriptingContext.GetStartDecision): a project with a script
        // enabled for this mode cannot start without Python; scripts that are all disabled
        // only produce a warning; a project without scripts starts silently.
        var scriptingDecision = Scripting.GetStartDecision();
        if (scriptingDecision == ScriptingStartDecision.EnabledScriptsBlocked)
        {
            throw new InvalidOperationException(Scripting.BuildBlockedStartMessage());
        }

        SetState(CommunicationState.Starting);
        if (scriptingDecision == ScriptingStartDecision.PythonEnabled)
        {
            RegisterScriptWrittenVariableRouting();
            await Scripting.InitializeSharedScopeAsync(Variables);
            // Before the script triggers: handlers run in subscription order, so the replay
            // sample time is already updated when the triggered script writes a variable.
            SubscribeReplaySampleTime();
            SubscribeOnValueChangedScriptTriggers();
        }
        else if (scriptingDecision == ScriptingStartDecision.DisabledScriptsOnly)
        {
            Logger?.Log(LogLevel.Warn, Scripting.BuildDisabledScriptsWarning());
        }

        var sinkDrivers = Drivers
            .Where(driver => driver is IProtocolVariableSinkDriver)
            .ToList();
        var sourceDrivers = Drivers
            .Where(driver => driver is not IProtocolVariableSinkDriver)
            .ToList();

        await Task.WhenAll(sinkDrivers.Select(driver => driver.StartAsync(ct)));
        SubscribeProtocolVariableSinkDrivers(sourceDrivers, sinkDrivers);
        SubscribeProtocolVariableCommandDrivers(sourceDrivers, ct);
        await Task.WhenAll(sourceDrivers.Select(driver => driver.StartAsync(ct)));
        SetState(CommunicationState.Running);
    }

    public virtual async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        UnsubscribeOnValueChangedScriptTriggers();
        UnsubscribeReplaySampleTime();

        var sinkDrivers = Drivers
            .Where(driver => driver is IProtocolVariableSinkDriver)
            .ToList();
        var sourceDrivers = Drivers
            .Where(driver => driver is not IProtocolVariableSinkDriver)
            .ToList();

        Scripting.RequestStop();
        Scripting.VariableWrittenCallback = null;
        await Task.WhenAll(sourceDrivers.Select(driver => driver.StopAsync(ct)));
        UnsubscribeProtocolVariableCommandDrivers();
        UnsubscribeProtocolVariableSinkDrivers();
        await Task.WhenAll(sinkDrivers.Select(driver => driver.StopAsync(ct)));
        await Scripting.DisposeSharedScopeAsync(ct);
        if (Scripting.HasAbandonedExecutions)
        {
            Scripting = Scripting.CreateCleanContextForNextSession();
        }

        SetState(CommunicationState.Stopped);
    }

    public virtual void Dispose()
    {
        foreach (var driver in Drivers)
        {
            driver.Dispose();
        }
    }

    #endregion

    protected void SetState(CommunicationState state, string? message = null)
    {
        if (State == state && StateMessage == message)
        {
            return;
        }

        var previousState = State;
        State = state;
        StateMessage = message;
        StateChanged?.Invoke(this, new CommunicationStateChangedEventArgs(previousState, state, message));
    }

    private void SubscribeOnValueChangedScriptTriggers()
    {
        UnsubscribeOnValueChangedScriptTriggers();

        var scriptingContext = Scripting;
        foreach (var protocolVariable in Drivers
                     .SelectMany(driver => driver.Protocols)
                     .SelectMany(protocol => protocol.Variables)
                     .Where(protocolVariable => protocolVariable.IsCommunicated))
        {
            if (!scriptingContext.HasOnValueChangedScriptTriggers(protocolVariable.Variable.Id))
            {
                continue;
            }

            Func<IProtocolVariable, Task> handler = changedProtocolVariable =>
                scriptingContext.HandleVariableValueChangedAsync(changedProtocolVariable.Variable);

            protocolVariable.SubscribeAsyncValueChanged(handler);
            onValueChangedScriptSubscriptions.Add((protocolVariable, handler));
        }
    }

    private void UnsubscribeOnValueChangedScriptTriggers()
    {
        foreach (var subscription in onValueChangedScriptSubscriptions)
        {
            subscription.ProtocolVariable.UnsubscribeAsyncValueChanged(subscription.Handler);
        }

        onValueChangedScriptSubscriptions.Clear();
    }

    /// <summary>
    /// Routes successful script writes to protocols that publish script-computed variables
    /// (IScriptWriteAwareProtocol). The scripting engine only reports "variable X was written";
    /// which protocols care is the module's knowledge, resolved here into a lookup by id.
    /// </summary>
    private void RegisterScriptWrittenVariableRouting()
    {
        var protocolsByVariableId = new Dictionary<int, List<IScriptWriteAwareProtocol>>();
        foreach (var protocol in Drivers.SelectMany(driver => driver.Protocols))
        {
            if (protocol is not IScriptWriteAwareProtocol scriptWriteProtocol)
            {
                continue;
            }

            foreach (var protocolVariable in protocol.Variables.Where(protocolVariable => protocolVariable.IsCommunicated))
            {
                if (!protocolsByVariableId.TryGetValue(protocolVariable.Variable.Id, out var protocols))
                {
                    protocolsByVariableId[protocolVariable.Variable.Id] = protocols = [];
                }

                protocols.Add(scriptWriteProtocol);
            }
        }

        var isReplay = Scripting.IsReplayMode;
        Scripting.VariableWrittenCallback = variable =>
        {
            if (!protocolsByVariableId.TryGetValue(variable.Id, out var protocols))
            {
                return;
            }

            var timestampUtc = GetScriptWriteTimestampUtc(isReplay);
            foreach (var protocol in protocols)
            {
                protocol.OnVariableWrittenByScript(variable, timestampUtc);
            }
        };
    }

    // Live session: the time of the write. Replay: the time of the last replayed sample, so a
    // script-computed sample lines up with the recorded ones (any replay speed, after a seek).
    private DateTime GetScriptWriteTimestampUtc(bool isReplay)
    {
        if (isReplay)
        {
            var ticks = Interlocked.Read(ref replaySampleTimestampUtcTicks);
            if (ticks > 0)
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        return DateTime.UtcNow;
    }

    /// <summary>
    /// Replay only: tracks the time of the last replayed sample (variables of replay drivers).
    /// </summary>
    private void SubscribeReplaySampleTime()
    {
        UnsubscribeReplaySampleTime();
        if (!Scripting.IsReplayMode)
        {
            return;
        }

        foreach (var protocolVariable in Drivers
                     .Where(driver => driver is IReplayDriver)
                     .SelectMany(driver => driver.Protocols)
                     .SelectMany(protocol => protocol.Variables)
                     .Where(protocolVariable => protocolVariable.IsCommunicated))
        {
            Func<IProtocolVariable, Task> handler = changedProtocolVariable =>
            {
                Interlocked.Exchange(
                    ref replaySampleTimestampUtcTicks,
                    changedProtocolVariable.Variable.Timestamp.Ticks);
                return Task.CompletedTask;
            };

            protocolVariable.SubscribeAsyncValueChanged(handler);
            replaySampleTimeSubscriptions.Add((protocolVariable, handler));
        }
    }

    private void UnsubscribeReplaySampleTime()
    {
        foreach (var subscription in replaySampleTimeSubscriptions)
        {
            subscription.ProtocolVariable.UnsubscribeAsyncValueChanged(subscription.Handler);
        }

        replaySampleTimeSubscriptions.Clear();
        Interlocked.Exchange(ref replaySampleTimestampUtcTicks, 0);
    }

    private void SubscribeProtocolVariableSinkDrivers(
        IEnumerable<IDriverBase> sourceDrivers,
        IEnumerable<IDriverBase> sinkDrivers)
    {
        UnsubscribeProtocolVariableSinkDrivers();

        var sourceProtocolVariables = sourceDrivers
            .SelectMany(driver => driver.Protocols)
            .SelectMany(protocol => protocol.Variables)
            .Where(protocolVariable => protocolVariable.IsCommunicated)
            .ToList();

        foreach (var sinkDriver in sinkDrivers.OfType<IProtocolVariableSinkDriver>())
        {
            foreach (var protocolVariable in sourceProtocolVariables)
            {
                if (!sinkDriver.CanSubscribe(protocolVariable))
                {
                    continue;
                }

                Func<IProtocolVariable, Task> handler = sinkDriver.OnProtocolVariableValueChangedAsync;
                protocolVariable.SubscribeAsyncValueChanged(handler);
                protocolVariableSinkSubscriptions.Add((protocolVariable, handler));
            }
        }
    }

    private void UnsubscribeProtocolVariableSinkDrivers()
    {
        foreach (var subscription in protocolVariableSinkSubscriptions)
        {
            subscription.ProtocolVariable.UnsubscribeAsyncValueChanged(subscription.Handler);
        }

        protocolVariableSinkSubscriptions.Clear();
    }

    private void SubscribeProtocolVariableCommandDrivers(IEnumerable<IDriverBase> sourceDrivers, CancellationToken ct)
    {
        UnsubscribeProtocolVariableCommandDrivers();

        foreach (var driver in sourceDrivers)
        {
            if (driver is not IProtocolVariableCommandDriver commandDriver)
            {
                continue;
            }

            var commandVariables = driver
                .Protocols
                .SelectMany(protocol => protocol.Variables)
                .Where(protocolVariable => protocolVariable.IsCommunicated)
                .Where(commandDriver.CanSendCommand)
                .ToList();

            foreach (var protocolVariable in commandVariables)
            {
                Func<IProtocolVariable, Task> handler = async changedProtocolVariable =>
                {
                    try
                    {
                        await commandDriver.OnProtocolVariableCommandAsync(changedProtocolVariable, ct);
                    }
                    catch (Exception e)
                    {
                        Logger?.Log(LogLevel.Warn, $"Protocol variable command could not be sent: {e.Message}", e);
                    }
                };

                protocolVariable.SubscribeAsyncValueChanged(handler);
                protocolVariableCommandSubscriptions.Add((protocolVariable, handler));
            }

            // On-request reads (variables bound to an On Request event): the read-requested
            // notification goes the same way — driver → owning protocol. Exceptions are NOT
            // swallowed here: the requesting control reports a failed read (the protocol has
            // already logged the reason).
            var readRequestVariables = driver
                .Protocols
                .SelectMany(protocol => protocol.Variables)
                .Where(protocolVariable => protocolVariable.IsCommunicated)
                .Where(commandDriver.CanRequestRead)
                .ToList();

            foreach (var protocolVariable in readRequestVariables)
            {
                Func<IProtocolVariable, CancellationToken, Task> handler = (requestedProtocolVariable, requestCt) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, requestCt);
                    return commandDriver.OnProtocolVariableReadRequestAsync(requestedProtocolVariable, linkedCts.Token);
                };

                protocolVariable.SubscribeAsyncReadRequested(handler);
                protocolVariableReadRequestSubscriptions.Add((protocolVariable, handler));
            }
        }
    }

    private void UnsubscribeProtocolVariableCommandDrivers()
    {
        foreach (var subscription in protocolVariableCommandSubscriptions)
        {
            subscription.ProtocolVariable.UnsubscribeAsyncValueChanged(subscription.Handler);
        }

        protocolVariableCommandSubscriptions.Clear();

        foreach (var subscription in protocolVariableReadRequestSubscriptions)
        {
            subscription.ProtocolVariable.UnsubscribeAsyncReadRequested(subscription.Handler);
        }

        protocolVariableReadRequestSubscriptions.Clear();
    }
    
}
