using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QSuite.Tests.ReconnectPolicyTest;

/// <summary>Console-style unit tests of the shared driver reconnect policy (repo convention,
/// same as the other _Tests projects). Exit code 0 = all passed.</summary>
internal static class Program
{
    private static int failures;

    private static int Main()
    {
        ParseDefaults();
        ParseValuesAndAliases();
        ParseInvalidValueFallsBack();
        ForeverNeverGivesUp();
        ZeroStopsAtFirstFailure();
        LimitedGivesUpAfterNRetries();
        SuccessResetsTheRun();
        LoggingFiniteEveryAttemptEndlessThrottled();

        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void ParseDefaults()
    {
        var policy = ReconnectPolicy.Parse(new Dictionary<string, string>(), 2000, null, "test");
        Check(policy.ReconnectTimeMs == 2000, "default reconnect time");
        Check(policy.NumberOfReconnections == ReconnectPolicy.RetryForever, "default = retry forever");
        Check(policy.RetriesForever, "RetriesForever for default");
        Check(ReconnectPolicy.SettingsTemplate(2000) == "reconnectTimeMs=2000;numberOfReconnections=-1", "settings template text");
    }

    private static void ParseValuesAndAliases()
    {
        var policy = ReconnectPolicy.Parse(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["reconnectTimeMs"] = "500", ["numberOfReconnections"] = "4" },
            2000, null, "test");
        Check(policy.ReconnectTimeMs == 500 && policy.NumberOfReconnections == 4, "explicit values parsed");

        var legacy = ReconnectPolicy.Parse(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["reconnectTime"] = "300", ["reconnections"] = "2" },
            2000, null, "test");
        Check(legacy.ReconnectTimeMs == 300 && legacy.NumberOfReconnections == 2, "legacy TCP client aliases accepted");

        var clamped = new ReconnectPolicy(0, -5);
        Check(clamped.ReconnectTimeMs == 1 && clamped.NumberOfReconnections == ReconnectPolicy.RetryForever, "values clamped");
    }

    private static void ParseInvalidValueFallsBack()
    {
        var logger = new CapturingLogger();
        var policy = ReconnectPolicy.Parse(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["numberOfReconnections"] = "many" },
            2000, logger, "test driver");
        Check(policy.NumberOfReconnections == ReconnectPolicy.RetryForever, "invalid value falls back to the default");
        Check(logger.Messages.Count == 1 && logger.Messages[0].Level == LogLevel.Warn && logger.Messages[0].Text.Contains("'many'"),
            "invalid value is reported as a warning");
    }

    private static void ForeverNeverGivesUp()
    {
        var policy = new ReconnectPolicy(100);
        var allRetry = true;
        for (var i = 0; i < 1000; i++)
        {
            allRetry &= policy.RegisterFailure();
        }

        Check(allRetry && policy.ConsecutiveFailures == 1000, "-1 retries forever");
        Check(policy.AttemptText == "attempt 1000", "attempt text without limit");
    }

    private static void ZeroStopsAtFirstFailure()
    {
        var policy = new ReconnectPolicy(100, ReconnectPolicy.NoRetry);
        Check(!policy.RegisterFailure(), "0 gives up at the first failure");
        Check(policy.GiveUpMessage("port missing").StartsWith("stopped at the first failure"), "give-up text for 0");
    }

    private static void LimitedGivesUpAfterNRetries()
    {
        var policy = new ReconnectPolicy(100, 3);
        Check(policy.RegisterFailure(), "failure 1 allows retry 1");
        Check(policy.RegisterFailure(), "failure 2 allows retry 2");
        Check(policy.RegisterFailure(), "failure 3 allows retry 3");
        Check(!policy.RegisterFailure(), "failure 4 gives up (3 retries used)");
        Check(policy.AttemptText == "attempt 4/4", "attempt text with limit counts the first try");
        Check(policy.GiveUpMessage("x") == "gave up after 3 reconnection attempt(s): x", "give-up text for N");
    }

    private static void SuccessResetsTheRun()
    {
        var policy = new ReconnectPolicy(100, 2);
        policy.RegisterFailure();
        policy.RegisterFailure();
        policy.ResetAfterSuccess();
        Check(policy.ConsecutiveFailures == 0, "success resets the counter");
        Check(policy.RegisterFailure() && policy.RegisterFailure() && !policy.RegisterFailure(),
            "after a success the full limit is available again");
    }

    private static void LoggingFiniteEveryAttemptEndlessThrottled()
    {
        var finite = new ReconnectPolicy(100, 8);
        var finiteLevels = new List<LogLevel>();
        for (var i = 0; i < 8; i++)
        {
            finite.RegisterFailure();
            finiteLevels.Add(finite.FailureLogLevel());
        }

        Check(finiteLevels.All(level => level == LogLevel.Warn), "finite limit: every attempt is a warning");

        var endless = new ReconnectPolicy(100);
        var endlessLevels = new List<LogLevel>();
        for (var i = 0; i < 10; i++)
        {
            endless.RegisterFailure();
            endlessLevels.Add(endless.FailureLogLevel());
        }

        Check(endlessLevels.Take(3).All(level => level == LogLevel.Warn), "endless: first three attempts are warnings");
        Check(endlessLevels.Skip(3).All(level => level == LogLevel.Debug), "endless: later attempts within a minute are debug");

        endless.ResetAfterSuccess();
        endless.RegisterFailure();
        Check(endless.FailureLogLevel() == LogLevel.Warn, "endless: a new run after success warns again");
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            return;
        }

        failures++;
        Console.WriteLine($"FAILED: {description}");
    }

    private sealed class CapturingLogger : ILogger
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
}
