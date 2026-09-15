using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Protocols.JsonSignalProtocol;

/// <summary>
/// Newline-delimited JSON signal stream, one message per line in both directions:
/// <c>{"name": "temp", "value": 23.4, "t": 12.345}</c>. The device sends the signals it measures
/// (variables with direction read / readWrite); QInsight sends the same kind of line for every
/// operator or script write of a variable with direction write / readWrite, so a device can be
/// controlled without any second protocol. The optional "t" (device time in seconds) rebuilds the
/// sample timestamps. Runs on any byte-stream driver: TCP client, TCP server or serial port.
/// </summary>
public class JsonSignalProtocol : ProtocolBase<byte[]>, ITransportProtocol<byte[]>, IProtocolVariableWriteProtocol
{
    private volatile bool exitRequested;
    private readonly EventWaitHandle waitHandle;
    private readonly ConcurrentQueue<string> receivedDataQueue;
    // Byte→text decoding and line splitting are this protocol's job — the hosting driver only
    // transports byte chunks. The framer is stateful (partial lines, split UTF-8 sequences).
    private readonly TextLineFramer lineFramer = new();
    private readonly Lock framerLock = new();
    private DateTime? sourceTimeBaseUtc;
    private double? lastSourceTimeSeconds;
    private Func<byte[], CancellationToken, Task>? transmitter;

    // Dropped input is reported once per cause so a silent signal can be diagnosed from the log
    // without the log being flooded by a device that sends one bad line per sample.
    private readonly ConcurrentDictionary<string, byte> reportedUnknownNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> reportedBadValues = new(StringComparer.OrdinalIgnoreCase);
    private int reportedUnparsableLines;

    // A serial port (or a TCP server accepting mid-stream) opens in the middle of a message: the
    // first "line" is the tail of it. That fragment is dropped without a warning; anything
    // unparsable after the first complete line is a real problem and gets reported.
    private volatile bool expectFragmentAfterConnect;

    public JsonSignalProtocol()
    {
        waitHandle = new AutoResetEvent(false);
        receivedDataQueue = new ConcurrentQueue<string>();

        Specification = new SpecificationBase
        {
            Name = "JsonSignalProtocol",
            Label = "JSON Signal Stream",
            Description = "Newline-delimited JSON signal messages in both directions ({\"name\": ..., \"value\": ...}). Use with the TCP Client, TCP Server or Serial Port driver.",
            CreatedOn = new DateTime(2026, 6, 1),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #region Configuration

    // The protocol has no settings of its own; the shared parser reports a stray key as unknown.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "JSON Signal protocol");
    }

    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return $"direction=\"read\";name=\"{variable.Name}\"";
    }

    #endregion

    #region Protocol variables

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        return new JsonSignalProtocolVariable
        {
            Variable = variable,
            IsCommunicated = isCommunicated,
            ProtocolVariableSpecification = JsonSignalProtocolVariableSpecification.Create(commParams, variable.Name)
        };
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return CreateProtocolVariable(variable, $"name=\"{id}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(
        IVariableBase variable,
        IEnumerable<IVarEvent> variableEvents,
        string commParams,
        bool isCommunicated)
    {
        return CreateProtocolVariable(variable, commParams, isCommunicated);
    }

    #endregion

    #region Protocol control

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled) return Task.CompletedTask;

        exitRequested = false;
        sourceTimeBaseUtc = null;
        lastSourceTimeSeconds = null;
        reportedUnknownNames.Clear();
        reportedBadValues.Clear();
        reportedUnparsableLines = 0;
        expectFragmentAfterConnect = true;
        lock (framerLock)
        {
            lineFramer.Reset();
        }

        _ = RunLoopAsync(ct);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct = default)
    {
        exitRequested = true;
        waitHandle.Set();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        waitHandle.Dispose();
    }

    // The driver reopened its port / connection: the byte stream restarts, possibly mid-message.
    public override void OnTransportConnectionChanged(bool connected)
    {
        if (!connected)
        {
            return;
        }

        lock (framerLock)
        {
            lineFramer.Reset();
        }

        expectFragmentAfterConnect = true;
    }

    #endregion

    #region Received data

    public override Task AddReceivedDataToQueueAsync(IEnumerable<byte[]> data, CancellationToken ct = default)
    {
        foreach (var line in ToLines(data))
        {
            receivedDataQueue.Enqueue(line);
        }

        waitHandle.Set();
        return Task.CompletedTask;
    }

    protected override void ProcessReceivedData(IEnumerable<byte[]> data)
    {
        foreach (var line in ToLines(data))
        {
            var protocolVariable = ApplyMessage(line);
            protocolVariable?.NotifyValueChanged();
        }
    }

    protected override async Task ProcessReceivedDataAsync(IEnumerable<byte[]> data, CancellationToken ct = default)
    {
        var notifyTasks = new List<Task>();
        foreach (var line in ToLines(data))
        {
            ct.ThrowIfCancellationRequested();
            var protocolVariable = ApplyMessage(line);
            if (protocolVariable != null)
            {
                notifyTasks.Add(protocolVariable.NotifyValueChangedAsync());
            }
        }

        await Task.WhenAll(notifyTasks);
    }

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<byte[]> data)
    {
        return ToLines(data)
            .Select(ApplyMessage)
            .Where(variable => variable != null)
            .Cast<IProtocolVariable>();
    }

    private List<string> ToLines(IEnumerable<byte[]> chunks)
    {
        var lines = new List<string>();
        lock (framerLock)
        {
            foreach (var chunk in chunks)
            {
                lines.AddRange(lineFramer.Append(chunk));
            }
        }

        return lines;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            SetState(CommunicationState.Running);
            while (!ct.IsCancellationRequested && !exitRequested)
            {
                if (receivedDataQueue.Count > 0)
                {
                    receivedDataQueue.TryDequeue(out var line);
                    try
                    {
                        var protocolVariable = ApplyMessage(line!);
                        if (protocolVariable != null)
                        {
                            await protocolVariable.NotifyValueChangedAsync();
                        }
                    }
                    catch (Exception e)
                    {
                        // One bad message must not kill the consumer loop for the rest of the session.
                        Logger?.Log(LogLevel.Error, $"JSON Signal protocol: message processing failed: {e}");
                    }
                }
                else
                {
                    waitHandle.WaitOne();
                }
            }

            SetState(CommunicationState.Stopped);
        }, ct);
        exitRequested = false;
    }

    private IProtocolVariable? ApplyMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (!TryParseMessage(line, out var signalName, out var value, out var sourceTimeSeconds))
        {
            if (expectFragmentAfterConnect)
            {
                // The tail of a message that was already in flight when the port opened.
                expectFragmentAfterConnect = false;
                return null;
            }

            // Only the first bad line is quoted; a device that keeps sending them would fill the log.
            if (Interlocked.Increment(ref reportedUnparsableLines) == 1)
            {
                Logger?.Log(LogLevel.Warn,
                    $"JSON Signal protocol: line ignored, expected {{\"name\": ..., \"value\": ...}} — got '{Truncate(line)}'. Further unparsable lines are not reported.");
            }

            return null;
        }

        expectFragmentAfterConnect = false;
        var protocolVariable = FindProtocolVariable(signalName);
        if (protocolVariable == null)
        {
            if (reportedUnknownNames.TryAdd(signalName, 0))
            {
                Logger?.Log(LogLevel.Warn,
                    $"JSON Signal protocol: no readable variable has name=\"{signalName}\"; its messages are ignored.");
            }

            return null;
        }

        try
        {
            protocolVariable.Variable.Timestamp = GetTimestamp(sourceTimeSeconds);
            protocolVariable.Variable.SetValue(ConvertValue(value, protocolVariable.Variable));
            return protocolVariable;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or InvalidOperationException or OverflowException or ArgumentException)
        {
            if (reportedBadValues.TryAdd(signalName, 0))
            {
                Logger?.Log(LogLevel.Warn,
                    $"JSON Signal protocol: value '{value}' of \"{signalName}\" cannot be converted to variable '{protocolVariable.Variable.Name}' ({e.Message}); such messages are ignored.");
            }

            return null;
        }
    }

    // Incoming messages feed the variables the device is allowed to send: read and readWrite.
    private IProtocolVariable? FindProtocolVariable(string signalName)
    {
        return Variables.FirstOrDefault(variable =>
            variable.IsCommunicated
            && variable.ProtocolVariableSpecification is JsonSignalProtocolVariableSpecification
            {
                Direction: CommDirection.Read or CommDirection.ReadWrite
            } spec
            && string.Equals(spec.SignalName, signalName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string line)
    {
        const int maxLength = 80;
        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }

    private bool TryParseMessage(
        string line,
        out string signalName,
        out JsonElement value,
        out double? sourceTimeSeconds)
    {
        signalName = string.Empty;
        value = default;
        sourceTimeSeconds = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var nameElement)
                || !root.TryGetProperty("value", out var valueElement))
            {
                return false;
            }

            signalName = nameElement.GetString() ?? string.Empty;
            value = valueElement.Clone();

            if (root.TryGetProperty("t", out var sourceTimeElement)
                && TryReadSourceTimeSeconds(sourceTimeElement, out var parsedSourceTimeSeconds))
            {
                sourceTimeSeconds = parsedSourceTimeSeconds;
            }

            return !string.IsNullOrWhiteSpace(signalName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private DateTime GetTimestamp(double? sourceTimeSeconds)
    {
        if (sourceTimeSeconds == null)
        {
            return DateTime.UtcNow;
        }

        var sourceTime = sourceTimeSeconds.Value;
        if (sourceTimeBaseUtc == null
            || (lastSourceTimeSeconds != null && sourceTime < lastSourceTimeSeconds.Value))
        {
            sourceTimeBaseUtc = DateTime.UtcNow - TimeSpan.FromSeconds(sourceTime);
        }

        lastSourceTimeSeconds = sourceTime;
        return DateTime.SpecifyKind(sourceTimeBaseUtc.Value + TimeSpan.FromSeconds(sourceTime), DateTimeKind.Utc);
    }

    private static bool TryReadSourceTimeSeconds(JsonElement element, out double sourceTimeSeconds)
    {
        sourceTimeSeconds = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out sourceTimeSeconds))
                {
                    return false;
                }

                break;
            case JsonValueKind.String:
                if (!double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out sourceTimeSeconds))
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        return !double.IsNaN(sourceTimeSeconds)
               && !double.IsInfinity(sourceTimeSeconds)
               && sourceTimeSeconds >= 0;
    }

    private static object ConvertValue(JsonElement value, IVariableBase variable)
    {
        var targetType = GetTargetType(variable);
        if (targetType == typeof(string))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        if (targetType == typeof(bool))
        {
            return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                ? value.GetBoolean()
                : bool.Parse(value.ToString());
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value.ToString(), ignoreCase: true);
        }

        var numericValue = value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.Parse(value.ToString(), CultureInfo.InvariantCulture);
        return Convert.ChangeType(numericValue, targetType, CultureInfo.InvariantCulture);
    }

    private static Type GetTargetType(IVariableBase variable)
    {
        if (variable is StringVariable)
        {
            return typeof(string);
        }

        return variable.GetValue()?.GetType() ?? typeof(double);
    }

    #endregion

    #region Writes to the device

    public void SetTransmitter(Func<byte[], CancellationToken, Task>? byteTransmitter)
    {
        transmitter = byteTransmitter;
    }

    /// <summary>Writable are the protocol's variables with direction write or readWrite.</summary>
    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable)
               && protocolVariable.ProtocolVariableSpecification is JsonSignalProtocolVariableSpecification
               {
                   Direction: CommDirection.Write or CommDirection.ReadWrite
               };
    }

    /// <summary>Sends one {"name": ..., "value": ...} line carrying the variable's current value.</summary>
    public async Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        var currentTransmitter = transmitter
            ?? throw new InvalidOperationException("Transport is not available (no transmitter injected).");

        foreach (var line in Encode([protocolVariable]))
        {
            await currentTransmitter(line, ct);
        }
    }

    // The outgoing line has the same shape as the incoming one, so a device needs one parser
    // for both directions. The raw value is sent: a presentation's conversion has already been
    // inverted by QInsight when the operator entered the engineering value.
    protected override IEnumerable<byte[]> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        foreach (var protocolVariable in protocolVariables)
        {
            if (protocolVariable.ProtocolVariableSpecification is not JsonSignalProtocolVariableSpecification spec)
            {
                continue;
            }

            var value = protocolVariable.Variable.GetValue();
            var jsonValue = value switch
            {
                null => "null",
                bool b => b ? "true" : "false",
                string s => JsonSerializer.Serialize(s),
                Enum e => JsonSerializer.Serialize(e.ToString()),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => JsonSerializer.Serialize(value.ToString())
            };

            var line = $"{{\"name\": {JsonSerializer.Serialize(spec.SignalName)}, \"value\": {jsonValue}}}\n";
            yield return Encoding.UTF8.GetBytes(line);
        }
    }

    #endregion
}
