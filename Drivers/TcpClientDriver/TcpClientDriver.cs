using System.Net.Sockets;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.TcpClientDriver;

/// <summary>
/// Unified bidirectional TCP client driver: connects out to a server and transports raw byte
/// chunks. Whether those bytes are text lines (JSON protocols decode them via TextLineFramer) or
/// binary frames (Modbus TCP) is the protocol's concern — the driver carries bytes only.
/// Received chunks are pushed to every ProtocolBase&lt;byte[]&gt; protocol, transmitting protocols
/// get their TX path via ITransportProtocol&lt;byte[]&gt;, and operator writes are delegated to
/// IProtocolVariableWriteProtocol implementations (same pattern as the CAN and serial drivers).
/// Reconnects after a failure according to the shared <see cref="ReconnectPolicy"/>
/// (reconnectDelayMs / reconnectAttempts, -1 = forever), the same way as the serial driver.
/// </summary>
public class TcpClientDriver : DriverBase, IProtocolVariableCommandDriver, ITransportSource<byte[]>
{
    private string host = "127.0.0.1";
    private int port = 5000;
    private int connectionTimeoutMs = 5000;
    private const int DefaultReconnectDelayMs = 1000;
    private ReconnectPolicy reconnect = new(DefaultReconnectDelayMs);
    private int keepAliveMs = TcpKeepAlive.DefaultMs; // 0 = no keepalive probes (see TcpKeepAlive)

    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public TcpClientDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "TcpClientDriver",
            Label = "TCP Client",
            Description = "Connects to a device over TCP and transports raw bytes.",
            CreatedOn = new DateTime(2026, 6, 1),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // A quiet line is never treated as a fault here (request/response protocols such as Modbus
    // TCP are silent between polls); a dead peer is detected by the TCP keepalive probes.
    public override string DefaultRawSettings =>
        "ip=127.0.0.1;port=5000;connectionTimeoutMs=5000;"
        + ReconnectPolicy.SettingsTemplate(DefaultReconnectDelayMs)
        + ";" + TcpKeepAlive.SettingsTemplate();

    private static readonly string[] KnownSettings =
    [
        "ip", "port", "connectionTimeoutMs",
        ReconnectPolicy.ReconnectDelayKey, ReconnectPolicy.ReconnectAttemptsKey,
        TcpKeepAlive.Key
    ];

    // Settings example: ip="127.0.0.1";port="5000";connectionTimeoutMs="5000";reconnectDelayMs="1000";
    //                   reconnectAttempts="-1";keepAliveMs="5000"
    // keepAliveMs: TCP keepalive probes after that much silence (dead cable / vanished server is
    // detected within a few seconds instead of the OS retransmission timeout); 0 = off.
    // reconnectAttempts: -1 = reconnect forever (default), 0 = stop at the first failure,
    // N = give up after N consecutive failed attempts (see ReconnectPolicy).
    public override void SetConfiguration()
    {
        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "TCP client driver");
        host = GetString(settings, "ip", host);
        port = GetInt(settings, "port", port);
        connectionTimeoutMs = Math.Max(1, GetInt(settings, "connectionTimeoutMs", connectionTimeoutMs));
        reconnect = ReconnectPolicy.Parse(settings, DefaultReconnectDelayMs, Logger, "TCP client driver");
        keepAliveMs = TcpKeepAlive.Parse(settings, Logger, "TCP client driver");
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

        // Protocols first, while the connection is up and the read loop still delivers data: an
        // XCP master sends DISCONNECT on stop and needs the slave's response. Closing the socket
        // first cut that response off and every stop ended with a DISCONNECT timeout. The loop
        // teardown below stops the protocols again (idempotent) for the abnormal paths.
        if (runTask is { IsCompleted: false } && stream != null)
        {
            await StopProtocolsAsync();
        }

        exitRequested = true;

        if (runCts != null)
        {
            await runCts.CancelAsync();
        }

        CloseClient();

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
                Logger?.Log(LogLevel.Warn, $"TCP client driver '{Label}' did not stop before timeout.");
            }
        }

        runCts?.Dispose();
        runCts = null;
        runTask = null;
    }

    public override void Dispose()
    {
        CloseClient();
        runCts?.Dispose();
    }

    #endregion

    #region Run loop

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var protocolsStarted = false;
        string? stopMessage = null;
        try
        {
            while (!ct.IsCancellationRequested && !exitRequested)
            {
                try
                {
                    await ConnectAsync(ct);
                    reconnect.ResetAfterSuccess();
                    SetTransmitters(SendChunkAsync);
                    NotifyProtocolsTransportConnectionChanged(true);

                    if (!protocolsStarted)
                    {
                        foreach (var protocol in Protocols)
                        {
                            await protocol.StartAsync(ct);
                        }

                        protocolsStarted = true;
                    }

                    SetState(CommunicationState.Running, $"Connected to {host}:{port}.");
                    await ReadLoopAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
                {
                    break;
                }
                catch (Exception e) when (e is SocketException or IOException or TimeoutException or ObjectDisposedException)
                {
                    if (ct.IsCancellationRequested || exitRequested)
                    {
                        break;
                    }

                    if (!reconnect.RegisterFailure())
                    {
                        stopMessage = reconnect.GiveUpMessage(e.Message);
                        Logger?.Log(LogLevel.Error, $"TCP client '{Label}' ({host}:{port}) {stopMessage}");
                        break;
                    }

                    Logger?.Log(reconnect.FailureLogLevel(),
                        $"TCP client '{Label}' ({host}:{port}) failed ({reconnect.AttemptText}): {e.Message} "
                        + $"Reconnecting in {reconnect.ReconnectDelayMs} ms.");
                    SetState(CommunicationState.Faulted, $"{host}:{port}: {e.Message}");
                    await Task.Delay(reconnect.ReconnectDelayMs, ct);
                }
                finally
                {
                    NotifyProtocolsTransportConnectionChanged(false);
                    SetTransmitters(null);
                    CloseClient();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        finally
        {
            if (protocolsStarted)
            {
                await StopProtocolsAsync();
                SetTransmitters(null);
            }

            // A give-up keeps its reason visible in the driver state (and in the log as Error).
            SetState(CommunicationState.Stopped, stopMessage);
            exitRequested = false;
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(connectionTimeoutMs), ct);
            TcpKeepAlive.Apply(client.Client, keepAliveMs);
            tcpClient = client;
            stream = client.GetStream();
            Logger?.Log(LogLevel.Info, $"TCP client '{Label}' connected to {host}:{port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var currentStream = stream ?? throw new InvalidOperationException("TCP client is not connected.");
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && !exitRequested)
        {
            var bytesRead = await currentStream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
            {
                throw new IOException("The server closed the connection.");
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
        stream = null;
        tcpClient?.Close();
        tcpClient?.Dispose();
        tcpClient = null;
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
            throw new NotSupportedException("TCP client driver can only send byte[] data.");
        }

        await SendChunkAsync(bytes, ct);
    }

    private async Task SendChunkAsync(byte[] bytes, CancellationToken ct)
    {
        var currentStream = stream ?? throw new InvalidOperationException("TCP client is not connected.");

        await writeLock.WaitAsync(ct);
        try
        {
            await currentStream.WriteAsync(bytes, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // Operator writes: delegated to the owning protocol (same pattern as the CAN and serial drivers).
    public bool CanSendCommand(IProtocolVariable protocolVariable)
    {
        return Protocols
            .OfType<IProtocolVariableWriteProtocol>()
            .Any(protocol => protocol.CanWriteVariable(protocolVariable));
    }

    public async Task OnProtocolVariableCommandAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        foreach (var protocol in Protocols.OfType<IProtocolVariableWriteProtocol>())
        {
            if (!protocol.CanWriteVariable(protocolVariable))
            {
                continue;
            }

            await protocol.WriteVariableAsync(protocolVariable, ct);
            return;
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
        Logger?.Log(LogLevel.Warn, $"TCP client: invalid value '{value}' for setting '{key}', using {defaultValue}.");
        return defaultValue;
    }

    #endregion
}
