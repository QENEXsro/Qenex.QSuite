using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Scripting.Script;
using Qenex.QSuite.Scripting.ScriptingEngine;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

public class ProjectConfigurationProtocolVariableWrapper : PropertyChangedBase
{
    private const string DefaultTriggerThreshold = "1000";
    private const string DefaultTriggerHysteresis = "0";
    private readonly IEnumerable<ProjectConfigurationProtocolOption> sourceOptions;
    private readonly IEnumerable<IVarEvent> variableEvents;
    private readonly Func<IEnumerable<string>>? variableEventOptionsProvider;
    private readonly IEnumerable<ProjectConfigurationScriptWrapper> scripts;
    private ProjectConfigurationProtocolOption originalSource;
    private ProjectConfigurationProtocolOption selectedSource;
    private string originalCommParam;
    private bool originalIsCommunicated;
    private bool originalIsFileLogEnabled;
    private List<(string Script, string AdditionalInfo)> originalTriggers;
    private string commParam;
    private bool isCommunicated;
    private bool isFileLogEnabled;
    private Dictionary<string, string> additionalCommParameters = new(StringComparer.OrdinalIgnoreCase);
    private string selectedVariableEventName = string.Empty;
    private CommDirection selectedDirection = CommDirection.Read;
    private int multiplier = 1;

    public ProjectConfigurationProtocolVariableWrapper(
        IProtocolVariable protocolVariable,
        ProjectConfigurationProtocolOption source,
        IEnumerable<ProjectConfigurationProtocolOption> sourceOptions,
        IEnumerable<IVarEvent> variableEvents,
        IEnumerable<ProjectConfigurationScriptWrapper> scripts,
        IEnumerable<OnValueChangedScriptTrigger> scriptTriggers,
        bool isFileLogEnabled,
        Func<IEnumerable<string>>? variableEventOptionsProvider = null)
    {
        ProtocolVariable = protocolVariable;
        originalSource = source;
        selectedSource = source;
        this.sourceOptions = sourceOptions;
        this.variableEvents = variableEvents;
        this.variableEventOptionsProvider = variableEventOptionsProvider;
        this.scripts = scripts;
        originalCommParam = protocolVariable.ProtocolVariableSpecification.ToCommParam();
        originalIsCommunicated = protocolVariable.IsCommunicated;
        originalIsFileLogEnabled = isFileLogEnabled;
        commParam = originalCommParam;
        isCommunicated = originalIsCommunicated;
        this.isFileLogEnabled = originalIsFileLogEnabled;

        Triggers = new ObservableCollection<OnValueChangedTriggerWrapper>(
            scriptTriggers.Select(trigger => OnValueChangedTriggerWrapper.FromTrigger(trigger, () => ScriptOptions, NotifyTriggersChanged)));
        originalTriggers = SnapshotTriggers();
        AddTriggerCommand = new RelayCommand<object>(_ => AddTrigger());
        RemoveTriggerCommand = new RelayCommand<OnValueChangedTriggerWrapper>(RemoveTrigger);

        ReadCommunicationFieldsFromCommParam();
    }

    public IProtocolVariable ProtocolVariable { get; private set; }
    public IVariableBase Variable => ProtocolVariable.Variable;
    public IEnumerable<ProjectConfigurationProtocolOption> SourceOptions => sourceOptions;
    public IEnumerable<string> VariableEventOptions => (variableEventOptionsProvider?.Invoke() ?? variableEvents.Select(variableEvent => variableEvent.Name))
        .Prepend(string.Empty);
    public IEnumerable<CommDirection> DirectionOptions => Enum.GetValues<CommDirection>();
    public IEnumerable<string> ScriptOptions => scripts
        .Where(script => script.ExecutionMode == ScriptExecutionMode.OnValueChanged)
        .Select(script => script.FileName)
        .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Prepend(string.Empty);

    public string DisplayName => VariableTypeDisplay.ListName(Variable, Variable.Label, Variable.Id);
    public string SourceText => $"{SelectedSource.DriverLabel} / {SelectedSource.ProtocolLabel}";

    public ProjectConfigurationProtocolOption SelectedSource
    {
        get => selectedSource;
        set
        {
            if (selectedSource == value)
            {
                return;
            }

            var previousProtocol = selectedSource.Protocol;
            selectedSource = value;

            // A commParam only has meaning for the protocol that authored it; carrying it over
            // to another protocol silently drops its parameters (tolerant parsers) or fails on
            // mandatory ones (strict parsers). A protocol change therefore restarts the
            // commParam from the target protocol's default template.
            if (!ReferenceEquals(previousProtocol, value.Protocol))
            {
                CommParam = value.Protocol.CreateDefaultCommParam(Variable, variableEvents);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceText));
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public string CommParam
    {
        get => commParam;
        set
        {
            if (commParam == value)
            {
                return;
            }

            commParam = value;
            ReadCommunicationFieldsFromCommParam();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public string SelectedVariableEventName
    {
        get => selectedVariableEventName;
        set
        {
            if (selectedVariableEventName == value)
            {
                return;
            }

            selectedVariableEventName = value;
            UpdateCommParamFromFields();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public CommDirection SelectedDirection
    {
        get => selectedDirection;
        set
        {
            if (selectedDirection == value)
            {
                return;
            }

            selectedDirection = value;
            UpdateCommParamFromFields();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public int Multiplier
    {
        get => multiplier;
        set
        {
            if (multiplier == value)
            {
                return;
            }

            multiplier = value;
            UpdateCommParamFromFields();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public bool IsCommunicated
    {
        get => isCommunicated;
        set
        {
            if (isCommunicated == value)
            {
                return;
            }

            isCommunicated = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public bool IsFileLogEnabled
    {
        get => isFileLogEnabled;
        set
        {
            if (isFileLogEnabled == value)
            {
                return;
            }

            isFileLogEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public ObservableCollection<OnValueChangedTriggerWrapper> Triggers { get; }
    public ICommand AddTriggerCommand { get; }
    public ICommand RemoveTriggerCommand { get; }

    public bool HasChanges =>
        SelectedSource != originalSource
        || CommParam != originalCommParam
        || IsCommunicated != originalIsCommunicated
        || IsFileLogEnabled != originalIsFileLogEnabled
        || !TriggerSignatures(SnapshotTriggers()).SequenceEqual(TriggerSignatures(originalTriggers));

    public void ApplyChanges()
    {
        if (SelectedSource != originalSource
            || CommParam != originalCommParam
            || IsCommunicated != originalIsCommunicated)
        {
            var protocolVariable = ProjectConfigurationProtocolVariableFactory.CreateProtocolVariable(
                SelectedSource.Protocol,
                Variable,
                variableEvents,
                CommParam,
                IsCommunicated);

            originalSource.Protocol.RemoveProtocolVariable(ProtocolVariable);
            SelectedSource.Protocol.AddVariable(protocolVariable);
            ProtocolVariable = protocolVariable;
            OnPropertyChanged(nameof(ProtocolVariable));
        }

        originalSource = SelectedSource;
        originalCommParam = CommParam;
        originalIsCommunicated = IsCommunicated;
        originalIsFileLogEnabled = IsFileLogEnabled;
        originalTriggers = SnapshotTriggers();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(HasChanges));
    }

    public void CancelChanges()
    {
        SelectedSource = originalSource;
        CommParam = originalCommParam;
        IsCommunicated = originalIsCommunicated;
        IsFileLogEnabled = originalIsFileLogEnabled;
        RestoreTriggers(originalTriggers);
        OnPropertyChanged(nameof(HasChanges));
    }

    public void ApplyScriptTrigger(IList<OnValueChangedScriptTrigger> triggers)
    {
        foreach (var trigger in triggers.Where(trigger => trigger.VariableId == Variable.Id).ToList())
        {
            triggers.Remove(trigger);
        }

        if (!IsCommunicated)
        {
            return;
        }

        foreach (var row in Triggers.Where(row => !string.IsNullOrWhiteSpace(row.SelectedScriptFileName)))
        {
            triggers.Add(new OnValueChangedScriptTrigger(Variable.Id, row.SelectedScriptFileName, row.BuildAdditionalInfo()));
        }
    }

    /// <summary>Odebere radky triggeru, ktere odkazuji na smazany skript.</summary>
    public void ClearTriggersForScript(params string[] scriptFileNames)
    {
        foreach (var row in Triggers
                     .Where(row => scriptFileNames.Any(name => !string.IsNullOrWhiteSpace(name) && row.ReferencesScript(name)))
                     .ToList())
        {
            Triggers.Remove(row);
        }

        NotifyTriggersChanged();
    }

    public void RefreshScriptOptions()
    {
        OnPropertyChanged(nameof(ScriptOptions));
        foreach (var row in Triggers)
        {
            row.RefreshScriptOptions();
        }
    }

    private void AddTrigger()
    {
        var defaultScript = ScriptOptions.FirstOrDefault(option => !string.IsNullOrWhiteSpace(option)) ?? string.Empty;
        Triggers.Add(new OnValueChangedTriggerWrapper(
            defaultScript,
            TriggerValueSource.Raw,
            TriggerConditionMode.Delta,
            DefaultTriggerThreshold,
            DefaultTriggerHysteresis,
            () => ScriptOptions,
            NotifyTriggersChanged));
        NotifyTriggersChanged();
    }

    private void RemoveTrigger(OnValueChangedTriggerWrapper? row)
    {
        if (row == null)
        {
            return;
        }

        Triggers.Remove(row);
        NotifyTriggersChanged();
    }

    private void NotifyTriggersChanged()
    {
        OnPropertyChanged(nameof(HasChanges));
    }

    private List<(string Script, string AdditionalInfo)> SnapshotTriggers()
    {
        return Triggers.Select(row => (row.SelectedScriptFileName, row.BuildAdditionalInfo())).ToList();
    }

    private void RestoreTriggers(IEnumerable<(string Script, string AdditionalInfo)> snapshot)
    {
        Triggers.Clear();
        foreach (var trigger in snapshot)
        {
            Triggers.Add(OnValueChangedTriggerWrapper.FromTrigger(
                new OnValueChangedScriptTrigger(Variable.Id, trigger.Script, trigger.AdditionalInfo),
                () => ScriptOptions,
                NotifyTriggersChanged));
        }
    }

    private static List<string> TriggerSignatures(IEnumerable<(string Script, string AdditionalInfo)> snapshot)
    {
        return snapshot
            .Select(trigger => $"{trigger.Script}|{trigger.AdditionalInfo}")
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToList();
    }

    public void RefreshVariableEventOptions()
    {
        OnPropertyChanged(nameof(VariableEventOptions));
    }

    public void RefreshSourceOptions()
    {
        OnPropertyChanged(nameof(SourceOptions));
    }

    private void ReadCommunicationFieldsFromCommParam()
    {
        var parameters = ProjectConfigurationProtocolVariableFactory.ParseCommParam(CommParam);

        selectedVariableEventName = parameters.TryGetValue("eventRef", out var eventRef)
            ? eventRef
            : string.Empty;
        selectedDirection = parameters.TryGetValue("direction", out var direction)
                            && Enum.TryParse<CommDirection>(direction, ignoreCase: true, out var parsedDirection)
            ? parsedDirection
            : CommDirection.Read;
        multiplier = parameters.TryGetValue("multiplier", out var multiplierText)
                     && int.TryParse(multiplierText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMultiplier)
            ? parsedMultiplier
            : 1;
        additionalCommParameters = parameters
            .Where(parameter => !IsKnownCommunicationField(parameter.Key))
            .ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(SelectedVariableEventName));
        OnPropertyChanged(nameof(SelectedDirection));
        OnPropertyChanged(nameof(Multiplier));
    }

    private void UpdateCommParamFromFields()
    {
        var parameters = new List<string>
        {
            $"direction=\"{SelectedDirection.ToString().ToLowerInvariant()}\""
        };

        if (!string.IsNullOrWhiteSpace(SelectedVariableEventName))
        {
            parameters.Add($"eventRef=\"{SelectedVariableEventName}\"");
        }

        parameters.Add($"multiplier=\"{Multiplier}\"");
        parameters.AddRange(additionalCommParameters.Select(parameter => $"{parameter.Key}=\"{parameter.Value}\""));
        commParam = string.Join(";", parameters);
        OnPropertyChanged(nameof(CommParam));
    }

    private static bool IsKnownCommunicationField(string name)
    {
        return name.Equals("direction", StringComparison.OrdinalIgnoreCase)
               || name.Equals("eventRef", StringComparison.OrdinalIgnoreCase)
               || name.Equals("multiplier", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ProjectConfigurationProtocolOption(
    IDriverBase Driver,
    IProtocolBase Protocol)
{
    public string DriverLabel => string.IsNullOrWhiteSpace(Driver.Label)
        ? Driver.Specification.Label
        : Driver.Label;

    public string ProtocolLabel => string.IsNullOrWhiteSpace(Protocol.Specification.Label)
        ? Protocol.Specification.Name
        : Protocol.Specification.Label;

    public string DisplayName => $"{DriverLabel} / {ProtocolLabel}";
}

public static class ProjectConfigurationProtocolVariableFactory
{
    public static IProtocolVariable CreateProtocolVariable(
        IProtocolBase protocol,
        IVariableBase variable,
        IEnumerable<IVarEvent> variableEvents,
        string commParam,
        bool isCommunicated)
    {
        IProtocolVariable? protocolVariable = null;
        string? firstError = null;

        try
        {
            protocolVariable = protocol.CreateProtocolVariable(variable, variableEvents, commParam, isCommunicated);
        }
        catch (Exception e)
        {
            firstError = e.Message;
        }

        if (protocolVariable == null)
        {
            try
            {
                protocolVariable = protocol.CreateProtocolVariable(variable, commParam, isCommunicated);
            }
            catch (Exception e)
            {
                firstError ??= e.Message;
            }
        }

        // Surface the protocol's own reason (e.g. "Missing mandatory commParam 'address'.") —
        // the generic message alone gives the user nothing to fix.
        return protocolVariable
            ?? throw new InvalidOperationException(
                $"Protocol variable for \"{variable.Name}\" could not be created by \"{protocol.Specification.Name}\""
                + (firstError == null ? "." : $": {firstError}"));
    }

    public static Dictionary<string, string> ParseCommParam(string commParam)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parts in commParam
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(parameter => parameter.Split('=', 2))
                     .Where(parts => parts.Length == 2))
        {
            parameters[parts[0].Trim()] = parts[1].Trim().Trim('"');
        }

        return parameters;
    }
}
