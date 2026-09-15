using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;

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
        SettingsParserReportsUnknownKeys();
        KeepAliveParseAndApply();

        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void ParseDefaults()
    {
        var policy = ReconnectPolicy.Parse(new Dictionary<string, string>(), 2000, null, "test");
        Check(policy.ReconnectDelayMs == 2000, "default reconnect time");
        Check(policy.ReconnectAttempts == ReconnectPolicy.RetryForever, "default = retry forever");
        Check(policy.RetriesForever, "RetriesForever for default");
        Check(ReconnectPolicy.SettingsTemplate(2000) == "reconnectDelayMs=2000;reconnectAttempts=-1", "settings template text");
    }

    private static void ParseValuesAndAliases()
    {
        var policy = ReconnectPolicy.Parse(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["reconnectDelayMs"] = "500", ["reconnectAttempts"] = "4" },
            2000, null, "test");
        Check(policy.ReconnectDelayMs == 500 && policy.ReconnectAttempts == 4, "explicit values parsed");

        var former = ReconnectPolicy.Parse(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["reconnectTimeMs"] = "300", ["numberOfReconnections"] = "2" },
            2000, null, "test");
        Check(former.ReconnectDelayMs == 2000 && former.ReconnectAttempts == ReconnectPolicy.RetryForever,
            "former names (reconnectTimeMs / numberOfReconnections) are not read any more");

        var clamped = new ReconnectPolicy(0, -5);
        Check(clamped.ReconnectDelayMs == 1 && clamped.ReconnectAttempts == ReconnectPolicy.RetryForever, "values clamped");
    }

    private static void ParseInvalidValueFallsBack()
    {
        var logger = new CapturingLogger();
        var policy = ReconnectPolicy.Parse(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["reconnectAttempts"] = "many" },
            2000, logger, "test driver");
        Check(policy.ReconnectAttempts == ReconnectPolicy.RetryForever, "invalid value falls back to the default");
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
        Check(policy.GiveUpMessage("x") == "gave up after 3 reconnect attempt(s): x", "give-up text for N");
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

    private static void SettingsParserReportsUnknownKeys()
    {
        var logger = new CapturingLogger();
        var settings = SettingsParser.Parse(
            "port=\"COM6\"; BaudRate = 9600;timeoutMs=2000;retries=10;broken",
            ["port", "baudRate"], logger, "Serial port driver");

        Check(settings.Count == 4 && settings["port"] == "COM6" && settings["baudrate"] == "9600",
            "parser: quotes and whitespace stripped, keys case-insensitive, entry without '=' skipped");
        Check(logger.Messages.Count == 1 && logger.Messages[0].Level == LogLevel.Warn,
            "parser: one warning for the unknown keys");
        Check(logger.Messages.Count == 1
              && logger.Messages[0].Text.Contains("'timeoutMs'") && logger.Messages[0].Text.Contains("'retries'")
              && !logger.Messages[0].Text.Contains("'port'") && logger.Messages[0].Text.Contains("Known settings: port, baudRate"),
            "parser: warning names the unknown keys and lists the known ones");

        var clean = new CapturingLogger();
        SettingsParser.Parse("port=COM1;baudRate=9600", ["port", "baudRate"], clean, "Serial port driver");
        Check(clean.Messages.Count == 0, "parser: no warning when every key is known");

        // A plugin without settings (Simulation / Virtual drivers and protocols): a legacy key such as
        // periodes= is reported too, with a message that says the plugin takes no settings at all.
        var none = new CapturingLogger();
        SettingsParser.Parse("periodes=20", [], none, "Simulation driver");
        Check(none.Messages.Count == 1 && none.Messages[0].Level == LogLevel.Warn
              && none.Messages[0].Text.Contains("'periodes'") && none.Messages[0].Text.Contains("has no settings"),
            "parser: plugin without settings warns and says so");

        var noneClean = new CapturingLogger();
        SettingsParser.Parse("", [], noneClean, "Simulation driver");
        Check(noneClean.Messages.Count == 0, "parser: plugin without settings and empty text stays quiet");
    }

    private static void KeepAliveParseAndApply()
    {
        var logger = new CapturingLogger();
        Check(TcpKeepAlive.Parse(new Dictionary<string, string>(), logger, "test") == TcpKeepAlive.DefaultMs, "keepalive: default 5000");
        Check(TcpKeepAlive.Parse(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["keepAliveMs"] = "0" }, logger, "test") == 0, "keepalive: 0 = off");
        Check(TcpKeepAlive.Parse(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["keepAliveMs"] = "x" }, logger, "test") == TcpKeepAlive.DefaultMs
              && logger.Messages.Count == 1, "keepalive: invalid value warns and falls back");
        Check(TcpKeepAlive.SettingsTemplate() == "keepAliveMs=5000", "keepalive: template text");

        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        TcpKeepAlive.Apply(socket, 5000);
        var on = (int)socket.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive)! != 0;
        var time = (int)socket.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime)!;
        Check(on && time == 5, "keepalive: applied to the socket (on, 5 s idle)");
        TcpKeepAlive.Apply(socket, 0);
        var off = (int)socket.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive)! == 0;
        Check(off, "keepalive: 0 switches the socket option off");
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
