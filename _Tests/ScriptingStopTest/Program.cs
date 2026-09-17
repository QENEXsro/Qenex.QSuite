using System.Diagnostics;
using Qenex.QSuite.LogSystems.ConsoleLogSubscriber;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Scripting.PythonScript;
using Qenex.QSuite.Scripting.Script;
using Qenex.QSuite.Scripting.ScriptingEngine;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Tests.ScriptingStopTest;

/// <summary>
/// Console-style integration tests of the scripting context stop contract (repo convention,
/// same as the other _Tests projects). Exit code 0 = all passed. Needs a real Python runtime:
/// the DLL comes from the QENEX_PYTHON_DLL environment variable or the default QInsight
/// location; without it the tests are skipped.
///
/// Contract under test (session stop = Disconnect):
///  - nothing running          -> stop continues immediately, shutdown scripts run, no abandon
///  - script caught mid-run    -> it finishes normally (bounded by the 2 s grace), no abandon
///  - script still running after the grace -> hard interrupt, context abandoned, shutdown skipped
///  - Manual script running    -> receives its stop token (as Stop button), no abandon
/// </summary>
internal static class Program
{
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);
    private static int failures;

    private static async Task<int> Main()
    {
        var pythonDll = ResolvePythonDll();
        if (pythonDll == null)
        {
            Console.WriteLine("SKIPPED: Python DLL not found (set QENEX_PYTHON_DLL).");
            return 0;
        }

        var logger = new Logger(LogLevel.Trace);
        logger.RegisterSubscriber(new ConsoleSubscriber());
        var settings = new ScriptEngineSettings { UsePythonScripts = true, PythonDllPath = pythonDll };

        await NothingRunningStopsImmediately(settings, logger);
        await PeriodicScriptCaughtMidRunFinishesWithinGrace(settings, logger);
        await HungPeriodicScriptIsAbandonedAfterGrace(settings, logger);
        await ManualScriptIsStoppedCooperativelyOnSessionStop(settings, logger);

        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static async Task NothingRunningStopsImmediately(ScriptEngineSettings settings, ILogger logger)
    {
        Console.WriteLine("--- NothingRunningStopsImmediately");
        var (context, periodic, shutdownCount) = CreateContext(settings, logger, "import time\ntime.sleep(0.05)", "period=5;unit=s");

        await context.InitializeSharedScopeAsync(new List<IVariableBase>());
        await Task.Delay(100);

        var elapsed = await StopAsync(context);
        Check(!context.HasAbandonedExecutions, "no abandon when nothing is running");
        Check(shutdownCount() == 1, $"shutdown script ran (count={shutdownCount()})");
        Check(elapsed < TimeSpan.FromMilliseconds(500), $"stop is immediate ({elapsed.TotalMilliseconds:0} ms)");
        Check(periodic.RunState != ScriptRunState.Faulted, "periodic script not faulted");
    }

    private static async Task PeriodicScriptCaughtMidRunFinishesWithinGrace(ScriptEngineSettings settings, ILogger logger)
    {
        Console.WriteLine("--- PeriodicScriptCaughtMidRunFinishesWithinGrace");
        var (context, periodic, shutdownCount) = CreateContext(settings, logger, "import time\ntime.sleep(0.4)", "period=50;unit=ms");

        await context.InitializeSharedScopeAsync(new List<IVariableBase>());
        await Task.Delay(150);
        Check(periodic.RunState == ScriptRunState.Running, "periodic script is mid-run at the stop request");

        var elapsed = await StopAsync(context);
        Check(!context.HasAbandonedExecutions, "script that finishes within the grace does not abandon the context");
        Check(shutdownCount() == 1, $"shutdown script ran (count={shutdownCount()})");
        Check(periodic.RunState == ScriptRunState.Idle, $"periodic script finished normally (state={periodic.RunState})");
        Check(elapsed < TimeSpan.FromMilliseconds(1500), $"stop waited only for the running execution ({elapsed.TotalMilliseconds:0} ms)");
    }

    private static async Task HungPeriodicScriptIsAbandonedAfterGrace(ScriptEngineSettings settings, ILogger logger)
    {
        Console.WriteLine("--- HungPeriodicScriptIsAbandonedAfterGrace");
        var (context, periodic, shutdownCount) = CreateContext(settings, logger, "import time\nwhile True:\n    time.sleep(0.01)", "period=50;unit=ms");

        await context.InitializeSharedScopeAsync(new List<IVariableBase>());
        await Task.Delay(150);
        Check(periodic.RunState == ScriptRunState.Running, "hung script is running at the stop request");

        var elapsed = await StopAsync(context);
        Check(context.HasAbandonedExecutions, "script still running after the grace abandons the context");
        Check(shutdownCount() == 0, $"shutdown scripts skipped on abandon (count={shutdownCount()})");
        Check(periodic.RunState == ScriptRunState.Faulted, $"hung script marked as interrupted (state={periodic.RunState})");
        Check(elapsed >= StopGracePeriod - TimeSpan.FromMilliseconds(200), $"stop waited the grace ({elapsed.TotalMilliseconds:0} ms)");
        Check(elapsed < StopGracePeriod + TimeSpan.FromSeconds(2), $"cooperative script ended right after the grace ({elapsed.TotalMilliseconds:0} ms)");

        var clean = context.CreateCleanContextForNextSession();
        Check(!clean.HasAbandonedExecutions, "clean context for the next session");
    }

    private static async Task ManualScriptIsStoppedCooperativelyOnSessionStop(ScriptEngineSettings settings, ILogger logger)
    {
        Console.WriteLine("--- ManualScriptIsStoppedCooperativelyOnSessionStop");
        var (context, _, shutdownCount) = CreateContext(settings, logger, "pass", "period=5;unit=s");
        var manual = new PyScript
        {
            FileName = "manual.py",
            ExecutionMode = ScriptExecutionMode.Manual,
            IsEnabled = true,
            Content = "import time\nwhile True:\n    time.sleep(0.01)"
        };
        context.AddScript(manual);

        await context.InitializeSharedScopeAsync(new List<IVariableBase>());
        var manualRun = context.ExecuteManualScriptAsync(manual);
        await Task.Delay(150);
        Check(manual.RunState == ScriptRunState.Running, "manual script is running at the stop request");

        var elapsed = await StopAsync(context);
        Check(!context.HasAbandonedExecutions, "manual script stopped via its stop token does not abandon the context");
        Check(shutdownCount() == 1, $"shutdown script ran (count={shutdownCount()})");
        Check(elapsed < TimeSpan.FromMilliseconds(1500), $"stop did not wait for the full grace ({elapsed.TotalMilliseconds:0} ms)");
        Check(await manualRun, "manual run reports a completed (cooperatively stopped) execution");
    }

    private static (ScriptingContext Context, PyScript Periodic, Func<int> ShutdownCount) CreateContext(
        ScriptEngineSettings settings,
        ILogger logger,
        string periodicContent,
        string periodicSettings)
    {
        var context = new ScriptingContext(settings, logger);
        var periodic = new PyScript
        {
            FileName = "periodic.py",
            ExecutionMode = ScriptExecutionMode.Periodic,
            IsEnabled = true,
            Content = periodicContent,
            AdditionalInfo = periodicSettings
        };
        var shutdown = new PyScript
        {
            FileName = "shutdown.py",
            ExecutionMode = ScriptExecutionMode.Shutdown,
            IsEnabled = true,
            Content = "pass"
        };
        context.AddScript(periodic);
        context.AddScript(shutdown);

        var shutdownCount = 0;
        context.ScriptExecuted += (_, e) =>
        {
            if (ReferenceEquals(e.Script, shutdown))
            {
                shutdownCount++;
            }
        };

        return (context, periodic, () => shutdownCount);
    }

    private static async Task<TimeSpan> StopAsync(ScriptingContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        await context.DisposeSharedScopeAsync();
        stopwatch.Stop();
        Console.WriteLine($"    stop took {stopwatch.Elapsed.TotalMilliseconds:0} ms");
        return stopwatch.Elapsed;
    }

    private static string? ResolvePythonDll()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("QENEX_PYTHON_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "Python", "pythoncore-3.13-64", "python313.dll");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static void Check(bool condition, string description)
    {
        Console.WriteLine($"    [{(condition ? "PASS" : "FAIL")}] {description}");
        if (!condition)
        {
            failures++;
        }
    }
}
