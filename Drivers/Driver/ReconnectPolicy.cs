using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QSuite.Drivers.Driver;

/// <summary>
/// Shared reconnect behaviour of the transport drivers (serial, TCP client, TCP server, CAN), so
/// every driver reads the same two settings and reacts the same way to a lost link:
/// <list type="bullet">
/// <item><c>reconnectDelayMs</c> — pause between two connection attempts.</item>
/// <item><c>reconnectAttempts</c> — how many times to retry after a failed attempt:
/// <c>-1</c> (default) retries forever, <c>0</c> never retries (the driver stops at the first
/// failure), <c>N</c> gives up after N consecutive failed retries.</item>
/// </list>
/// The failure counter restarts after every successful connection, so a long test survives
/// scattered outages; only an unbroken run of failures exhausts the limit. Logging: with a finite
/// limit every failed attempt is a warning (the operator watches the countdown); with an endless
/// retry the first attempts are warnings and then one warning per minute carrying the attempt
/// number, so the log shows the driver is alive without being flooded overnight.
/// </summary>
public sealed class ReconnectPolicy
{
    public const string ReconnectDelayKey = "reconnectDelayMs";
    public const string ReconnectAttemptsKey = "reconnectAttempts";

    public const int RetryForever = -1;
    public const int NoRetry = 0;

    private const int FullyLoggedFailures = 3;
    private const long ThrottledLogIntervalMs = 60_000;

    private long lastWarnedAtMs = long.MinValue;

    public ReconnectPolicy(int reconnectDelayMs, int reconnectAttempts = RetryForever)
    {
        ReconnectDelayMs = Math.Max(1, reconnectDelayMs);
        ReconnectAttempts = Math.Max(RetryForever, reconnectAttempts);
    }

    /// <summary>Pause between two connection attempts.</summary>
    public int ReconnectDelayMs { get; }

    /// <summary>-1 = forever, 0 = never, N = that many consecutive retries.</summary>
    public int ReconnectAttempts { get; }

    public bool RetriesForever => ReconnectAttempts == RetryForever;

    /// <summary>Consecutive failed attempts since the last successful connection.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Text for the default settings template of a driver.</summary>
    public static string SettingsTemplate(int reconnectDelayMs, int reconnectAttempts = RetryForever)
        => $"{ReconnectDelayKey}={reconnectDelayMs};{ReconnectAttemptsKey}={reconnectAttempts}";

    /// <summary>
    /// Reads the two settings from a parsed key=value dictionary. An unparsable value is
    /// reported and the default used, so the driver never runs with a value the operator did
    /// not choose.
    /// </summary>
    public static ReconnectPolicy Parse(
        IReadOnlyDictionary<string, string> settings,
        int defaultReconnectDelayMs,
        ILogger? logger,
        string driverName,
        int defaultReconnectAttempts = RetryForever)
    {
        var reconnectDelayMs = GetInt(settings, ReconnectDelayKey, defaultReconnectDelayMs, logger, driverName);
        var reconnectAttempts = GetInt(settings, ReconnectAttemptsKey, defaultReconnectAttempts, logger, driverName);
        return new ReconnectPolicy(reconnectDelayMs, reconnectAttempts);
    }

    /// <summary>Call after a successful connection: the failure run starts over.</summary>
    public void ResetAfterSuccess()
    {
        ConsecutiveFailures = 0;
        lastWarnedAtMs = long.MinValue;
    }

    /// <summary>
    /// Registers one failed attempt. Returns true when the driver should wait and try again,
    /// false when the retry limit is exhausted and the driver must stop.
    /// </summary>
    public bool RegisterFailure()
    {
        ConsecutiveFailures++;
        return RetriesForever || ConsecutiveFailures <= ReconnectAttempts;
    }

    /// <summary>
    /// Log level for the current failure. Finite limit: always a warning. Endless retry: warning
    /// for the first few failures and then once a minute, debug in between. Call once per
    /// registered failure.
    /// </summary>
    public LogLevel FailureLogLevel()
    {
        if (!RetriesForever)
        {
            return LogLevel.Warn;
        }

        var now = Environment.TickCount64;
        if (ConsecutiveFailures <= FullyLoggedFailures || now - lastWarnedAtMs >= ThrottledLogIntervalMs)
        {
            lastWarnedAtMs = now;
            return LogLevel.Warn;
        }

        return LogLevel.Debug;
    }

    /// <summary>"attempt 3" (forever) or "attempt 3/5" (limited), for log messages.</summary>
    public string AttemptText => RetriesForever
        ? $"attempt {ConsecutiveFailures}"
        : $"attempt {ConsecutiveFailures}/{ReconnectAttempts + 1}";

    /// <summary>Message logged (Error) and set as state message when the driver gives up.</summary>
    public string GiveUpMessage(string lastError)
        => ReconnectAttempts == NoRetry
            ? $"stopped at the first failure ({ReconnectAttemptsKey}=0): {lastError}"
            : $"gave up after {ReconnectAttempts} reconnect attempt(s): {lastError}";

    private static int GetInt(
        IReadOnlyDictionary<string, string> settings,
        string key,
        int defaultValue,
        ILogger? logger,
        string driverName)
    {
        if (!settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        logger?.Log(LogLevel.Warn, $"{driverName}: invalid value '{value}' for setting '{key}', using {defaultValue}.");
        return defaultValue;
    }
}
