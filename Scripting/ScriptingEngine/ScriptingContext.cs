using Python.Runtime;
using System.Globalization;
using System.Text;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Scripting.Script;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Scripting.ScriptingEngine;

public class ScriptingContext
{
    private const string LastScriptExceptionTextName = "__qenex_script_exception_text";
    private const string ScriptStopTokenName = "__qenex_stop_token";
    private const double ScriptOverloadWarningRatio = 0.7;
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScriptOverloadWarningInterval = TimeSpan.FromSeconds(5);
    private readonly ILogger? logger;
    private readonly TaskFactory pythonFactory;
    private PythonLogWriter stdoutWriter = null!;
    private PythonLogWriter stderrWriter = null!;
    private CancellationTokenSource? periodicScriptsCts;
    private readonly List<Task> periodicScriptTasks = [];
    private readonly object manualRunLock = new();
    private readonly Dictionary<IScriptBase, CancellationTokenSource> manualRunSources = [];
    /// <summary>
    /// Upper bound on how long a script execution may keep running after a stop request before it
    /// is written off as hung. Shared by the Manual-script Stop button and the session stop
    /// (Disconnect). It is a maximum, not a delay: the stop continues the moment the running
    /// executions finish, and nothing is waited for when no execution is running.
    /// </summary>
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);
    private readonly object executionStateLock = new();
    private readonly object onValueChangedStateLock = new();
    private readonly object scriptOverloadStateLock = new();
    private readonly HashSet<IScriptBase> scheduledScripts = [];
    private readonly HashSet<IScriptBase> interruptedScripts = [];
    private readonly HashSet<IScriptBase> notifiedInterruptedScripts = [];
    private readonly Dictionary<OnValueChangedScriptTrigger, OnValueChangedTriggerRuntimeState> onValueChangedTriggerStates = [];
    private readonly Dictionary<string, ScriptCallOverloadState> scriptOverloadStates = [];
    private CancellationTokenSource activeExecutionCts = new();
    // Completed when the last reserved execution releases its slot while the context is stopping;
    // created lazily by the stop path, guarded by executionStateLock.
    private TaskCompletionSource? scheduledExecutionsDrained;
    private volatile bool isStopping;
    private long stopRequestedTimestamp;
    private bool suppressLateExecutionOutput;
    private static readonly object pythonInitLock = new();
    private static bool pythonRuntimeInitialized;
    // True between a successful InitializeSharedScope and DisposeSharedScope. A session
    // started with Python disabled never initializes the scope, so Stop must not touch Python.
    private bool sharedScopeStarted;
    
    #region Constructors

    public ScriptingContext(ScriptEngineSettings settings, ILogger? log = null)
    {
        logger = log;
        pythonFactory = new TaskFactory(new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);
        VariableBindings = [];
        OnValueChangedScriptTriggers = [];
        EngineSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        Scripts = new List<IScriptBase>();
    }

    #endregion

    #region Properties
    
    public IList<IScriptBase> Scripts { get; set; }
    public IList<VariableBinding> VariableBindings { get; set; }
    public IList<OnValueChangedScriptTrigger> OnValueChangedScriptTriggers { get; set; }
    public ScriptEngineSettings EngineSettings { get; set; }
    public bool IsReplayMode { get; set; }
    public bool IsPythonEnabled => EngineSettings.UsePythonScripts;

    /// <summary>
    /// Invoked after a script successfully writes a variable value (raw or eng). The host
    /// (module) routes the write to protocols that publish script-computed variables. Runs on
    /// the Python execution thread, so the callback must only enqueue and never block.
    /// </summary>
    public Action<IVariableBase>? VariableWrittenCallback { get; set; }
    public PyModule? SharedScope { get; internal set; }
    public bool HasAbandonedExecutions { get; private set; }
    public event EventHandler<ScriptExecutedEventArgs>? ScriptExecuted;
    
    #endregion

    #region Scope

    /// <summary>
    /// Decides how a runtime/replay session may start with respect to Python. Python is
    /// optional: with it disabled the session still starts unless the project has a script
    /// enabled for the current mode (Runtime flag, or Replay flag in replay mode).
    /// </summary>
    public ScriptingStartDecision GetStartDecision()
    {
        if (IsPythonEnabled)
        {
            return ScriptingStartDecision.PythonEnabled;
        }

        if (Scripts.Count == 0)
        {
            return ScriptingStartDecision.NoScripts;
        }

        return Scripts.Any(IsScriptEnabledForCurrentMode)
            ? ScriptingStartDecision.EnabledScriptsBlocked
            : ScriptingStartDecision.DisabledScriptsOnly;
    }

    public string BuildBlockedStartMessage()
    {
        var session = IsReplayMode ? "Replay" : "Runtime";
        var names = string.Join(", ", Scripts.Where(IsScriptEnabledForCurrentMode).Select(script => $"\"{script.FileName}\""));
        return $"{session} cannot start: the project contains enabled Python script(s) ({names}) but Python scripting is disabled. "
               + "Enable 'Use Python scripts' in Options -> Preferences -> General, or disable the script(s) in Project Configuration -> Scripts.";
    }

    public string BuildDisabledScriptsWarning()
    {
        return "The project contains Python script(s), but Python scripting is disabled (Options -> Preferences -> General); no script will run.";
    }

    private bool IsScriptEnabledForCurrentMode(IScriptBase script)
    {
        return IsReplayMode ? script.IsReplayEnabled : script.IsEnabled;
    }

    public async Task InitializeSharedScopeAsync(IList<IVariableBase> variables)
    {
        ResetStopState();
        await pythonFactory.StartNew(() => InitializeSharedScope(variables));
        await ExecuteScriptsAsync(ScriptExecutionMode.Startup, allowNonBlocking: true);
        StartPeriodicScripts();
    }
    
    private void InitializeSharedScope(IList<IVariableBase> variables)
    {
        if (HasAbandonedExecutions)
        {
            throw new InvalidOperationException("Abandoned scripting context cannot be restarted.");
        }

        EnsurePythonRuntimeInitialized();
        VariableBindings.Clear();
        lock (onValueChangedStateLock)
        {
            onValueChangedTriggerStates.Clear();
        }
        ClearScriptOverloadState();
        
        var variablesById = variables.ToDictionary(v => v.Id);
        foreach (var varById in variablesById)
        {
            VariableBindings.Add(new VariableBinding(varById.Value.Name, varById.Key));
        }


        using (Py.GIL())
        {
            stdoutWriter = new PythonLogWriter(logger!, LogLevel.Script);
            stderrWriter = new PythonLogWriter(logger!, LogLevel.Script);
            
            using (var sys = Py.Import("sys"))
            {
                sys.SetAttr("stdout", stdoutWriter.ToPython());
                sys.SetAttr("stderr", stderrWriter.ToPython());
            }
            
            SharedScope = Py.CreateScope("qenex_scripts_shared");
            sharedScopeStarted = true;

            foreach (var binding in VariableBindings)
            {
                if (!variablesById.TryGetValue(binding.VariableId, out var variable))
                {
                    logger?.Log(LogLevel.Warn, $"Variable {binding.VariableId} not found.");
                    continue;
                }

                SharedScope.Set(binding.PythonName, new VariableBridge(variable, () => VariableWrittenCallback));
            }

            //SharedScope.Exec("print(\"Ahoj - toto je test\")");
        }
        
    }

    public Task<InteractivePythonExecutionResult> ExecuteInteractiveAsync(string input, CancellationToken ct = default)
    {
        return pythonFactory.StartNew(() => ExecuteInteractive(input), ct);
    }

    private InteractivePythonExecutionResult ExecuteInteractive(string input)
    {
        if (SharedScope == null)
        {
            throw new InvalidOperationException("Python shared scope is not initialized.");
        }

        using (Py.GIL())
        {
            EnsureInteractiveConsole();

            var stdoutWriter = new PythonOutputBufferWriter();
            var stderrWriter = new PythonOutputBufferWriter();

            using var sys = Py.Import("sys");
            using var previousStdout = sys.GetAttr("stdout");
            using var previousStderr = sys.GetAttr("stderr");

            try
            {
                sys.SetAttr("stdout", stdoutWriter.ToPython());
                sys.SetAttr("stderr", stderrWriter.ToPython());

                using var console = SharedScope.Get("__qenex_interactive_console");
                using var result = console.InvokeMethod("push", input.ToPython());

                return new InteractivePythonExecutionResult
                {
                    Output = stdoutWriter.Text,
                    Error = stderrWriter.Text,
                    IsIncomplete = result.As<bool>()
                };
            }
            finally
            {
                sys.SetAttr("stdout", previousStdout);
                sys.SetAttr("stderr", previousStderr);
            }
        }
    }

    private void EnsureInteractiveConsole()
    {
        SharedScope!.Exec("""
import code as __qenex_code
if "__qenex_interactive_console" not in globals():
    __qenex_interactive_console = __qenex_code.InteractiveConsole(locals())
""");
    }

    private void EnsurePythonRuntimeInitialized()
    {
        if (pythonRuntimeInitialized) return;

        lock (pythonInitLock)
        {
            if (pythonRuntimeInitialized) return;
            if (PythonEngine.IsInitialized) { pythonRuntimeInitialized = true; return; }

            if (!EngineSettings.UsePythonScripts)
            {
                throw new InvalidOperationException("Python scripting is disabled. Enable 'Use Python scripts' in Options -> Preferences -> General.");
            }

            if (string.IsNullOrWhiteSpace(EngineSettings.PythonDllPath))
            {
                throw new InvalidOperationException("Python DLL path is not configured.");
            }

            if (!File.Exists(EngineSettings.PythonDllPath))
            {
                //logger?.Log(LogLevel.Error, $"Configured Python DLL path was not found: {EngineSettings.PythonDllPath}");
                throw new FileNotFoundException("Configured Python DLL path was not found.", EngineSettings.PythonDllPath);
            }

            Runtime.PythonDLL = EngineSettings.PythonDllPath;
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();
            pythonRuntimeInitialized = true;
        }
    }
    
    public async Task DisposeSharedScopeAsync(CancellationToken ct = default)
    {
        if (!sharedScopeStarted)
        {
            // Session ran without Python (disabled in preferences): nothing to shut down.
            return;
        }

        RequestStop();
        await WaitForRunningExecutionsAsync();
        await StopPeriodicScriptsAsync();
        if (!HasAbandonedExecutions)
        {
            await ExecuteScriptsAsync(
                ScriptExecutionMode.Shutdown,
                allowNonBlocking: false,
                ct,
                allowStopCancellation: false,
                defaultTimeout: StopWaitTimeout);
        }
        else
        {
            logger?.Log(LogLevel.Warn, "Shutdown scripts skipped because scripting context has abandoned executions.");
        }

        await RunPythonFactoryActionAsync(DisposeSharedScope, "disposing Python shared scope", StopWaitTimeout, ct);
    }

    /// <summary>
    /// Enters the stopping state: no new execution is scheduled (periodic timers are cancelled,
    /// running Manual scripts receive their stop token as if the user pressed Stop), but executions
    /// already running are left alone so they can finish normally. <see cref="DisposeSharedScopeAsync"/>
    /// then waits for them (bounded by <see cref="StopGracePeriod"/>, measured from this call) and
    /// hard-interrupts only what is still running afterwards.
    /// </summary>
    public void RequestStop()
    {
        if (isStopping)
        {
            return;
        }

        stopRequestedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        isStopping = true;
        periodicScriptsCts?.Cancel();
        CancelManualScriptRuns();
    }

    /// <summary>
    /// Waits for the executions that were running at the stop request to finish on their own,
    /// for at most the remainder of <see cref="StopGracePeriod"/>. Returns immediately when nothing
    /// is running. Whatever is still running afterwards is marked as interrupted (abandoning the
    /// context) and its stop token is raised so a cooperative script ends at its next line.
    /// </summary>
    private async Task WaitForRunningExecutionsAsync()
    {
        var drainedTask = GetScheduledExecutionsDrainedTask();
        if (!drainedTask.IsCompleted)
        {
            var remainingGrace = StopGracePeriod - System.Diagnostics.Stopwatch.GetElapsedTime(stopRequestedTimestamp);
            if (remainingGrace > TimeSpan.Zero)
            {
                await WaitForTaskCompletionAsync(drainedTask, remainingGrace, CancellationToken.None);
            }
        }

        MarkScheduledExecutionsInterruptedOnStop();
        if (!activeExecutionCts.IsCancellationRequested)
        {
            await activeExecutionCts.CancelAsync();
        }
    }

    private void CancelManualScriptRuns()
    {
        List<CancellationTokenSource> runSources;
        lock (manualRunLock)
        {
            if (manualRunSources.Count == 0)
            {
                return;
            }

            runSources = manualRunSources.Values.ToList();
        }

        foreach (var runCts in runSources)
        {
            try
            {
                runCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the snapshot and the cancel; nothing to stop.
            }
        }
    }

    public ScriptingContext CreateCleanContextForNextSession()
    {
        return new ScriptingContext(EngineSettings, logger)
        {
            Scripts = Scripts,
            OnValueChangedScriptTriggers = OnValueChangedScriptTriggers,
            IsReplayMode = IsReplayMode,
            ScriptExecuted = ScriptExecuted
        };
    }

    private void DisposeSharedScope()
    {
        using (Py.GIL())
        {
            using (var sys = Py.Import("sys"))
            {
                sys.SetAttr("stdout", sys.GetAttr("__stdout__"));
                sys.SetAttr("stderr", sys.GetAttr("__stderr__"));
            }
            
            SharedScope?.Dispose();
            SharedScope = null;
        }
        sharedScopeStarted = false;
        stdoutWriter = null!;
        stderrWriter = null!;
    }

    #endregion

    #region Scripts
    
    public void AddScript(IScriptBase script)
    {
        if (Scripts.FirstOrDefault(s => s.FileName == script.FileName) != null)
        {
            logger?.Log(LogLevel.Warn, $"Script with Name {script.FileName} already exists.");
            return;
        }
        Scripts.Add(script);
    }

    public void AddScripts(IList<IScriptBase> scripts)
    {
        foreach (var script in scripts)
        {
            AddScript(script);
        }
    }

    public void RemoveScript(IScriptBase script)
    {
        Scripts.Remove(script);
    }

    public void AddOnValueChangedScriptTrigger(int variableId, string scriptFileName, string additionalInfo)
    {
        if (string.IsNullOrWhiteSpace(scriptFileName))
        {
            logger?.Log(LogLevel.Warn, $"OnValueChanged script trigger for variable {variableId} has empty script reference.");
            return;
        }

        if (OnValueChangedScriptTriggers.Any(t =>
                t.VariableId == variableId &&
                t.ScriptFileName == scriptFileName &&
                t.AdditionalInfo == additionalInfo))
        {
            return;
        }

        OnValueChangedScriptTriggers.Add(new OnValueChangedScriptTrigger(variableId, scriptFileName, additionalInfo));
    }

    public IEnumerable<OnValueChangedScriptTrigger> GetOnValueChangedScriptTriggers(int variableId)
    {
        return OnValueChangedScriptTriggers.Where(t => t.VariableId == variableId);
    }

    public bool HasOnValueChangedScriptTriggers(int variableId)
    {
        return OnValueChangedScriptTriggers.Any(t => t.VariableId == variableId);
    }

    public async Task HandleVariableValueChangedAsync(IVariableBase variable)
    {
        if (isStopping)
        {
            return;
        }

        var triggers = GetOnValueChangedScriptTriggers(variable.Id).ToList();
        if (triggers.Count == 0)
        {
            return;
        }

        var rawObject = variable.GetValue();
        if (!TryConvertToDouble(rawObject, out var rawValue))
        {
            logger?.Log(LogLevel.Warn, $"OnValueChanged script trigger for variable \"{variable.Name}\" skipped because value \"{rawObject}\" is not numeric.");
            return;
        }

        var engValue = variable is ScalarVariable scalar ? scalar.GetEngValue() : rawValue;

        List<(OnValueChangedScriptTrigger Trigger, double OldValue, double NewValue, double Delta)> scriptExecutions = [];
        lock (onValueChangedStateLock)
        {
            foreach (var trigger in triggers)
            {
                if (!OnValueChangedTriggerConfig.TryParse(trigger.AdditionalInfo, out var config, out var parseError))
                {
                    logger?.Log(LogLevel.Warn, $"OnValueChanged script \"{trigger.ScriptFileName}\" has invalid trigger settings ({parseError}).");
                    continue;
                }

                var current = config.Source == TriggerValueSource.Eng ? engValue : rawValue;
                var state = GetOnValueChangedTriggerState(trigger);

                if (EvaluateOnValueChangedTrigger(config, state, current, out var oldValue, out var delta))
                {
                    scriptExecutions.Add((trigger, oldValue, current, delta));
                }
            }
        }

        foreach (var scriptExecution in scriptExecutions)
        {
            await ExecuteOnValueChangedScriptAsync(
                scriptExecution.Trigger,
                variable.Name,
                scriptExecution.OldValue,
                scriptExecution.NewValue,
                scriptExecution.Delta);
        }
    }

    /// <summary>Runs a Manual-mode script on explicit user request. Scripts with any other
    /// execution mode are refused; every refusal is logged so the request never no-ops silently.</summary>
    public async Task<bool> ExecuteManualScriptAsync(IScriptBase script, CancellationToken ct = default)
    {
        if (script.ExecutionMode != ScriptExecutionMode.Manual)
        {
            logger?.Log(LogLevel.Warn, $"Script \"{script.FileName}\" cannot be run manually because its execution mode is {script.ExecutionMode}.");
            return false;
        }

        if (SharedScope == null)
        {
            logger?.Log(LogLevel.Warn, IsPythonEnabled
                ? $"Manual script \"{script.FileName}\" cannot run because the scripting context is not running."
                : $"Manual script \"{script.FileName}\" cannot run because Python scripting is disabled (Options -> Preferences -> General).");
            return false;
        }

        if (!CanExecuteScript(script))
        {
            logger?.Log(LogLevel.Warn, $"Manual script \"{script.FileName}\" is disabled and was not run.");
            return false;
        }

        CancellationTokenSource runCts;
        lock (manualRunLock)
        {
            if (manualRunSources.ContainsKey(script))
            {
                logger?.Log(LogLevel.Warn, $"Manual script \"{script.FileName}\" is already running.");
                return false;
            }

            runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            manualRunSources[script] = runCts;
        }

        try
        {
            var executed = await ExecuteScriptAsync(
                script,
                GetScriptExecutionOptions(script),
                runCts.Token,
                cancellationGracePeriod: StopGracePeriod);
            if (!executed)
            {
                logger?.Log(LogLevel.Warn, $"Manual script \"{script.FileName}\" was not executed (already running or the execution was cancelled).");
            }

            return executed;
        }
        finally
        {
            lock (manualRunLock)
            {
                manualRunSources.Remove(script);
            }

            runCts.Dispose();
        }
    }

    /// <summary>Stops a running Manual-mode script started by <see cref="ExecuteManualScriptAsync"/>.
    /// The stop token interrupts the script at its next executed Python line; a script stuck in
    /// a blocking native call falls back to the hard-interrupt path after a short grace period.</summary>
    public void StopManualScript(IScriptBase script)
    {
        CancellationTokenSource? runCts;
        lock (manualRunLock)
        {
            manualRunSources.TryGetValue(script, out runCts);
        }

        if (runCts == null)
        {
            logger?.Log(LogLevel.Warn, $"Manual script \"{script.FileName}\" is not running.");
            return;
        }

        try
        {
            runCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the lookup and the cancel; nothing to stop.
        }
    }

    private async Task ExecuteScriptsAsync(
        ScriptExecutionMode scriptMode,
        bool allowNonBlocking,
        CancellationToken ct = default,
        bool allowStopCancellation = true,
        TimeSpan? defaultTimeout = null)
    {
        var scripts = Scripts.Where(s => s.ExecutionMode == scriptMode);
        foreach (var script in scripts)
        {
            var options = GetScriptExecutionOptions(script, defaultTimeout);
            if (allowNonBlocking && !options.Blocking)
            {
                _ = RunNonBlockingScriptAsync(script, options, allowStopCancellation);
                continue;
            }

            await ExecuteScriptAsync(script, options, ct, allowStopCancellation: allowStopCancellation);
        }
    }

    private async Task RunNonBlockingScriptAsync(
        IScriptBase script,
        ScriptExecutionOptions options,
        bool allowStopCancellation)
    {
        try
        {
            await ExecuteScriptAsync(script, options, allowStopCancellation: allowStopCancellation);
        }
        catch (Exception e)
        {
            logger?.Log(LogLevel.Error, $"script \"{script.FileName}\" background execution failed: {e.Message}");
        }
    }

    private async Task<bool> ExecuteScriptAsync(
        IScriptBase script,
        ScriptExecutionOptions options,
        CancellationToken ct = default,
        Action? beforeExecute = null,
        bool allowStopCancellation = true,
        TimeSpan? cancellationGracePeriod = null)
    {
        if (!CanExecuteScript(script))
        {
            return false;
        }

        using var linkedCts = allowStopCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, activeExecutionCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (linkedCts.IsCancellationRequested)
        {
            return false;
        }

        if (!TryReserveScriptExecution(script))
        {
            return false;
        }

        // The task is queued without a cancellation token on purpose: ExecuteScript always runs
        // (it bails out on a cancelled token itself) so its finally block releases the reservation
        // taken above. A task cancelled before it started would keep the reservation forever.
        var executionTask = pythonFactory.StartNew(() => ExecuteScript(script, beforeExecute, linkedCts.Token));
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);
        Task completedTask;

        if (options.Timeout == null)
        {
            completedTask = await Task.WhenAny(executionTask, cancellationTask);
        }
        else
        {
            var timeoutTask = Task.Delay(options.Timeout.Value, ct);
            completedTask = await Task.WhenAny(executionTask, cancellationTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                MarkScriptInterrupted(script, $"script \"{script.FileName}\" timed out after {options.Timeout.Value.TotalMilliseconds:0} ms.");
                await linkedCts.CancelAsync();
                ObserveBackgroundTask(executionTask, $"script \"{script.FileName}\" after timeout");
                return false;
            }
        }

        if (completedTask == executionTask)
        {
            await AwaitExecutionTaskAsync(executionTask, ct);
            return true;
        }

        // A cancelled run gets a grace period to end itself via the stop token (raised at the
        // next executed Python line) before it is written off as a hard interrupt.
        if (cancellationGracePeriod.HasValue)
        {
            await linkedCts.CancelAsync();
            if (await WaitForTaskCompletionAsync(executionTask, cancellationGracePeriod.Value, CancellationToken.None))
            {
                await AwaitExecutionTaskAsync(executionTask, CancellationToken.None);
                return true;
            }
        }

        MarkScriptInterrupted(script, $"script \"{script.FileName}\" was interrupted because scripting context is stopping.");
        await linkedCts.CancelAsync();
        ObserveBackgroundTask(executionTask, $"script \"{script.FileName}\" after stop request");
        return false;
    }

    private void ExecuteScript(IScriptBase script, Action? beforeExecute = null, CancellationToken ct = default)
    {
        try
        {
            if (ct.IsCancellationRequested || !CanExecuteScript(script))
            {
                return;
            }

            ExecuteReservedScript(script, beforeExecute, ct);
        }
        finally
        {
            ReleaseScriptExecution(script);
        }
    }

    private void ExecuteReservedScript(IScriptBase script, Action? beforeExecute, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        script.RunState = ScriptRunState.Running;

        try
        {
            using (Py.GIL())
            {
                beforeExecute?.Invoke();
                SharedScope!.Set(ScriptStopTokenName, new ScriptStopToken(ct));
                var pythonError = ExecuteScriptContent(script.Content);
                if (!string.IsNullOrWhiteSpace(pythonError))
                {
                    if (!ShouldSuppressScriptResult(script))
                    {
                        script.RunState = ScriptRunState.Faulted;
                        logger?.Log(LogLevel.Warn, $"python script \"{script.FileName}\": {pythonError}");
                    }

                    return;
                }
            }

            if (!ShouldSuppressScriptResult(script))
            {
                script.RunState = ScriptRunState.Idle;
            }
        }
        catch (PythonException e)
        {
            if (!ShouldSuppressScriptResult(script))
            {
                script.RunState = ScriptRunState.Faulted;
                logger?.Log(LogLevel.Warn, $"python script \"{script.FileName}\": {e.Message}");
            }
        }
        catch (Exception e)
        {
            if (!ShouldSuppressScriptResult(script))
            {
                script.RunState = ScriptRunState.Faulted;
                logger?.Log(LogLevel.Error, $"script \"{script.FileName}\": {e.Message}");
            }
        }
        finally
        {
            if (!ShouldSuppressScriptNotification(script))
            {
                stopwatch.Stop();
                script.LastExecutionDurationMs = stopwatch.Elapsed.TotalMilliseconds;
                OnScriptExecuted(script);
            }
        }
    }

    private bool CanExecuteScript(IScriptBase script)
    {
        if (isStopping && script.ExecutionMode != ScriptExecutionMode.Shutdown)
        {
            return false;
        }

        return IsReplayMode
            ? script.IsReplayEnabled
            : script.IsEnabled;
    }

    private OnValueChangedTriggerRuntimeState GetOnValueChangedTriggerState(OnValueChangedScriptTrigger trigger)
    {
        if (!onValueChangedTriggerStates.TryGetValue(trigger, out var state))
        {
            state = new OnValueChangedTriggerRuntimeState();
            onValueChangedTriggerStates[trigger] = state;
        }

        return state;
    }

    /// <summary>
    /// Vyhodnoti jeden trigger proti aktualni hodnote. Prvni vzorek jen inicializuje stav (nespousti).
    /// Delta: |Xn - baseline| &gt; threshold (deadband od posledniho spusteni). Above/Below: hranove
    /// (spusti se jen pri prechodu podminky z neplati -&gt; plati).
    /// </summary>
    private static bool EvaluateOnValueChangedTrigger(
        OnValueChangedTriggerConfig config,
        OnValueChangedTriggerRuntimeState state,
        double current,
        out double oldValue,
        out double delta)
    {
        oldValue = state.Initialized ? state.LastSampleValue : current;
        delta = Math.Abs(current - oldValue);

        if (!state.Initialized)
        {
            state.Initialized = true;
            state.Baseline = current;
            state.LastSampleValue = current;
            state.ConditionActive = config.Mode switch
            {
                TriggerConditionMode.Above => current > config.Threshold,
                TriggerConditionMode.Below => current < config.Threshold,
                _ => false
            };
            return false;
        }

        bool fire;
        switch (config.Mode)
        {
            case TriggerConditionMode.Delta:
                oldValue = state.Baseline;
                delta = Math.Abs(current - state.Baseline);
                fire = delta > config.Threshold;
                if (fire)
                {
                    state.Baseline = current;
                }

                break;

            case TriggerConditionMode.Above:
                if (state.ConditionActive)
                {
                    // Drz sepnute, dokud hodnota neklesne pod vypinaci mez (T - hystereze).
                    state.ConditionActive = current > config.Threshold - config.Hysteresis;
                    fire = false;
                }
                else if (current > config.Threshold)
                {
                    state.ConditionActive = true;
                    fire = true;
                }
                else
                {
                    fire = false;
                }

                break;

            case TriggerConditionMode.Below:
                if (state.ConditionActive)
                {
                    // Drz sepnute, dokud hodnota nestoupne nad vypinaci mez (T + hystereze).
                    state.ConditionActive = current < config.Threshold + config.Hysteresis;
                    fire = false;
                }
                else if (current < config.Threshold)
                {
                    state.ConditionActive = true;
                    fire = true;
                }
                else
                {
                    fire = false;
                }

                break;

            default:
                fire = false;
                break;
        }

        state.LastSampleValue = current;
        return fire;
    }

    private async Task ExecuteOnValueChangedScriptAsync(
        OnValueChangedScriptTrigger trigger,
        string variableName,
        double oldValue,
        double newValue,
        double delta)
    {
        var script = Scripts.FirstOrDefault(s => s.FileName == trigger.ScriptFileName);
        if (script == null)
        {
            logger?.Log(LogLevel.Warn, $"OnValueChanged script \"{trigger.ScriptFileName}\" was not found.");
            return;
        }

        if (script.ExecutionMode != ScriptExecutionMode.OnValueChanged)
        {
            logger?.Log(LogLevel.Warn, $"Script \"{script.FileName}\" is referenced by an OnValueChanged trigger but has execution mode {script.ExecutionMode}.");
            return;
        }

        var overloadKey = GetScriptOverloadKey("onvaluechanged", script);
        if (!TryEnterObservedScriptCall(overloadKey, out var observedCallPeriod))
        {
            return;
        }

        var executed = await ExecuteScriptAsync(script, GetScriptExecutionOptions(script), beforeExecute: () =>
        {
            SharedScope!.Set("qenex_trigger_variable_id", trigger.VariableId);
            SharedScope.Set("qenex_trigger_variable_name", variableName);
            SharedScope.Set("qenex_trigger_old_value", oldValue);
            SharedScope.Set("qenex_trigger_new_value", newValue);
            SharedScope.Set("qenex_trigger_delta", delta);
        });

        if (executed && observedCallPeriod.HasValue)
        {
            UpdateScriptOverloadGuard(
                overloadKey,
                script,
                observedCallPeriod.Value,
                "OnValueChanged script");
        }
    }

    private static bool TryConvertToDouble(object value, out double result)
    {
        try
        {
            result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }

    private bool TryEnterObservedScriptCall(string overloadKey, out TimeSpan? observedCallPeriod)
    {
        var nowUtc = DateTime.UtcNow;
        lock (scriptOverloadStateLock)
        {
            var state = GetScriptOverloadState(overloadKey);
            observedCallPeriod = state.LastCallUtc.HasValue
                ? nowUtc - state.LastCallUtc.Value
                : null;
            state.LastCallUtc = nowUtc;

            if (state.SkipCallsRemaining <= 0)
            {
                return true;
            }

            state.SkipCallsRemaining--;
            return false;
        }
    }

    private bool TrySkipScriptCall(string overloadKey)
    {
        lock (scriptOverloadStateLock)
        {
            var state = GetScriptOverloadState(overloadKey);
            if (state.SkipCallsRemaining <= 0)
            {
                return false;
            }

            state.SkipCallsRemaining--;
            return true;
        }
    }

    private void UpdateScriptOverloadGuard(
        string overloadKey,
        IScriptBase script,
        TimeSpan callPeriod,
        string executionSource)
    {
        var skipCalls = CalculateScriptOverloadSkipCount(script.LastExecutionDurationMs, callPeriod);
        if (skipCalls <= 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        bool shouldLogWarning;
        lock (scriptOverloadStateLock)
        {
            var state = GetScriptOverloadState(overloadKey);
            state.SkipCallsRemaining = Math.Max(state.SkipCallsRemaining, skipCalls);
            shouldLogWarning = nowUtc - state.LastWarningUtc >= ScriptOverloadWarningInterval;
            if (shouldLogWarning)
            {
                state.LastWarningUtc = nowUtc;
            }
        }

        if (!shouldLogWarning)
        {
            return;
        }

        var periodMs = callPeriod.TotalMilliseconds;
        var durationRatio = script.LastExecutionDurationMs / periodMs * 100;
        logger?.Log(
            LogLevel.Warn,
            $"{executionSource} \"{script.FileName}\" took {script.LastExecutionDurationMs:0.0} ms ({durationRatio:0}% of {periodMs:0.0} ms call period). It exceeds 70% of the call period; skipping next {skipCalls} scheduled call(s).");
    }

    private static int CalculateScriptOverloadSkipCount(double executionDurationMs, TimeSpan callPeriod)
    {
        if (executionDurationMs <= 0 || callPeriod <= TimeSpan.Zero)
        {
            return 0;
        }

        var allowedDurationMs = callPeriod.TotalMilliseconds * ScriptOverloadWarningRatio;
        if (allowedDurationMs <= 0 || executionDurationMs <= allowedDurationMs)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(executionDurationMs / allowedDurationMs) - 1);
    }

    private ScriptCallOverloadState GetScriptOverloadState(string overloadKey)
    {
        if (!scriptOverloadStates.TryGetValue(overloadKey, out var state))
        {
            state = new ScriptCallOverloadState();
            scriptOverloadStates[overloadKey] = state;
        }

        return state;
    }

    private void ClearScriptOverloadState()
    {
        lock (scriptOverloadStateLock)
        {
            scriptOverloadStates.Clear();
        }
    }

    private static string GetScriptOverloadKey(string executionSource, IScriptBase script)
    {
        return $"{executionSource}:{script.FileName}";
    }

    private void StartPeriodicScripts()
    {
        if (periodicScriptsCts != null)
        {
            return;
        }

        periodicScriptsCts = new CancellationTokenSource();
        foreach (var script in Scripts.Where(s => s.ExecutionMode == ScriptExecutionMode.Periodic))
        {
            if (!TryGetPeriodicInterval(script, out var interval))
            {
                continue;
            }

            periodicScriptTasks.Add(RunPeriodicScriptAsync(script, interval, periodicScriptsCts.Token));
        }
    }

    private async Task StopPeriodicScriptsAsync()
    {
        if (periodicScriptsCts == null)
        {
            return;
        }

        await periodicScriptsCts.CancelAsync();

        try
        {
            var stopTask = Task.WhenAll(periodicScriptTasks);
            if (await WaitForTaskCompletionAsync(stopTask, StopWaitTimeout, CancellationToken.None))
            {
                await stopTask;
                return;
            }

            ObserveBackgroundTask(stopTask, "periodic scripts after stop request");
            logger?.Log(LogLevel.Warn, $"Periodic scripts did not stop within {StopWaitTimeout.TotalMilliseconds:0} ms.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            periodicScriptsCts.Dispose();
            periodicScriptsCts = null;
            periodicScriptTasks.Clear();
        }
    }

    private async Task RunPeriodicScriptAsync(IScriptBase script, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        using var cancellationRegistration = ct.Register(static state => ((PeriodicTimer)state!).Dispose(), timer);
        var overloadKey = GetScriptOverloadKey("periodic", script);

        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync())
            {
                if (TrySkipScriptCall(overloadKey))
                {
                    continue;
                }

                // The loop token only stops the timer. The execution itself is not linked to it so a
                // script caught mid-run by the stop request finishes normally within the stop grace.
                var executed = await ExecuteScriptAsync(script, GetScriptExecutionOptions(script));
                if (executed)
                {
                    UpdateScriptOverloadGuard(
                        overloadKey,
                        script,
                        interval,
                        "Periodic script");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private bool TryGetPeriodicInterval(IScriptBase script, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        var settings = ParseAdditionalInfo(script.AdditionalInfo);

        if (!settings.TryGetValue("period", out var periodText) ||
            !double.TryParse(periodText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var period) ||
            period <= 0)
        {
            logger?.Log(LogLevel.Warn, $"Periodic script \"{script.FileName}\" has invalid period setting.");
            return false;
        }

        if (!settings.TryGetValue("unit", out var unit))
        {
            logger?.Log(LogLevel.Warn, $"Periodic script \"{script.FileName}\" has no unit setting.");
            return false;
        }

        interval = unit.Trim().ToLowerInvariant() switch
        {
            "ms" or "msec" or "millisec" or "millisecond" or "milliseconds" => TimeSpan.FromMilliseconds(period),
            "s" or "sec" or "second" or "seconds" => TimeSpan.FromSeconds(period),
            "m" or "min" or "minute" or "minutes" => TimeSpan.FromMinutes(period),
            "h" or "hour" or "hours" => TimeSpan.FromHours(period),
            _ => TimeSpan.Zero
        };

        if (interval > TimeSpan.Zero)
        {
            return true;
        }

        logger?.Log(LogLevel.Warn, $"Periodic script \"{script.FileName}\" has unsupported unit setting \"{unit}\".");
        return false;
    }

    private static Dictionary<string, string> ParseAdditionalInfo(string additionalInfo)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = additionalInfo.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (keyValue.Length == 2)
            {
                settings[keyValue[0]] = keyValue[1];
            }
        }

        return settings;
    }

    private ScriptExecutionOptions GetScriptExecutionOptions(IScriptBase script, TimeSpan? defaultTimeout = null)
    {
        TimeSpan? timeout = defaultTimeout;
        if (script.TimeoutMs > 0)
        {
            timeout = TimeSpan.FromMilliseconds(script.TimeoutMs);
        }
        else if (script.TimeoutMs < 0)
        {
            logger?.Log(LogLevel.Warn, $"Script \"{script.FileName}\" has invalid timeout setting \"{script.TimeoutMs}\".");
        }

        return new ScriptExecutionOptions(script.Blocking, timeout);
    }

    private void ResetStopState()
    {
        if (HasAbandonedExecutions)
        {
            throw new InvalidOperationException("Abandoned scripting context cannot be restarted.");
        }

        isStopping = false;
        suppressLateExecutionOutput = false;
        lock (executionStateLock)
        {
            scheduledExecutionsDrained = null;
        }

        if (!activeExecutionCts.IsCancellationRequested)
        {
            return;
        }

        activeExecutionCts.Dispose();
        activeExecutionCts = new CancellationTokenSource();
    }

    private async Task RunPythonFactoryActionAsync(
        Action action,
        string operationName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var actionTask = pythonFactory.StartNew(action, timeoutCts.Token);
        if (await WaitForTaskCompletionAsync(actionTask, timeout, ct))
        {
            await AwaitExecutionTaskAsync(actionTask, ct);
            return;
        }

        await timeoutCts.CancelAsync();
        ObserveBackgroundTask(actionTask, operationName);
        logger?.Log(LogLevel.Warn, $"{operationName} did not finish within {timeout.TotalMilliseconds:0} ms.");
    }

    private static async Task<bool> WaitForTaskCompletionAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var delayTask = Task.Delay(timeout, ct);
        return await Task.WhenAny(task, delayTask) == task;
    }

    private static async Task AwaitExecutionTaskAsync(Task task, CancellationToken ct)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MarkScriptInterrupted(IScriptBase script, string message)
    {
        if (!TryMarkScriptInterrupted(script))
        {
            return;
        }

        script.RunState = ScriptRunState.Faulted;
        logger?.Log(LogLevel.Warn, message);
        OnScriptExecuted(script);
    }

    private bool TryMarkScriptInterrupted(IScriptBase script)
    {
        bool added;
        lock (executionStateLock)
        {
            HasAbandonedExecutions = true;
            suppressLateExecutionOutput = true;
            added = interruptedScripts.Add(script);
            notifiedInterruptedScripts.Add(script);
        }

        DisableOutputWriters();
        return added;
    }

    private bool TryReserveScriptExecution(IScriptBase script)
    {
        lock (executionStateLock)
        {
            return scheduledScripts.Add(script);
        }
    }

    private void ReleaseScriptExecution(IScriptBase script)
    {
        TaskCompletionSource? drainedSignal = null;
        lock (executionStateLock)
        {
            scheduledScripts.Remove(script);
            if (scheduledScripts.Count == 0 && scheduledExecutionsDrained != null)
            {
                drainedSignal = scheduledExecutionsDrained;
                scheduledExecutionsDrained = null;
            }
        }

        drainedSignal?.TrySetResult();
    }

    /// <summary>Task that completes once no reserved execution is left; already completed when none is running.</summary>
    private Task GetScheduledExecutionsDrainedTask()
    {
        lock (executionStateLock)
        {
            if (scheduledScripts.Count == 0)
            {
                return Task.CompletedTask;
            }

            scheduledExecutionsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return scheduledExecutionsDrained.Task;
        }
    }

    private bool ShouldSuppressScriptResult(IScriptBase script)
    {
        lock (executionStateLock)
        {
            return interruptedScripts.Remove(script) || suppressLateExecutionOutput;
        }
    }

    private bool ShouldSuppressScriptNotification(IScriptBase script)
    {
        lock (executionStateLock)
        {
            return notifiedInterruptedScripts.Remove(script) || suppressLateExecutionOutput;
        }
    }

    private void ObserveBackgroundTask(Task task, string operationName)
    {
        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Exception != null && !ShouldSuppressLateExecutionOutput())
                {
                    logger?.Log(LogLevel.Error, $"{operationName} failed after stop returned: {completedTask.Exception.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void MarkScheduledExecutionsInterruptedOnStop()
    {
        List<IScriptBase> scriptsToNotify;
        lock (executionStateLock)
        {
            if (scheduledScripts.Count == 0)
            {
                return;
            }

            HasAbandonedExecutions = true;
            suppressLateExecutionOutput = true;
            scriptsToNotify = scheduledScripts.ToList();
            foreach (var script in scriptsToNotify)
            {
                interruptedScripts.Add(script);
                notifiedInterruptedScripts.Add(script);
                script.RunState = ScriptRunState.Faulted;
            }
        }

        DisableOutputWriters();
        logger?.Log(LogLevel.Warn, $"Scripting context stopped with {scriptsToNotify.Count} script execution(s) still running after {StopGracePeriod.TotalMilliseconds:0} ms grace period ({string.Join(", ", scriptsToNotify.Select(s => $"\"{s.FileName}\""))}); abandoning context.");
        foreach (var script in scriptsToNotify)
        {
            OnScriptExecuted(script);
        }
    }

    private bool ShouldSuppressLateExecutionOutput()
    {
        lock (executionStateLock)
        {
            return suppressLateExecutionOutput;
        }
    }

    private void DisableOutputWriters()
    {
        stdoutWriter?.Disable();
        stderrWriter?.Disable();
    }

    private string ExecuteScriptContent(string content)
    {
        SharedScope!.Exec(WrapScriptContent(content));
        using var exceptionText = SharedScope.Get(LastScriptExceptionTextName);
        return exceptionText.As<string>();
    }

    private static string WrapScriptContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"""
                {LastScriptExceptionTextName} = ""
                import sys as __qenex_sys
                class __QenexScriptStop(BaseException):
                    pass
                __qenex_script_globals = globals()
                def __qenex_script_stop_trace(frame, event, arg):
                    if frame.f_globals is not __qenex_script_globals:
                        return None
                    if event == "line" and {ScriptStopTokenName}.IsStopRequested():
                        raise __QenexScriptStop()
                    return __qenex_script_stop_trace
                __qenex_previous_trace = __qenex_sys.gettrace()
                try:
                    __qenex_sys.settrace(__qenex_script_stop_trace)
                    __qenex_sys._getframe().f_trace = __qenex_script_stop_trace
                    pass
                except __QenexScriptStop:
                    {LastScriptExceptionTextName} = "Script execution was stopped."
                except BaseException:
                    import traceback as __qenex_traceback
                    {LastScriptExceptionTextName} = __qenex_traceback.format_exc()
                finally:
                    __qenex_sys.settrace(__qenex_previous_trace)
                """;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{LastScriptExceptionTextName} = \"\"");
        builder.AppendLine("import sys as __qenex_sys");
        builder.AppendLine("class __QenexScriptStop(BaseException):");
        builder.AppendLine("    pass");
        builder.AppendLine("__qenex_script_globals = globals()");
        builder.AppendLine("def __qenex_script_stop_trace(frame, event, arg):");
        builder.AppendLine("    if frame.f_globals is not __qenex_script_globals:");
        builder.AppendLine("        return None");
        builder.AppendLine($"    if event == \"line\" and {ScriptStopTokenName}.IsStopRequested():");
        builder.AppendLine("        raise __QenexScriptStop()");
        builder.AppendLine("    return __qenex_script_stop_trace");
        builder.AppendLine("__qenex_previous_trace = __qenex_sys.gettrace()");
        builder.AppendLine("try:");
        builder.AppendLine("    __qenex_sys.settrace(__qenex_script_stop_trace)");
        builder.AppendLine("    __qenex_sys._getframe().f_trace = __qenex_script_stop_trace");

        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            builder.Append("    ");
            builder.AppendLine(line);
        }

        builder.AppendLine("except __QenexScriptStop:");
        builder.AppendLine($"    {LastScriptExceptionTextName} = \"Script execution was stopped.\"");
        builder.AppendLine("except BaseException:");
        builder.AppendLine("    import traceback as __qenex_traceback");
        builder.AppendLine($"    {LastScriptExceptionTextName} = __qenex_traceback.format_exc()");
        builder.AppendLine("finally:");
        builder.AppendLine("    __qenex_sys.settrace(__qenex_previous_trace)");
        return builder.ToString();
    }
    
    #endregion

    private void OnScriptExecuted(IScriptBase script)
    {
        ScriptExecuted?.Invoke(this, new ScriptExecutedEventArgs(script));
    }
}

internal readonly record struct ScriptExecutionOptions(bool Blocking, TimeSpan? Timeout);

internal sealed class OnValueChangedTriggerRuntimeState
{
    public bool Initialized { get; set; }
    public double Baseline { get; set; }
    public double LastSampleValue { get; set; }
    public bool ConditionActive { get; set; }
}

internal sealed class ScriptCallOverloadState
{
    public DateTime? LastCallUtc { get; set; }
    public int SkipCallsRemaining { get; set; }
    public DateTime LastWarningUtc { get; set; } = DateTime.MinValue;
}

public sealed class ScriptStopToken(CancellationToken cancellationToken)
{
    public bool IsStopRequested()
    {
        try
        {
            return cancellationToken.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}

public class ScriptExecutedEventArgs(IScriptBase script) : EventArgs
{
    public IScriptBase Script { get; } = script;
}
