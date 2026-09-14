using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.TcpServerDriver;

/// <summary>
/// Binary TCP server (listener) driver: accepts an incoming connection and transports raw byte
/// chunks — the transport for server-side protocols such as a Modbus TCP slave. One client is
/// served at a time; a new incoming connection replaces the current one (the common convention for
/// embedded Modbus TCP servers). Received data is pushed to every ProtocolBase&lt;byte[]&gt;
/// protocol; transmitting protocols (responses) get their TX path via ITransportProtocol&lt;byte[]&gt;.
/// </summary>
public class TcpServer : DriverBase, ITransportSource<byte[]>
{
    private string bindAddress = "0.0.0.0";
    private int port = 502;
    private const int DefaultReconnectDelayMs = 1000;
    private ReconnectPolicy reconnect = new(DefaultReconnectDelayMs);
    private int keepAliveMs = TcpKeepAlive.DefaultMs; // 0 = no keepalive probes on the accepted connection

    private TcpListener? listener;
    private TcpClient? currentClient;
    private NetworkStream? currentStream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public TcpServer()
    {
        Specification = new SpecificationBase
        {
            Name = "TcpServerDriver",
            Label = "TCP Server",
            Description = "Listens for one TCP connection and transports raw bytes.",
            CreatedOn = new DateTime(2026, 7, 10),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    public override string DefaultRawSettings =>
        "bindAddress=0.0.0.0;port=502;"
        + ReconnectPolicy.SettingsTemplate(DefaultReconnectDelayMs)
        + ";" + TcpKeepAlive.SettingsTemplate();

    private static readonly string[] KnownSettings =
    [
        "bindAddress", "port",
        ReconnectPolicy.ReconnectDelayKey, ReconnectPolicy.ReconnectAttemptsKey,
        TcpKeepAlive.Key
    ];

    // Settings example: bindAddress="0.0.0.0";port="502";reconnectDelayMs="1000";reconnectAttempts="-1";keepAliveMs="5000"
    // reconnectDelayMs / reconnectAttempts apply to opening the listening port (port already in use,
    // a specific bindAddress not available yet): -1 = retry forever (default), 0 = stop at the first
    // failure, N = give up after N failed retries (see ReconnectPolicy).
    // keepAliveMs: TCP keepalive probes on the accepted connection (see TcpKeepAlive); 0 = off.
    public override void SetConfiguration()
    {
        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "TCP server driver");
        bindAddress = GetString(settings, "bindAddress", bindAddress);
        port = GetInt(settings, "port", port);
        reconnect = ReconnectPolicy.Parse(settings, DefaultReconnectDelayMs, Logger, "TCP server driver");
        keepAliveMs = TcpKeepAlive.Parse(settings, Logger, "TCP server driver");
    }

    #region Driver control

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Stopped);
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

        CloseClient();
        listener?.Stop();

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
                Logger?.Log(LogLevel.Warn, $"TCP server driver '{Label}' did not stop before timeout.");
            }
        }

        runCts?.Dispose();
        runCts = null;
        runTask = null;
    }

    public override void Dispose()
    {
        CloseClient();
        listener?.Stop();
        runCts?.Dispose();
    }

    #endregion

    #region Run loop

    private async Task RunLoopAsync(CancellationToken ct)
    {
        string? stopMessage = null;
        try
        {
            stopMessage = await StartListenerAsync(ct);
            if (stopMessage != null)
            {
                return;
            }

            Logger?.Log(LogLevel.Info, $"TCP server driver '{Label}' listening on {bindAddress}:{port}.");

            SetTransmitters(SendChunkAsync);
            foreach (var protocol in Protocols)
            {
                await protocol.StartAsync(ct);
            }

            SetState(CommunicationState.Running, $"Listening on {bindAddress}:{port}.");

            while (!ct.IsCancellationRequested && !exitRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
                {
                    break;
                }

                // One client at a time: a newer connection replaces the current one.
                CloseClient();
                currentClient = client;
                currentClient.NoDelay = true;
                TcpKeepAlive.Apply(client.Client, keepAliveMs);
                currentStream = client.GetStream();
                var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                Logger?.Log(LogLevel.Info, $"TCP server driver '{Label}': client {endpoint} connected.");
                SetState(CommunicationState.Running, $"Client {endpoint} connected.");
                NotifyProtocolsTransportConnectionChanged(true);

                try
                {
                    await ReadLoopAsync(currentStream, ct);
                    Logger?.Log(LogLevel.Info, $"TCP server driver '{Label}': client {endpoint} disconnected.");
                    SetState(CommunicationState.Running, $"Listening on {bindAddress}:{port}.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
                {
                    break;
                }
                catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
                {
                    // The connection broke without the client closing it (keepalive probes unanswered,
                    // connection reset): report it and go back to listening for the next client.
                    Logger?.Log(LogLevel.Warn, $"TCP server driver '{Label}': client {endpoint} lost ({e.Message}).");
                    SetState(CommunicationState.Running, $"Listening on {bindAddress}:{port}.");
                }
                finally
                {
                    NotifyProtocolsTransportConnectionChanged(false);
                    CloseClient();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"TCP server driver '{Label}' failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
        }
        finally
        {
            foreach (var protocol in Protocols)
            {
                await protocol.StopAsync(CancellationToken.None);
            }

            SetTransmitters(null);
            CloseClient();
            listener?.Stop();
            listener = null;

            if (stopMessage != null)
            {
                // Gave up opening the port: the reason stays visible in the driver state.
                SetState(CommunicationState.Stopped, stopMessage);
            }
            else if (State != CommunicationState.Faulted)
            {
                SetState(CommunicationState.Stopped);
            }

            exitRequested = false;
        }
    }

    /// <summary>
    /// Opens the listening port, retrying according to the reconnect policy (port in use, bind
    /// address not available). Returns null on success, or the give-up message (already logged as
    /// Error) when the policy is exhausted.
    /// </summary>
    private async Task<string?> StartListenerAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                listener = new TcpListener(IPAddress.Parse(bindAddress), port);
                listener.Start();
                reconnect.ResetAfterSuccess();
                return null;
            }
            catch (Exception e) when (e is SocketException or FormatException)
            {
                listener = null;
                if (!reconnect.RegisterFailure())
                {
                    var giveUp = reconnect.GiveUpMessage(e.Message);
                    Logger?.Log(LogLevel.Error, $"TCP server driver '{Label}' ({bindAddress}:{port}) {giveUp}");
                    return giveUp;
                }

                Logger?.Log(reconnect.FailureLogLevel(),
                    $"TCP server driver '{Label}' ({bindAddress}:{port}) failed ({reconnect.AttemptText}): {e.Message} "
                    + $"Retrying in {reconnect.ReconnectDelayMs} ms.");
                SetState(CommunicationState.Faulted, $"{bindAddress}:{port}: {e.Message}");
                await Task.Delay(reconnect.ReconnectDelayMs, ct);
            }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && !exitRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
            {
                return; // client closed the connection
            }

            var chunk = buffer.AsSpan(0, bytesRead).ToArray();
            foreach (var protocol in Protocols)
            {
                if (protocol is ProtocolBase<byte[]> byteProtocol)
                {
                    await byteProtocol.AddReceivedDataToQueueAsync([chunk], ct);
                }
            }
        }
    }

    private void SetTransmitters(Func<byte[], CancellationToken, Task>? transmitter)
    {
        foreach (var protocol in Protocols)
        {
            if (protocol is ITransportProtocol<byte[]> transportProtocol)
            {
                transportProtocol.SetTransmitter(transmitter);
            }
        }
    }

    private void CloseClient()
    {
        currentStream = null;
        currentClient?.Close();
        currentClient?.Dispose();
        currentClient = null;
    }

    #endregion

    #region Communication

    public override void Send<T>(T data)
    {
        SendAsync(data).GetAwaiter().GetResult();
    }

    public override async Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        if (data is not byte[] bytes)
        {
            throw new NotSupportedException("TCP server driver can only send byte[] data.");
        }

        await SendChunkAsync(bytes, ct);
    }

    private async Task SendChunkAsync(byte[] bytes, CancellationToken ct)
    {
        var stream = currentStream ?? throw new InvalidOperationException("No TCP client is connected.");

        await writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    #endregion

    #region Configuration helpers

    private static string GetString(IReadOnlyDictionary<string, string> settings, string key, string defaultValue)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;
    }

    private int GetInt(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
    {
        if (!settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsedValue))
        {
            return parsedValue;
        }

        // A typo must not pass silently — the driver would run with a value the operator never chose.
        Logger?.Log(LogLevel.Warn, $"TCP server: invalid value '{value}' for setting '{key}', using {defaultValue}.");
        return defaultValue;
    }

    #endregion
}
