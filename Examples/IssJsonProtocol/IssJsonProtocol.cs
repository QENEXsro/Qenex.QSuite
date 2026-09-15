using System.Reflection;
using System.Text.Json;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Examples.IssJsonProtocol;

/// <summary>
/// Example protocol: picks numeric fields out of the received ISS position JSON by name. Each
/// variable states which field it wants via the "path" commParam (path="latitude"), so one JSON
/// document can feed many variables at once. Nothing here is ISS-specific except the name — the
/// same code would decode any flat JSON object — but as an example it is paired with the IssDriver.
/// </summary>
public class IssJsonProtocol : ProtocolBase<string>
{
    public IssJsonProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "IssJsonProtocol",
            Label = "ISS JSON Protocol (example)",
            Description = "Example protocol decoding numeric fields of the ISS position JSON into variables.",
            CreatedOn = new DateTime(2026, 7, 27),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // The transport type (string) alone cannot tell text-line drivers apart, and this
    // protocol only understands the ISS position JSON, so it narrows itself to its driver.
    public override IReadOnlyList<string> CompatibleDrivers => ["IssDriver"];

    #region Configuration

    // The protocol has no protocol-level settings; everything is per-variable commParams.
    // The shared parser still runs so that a key typed into the protocol settings is reported
    // as unknown instead of being ignored silently.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "ISS JSON protocol");
    }

    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return "path=\"latitude\"";
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
                ProtocolVariableSpecification = IssJsonVariableSpecification.Create(commParams)
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
        return CreateProtocolVariable(variable, $"path=\"{id}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents,
        string commParams, bool isCommunicated)
    {
        // The data arrive whenever the driver polls them, so variable events are not used.
        return CreateProtocolVariable(variable, commParams, isCommunicated);
    }

    #endregion

    #region Protocol control

    // No worker loop is needed: received documents are decoded directly in
    // AddReceivedDataToQueueAsync, so start/stop only maintain the state machine.
    // (For a queued, cancelable consumer loop see the TempSensorProtocol example.)
    public override Task StartAsync(CancellationToken ct = default)
    {
        SetState(IsEnabled ? CommunicationState.Running : CommunicationState.Disabled);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopped);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    #endregion

    #region Received data

    // At ~1 document per second there is no need for an internal queue — the data are
    // decoded right away on the driver's polling task.
    public override async Task AddReceivedDataToQueueAsync(IEnumerable<string> data, CancellationToken ct = default)
    {
        if (State != CommunicationState.Running)
        {
            return;
        }

        await ProcessReceivedDataAsync(data, ct);
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

        foreach (var json in data)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException e)
            {
                Logger?.Log(LogLevel.Warn, $"ISS JSON protocol: cannot parse received data ({e.Message}).");
                continue;
            }

            using (document)
            {
                // Every variable whose "path" names a numeric field of this document gets its value.
                foreach (var protocolVariable in Variables)
                {
                    if (!protocolVariable.IsCommunicated
                        || protocolVariable.ProtocolVariableSpecification is not IssJsonVariableSpecification spec
                        || protocolVariable.Variable is not ScalarVariable scalarVariable
                        || !document.RootElement.TryGetProperty(spec.Path, out var field)
                        || field.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (!TrySetValue(scalarVariable, field.GetDouble()))
                    {
                        Logger?.Log(LogLevel.Warn, $"ISS JSON protocol: variable '{scalarVariable.Name}' has an unsupported value type.");
                        continue;
                    }

                    scalarVariable.Timestamp = DateTime.UtcNow;
                    updated.Add(protocolVariable);
                }
            }
        }

        return updated;
    }

    // The protocol is read-only (a REST API you can only GET), so nothing is ever encoded.
    // For the write direction see the TempSensorProtocol example.
    protected override IEnumerable<string> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        yield break;
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
}
