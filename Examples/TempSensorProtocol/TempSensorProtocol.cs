using System.Globalization;
using System.Reflection;
using System.Threading.Channels;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Examples.TempSensorProtocol;

/// <summary>
/// Example protocol: decodes "ch1=23.47" text lines from the TempSensorDriver into variables
/// addressed by the "channel" commParam, and encodes operator writes back into the same lines.
/// The generic parameter (string) is what pairs the protocol with a text-line driver.
/// </summary>
public class TempSensorProtocol : ProtocolBase<string>, ITransportProtocol<string>, IProtocolVariableWriteProtocol
{
    // Received lines waiting for processing; the channel keeps the consumer loop cancelable.
    private Channel<string>? receivedLines;
    private volatile Func<string, CancellationToken, Task>? transmitter;

    private volatile bool exitRequested;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public TempSensorProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "TempSensorProtocol",
            Label = "Temperature Sensor Protocol (example)",
            Description = "Example protocol decoding text lines of the simulated temperature sensor.",
            CreatedOn = new DateTime(2026, 7, 26),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // The transport type (string) alone cannot tell text-line drivers apart, and this
    // protocol only understands the "chN=value" sensor lines, so it narrows itself to its driver.
    public override IReadOnlyList<string> CompatibleDrivers => ["TempSensorDriver"];

    #region Configuration

    // The protocol has no protocol-level settings; everything is per-variable commParams.
    // The shared parser still runs so that a key typed into the protocol settings is reported
    // as unknown instead of being ignored silently.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Temperature sensor protocol");
    }

    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return "direction=\"read\";channel=\"ch1\"";
    }

    #endregion

    #region Protocol variables

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        try
        {
            return new ProtocolVariable
            {
                Variable = variable,
                IsCommunicated = isCommunicated,
                ProtocolVariableSpecification = TempSensorVariableSpecification.Create(commParams)
            };
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"Protocol variable specification for variable {variable.Name} could not be created ({e.Message}).");
            return null;
        }
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return CreateProtocolVariable(variable, $"direction=\"read\";channel=\"{id}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents,
        string commParams, bool isCommunicated)
    {
        // The data arrive whenever the device sends them, so variable events are not used.
        return CreateProtocolVariable(variable, commParams, isCommunicated);
    }

    #endregion

    #region Protocol control

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return Task.CompletedTask;
        }

        if (State == CommunicationState.Running || runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Starting);
        exitRequested = false;
        receivedLines = Channel.CreateUnbounded<string>();

        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: the loop must not inherit the caller's (UI) SynchronizationContext —
        // a blocked dispatcher (window drag, busy UI) would stall the whole session.
        // Capture the token now: a racing StopAsync may null runCts before the loop starts.
        var runToken = runCts.Token;
        runTask = Task.Run(() => RunLoopAsync(runToken));
        SetState(CommunicationState.Running);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        exitRequested = true;
        receivedLines?.Writer.TryComplete();

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
                Logger?.Log(LogLevel.Warn, "Temp sensor protocol did not stop before timeout.");
            }
        }

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

    #region Received data

    public override Task AddReceivedDataToQueueAsync(IEnumerable<string> data, CancellationToken ct = default)
    {
        foreach (var line in data)
        {
            receivedLines?.Writer.TryWrite(line);
        }

        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in receivedLines!.Reader.ReadAllAsync(ct))
            {
                if (exitRequested)
                {
                    break;
                }

                await ProcessReceivedDataAsync([line], ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Temp sensor protocol loop failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
        }
    }

    protected override void ProcessReceivedData(IEnumerable<string> data)
    {
        foreach (var protocolVariable in Decode(data))
        {
            protocolVariable.NotifyValueChanged();
        }
    }

    protected override async Task ProcessReceivedDataAsync(IEnumerable<string> data, CancellationToken ct = default)
    {
        var notifyTasks = Decode(data).Select(protocolVariable => protocolVariable.NotifyValueChangedAsync());
        await Task.WhenAll(notifyTasks);
    }

    #endregion

    #region Encoding and decoding

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<string> data)
    {
        var updated = new List<IProtocolVariable>();

        foreach (var line in data)
        {
            var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2
                || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                Logger?.Log(LogLevel.Warn, $"Temp sensor protocol: cannot decode line '{line}'.");
                continue;
            }

            foreach (var protocolVariable in Variables)
            {
                if (!protocolVariable.IsCommunicated
                    || protocolVariable.ProtocolVariableSpecification is not TempSensorVariableSpecification spec
                    || spec.Direction != CommDirection.Read
                    || !spec.Channel.Equals(pair[0], StringComparison.OrdinalIgnoreCase)
                    || protocolVariable.Variable is not ScalarVariable scalarVariable)
                {
                    continue;
                }

                if (!TrySetValue(scalarVariable, value))
                {
                    Logger?.Log(LogLevel.Warn, $"Temp sensor protocol: variable '{scalarVariable.Name}' has an unsupported value type.");
                    continue;
                }

                scalarVariable.Timestamp = DateTime.UtcNow;
                updated.Add(protocolVariable);
            }
        }

        return updated;
    }

    protected override IEnumerable<string> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        foreach (var protocolVariable in protocolVariables)
        {
            if (protocolVariable.ProtocolVariableSpecification is not TempSensorVariableSpecification spec
                || protocolVariable.Variable is not ScalarVariable scalarVariable)
            {
                continue;
            }

            var value = Convert.ToDouble(scalarVariable.Values.GetValue(), CultureInfo.InvariantCulture);
            yield return $"{spec.Channel}={value.ToString("F2", CultureInfo.InvariantCulture)}";
        }
    }

    // Values<T>.SetValue does not convert, so the received double is converted to the
    // variable's type here; the example supports the common numeric types.
    private static bool TrySetValue(ScalarVariable scalarVariable, double value)
    {
        switch (scalarVariable.Values)
        {
            case Values<double> doubleValues:
                doubleValues.Value = value;
                return true;
            case Values<float> floatValues:
                floatValues.Value = (float)value;
                return true;
            case Values<int> intValues:
                intValues.Value = (int)Math.Round(value);
                return true;
            default:
                return false;
        }
    }

    #endregion

    #region Operator writes

    public void SetTransmitter(Func<string, CancellationToken, Task>? byteTransmitter)
    {
        transmitter = byteTransmitter;
    }

    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable)
               && protocolVariable.ProtocolVariableSpecification is TempSensorVariableSpecification { Direction: CommDirection.Write };
    }

    public async Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        var currentTransmitter = transmitter
            ?? throw new InvalidOperationException("Transport is not available (no transmitter injected).");

        foreach (var line in Encode([protocolVariable]))
        {
            await currentTransmitter(line, ct);
        }
    }

    #endregion
}
