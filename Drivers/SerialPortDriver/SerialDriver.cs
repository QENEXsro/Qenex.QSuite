using System.IO.Ports;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.SerialPortDriver;

/// <summary>
/// Classic serial COM port driver (native port or a virtual one from an RS-485/USB converter).
/// Transports raw byte chunks: received data is pushed to every ProtocolBase&lt;byte[]&gt; protocol
/// (framing — e.g. Modbus RTU — is the protocol's job), transmitting protocols get their TX path
/// via ITransportProtocol&lt;byte[]&gt;, and operator writes are delegated to
/// IProtocolVariableWriteProtocol implementations (same pattern as the CAN driver).
/// One driver instance = one COM port. Reopens the port automatically after failures (USB unplug)
/// according to the shared <see cref="ReconnectPolicy"/> (reconnectDelayMs / reconnectAttempts).
/// </summary>
public class SerialDriver : DriverBase, IProtocolVariableCommandDriver, ITransportSource<byte[]>
{
    private string portName = string.Empty;
    private int baudRate = 9600;
    private int dataBits = 8;
    private Parity parity = Parity.None;
    private StopBits stopBits = StopBits.One;
    private Handshake handshake = Handshake.None;
    private const int DefaultReconnectDelayMs = 2000;
    private ReconnectPolicy reconnect = new(DefaultReconnectDelayMs);

    private SerialPort? serialPort;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public SerialDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "SerialPortDriver",
            Label = "Serial Port (COM)",
            Description = "Transports raw bytes over a serial COM port (RS-232/485).",
            CreatedOn = new DateTime(2026, 7, 10),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    public override string DefaultRawSettings =>
        "port=COM1;baudRate=9600;dataBits=8;parity=none;stopBits=1;handshake=none;"
        + ReconnectPolicy.SettingsTemplate(DefaultReconnectDelayMs);

    private static readonly string[] KnownSettings =
    [
        "port", "baudRate", "dataBits", "parity", "stopBits", "handshake",
        ReconnectPolicy.ReconnectDelayKey, ReconnectPolicy.ReconnectAttemptsKey
    ];

    // Settings example: port="COM3";baudRate="19200";dataBits="8";parity="even";stopBits="1";
    //                   handshake="none";reconnectDelayMs="2000";reconnectAttempts="-1"
    // reconnectAttempts: -1 = reopen forever (default), 0 = stop at the first failure,
    // N = give up after N consecutive failed reopen attempts (see ReconnectPolicy).
    public override void SetConfiguration()
    {
        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "Serial port driver");

        portName = GetString(settings, "port", portName);
        baudRate = GetInt(settings, "baudRate", baudRate);
        dataBits = GetInt(settings, "dataBits", dataBits);
        reconnect = ReconnectPolicy.Parse(settings, DefaultReconnectDelayMs, Logger, "Serial port driver");

        parity = GetString(settings, "parity", "none").ToLowerInvariant() switch
        {
            "none" => Parity.None,
            "even" => Parity.Even,
            "odd" => Parity.Odd,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            var other => LogInvalid("parity", other, Parity.None)
        };

        stopBits = GetString(settings, "stopBits", "1") switch
        {
            "1" => StopBits.One,
            "1.5" => StopBits.OnePointFive,
            "2" => StopBits.Two,
            var other => LogInvalid("stopBits", other, StopBits.One)
        };

        handshake = GetString(settings, "handshake", "none").ToLowerInvariant() switch
        {
            "none" => Handshake.None,
            "rts" => Handshake.RequestToSend,
            "xonxoff" => Handshake.XOnXOff,
            "rtsxonxoff" => Handshake.RequestToSendXOnXOff,
            var other => LogInvalid("handshake", other, Handshake.None)
        };

        // A freshly added driver arrives with empty settings — stay quiet until the user applies
        // something. Connecting without a port still fails properly (StartAsync goes Faulted).
        if (string.IsNullOrWhiteSpace(portName) && !string.IsNullOrWhiteSpace(RawSettings))
        {
            Logger?.Log(LogLevel.Warn, "Serial port driver: mandatory setting 'port' is missing.");
        }
    }

    private T LogInvalid<T>(string key, string value, T fallback)
    {
        Logger?.Log(LogLevel.Warn, $"Serial port driver: invalid setting {key}='{value}'; using {fallback}.");
        return fallback;
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

        if (string.IsNullOrWhiteSpace(portName))
        {
            SetState(CommunicationState.Faulted, "Serial port is not configured (setting 'port').");
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

        // Closing the port aborts a pending BaseStream read (SerialPort reads do not honor
        // cancellation tokens reliably).
        ClosePort();

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
                Logger?.Log(LogLevel.Warn, $"Serial port driver '{Label}' did not stop before timeout.");
            }
        }

        runCts?.Dispose();
        runCts = null;
        runTask = null;
    }

    public override void Dispose()
    {
        ClosePort();
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
                    OpenPort();
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

                    SetState(CommunicationState.Running,
                        $"{portName} @ {baudRate} bit/s, {dataBits}{ParityLetter()}{StopBitsText()}");
                    await ReadLoopAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested || exitRequested)
                    {
                        break;
                    }

                    if (!reconnect.RegisterFailure())
                    {
                        stopMessage = reconnect.GiveUpMessage(e.Message);
                        Logger?.Log(LogLevel.Error, $"Serial port driver '{Label}' ({portName}) {stopMessage}");
                        break;
                    }

                    Logger?.Log(reconnect.FailureLogLevel(),
                        $"Serial port driver '{Label}' ({portName}) failed ({reconnect.AttemptText}): {e.Message} "
                        + $"Reopening in {reconnect.ReconnectDelayMs} ms.");
                    SetState(CommunicationState.Faulted, $"{portName}: {e.Message}");
                    await Task.Delay(reconnect.ReconnectDelayMs, ct);
                }
                finally
                {
                    NotifyProtocolsTransportConnectionChanged(false);
                    SetTransmitters(null);
                    ClosePort();
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
                foreach (var protocol in Protocols)
                {
                    await protocol.StopAsync(CancellationToken.None);
                }

                SetTransmitters(null);
            }

            // A give-up keeps its reason visible in the driver state (and in the log as Error).
            SetState(CommunicationState.Stopped, stopMessage);
            exitRequested = false;
        }
    }

    private void OpenPort()
    {
        var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            Handshake = handshake,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000
        };
        port.Open();
        serialPort = port;
        Logger?.Log(LogLevel.Info,
            $"Serial port driver '{Label}' opened {portName} ({baudRate} bit/s, {dataBits}{ParityLetter()}{StopBitsText()}).");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var port = serialPort ?? throw new InvalidOperationException("Serial port is not open.");
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && !exitRequested)
        {
            var bytesRead = await port.BaseStream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
            {
                throw new IOException("Serial port stream ended.");
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

    private void ClosePort()
    {
        var port = serialPort;
        serialPort = null;
        if (port == null)
        {
            return;
        }

        try
        {
            port.Close();
            port.Dispose();
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"Serial port driver '{Label}' close returned: {e.Message}");
        }
    }

    private string ParityLetter() => parity switch
    {
        Parity.Even => "E",
        Parity.Odd => "O",
        Parity.Mark => "M",
        Parity.Space => "S",
        _ => "N"
    };

    private string StopBitsText() => stopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1"
    };

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
            throw new NotSupportedException("Serial port driver can only send byte[] data.");
        }

        await SendChunkAsync(bytes, ct);
    }

    private async Task SendChunkAsync(byte[] bytes, CancellationToken ct)
    {
        var port = serialPort ?? throw new InvalidOperationException("Serial port is not open.");

        await writeLock.WaitAsync(ct);
        try
        {
            await port.BaseStream.WriteAsync(bytes, ct);
            await port.BaseStream.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // Operator writes: the module wires variable value-changed notifications to this driver;
    // the driver delegates to the owning protocol, which executes the write as a protocol
    // transaction (e.g. Modbus SET + echo) over this driver's TX path.
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

    // On-request reads: the module wires read-requested notifications to this driver; the driver
    // delegates to the owning protocol (IProtocolVariableReadProtocol), which reads the variable
    // once over this driver's transport. Failures propagate so the requesting control sees them.
    public bool CanRequestRead(IProtocolVariable protocolVariable)
    {
        return Protocols
            .OfType<IProtocolVariableReadProtocol>()
            .Any(protocol => protocol.CanReadVariable(protocolVariable));
    }

    public async Task OnProtocolVariableReadRequestAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        foreach (var protocol in Protocols.OfType<IProtocolVariableReadProtocol>())
        {
            if (!protocol.CanReadVariable(protocolVariable))
            {
                continue;
            }

            await protocol.ReadVariableAsync(protocolVariable, ct);
            return;
        }

        throw new InvalidOperationException(
            $"No protocol of this driver can read variable '{protocolVariable.Variable?.Name}' on request.");
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
        Logger?.Log(LogLevel.Warn, $"Serial port: invalid value '{value}' for setting '{key}', using {defaultValue}.");
        return defaultValue;
    }

    #endregion
}
