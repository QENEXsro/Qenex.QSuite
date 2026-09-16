using System.Globalization;
using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Protocols.DataLogReplayProtocol;

public class DataLogReplayProtocol : ProtocolBase<DataLogRecord>
{
    // One-shot warning guards so a whole missing signal is reported once, not per record.
    private readonly object diagnosticsLock = new();
    private readonly HashSet<string> reportedUnknownVariables = [];
    private readonly HashSet<string> reportedFailedRecords = [];
    private bool reportedDroppedWhileNotRunning;

    public DataLogReplayProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "DataLogReplayProtocol",
            Label = "Data Log Replay",
            Description = "Feeds replayed .qilog values into project variables (added by Import).",
            CreatedOn = new DateTime(2026, 5, 25),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // The protocol has no settings of its own; the shared parser reports a stray key as unknown.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Data log replay protocol");
    }

    // Replayed variables carry no per-variable parameters: records are matched to variables by
    // id (or namespace + name), so the template is empty (the generic direction/eventRef/id keys
    // would be meaningless here).
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return string.Empty;
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        return new DataLogReplayProtocolVariable
        {
            Variable = variable,
            IsCommunicated = isCommunicated,
            ProtocolVariableSpecification = DataLogReplayProtocolVariableSpecification.Create(commParams)
        };
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return CreateProtocolVariable(variable, $"id=\"{id}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(
        IVariableBase variable,
        IEnumerable<IVarEvent> variableEvents,
        string commParams,
        bool isCommunicated)
    {
        return CreateProtocolVariable(variable, commParams, isCommunicated);
    }

    public override Task StartAsync(CancellationToken ct = default)
    {
        lock (diagnosticsLock)
        {
            reportedUnknownVariables.Clear();
            reportedFailedRecords.Clear();
            reportedDroppedWhileNotRunning = false;
        }

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

    public override Task AddReceivedDataToQueueAsync(IEnumerable<DataLogRecord> data, CancellationToken ct = default)
    {
        if (State == CommunicationState.Running)
        {
            return ProcessReceivedDataAsync(data, ct);
        }

        ReportDroppedWhileNotRunning();
        return Task.CompletedTask;
    }

    protected override void ProcessReceivedData(IEnumerable<DataLogRecord> data)
    {
        foreach (var record in data)
        {
            var protocolVariable = ApplyRecordSafe(record);
            protocolVariable?.NotifyValueChanged();
        }
    }

    protected override async Task ProcessReceivedDataAsync(IEnumerable<DataLogRecord> data, CancellationToken ct = default)
    {
        var notifyTasks = new List<Task>();
        foreach (var record in data)
        {
            ct.ThrowIfCancellationRequested();
            var protocolVariable = ApplyRecordSafe(record);
            if (protocolVariable != null)
            {
                notifyTasks.Add(protocolVariable.NotifyValueChangedAsync());
            }
        }

        await Task.WhenAll(notifyTasks);
    }

    protected override IEnumerable<DataLogRecord> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        throw new NotSupportedException("Data log replay protocol does not encode outgoing data.");
    }

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<DataLogRecord> data)
    {
        return data
            .Select(ApplyRecordSafe)
            .Where(variable => variable != null)
            .Cast<IProtocolVariable>();
    }

    /// <summary>
    /// Applies a single record; a bad record (unknown variable, malformed value) is skipped
    /// and reported once so the rest of the replay keeps running instead of dying mid-file.
    /// </summary>
    private IProtocolVariable? ApplyRecordSafe(DataLogRecord record)
    {
        try
        {
            return ApplyRecord(record);
        }
        catch (Exception e)
        {
            ReportFailedRecord(record, e);
            return null;
        }
    }

    private IProtocolVariable? ApplyRecord(DataLogRecord record)
    {
        var protocolVariable = FindProtocolVariable(record);
        if (protocolVariable == null)
        {
            ReportUnknownVariable(record);
            return null;
        }

        var variable = protocolVariable.Variable;
        variable.Timestamp = record.TimestampUtcTicks > 0
            ? new DateTime(record.TimestampUtcTicks, DateTimeKind.Utc)
            : DateTime.UtcNow;
        variable.SetValue(ConvertValue(record, variable));

        return protocolVariable;
    }

    private void ReportDroppedWhileNotRunning()
    {
        lock (diagnosticsLock)
        {
            if (reportedDroppedWhileNotRunning)
            {
                return;
            }

            reportedDroppedWhileNotRunning = true;
        }

        Logger?.Log(
            LogLevel.Warn,
            $"Data log replay protocol is not running (state {State}) — replayed records are being dropped.");
    }

    private void ReportUnknownVariable(DataLogRecord record)
    {
        var key = $"{record.VariableId}|{record.VariableNamespace}|{record.VariableName}";
        lock (diagnosticsLock)
        {
            if (!reportedUnknownVariables.Add(key))
            {
                return;
            }
        }

        Logger?.Log(
            LogLevel.Warn,
            $"Data log contains records for variable '{record.VariableName}' (Id {record.VariableId}, "
            + $"namespace '{record.VariableNamespace}') which does not exist in this workspace — "
            + "the whole signal is skipped during replay.");
    }

    private void ReportFailedRecord(DataLogRecord record, Exception e)
    {
        var key = $"{record.VariableId}|{record.VariableName}|{e.GetType().Name}";
        lock (diagnosticsLock)
        {
            if (!reportedFailedRecords.Add(key))
            {
                return;
            }
        }

        Logger?.Log(
            LogLevel.Error,
            $"Replay record for variable '{record.VariableName}' (Id {record.VariableId}) could not be "
            + $"applied and was skipped (value '{record.Value}', type '{record.ValueType}'): {e.Message}",
            e);
    }

    private IProtocolVariable? FindProtocolVariable(DataLogRecord record)
    {
        return Variables.FirstOrDefault(variable => variable.Variable.Id == record.VariableId)
               ?? Variables.FirstOrDefault(variable =>
                   string.Equals(variable.Variable.Namespace, record.VariableNamespace, StringComparison.Ordinal)
                   && string.Equals(variable.Variable.Name, record.VariableName, StringComparison.Ordinal));
    }

    private static object ConvertValue(DataLogRecord record, IVariableBase variable)
    {
        var targetType = GetTargetType(record, variable);
        if (targetType == typeof(string))
        {
            return record.Value;
        }

        if (string.IsNullOrEmpty(record.Value))
        {
            return Activator.CreateInstance(targetType) ?? record.Value;
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, record.Value, ignoreCase: true);
        }

        return Convert.ChangeType(record.Value, targetType, CultureInfo.InvariantCulture);
    }

    private static Type GetTargetType(DataLogRecord record, IVariableBase variable)
    {
        if (variable is StringVariable)
        {
            return typeof(string);
        }

        var currentValue = variable.GetValue();
        if (currentValue != null)
        {
            return currentValue.GetType();
        }

        return Type.GetType(record.ValueType, throwOnError: false) ?? typeof(string);
    }
}
