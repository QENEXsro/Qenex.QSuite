using System.Globalization;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Examples.TempSensorDriver;

/// <summary>
/// Example driver: simulates a temperature sensor device with a few channels. Every period it
/// produces one text line per channel ("ch1=23.47") and hands the lines to its protocols.
/// An operator write coming back through Send re-bases the channel's temperature, so the whole
/// read/write chain can be tried without any hardware.
/// </summary>
public class TempSensorDriver : DriverBase, IProtocolVariableCommandDriver, ITransportSource<string>
{
    private int periodMs = 500;
    private int channelCount = 2;

    // The simulated device state: current temperature of every channel.
    private readonly object channelLock = new();
    private double[] channelTemps = [];

    private readonly Random random = new();
    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public TempSensorDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "TempSensorDriver",
            Label = "Temperature Sensor (example)",
            Description = "Example driver simulating a multi-channel temperature sensor.",
            CreatedOn = new DateTime(2026, 7, 26),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #region Configuration

    public override string DefaultRawSettings => "periodMs=500;channels=2";

    private static readonly string[] KnownSettings = ["periodMs", "channels"];

    public override void SetConfiguration()
    {
        // The shared parser splits "key=value;..." and warns about every key it does not know,
        // the same way the built-in drivers do. A missing key keeps the default; an invalid
        // value is reported and the default stays.
        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "Temperature sensor driver");

        if (settings.TryGetValue("periodMs", out var periodText))
        {
            if (int.TryParse(periodText, out var parsedPeriod) && parsedPeriod > 0)
            {
                periodMs = parsedPeriod;
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Temperature sensor driver: invalid periodMs '{periodText}', using {periodMs} ms.");
            }
        }

        if (settings.TryGetValue("channels", out var channelsText))
        {
            if (int.TryParse(channelsText, out var parsedChannels))
            {
                channelCount = Math.Clamp(parsedChannels, 1, 16);
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Temperature sensor driver: invalid channels '{channelsText}', using {channelCount}.");
            }
        }
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
        exitRequested = false;

        // Every channel starts at a slightly different temperature.
        lock (channelLock)
        {
            channelTemps = Enumerable.Range(0, channelCount).Select(i => 20.0 + 2.0 * i).ToArray();
        }

        // Give write-capable protocols a TX path into this driver before they start.
        SetTransmitters(ApplyWriteAsync);
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
        exitRequested = true;

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
                Logger?.Log(LogLevel.Warn, "Temp sensor: the sampling loop did not stop before timeout.");
            }
        }

        foreach (var protocol in Protocols)
        {
            await protocol.StopAsync(ct);
        }

        SetTransmitters(null);
        runCts?.Dispose();
        runCts = null;
        runTask = null;
        SetState(CommunicationState.Stopped);
    }

    public override void Dispose()
    {
        runCts?.Dispose();
    }

    #endregion

    #region Communication

    // The protocol encoded an operator write into a "ch1=50" line — apply it to the device.
    public override void Send<T>(T data)
    {
        if (data is string line)
        {
            ApplyWrite(line);
        }
    }

    public override Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        Send(data);
        return Task.CompletedTask;
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

    #region Simulated device

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !exitRequested)
            {
                var lines = new List<string>(channelCount);
                lock (channelLock)
                {
                    for (var i = 0; i < channelTemps.Length; i++)
                    {
                        // The temperature drifts slowly around its current base.
                        channelTemps[i] += 0.1 * (random.NextDouble() * 2.0 - 1.0);
                        lines.Add($"ch{i + 1}={channelTemps[i].ToString("F2", CultureInfo.InvariantCulture)}");
                    }
                }

                foreach (var protocol in Protocols)
                {
                    if (protocol is ProtocolBase<string> textProtocol)
                    {
                        await textProtocol.AddReceivedDataToQueueAsync(lines, ct);
                    }
                }

                await Task.Delay(periodMs, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Temp sensor loop failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
        }
    }

    private Task ApplyWriteAsync(string line, CancellationToken ct)
    {
        ApplyWrite(line);
        return Task.CompletedTask;
    }

    private void ApplyWrite(string line)
    {
        var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
        if (pair.Length != 2
            || !pair[0].StartsWith("ch", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(pair[0][2..], out var channelNumber)
            || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            Logger?.Log(LogLevel.Warn, $"Temp sensor: cannot apply write '{line}'.");
            return;
        }

        lock (channelLock)
        {
            var index = channelNumber - 1;
            if (index < 0 || index >= channelTemps.Length)
            {
                Logger?.Log(LogLevel.Warn, $"Temp sensor: channel '{pair[0]}' does not exist.");
                return;
            }

            channelTemps[index] = value;
        }

        Logger?.Log(LogLevel.Info, $"Temp sensor: channel {pair[0]} re-based to {pair[1]}.");
    }

    private void SetTransmitters(Func<string, CancellationToken, Task>? transmitter)
    {
        foreach (var protocol in Protocols)
        {
            if (protocol is ITransportProtocol<string> transportProtocol)
            {
                transportProtocol.SetTransmitter(transmitter);
            }
        }
    }

    #endregion
}
