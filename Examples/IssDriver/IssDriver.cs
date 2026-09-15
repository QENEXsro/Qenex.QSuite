using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Examples.IssDriver;

/// <summary>
/// Example driver: reads live data from a public REST API — the current position of the
/// International Space Station. Every period it performs one HTTP GET and hands the raw JSON
/// response to its protocols; turning the JSON into variable values is the protocol's job
/// (see the IssJsonProtocol example).
/// This is about the smallest possible real-data driver: read-only, one fixed URL, one setting
/// of its own — plus the shared settings parser and reconnect policy every QENEX driver uses,
/// so the driver behaves in Project Configuration and at runtime like the built-in ones.
/// </summary>
public class IssDriver : DriverBase, ITransportSource<string>
{
    // Free, key-less API returning one flat JSON object with the current ISS position.
    // Be polite to the public service: do not poll faster than ~1 request per second.
    private const string Url = "https://api.wheretheiss.at/v1/satellites/25544";
    private const int MinPeriodMs = 1000;

    // The internet is a flaky transport: wait a few seconds before the next attempt.
    private const int DefaultReconnectDelayMs = 5000;

    private int periodMs = MinPeriodMs;
    private ReconnectPolicy reconnect = new(DefaultReconnectDelayMs);

    private HttpClient? httpClient;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public IssDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "IssDriver",
            Label = "ISS Position (example)",
            Description = "Example driver polling the ISS position from a public REST API.",
            CreatedOn = new DateTime(2026, 7, 27),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #region Configuration

    // Shown pre-filled when the driver is added in Project Configuration. The reconnect part
    // (reconnectDelayMs / reconnectAttempts) comes from the shared template, so it reads the
    // same as in every built-in driver: -1 = retry forever, 0 = stop at the first failure,
    // N = give up after N consecutive failures.
    public override string DefaultRawSettings =>
        $"periodMs={MinPeriodMs};" + ReconnectPolicy.SettingsTemplate(DefaultReconnectDelayMs);

    private static readonly string[] KnownSettings =
    [
        "periodMs", ReconnectPolicy.ReconnectDelayKey, ReconnectPolicy.ReconnectAttemptsKey
    ];

    public override void SetConfiguration()
    {
        // The shared parser splits "key=value;..." and warns about every key it does not know —
        // a typo in Project Configuration is reported instead of being ignored silently.
        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "ISS position driver");

        if (settings.TryGetValue("periodMs", out var periodText))
        {
            if (int.TryParse(periodText, out var parsedPeriod))
            {
                // The API asks for at most ~1 request per second, so slower is allowed, faster is not.
                periodMs = Math.Max(parsedPeriod, MinPeriodMs);
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"ISS position driver: invalid periodMs '{periodText}', using {periodMs} ms.");
            }
        }

        reconnect = ReconnectPolicy.Parse(settings, DefaultReconnectDelayMs, Logger, "ISS position driver");
    }

    #endregion

    #region Driver control

    public override async Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return;
        }

        if (State == CommunicationState.Running || runTask is { IsCompleted: false })
        {
            return;
        }

        SetState(CommunicationState.Starting);

        // Short timeout so a slow or unreachable server cannot block the loop for long.
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        foreach (var protocol in Protocols)
        {
            await protocol.StartAsync(ct);
        }

        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: the loop must not inherit the caller's (UI) SynchronizationContext —
        // a blocked dispatcher (window drag, busy UI) would stall the whole session.
        // Capture the token now: a racing StopAsync may null runCts before the loop starts.
        var runToken = runCts.Token;
        runTask = Task.Run(() => RunLoopAsync(runToken));
        SetState(CommunicationState.Running);
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);

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
                Logger?.Log(LogLevel.Warn, "ISS position: the polling loop did not stop before timeout.");
            }
        }

        foreach (var protocol in Protocols)
        {
            await protocol.StopAsync(ct);
        }

        httpClient?.Dispose();
        httpClient = null;
        runCts?.Dispose();
        runCts = null;
        runTask = null;
        SetState(CommunicationState.Stopped);
    }

    public override void Dispose()
    {
        httpClient?.Dispose();
        runCts?.Dispose();
    }

    #endregion

    #region Communication

    // The API is read-only, so there is nothing to send. A writable driver would push the
    // encoded data to the device here (see the TempSensorDriver example).
    public override void Send<T>(T data)
    {
    }

    public override Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Polling loop

    private async Task RunLoopAsync(CancellationToken ct)
    {
        string? stopMessage = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // One GET returns one JSON object carrying several signals at once
                    // (latitude, longitude, altitude, velocity, ...).
                    var json = await httpClient!.GetStringAsync(Url, ct);

                    // Back online after a failure: clear the failure counter and the Faulted state.
                    reconnect.ResetAfterSuccess();
                    if (State != CommunicationState.Running)
                    {
                        SetState(CommunicationState.Running);
                    }

                    foreach (var protocol in Protocols)
                    {
                        if (protocol is ProtocolBase<string> textProtocol)
                        {
                            await textProtocol.AddReceivedDataToQueueAsync([json], ct);
                        }
                    }

                    await Task.Delay(periodMs, ct);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // Network hiccups are normal with a public API. The reconnect policy decides
                    // whether to keep trying (Faulted, retry after reconnectDelayMs) or to give up
                    // (Stopped with the reason; the Start button in the driver's Properties restarts it).
                    if (!reconnect.RegisterFailure())
                    {
                        stopMessage = reconnect.GiveUpMessage(e.Message);
                        Logger?.Log(LogLevel.Error, $"ISS position driver '{Label}' {stopMessage}");
                        break;
                    }

                    Logger?.Log(reconnect.FailureLogLevel(),
                        $"ISS position driver '{Label}' request failed ({reconnect.AttemptText}): {e.Message} "
                        + $"Retrying in {reconnect.ReconnectDelayMs} ms.");
                    SetState(CommunicationState.Faulted, e.Message);
                    await Task.Delay(reconnect.ReconnectDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"ISS position loop failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
            return;
        }

        if (stopMessage != null)
        {
            // Gave up: stop the protocols and keep the reason visible in the driver state.
            foreach (var protocol in Protocols)
            {
                await protocol.StopAsync(CancellationToken.None);
            }

            SetState(CommunicationState.Stopped, stopMessage);
        }
    }

    #endregion
}
