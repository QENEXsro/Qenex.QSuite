using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Qenex.QInsight.EventAggregatorMsgs;
using Qenex.QInsight.Helpers;
using Qenex.QInsight.Licensing;
using Qenex.QInsight.ViewModels.ModelWrappers;
using Qenex.QLibs.QUI;
using Qenex.QLibs.XmlInOut;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Common.PluginManager;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.ModuleXmlHandler;
using Qenex.QSuite.ModuleXmlHandler.XmlStructure;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Modules.Module;
using Qenex.QSuite.Scripting.PythonScript;
using Qenex.QSuite.Scripting.Script;
using Qenex.QSuite.Scripting.ScriptingEngine;
using Qenex.QSuite.UnifModule;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QSuite.Variables.VariableEvents;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.FileDialogs;
using Telerik.Windows.Data;

namespace Qenex.QInsight.ViewModels;

public class ProjectConfigurationViewModel : PropertyChangedBase
{
    private const string FileDataLoggerDriverName = "FileDataLoggerDriver";
    private const string FileDataReplayDriverName = "FileDataReplayDriver";
    private const string DataLogReplayProtocolName = "DataLogReplayProtocol";
    private const string SinkProtocolName = "PassThroughProtocol";
    private const string XmlFileFilter = "QInsight XML files (*.xml)|*.xml|All files (*.*)|*.*";

    private readonly EventAggregator? eventAggregator;
    private readonly int? communicatedSignalsLimit;
    private readonly IModuleBase module;
    private readonly IList<IDriverBase> drivers;
    private readonly IEnumerable<PluginDetails> driverPlugins;
    private readonly IEnumerable<PluginDetails> protocolPlugins;
    private readonly IList<IVariableBase> variables;
    private readonly IList<IVarEvent> variableEvents;
    private readonly IList<IValConversion> conversions;
    private readonly IList<IPresentation> presentations;
    private readonly IList<IScriptBase> scripts;
    private readonly IList<OnValueChangedScriptTrigger> onValueChangedScriptTriggers;
    private readonly List<ProjectConfigurationDriverWrapper> addedDrivers = [];
    private readonly List<ProjectConfigurationDriverWrapper> removedDrivers = [];
    private readonly List<AddedProtocol> addedProtocols = [];
    private readonly List<RemovedProtocol> removedProtocols = [];
    private readonly List<ProjectConfigurationVariableWrapper> addedVariables = [];
    private readonly List<ProjectConfigurationVariableWrapper> removedVariables = [];
    private readonly List<ProjectConfigurationProtocolVariableWrapper> addedCommunicatedVariables = [];
    private readonly List<RemovedCommunicatedVariable> removedCommunicatedVariables = [];
    private readonly List<ProjectConfigurationConversionWrapper> addedConversions = [];
    private readonly List<ProjectConfigurationConversionWrapper> removedConversions = [];
    private readonly List<ProjectConfigurationPresentationWrapper> addedPresentations = [];
    private readonly List<ProjectConfigurationPresentationWrapper> removedPresentations = [];
    private readonly List<ProjectConfigurationEventWrapper> addedEvents = [];
    private readonly List<ProjectConfigurationEventWrapper> removedEvents = [];
    private readonly List<ProjectConfigurationScriptWrapper> addedScripts = [];
    private readonly List<ProjectConfigurationScriptWrapper> removedScripts = [];
    private RadWindow? parentWindow;
    private string? lastXmlDialogDirectory;

    public ProjectConfigurationViewModel()
        : this(null, new UnifiedModuleFactory().Create(new ScriptEngineSettings(), null), [], [], [], [], [], [], [], [], [])
    {
    }

    public ProjectConfigurationViewModel(
        EventAggregator? eventAggregator,
        IModuleBase module,
        IEnumerable<IDriverBase> drivers,
        IEnumerable<PluginDetails> driverPlugins,
        IEnumerable<PluginDetails> protocolPlugins,
        IEnumerable<IVariableBase> variables,
        IEnumerable<IValConversion> conversions,
        IEnumerable<IPresentation> presentations,
        IEnumerable<IVarEvent> variableEvents,
        IList<OnValueChangedScriptTrigger> onValueChangedScriptTriggers,
        IList<IScriptBase> scripts,
        int? communicatedSignalsLimit = null)
    {
        this.communicatedSignalsLimit = communicatedSignalsLimit;
        this.eventAggregator = eventAggregator;
        this.module = module;
        this.drivers = drivers as IList<IDriverBase> ?? drivers.ToList();
        this.driverPlugins = driverPlugins.ToList();
        this.protocolPlugins = protocolPlugins.ToList();
        this.variables = variables as IList<IVariableBase> ?? variables.ToList();
        this.variableEvents = variableEvents as IList<IVarEvent> ?? variableEvents.ToList();
        this.conversions = conversions as IList<IValConversion> ?? conversions.ToList();
        this.presentations = presentations as IList<IPresentation> ?? presentations.ToList();
        this.onValueChangedScriptTriggers = onValueChangedScriptTriggers;
        this.scripts = scripts;
        Project = new ProjectConfigurationProjectWrapper(this.module);
        Project.PropertyChanged += OnProjectPropertyChanged;
        NavigationItems =
        [
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.Project, "Project"),
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.CommunicationDrivers, "Drivers & Protocols"),
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.Events, "Events"),
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.Variables, "Variables"),
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.Presentations, "Presentations"),
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.Conversions, "Conversions"),
            new ProjectConfigurationNavigationItem(ProjectConfigurationSection.Scripts, "Scripts")
        ];
        Drivers = new ObservableCollection<ProjectConfigurationDriverWrapper>(
            this.drivers.Select(driver => new ProjectConfigurationDriverWrapper(driver)));
        foreach (var driver in Drivers)
        {
            SubscribeDriverWrapper(driver);
        }
        DriverPluginOptions = this.driverPlugins
            .Where(plugin => !plugin.Name.Equals(FileDataReplayDriverName, StringComparison.OrdinalIgnoreCase))
            .Select(plugin => new ProjectConfigurationDriverPluginOption(plugin))
            .OrderBy(option => option.DisplayName)
            .ToList();
        SelectedDriverPlugin = DriverPluginOptions.FirstOrDefault();
        RefreshProtocolPluginOptions();
        Variables = new ObservableCollection<ProjectConfigurationVariableWrapper>(
            this.variables.Select(variable => new ProjectConfigurationVariableWrapper(variable, presentations)));
        foreach (var variable in Variables)
        {
            variable.PropertyChanged += OnVariablePropertyChanged;
        }

        Conversions = new ObservableCollection<ProjectConfigurationConversionWrapper>(
            conversions.Select(conversion => new ProjectConfigurationConversionWrapper(conversion)));
        foreach (var conversion in Conversions)
        {
            conversion.PropertyChanged += OnConversionPropertyChanged;
        }

        Presentations = new ObservableCollection<ProjectConfigurationPresentationWrapper>(
            presentations.Select(presentation => CreatePresentationWrapper(presentation)));
        foreach (var presentation in Presentations)
        {
            presentation.PropertyChanged += OnPresentationPropertyChanged;
        }

        Events = new ObservableCollection<ProjectConfigurationEventWrapper>(
            this.variableEvents.Select(variableEvent => new ProjectConfigurationEventWrapper(variableEvent)));
        foreach (var variableEvent in Events)
        {
            variableEvent.PropertyChanged += OnEventPropertyChanged;
        }

        Scripts = new ObservableCollection<ProjectConfigurationScriptWrapper>(
            this.scripts.Select(script => new ProjectConfigurationScriptWrapper(script)));
        foreach (var script in Scripts)
        {
            script.PropertyChanged += OnScriptPropertyChanged;
        }

        SourceOptions = new ObservableCollection<ProjectConfigurationProtocolOption>(CreateSourceOptions(this.drivers));
        SelectedSourceOption = SourceOptions.FirstOrDefault();
        CommunicatedVariables = new ObservableCollection<ProjectConfigurationProtocolVariableWrapper>(
            SourceOptions.SelectMany(source => source.Protocol.Variables.Select(protocolVariable =>
                CreateCommunicatedVariableWrapper(protocolVariable, source))));
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            communicatedVariable.PropertyChanged += OnCommunicatedVariablePropertyChanged;
        }

        ApplyCommand = new RelayCommand<object>(_ => ApplyChanges(), _ => HasChanges);
        OkCommand = new RelayCommand<object>(_ =>
        {
            // Same as Apply; the dialog closes only when the changes were applied,
            // a validation problem keeps it open with the message shown.
            if (!HasChanges || ApplyChanges())
            {
                parentWindow?.Close();
            }
        });
        CancelCommand = new RelayCommand<object>(_ => Cancel());
        AddDriverCommand = new RelayCommand<object>(_ => AddDriver(), _ => CanAddDriver());
        RemoveDriverCommand = new RelayCommand<object>(_ => RemoveDriver(), _ => SelectedDriver != null);
        AddProtocolCommand = new RelayCommand<object>(_ => AddProtocol(), _ => CanAddProtocol());
        // Managed protocols (replay, logger sink) are added and removed together with their driver.
        RemoveProtocolCommand = new RelayCommand<object>(_ => RemoveProtocol(), _ => SelectedProtocol is { IsManaged: false });
        SelectDriverCommand = new RelayCommand<object>(SelectDriver);
        AddVariableCommand = new RelayCommand<object>(_ => AddVariable(), _ => CanAddVariable());
        RemoveVariableCommand = new RelayCommand<object>(_ => RemoveVariable(), _ => CanRemoveVariable());
        CopyVariableCommand = new RelayCommand<object>(_ => CopyVariable(), _ => SelectedVariable != null);
        ExportVariablesCommand = new RelayCommand<object>(_ => ExportVariables(), _ => Variables.Count > 0);
        ImportVariablesCommand = new RelayCommand<object>(_ => ImportVariables());
        AddVariableToSourceCommand = new RelayCommand<object>(_ => AddVariableToSource(), _ => CanAddVariableToSource());
        RemoveCommunicatedVariableCommand = new RelayCommand<object>(_ => RemoveCommunicatedVariable(), _ => CanRemoveCommunicatedVariable());
        SelectScriptCommand = new RelayCommand<object>(SelectScript);
        AddConversionCommand = new RelayCommand<object>(_ => AddConversion());
        RemoveConversionCommand = new RelayCommand<object>(_ => RemoveConversion(), _ => SelectedConversion != null);
        CopyConversionCommand = new RelayCommand<object>(_ => CopyConversion(), _ => SelectedConversion != null);
        ExportConversionsCommand = new RelayCommand<object>(_ => ExportConversions(), _ => Conversions.Count > 0);
        ImportConversionsCommand = new RelayCommand<object>(_ => ImportConversions());
        AddPresentationCommand = new RelayCommand<object>(_ => AddPresentation(), _ => CanAddPresentation());
        RemovePresentationCommand = new RelayCommand<object>(_ => RemovePresentation(), _ => CanRemovePresentation());
        CopyPresentationCommand = new RelayCommand<object>(_ => CopyPresentation(), _ => SelectedPresentation != null);
        ExportPresentationsCommand = new RelayCommand<object>(_ => ExportPresentations(), _ => Presentations.Count > 0);
        ImportPresentationsCommand = new RelayCommand<object>(_ => ImportPresentations());
        AddEventCommand = new RelayCommand<object>(_ => AddEvent());
        RemoveEventCommand = new RelayCommand<object>(_ => RemoveEvent(), _ => CanRemoveEvent());
        CopyEventCommand = new RelayCommand<object>(_ => CopyEvent(), _ => SelectedEvent != null);
        ExportEventsCommand = new RelayCommand<object>(_ => ExportEvents(), _ => Events.Count > 0);
        ImportEventsCommand = new RelayCommand<object>(_ => ImportEvents());
        AddScriptCommand = new RelayCommand<object>(_ => AddScript());
        RemoveScriptCommand = new RelayCommand<object>(_ => RemoveScript(), _ => SelectedScript != null);
        ExportScriptCommand = new RelayCommand<object>(_ => ExportSelectedScript(), _ => SelectedScript != null);
        ExportAllScriptsCommand = new RelayCommand<object>(_ => ExportAllScripts(), _ => Scripts.Count > 0);
        ImportScriptsCommand = new RelayCommand<object>(_ => ImportScripts());
        EnsureInitialVariableSelections();
        SelectedDriver = Drivers.FirstOrDefault();
        SelectedConversion = Conversions.FirstOrDefault();
        SelectedPresentation = Presentations.FirstOrDefault();
        SelectedEvent = Events.FirstOrDefault();
        SelectedScript = Scripts.FirstOrDefault();
        SelectedNavigationItem = NavigationItems[0];
    }

    public ObservableCollection<ProjectConfigurationNavigationItem> NavigationItems { get; }
    public ProjectConfigurationProjectWrapper Project { get; }
    public ObservableCollection<ProjectConfigurationDriverWrapper> Drivers { get; }
    public IReadOnlyList<ProjectConfigurationDriverPluginOption> DriverPluginOptions { get; }
    public IReadOnlyList<ProjectConfigurationProtocolPluginOption> ProtocolPluginOptions { get; private set; } = [];
    public ObservableCollection<ProjectConfigurationVariableWrapper> Variables { get; }
    public ObservableCollection<ProjectConfigurationConversionWrapper> Conversions { get; }
    public ObservableCollection<ProjectConfigurationPresentationWrapper> Presentations { get; }
    public ObservableCollection<ProjectConfigurationEventWrapper> Events { get; }
    public ObservableCollection<ProjectConfigurationProtocolVariableWrapper> CommunicatedVariables { get; }

    /// <summary>Free-tier warning: the edited state exceeds the communicated-signal limit.
    /// Editing stays allowed; only the runtime start is blocked (ShellWindowModel guards).</summary>
    public bool HasCommunicatedSignalsWarning =>
        communicatedSignalsLimit is { } limit
        && CommunicatedVariables.Count(variable => variable.IsCommunicated) > limit;

    public string CommunicatedSignalsWarning =>
        communicatedSignalsLimit is { } limit
            ? CommunicatedSignals.BuildOverLimitMessage(
                  CommunicatedVariables.Count(variable => variable.IsCommunicated), limit)
              + " Runtime will not start until some variables stop being communicated."
            : string.Empty;

    public ObservableCollection<ProjectConfigurationProtocolOption> SourceOptions { get; }
    public ObservableCollection<ProjectConfigurationScriptWrapper> Scripts { get; }
    public IEnumerable<EnumMemberViewModel> ExecutionModes { get; } = ScriptExecutionModeSource.Visible;
    public RelayCommand<object> ApplyCommand { get; }
    public RelayCommand<object> OkCommand { get; }
    public RelayCommand<object> CancelCommand { get; }
    /// <summary>True when the project (as currently edited in this dialog, before Apply) contains an
    /// enabled File data logger driver — only then can "Log to FileDataLogger" do anything. Drives the
    /// IsEnabled of the checkbox on the Variables page (CEO decision 2026-09-18). Recomputed from the
    /// dialog's own driver list, so a driver added a moment ago in this dialog counts immediately.
    /// The per-variable flag itself is never cleared: remove and re-add the driver and it is back.</summary>
    public bool IsFileLoggingAvailable => Drivers.Any(driver =>
        IsFileLogDriver(driver.Driver) && driver.IsEnabled
        && driver.Protocols.Any(protocol => protocol.Protocol is IProtocolVariableSinkProtocol && protocol.IsEnabled));

    /// <summary>Explanation shown under the greyed-out checkbox; empty when logging is available.
    /// Names the missing piece: the driver, the driver's enabled state, or its Data Log Pass-Through
    /// protocol (the sink the logged variables are attached to; added automatically with the driver,
    /// but it can be removed or disabled by hand).</summary>
    public string FileLoggingHint
    {
        get
        {
            if (IsFileLoggingAvailable)
            {
                return string.Empty;
            }

            var fileLogDriver = Drivers.FirstOrDefault(driver => IsFileLogDriver(driver.Driver));
            if (fileLogDriver == null)
            {
                return "Add the File data logger driver on the Drivers page to log variables to a file. The setting is kept and takes effect once the driver is present.";
            }

            if (!fileLogDriver.IsEnabled)
            {
                return "The File data logger driver is disabled. Enable it on the Drivers page to log variables to a file.";
            }

            return fileLogDriver.Protocols.Any(protocol => protocol.Protocol is IProtocolVariableSinkProtocol)
                ? "The Data Log Pass-Through protocol of the File data logger driver is disabled. Enable it to log variables to a file."
                : "Add the Data Log Pass-Through protocol to the File data logger driver (Drivers page) to log variables to a file.";
        }
    }

    private void NotifyFileLoggingAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsFileLoggingAvailable));
        OnPropertyChanged(nameof(FileLoggingHint));
    }

    public RelayCommand<object> AddDriverCommand { get; }
    public RelayCommand<object> RemoveDriverCommand { get; }
    public RelayCommand<object> AddProtocolCommand { get; }
    public RelayCommand<object> RemoveProtocolCommand { get; }
    public RelayCommand<object> SelectDriverCommand { get; }
    public RelayCommand<object> AddVariableCommand { get; }
    public RelayCommand<object> RemoveVariableCommand { get; }
    public RelayCommand<object> CopyVariableCommand { get; }
    public RelayCommand<object> ExportVariablesCommand { get; }
    public RelayCommand<object> ImportVariablesCommand { get; }
    public RelayCommand<object> AddVariableToSourceCommand { get; }
    public RelayCommand<object> RemoveCommunicatedVariableCommand { get; }
    public RelayCommand<object> SelectScriptCommand { get; }
    public RelayCommand<object> AddConversionCommand { get; }
    public RelayCommand<object> RemoveConversionCommand { get; }
    public RelayCommand<object> CopyConversionCommand { get; }
    public RelayCommand<object> ExportConversionsCommand { get; }
    public RelayCommand<object> ImportConversionsCommand { get; }
    public RelayCommand<object> AddPresentationCommand { get; }
    public RelayCommand<object> RemovePresentationCommand { get; }
    public RelayCommand<object> CopyPresentationCommand { get; }
    public RelayCommand<object> ExportPresentationsCommand { get; }
    public RelayCommand<object> ImportPresentationsCommand { get; }
    public RelayCommand<object> AddEventCommand { get; }
    public RelayCommand<object> RemoveEventCommand { get; }
    public RelayCommand<object> CopyEventCommand { get; }
    public RelayCommand<object> ExportEventsCommand { get; }
    public RelayCommand<object> ImportEventsCommand { get; }
    public RelayCommand<object> AddScriptCommand { get; }
    public RelayCommand<object> RemoveScriptCommand { get; }
    public RelayCommand<object> ExportScriptCommand { get; }
    public RelayCommand<object> ExportAllScriptsCommand { get; }
    public RelayCommand<object> ImportScriptsCommand { get; }

    public ProjectConfigurationDriverWrapper? SelectedDriver
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            // Notify the driver change BEFORE selecting the protocol: the protocol grid rebinds its
            // ItemsSource on this notification and resets its selection, which the TwoWay binding
            // pushes back as SelectedProtocol = null. Selecting first and notifying afterwards left the
            // settings box bound to the previous driver's protocol (or disabled) for every driver but
            // the first one.
            OnPropertyChanged();
            SelectedProtocol = field?.Protocols.FirstOrDefault();
            RefreshProtocolPluginOptions();
            ErrorMessage = string.Empty;
            AddDriverCommand?.OnCanExecuteChanged();
            RemoveDriverCommand?.OnCanExecuteChanged();
            AddProtocolCommand?.OnCanExecuteChanged();
            RemoveProtocolCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationDriverPluginOption? SelectedDriverPlugin
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            AddDriverCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationLoadedProtocolWrapper? SelectedProtocol
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemoveProtocolCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationProtocolPluginOption? SelectedProtocolPlugin
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            AddProtocolCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationVariableWrapper? SelectedVariable
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemoveVariableCommand?.OnCanExecuteChanged();
            AddVariableToSourceCommand?.OnCanExecuteChanged();
            CopyVariableCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationProtocolVariableWrapper? SelectedCommunicatedVariable
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemoveCommunicatedVariableCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationScriptWrapper? SelectedScript
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemoveScriptCommand?.OnCanExecuteChanged();
            ExportScriptCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationConversionWrapper? SelectedConversion
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemoveConversionCommand?.OnCanExecuteChanged();
            CopyConversionCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationPresentationWrapper? SelectedPresentation
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemovePresentationCommand?.OnCanExecuteChanged();
            CopyPresentationCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationEventWrapper? SelectedEvent
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            RemoveEventCommand?.OnCanExecuteChanged();
            CopyEventCommand?.OnCanExecuteChanged();
        }
    }

    public ProjectConfigurationProtocolOption? SelectedSourceOption
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            AddVariableToSourceCommand?.OnCanExecuteChanged();
        }
    }

    public string ErrorMessage
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasChanges =>
        Project.HasChanges
        || Drivers.Any(driver => driver.HasChanges)
        || addedDrivers.Count > 0
        || removedDrivers.Count > 0
        || Drivers.SelectMany(driver => driver.Protocols).Any(protocol => protocol.HasChanges)
        || addedProtocols.Count > 0
        || removedProtocols.Count > 0
        || Variables.Any(variable => variable.HasChanges)
        || addedVariables.Count > 0
        || removedVariables.Count > 0
        || Conversions.Any(conversion => conversion.HasChanges)
        || addedConversions.Count > 0
        || removedConversions.Count > 0
        || Presentations.Any(presentation => presentation.HasChanges)
        || addedPresentations.Count > 0
        || removedPresentations.Count > 0
        || Events.Any(variableEvent => variableEvent.HasChanges)
        || addedEvents.Count > 0
        || removedEvents.Count > 0
        || CommunicatedVariables.Any(variable => variable.HasChanges)
        || addedCommunicatedVariables.Count > 0
        || removedCommunicatedVariables.Count > 0
        || addedScripts.Count > 0
        || removedScripts.Count > 0
        || Scripts.Any(script => script.HasChanges);

    public ProjectConfigurationNavigationItem SelectedNavigationItem
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            // The message strip relates to an action taken in the current section —
            // switching to another section must not leave it lit.
            ErrorMessage = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSection));
            if (field.Section == ProjectConfigurationSection.Variables)
            {
                EnsureInitialVariableSelections();
            }
        }
    }

    public ProjectConfigurationSection SelectedSection => SelectedNavigationItem.Section;

    public void SelectSection(ProjectConfigurationSection section)
    {
        SelectedNavigationItem = NavigationItems.First(item => item.Section == section);
    }

    // After removing the item at removedIndex the selection stays on the item just above it (the "previous"
    // row from the user's point of view); removing the first row selects the new first row. Jumping back to
    // the top of the list on every removal was reported as annoying when cleaning up several variables in a row.
    private static T? NeighbourAfterRemoval<T>(IList<T> items, int removedIndex) where T : class
    {
        if (items.Count == 0)
        {
            return null;
        }

        return items[Math.Clamp(removedIndex - 1, 0, items.Count - 1)];
    }

    private void EnsureInitialVariableSelections()
    {
        SelectedVariable ??= Variables.FirstOrDefault();
        SelectedSourceOption ??= SourceOptions.FirstOrDefault();
        SelectedCommunicatedVariable ??= SelectedSourceOption == null
            ? CommunicatedVariables.FirstOrDefault()
            : CommunicatedVariables.FirstOrDefault(variable => variable.SelectedSource == SelectedSourceOption)
              ?? CommunicatedVariables.FirstOrDefault();
    }

    public void SetParentWindow(RadWindow window)
    {
        parentWindow = window;
    }

    private bool ApplyChanges()
    {
        ErrorMessage = string.Empty;

        // Names are how components reference each other (presentation -> conversion,
        // variable -> presentation, communicated variable -> event/script), so duplicates
        // or blanks must never be applied — whether they came from a rename, copy or import.
        var validationProblems = ValidateNamesBeforeApply();
        if (validationProblems.Count > 0)
        {
            ReportValidationProblems(validationProblems);
            return false;
        }

        try
        {
            Project.MarkModifiedNow();
            Project.ApplyChanges();
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
            return false;
        }

        // A changed presentation (unit, print format, ...) must refresh the controls of every
        // variable that references it — the variable wrapper itself has no changes then, but
        // controls cache the presentation-derived header texts (unit, label).
        var changedPresentations = Presentations
            .Where(presentation => presentation.HasChanges)
            .Select(presentation => presentation.Presentation)
            .ToHashSet();

        var changedVariables = Variables
            .Where(variable => variable.HasChanges)
            .Select(variable => variable.Variable)
            .Concat(Variables
                .Select(variable => variable.Variable)
                .Where(variable => VariableUsesPresentation(variable, changedPresentations)))
            .Distinct()
            .ToList();

        foreach (var driver in Drivers)
        {
            driver.ApplyChanges();
        }
        foreach (var protocol in Drivers.SelectMany(driver => driver.Protocols))
        {
            protocol.ApplyChanges();
        }
        addedDrivers.Clear();
        removedDrivers.Clear();
        addedProtocols.Clear();
        removedProtocols.Clear();
        foreach (var variable in Variables)
        {
            variable.ApplyChanges();
        }
        foreach (var removedVariable in removedVariables)
        {
            variables.Remove(removedVariable.Variable);
        }
        foreach (var addedVariable in addedVariables)
        {
            if (!variables.Contains(addedVariable.Variable))
            {
                variables.Add(addedVariable.Variable);
            }
        }
        addedVariables.Clear();
        removedVariables.Clear();
        foreach (var conversion in Conversions)
        {
            var previousConversion = conversion.Conversion;
            var appliedConversion = conversion.ApplyChanges();
            if (!ReferenceEquals(previousConversion, appliedConversion))
            {
                ReplaceConversionInstance(previousConversion, appliedConversion);
            }
        }
        foreach (var removedConversion in removedConversions)
        {
            conversions.Remove(removedConversion.Conversion);
        }
        foreach (var addedConversion in addedConversions)
        {
            var conversion = addedConversion.ApplyChanges();
            if (!conversions.Contains(conversion))
            {
                conversions.Add(conversion);
            }
        }
        addedConversions.Clear();
        removedConversions.Clear();

        // Az ted (po Apply) maji vsechny convertery potvrzeny realny nazev (ApplyChanges
        // propsalo currentState.Name do Conversion.Name) a addedConversions je prazdne.
        // Teprve ted se nabidka converteru u prezentaci aktualizuje - pokryva pridani,
        // prejmenovani i odebrani converteru najednou. Volame pred Apply prezentaci, aby
        // GetConversion() pri jejich Apply nasel converter podle aktualniho nazvu.
        RefreshPresentationConverterOptions();

        foreach (var presentation in Presentations)
        {
            presentation.ApplyChanges();
        }
        foreach (var removedPresentation in removedPresentations)
        {
            presentations.Remove(removedPresentation.Presentation);
        }
        foreach (var addedPresentation in addedPresentations)
        {
            if (!presentations.Contains(addedPresentation.Presentation))
            {
                presentations.Add(addedPresentation.Presentation);
            }
        }
        addedPresentations.Clear();
        removedPresentations.Clear();

        // Az ted (po Apply) maji vsechny prezentace potvrzeny realny nazev (ApplyChanges
        // propsalo currentState.Name do Presentation.Name) a addedPresentations je prazdne.
        // Teprve ted se nabidka prezentaci u promennych aktualizuje - pokryva pridani,
        // prejmenovani i odebrani prezentaci najednou.
        RefreshVariablePresentationOptions();

        SynchronizeRenamedEventReferences();
        foreach (var variableEvent in Events)
        {
            var previousEvent = variableEvent.VariableEvent;
            var appliedEvent = variableEvent.ApplyChanges();
            if (!ReferenceEquals(previousEvent, appliedEvent))
            {
                ReplaceEventInstance(previousEvent, appliedEvent);
            }
        }
        foreach (var removedEvent in removedEvents)
        {
            variableEvents.Remove(removedEvent.VariableEvent);
        }
        foreach (var addedEvent in addedEvents)
        {
            var variableEvent = addedEvent.ApplyChanges();
            if (!variableEvents.Contains(variableEvent))
            {
                variableEvents.Add(variableEvent);
            }
        }
        addedEvents.Clear();
        removedEvents.Clear();
        foreach (var removed in removedCommunicatedVariables)
        {
            if (CommunicatedVariables.Any(variable => variable.Variable.Id == removed.ProtocolVariable.Variable.Id))
            {
                continue;
            }

            foreach (var trigger in onValueChangedScriptTriggers
                         .Where(trigger => trigger.VariableId == removed.ProtocolVariable.Variable.Id)
                         .ToList())
            {
                onValueChangedScriptTriggers.Remove(trigger);
            }
        }

        // One failing variable (e.g. a commParam the target protocol rejects) must not abort the
        // whole apply half-way — that used to leave the module in an inconsistent state. The
        // failed wrapper keeps its pending changes, everything else applies normally.
        var communicatedVariableProblems = new List<string>();
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            try
            {
                communicatedVariable.ApplyChanges();
                communicatedVariable.ApplyScriptTrigger(onValueChangedScriptTriggers);
            }
            catch (Exception e)
            {
                communicatedVariableProblems.Add($"{communicatedVariable.DisplayName}: {e.Message}");
            }
        }

        SynchronizeFileLogAndReplayVariables();
        addedCommunicatedVariables.Clear();
        removedCommunicatedVariables.Clear();

        var removedScriptModels = removedScripts
            .Select(script => script.Script)
            .ToList();

        foreach (var script in Scripts)
        {
            script.ApplyChanges();
        }
        foreach (var removedScript in removedScripts)
        {
            RemoveScriptTriggers(removedScript);
            scripts.Remove(removedScript.Script);
        }
        foreach (var addedScript in addedScripts)
        {
            if (!scripts.Any(script => string.Equals(script.FileName, addedScript.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                scripts.Add(addedScript.Script);
            }
        }
        addedScripts.Clear();
        removedScripts.Clear();

        foreach (var variable in changedVariables)
        {
            eventAggregator?.Publish(new VariablePropertiesChangedMsg { Variable = variable });
        }
        if (removedScriptModels.Count > 0)
        {
            eventAggregator?.Publish(new ScriptsRemovedMsg { Scripts = removedScriptModels });
        }

        eventAggregator?.Publish(new ProjectConfigurationAppliedMsg());

        if (communicatedVariableProblems.Count > 0)
        {
            ErrorMessage = "Some variables could not be applied: "
                           + string.Join("; ", communicatedVariableProblems);
        }

        NotifyHasChangesChanged();
        // Partially applied communicated variables leave a message the user must see.
        return communicatedVariableProblems.Count == 0;
    }

    private void Cancel()
    {
        Project.CancelChanges();
        foreach (var driver in Drivers)
        {
            driver.CancelChanges();
        }
        foreach (var protocol in Drivers.SelectMany(driver => driver.Protocols))
        {
            protocol.CancelChanges();
        }
        foreach (var addedProtocol in addedProtocols)
        {
            RemoveProtocolFromSourceOptions(addedProtocol.Protocol.Protocol);
            addedProtocol.Driver.Driver.RemoveProtocol(addedProtocol.Protocol.Protocol);
            addedProtocol.Driver.Protocols.Remove(addedProtocol.Protocol);
            addedProtocol.Protocol.PropertyChanged -= OnProtocolPropertyChanged;
        }
        foreach (var removedProtocol in removedProtocols)
        {
            removedProtocol.Protocol.CancelChanges();
            removedProtocol.Driver.Driver.AddProtocol(removedProtocol.Protocol.Protocol);
            removedProtocol.Driver.Protocols.Add(removedProtocol.Protocol);
            AddProtocolToSourceOptions(removedProtocol.Driver, removedProtocol.Protocol.Protocol);
            removedProtocol.Protocol.PropertyChanged += OnProtocolPropertyChanged;
        }
        foreach (var addedDriver in addedDrivers)
        {
            RemoveDriverFromProject(addedDriver);
        }
        foreach (var removedDriver in removedDrivers)
        {
            RestoreDriverToProject(removedDriver);
        }
        addedDrivers.Clear();
        removedDrivers.Clear();
        addedProtocols.Clear();
        removedProtocols.Clear();
        foreach (var variable in Variables)
        {
            variable.CancelChanges();
        }
        addedVariables.Clear();
        removedVariables.Clear();
        foreach (var conversion in Conversions)
        {
            conversion.CancelChanges();
        }
        addedConversions.Clear();
        removedConversions.Clear();
        foreach (var presentation in Presentations)
        {
            presentation.CancelChanges();
        }
        addedPresentations.Clear();
        removedPresentations.Clear();
        foreach (var variableEvent in Events)
        {
            variableEvent.CancelChanges();
        }
        addedEvents.Clear();
        removedEvents.Clear();
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            communicatedVariable.CancelChanges();
        }
        foreach (var communicatedVariable in addedCommunicatedVariables)
        {
            communicatedVariable.SelectedSource.Protocol.RemoveProtocolVariable(communicatedVariable.ProtocolVariable);
        }
        foreach (var removed in removedCommunicatedVariables)
        {
            removed.Source.Protocol.AddVariable(removed.ProtocolVariable);
        }

        foreach (var script in Scripts)
        {
            script.CancelChanges();
        }
        addedScripts.Clear();
        removedScripts.Clear();

        parentWindow?.Close();
    }

    private void AddDriver()
    {
        if (!CanAddDriver() || SelectedDriverPlugin == null)
        {
            return;
        }

        try
        {
            ErrorMessage = string.Empty;
            var driver = CreateDriver(SelectedDriverPlugin.Plugin);
            if (IsFileLogDriver(driver))
            {
                // The logger is useless without its Data Log Pass-Through sink protocol: add it right
                // here (before the wrapper is built) so the user sees it in the dialog at once and the
                // "Log to FileDataLogger" checkbox becomes available immediately, not only after Apply.
                EnsureSinkProtocol(driver);
            }

            var wrapper = AddDriverToProject(driver, isNew: true);
            addedDrivers.Add(wrapper);
            SelectedDriver = wrapper;

            if (IsFileLogDriver(driver))
            {
                EnsureReplayDriverForFileLogger();
            }

            NotifySourceOptionsChanged();
            NotifyHasChangesChanged();
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    private bool CanAddDriver()
    {
        return SelectedDriverPlugin != null;
    }

    private void RemoveDriver()
    {
        if (SelectedDriver == null)
        {
            return;
        }

        RemoveDriver(SelectedDriver, removePairedReplayDriver: true);
        NotifySourceOptionsChanged();
        NotifyHasChangesChanged();
    }

    private void SelectDriver(object? parameter)
    {
        if (parameter is ProjectConfigurationDriverWrapper driver)
        {
            SelectedDriver = driver;
        }
    }

    private IDriverBase CreateDriver(PluginDetails plugin)
    {
        var pluginLoader = new PluginLoader();
        var driver = pluginLoader.LoadPlugin<IDriverBase>(plugin.PathName)
                     ?? throw new InvalidOperationException($"Driver \"{plugin.Name}\" could not be loaded.");

        driver.Id = CreateUniqueDriverId();
        driver.Label = driver.Specification.Label;
        // Pre-fill the driver's own settings template (DefaultRawSettings) so the operator sees
        // every parameter and only edits the values. No per-driver special-casing here — each
        // driver owns its template.
        driver.RawSettings = driver.DefaultRawSettings;
        driver.RawEncryptedSettings = string.Empty;
        driver.IsEnabled = !driver.Specification.Name.Equals(FileDataReplayDriverName, StringComparison.OrdinalIgnoreCase);
        if (driver is DriverBase driverBase && module is ModuleBase moduleBase)
        {
            driverBase.Logger = moduleBase.Logger;
        }

        // Friendlier instance labels for the auto-paired file logger / replay drivers.
        if (driver.Specification.Name.Equals(FileDataLoggerDriverName, StringComparison.OrdinalIgnoreCase))
        {
            driver.Label = "File data logger";
        }
        else if (driver.Specification.Name.Equals(FileDataReplayDriverName, StringComparison.OrdinalIgnoreCase))
        {
            driver.Label = "File data replay";
        }

        driver.SetConfiguration();
        return driver;
    }

    private ProjectConfigurationDriverWrapper AddDriverToProject(IDriverBase driver, bool isNew)
    {
        if (!drivers.Contains(driver))
        {
            drivers.Add(driver);
        }

        var wrapper = new ProjectConfigurationDriverWrapper(driver, isNew);
        SubscribeDriverWrapper(wrapper);
        Drivers.Add(wrapper);
        foreach (var protocol in wrapper.Protocols)
        {
            AddProtocolToSourceOptions(wrapper, protocol.Protocol);
        }

        NotifyFileLoggingAvailabilityChanged();
        return wrapper;
    }

    private void RemoveDriver(ProjectConfigurationDriverWrapper driver, bool removePairedReplayDriver)
    {
        if (removePairedReplayDriver && IsFileLogDriver(driver.Driver))
        {
            foreach (var replayDriver in Drivers.Where(IsReplayDriverWrapper).ToList())
            {
                RemoveDriver(replayDriver, removePairedReplayDriver: false);
            }
        }

        foreach (var protocol in driver.Protocols.Select(protocol => protocol.Protocol).ToList())
        {
            foreach (var communicatedVariable in CommunicatedVariables
                         .Where(communicatedVariable => ReferenceEquals(communicatedVariable.SelectedSource.Protocol, protocol))
                         .ToList())
            {
                RemoveCommunicatedVariable(communicatedVariable);
            }

            RemoveProtocolFromSourceOptions(protocol);
        }

        UnsubscribeDriverWrapper(driver);
        Drivers.Remove(driver);
        drivers.Remove(driver.Driver);

        if ((!driver.IsNew || !addedDrivers.Remove(driver)) && !removedDrivers.Contains(driver))
        {
            removedDrivers.Add(driver);
        }

        if (SelectedDriver == driver)
        {
            SelectedDriver = Drivers.FirstOrDefault();
        }

        NotifyFileLoggingAvailabilityChanged();
    }

    private void RemoveDriverFromProject(ProjectConfigurationDriverWrapper driver)
    {
        foreach (var protocol in driver.Protocols.Select(protocol => protocol.Protocol).ToList())
        {
            RemoveProtocolFromSourceOptions(protocol);
        }

        UnsubscribeDriverWrapper(driver);
        Drivers.Remove(driver);
        drivers.Remove(driver.Driver);
        NotifyFileLoggingAvailabilityChanged();
    }

    private void RestoreDriverToProject(ProjectConfigurationDriverWrapper driver)
    {
        if (!drivers.Contains(driver.Driver))
        {
            drivers.Add(driver.Driver);
        }

        if (!Drivers.Contains(driver))
        {
            Drivers.Add(driver);
        }

        SubscribeDriverWrapper(driver);
        NotifyFileLoggingAvailabilityChanged();
        foreach (var protocol in driver.Protocols)
        {
            AddProtocolToSourceOptions(driver, protocol.Protocol);
        }
    }

    private void EnsureReplayDriverForFileLogger()
    {
        if (Drivers.Any(IsReplayDriverWrapper))
        {
            return;
        }

        var removedReplayDriver = removedDrivers.FirstOrDefault(IsReplayDriverWrapper);
        if (removedReplayDriver != null)
        {
            removedDrivers.Remove(removedReplayDriver);
            RestoreDriverToProject(removedReplayDriver);
            return;
        }

        var replayDriverPlugin = driverPlugins.FirstOrDefault(plugin =>
            plugin.Name.Equals(FileDataReplayDriverName, StringComparison.OrdinalIgnoreCase));
        if (replayDriverPlugin == null)
        {
            throw new InvalidOperationException("FileDataReplayDriver plugin was not found.");
        }

        var replayDriver = CreateDriver(replayDriverPlugin);
        AddReplayProtocol(replayDriver);
        var replayWrapper = AddDriverToProject(replayDriver, isNew: true);
        addedDrivers.Add(replayWrapper);
    }

    private void AddReplayProtocol(IDriverBase replayDriver)
    {
        var replayProtocolPlugin = protocolPlugins.FirstOrDefault(plugin =>
            plugin.Name.Equals(DataLogReplayProtocolName, StringComparison.OrdinalIgnoreCase));
        if (replayProtocolPlugin == null)
        {
            return;
        }

        var pluginLoader = new PluginLoader();
        var replayProtocol = pluginLoader.LoadPlugin<IProtocolBase>(replayProtocolPlugin.PathName);
        if (replayProtocol == null)
        {
            return;
        }

        replayProtocol.IsEnabled = false;
        replayProtocol.RawSettings = string.Empty;
        replayProtocol.RawEncryptedSettings = string.Empty;
        replayProtocol.SetConfiguration();
        replayDriver.AddProtocol(replayProtocol);
    }

    // FileDataLogger potrebuje sink protokol (PassThroughProtocol), do ktereho se pridavaji logovane
    // promenne - bez nej se "Log to FileDataLogger" po Apply neulozi (a logovani nefunguje).
    // Pridava se automaticky (analogicky k AddReplayProtocol u replay driveru); self-heal i pro
    // drivery pridane drive bez sink protokolu.
    private void EnsureSinkProtocol(IDriverBase fileLogDriver)
    {
        if (fileLogDriver.Protocols.Any(protocol => protocol is IProtocolVariableSinkProtocol))
        {
            return;
        }

        var sinkProtocolPlugin = protocolPlugins.FirstOrDefault(plugin =>
            plugin.Name.Equals(SinkProtocolName, StringComparison.OrdinalIgnoreCase));
        if (sinkProtocolPlugin == null)
        {
            return;
        }

        var pluginLoader = new PluginLoader();
        var sinkProtocol = pluginLoader.LoadPlugin<IProtocolBase>(sinkProtocolPlugin.PathName);
        if (sinkProtocol == null)
        {
            return;
        }

        sinkProtocol.IsEnabled = true;
        sinkProtocol.RawSettings = string.Empty;
        sinkProtocol.RawEncryptedSettings = string.Empty;
        sinkProtocol.SetConfiguration();
        fileLogDriver.AddProtocol(sinkProtocol);
    }

    private int CreateUniqueDriverId()
    {
        var usedIds = Drivers
            .Select(driver => driver.Driver.Id)
            .Concat(drivers.Select(driver => driver.Id))
            .ToHashSet();

        var id = usedIds.Count == 0 ? 1 : usedIds.Max() + 1;
        while (usedIds.Contains(id))
        {
            id++;
        }

        return id;
    }

    private static bool IsReplayDriverWrapper(ProjectConfigurationDriverWrapper driver)
    {
        return IsReplayDriver(driver.Driver);
    }

    private void SubscribeDriverWrapper(ProjectConfigurationDriverWrapper driver)
    {
        driver.PropertyChanged += OnDriverPropertyChanged;
        driver.Protocols.CollectionChanged += OnDriverProtocolsChanged;
        foreach (var protocol in driver.Protocols)
        {
            protocol.PropertyChanged += OnProtocolPropertyChanged;
        }
    }

    private void UnsubscribeDriverWrapper(ProjectConfigurationDriverWrapper driver)
    {
        driver.PropertyChanged -= OnDriverPropertyChanged;
        driver.Protocols.CollectionChanged -= OnDriverProtocolsChanged;
        foreach (var protocol in driver.Protocols)
        {
            protocol.PropertyChanged -= OnProtocolPropertyChanged;
        }
    }

    /// <summary>Adding or removing a protocol (e.g. the logger's Data Log Pass-Through sink) changes
    /// whether "Log to FileDataLogger" can do anything.</summary>
    private void OnDriverProtocolsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyFileLoggingAvailabilityChanged();
    }

    private void AddProtocol()
    {
        if (!CanAddProtocol() || SelectedDriver == null || SelectedProtocolPlugin == null)
        {
            return;
        }

        try
        {
            ErrorMessage = string.Empty;
            var pluginLoader = new PluginLoader();
            var protocol = pluginLoader.LoadPlugin<IProtocolBase>(SelectedProtocolPlugin.Plugin.PathName)
                           ?? throw new InvalidOperationException($"Protocol \"{SelectedProtocolPlugin.Plugin.Name}\" could not be loaded.");
            protocol.IsEnabled = true;
            // Pre-fill the protocol's own settings template (DefaultRawSettings) so the operator
            // sees every parameter and only edits the values (empty for protocols configured purely
            // through per-variable comm params).
            protocol.RawSettings = protocol.DefaultRawSettings;
            protocol.RawEncryptedSettings = string.Empty;
            protocol.SetConfiguration();

            SelectedDriver.Driver.AddProtocol(protocol);
            var wrapper = new ProjectConfigurationLoadedProtocolWrapper(protocol, isNew: true);
            wrapper.PropertyChanged += OnProtocolPropertyChanged;
            SelectedDriver.Protocols.Add(wrapper);
            addedProtocols.Add(new AddedProtocol(SelectedDriver, wrapper));
            AddProtocolToSourceOptions(SelectedDriver, protocol);
            SelectedProtocol = wrapper;

            NotifySourceOptionsChanged();
            NotifyHasChangesChanged();
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    private bool CanAddProtocol()
    {
        return SelectedDriver is { HasManagedProtocols: false }
               && SelectedProtocolPlugin is { IsCompatible: true }
               && SelectedDriver.Protocols.All(protocol => !protocol.Protocol.Specification.Name.Equals(
                   SelectedProtocolPlugin.Plugin.Name,
                   StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshProtocolPluginOptions()
    {
        // Grey out protocols whose ProtocolBase<T> frame type the selected driver does not
        // provide (declared via ITransportSource<T>); an undeclared side stays unrestricted.
        // Protocols designed for one concrete driver additionally narrow themselves by
        // driver Names (CompatibleDrivers); an empty list keeps the type-only match.
        IReadOnlyList<Type> driverTransports = SelectedDriver is { } driverWrapper
            ? TransportTypes.GetDriverTransports(driverWrapper.Driver.GetType())
            : [];
        var driverName = SelectedDriver?.Name;
        var previousPlugin = SelectedProtocolPlugin?.Plugin;

        ProtocolPluginOptions = protocolPlugins
            .Select(plugin =>
            {
                var typeCompatible = TransportTypes.AreCompatible(driverTransports, plugin.Transports);
                var nameCompatible = plugin.CompatibleDrivers.Count == 0
                                     || driverName == null
                                     || plugin.CompatibleDrivers.Contains(driverName, StringComparer.OrdinalIgnoreCase);
                var compatible = typeCompatible && nameCompatible;
                return new ProjectConfigurationProtocolPluginOption(
                    plugin,
                    compatible,
                    compatible
                        ? null
                        : nameCompatible
                            ? BuildCompatibilityHint(plugin.Transports)
                            : BuildCompatibleDriversHint(plugin.CompatibleDrivers));
            })
            .OrderBy(option => option.DisplayName)
            .ToList();
        OnPropertyChanged(nameof(ProtocolPluginOptions));

        SelectedProtocolPlugin =
            ProtocolPluginOptions.FirstOrDefault(option =>
                option.IsCompatible && ReferenceEquals(option.Plugin, previousPlugin))
            ?? ProtocolPluginOptions.FirstOrDefault(option => option.IsCompatible);
    }

    private static string BuildCompatibilityHint(IReadOnlyList<Type> protocolTransports)
    {
        var required = string.Join(" or ", protocolTransports.Select(DescribeTransport));
        return $"Not compatible with the selected driver — requires a driver providing {required}.";
    }

    private string BuildCompatibleDriversHint(IReadOnlyList<string> compatibleDrivers)
    {
        // Show user-facing driver labels; Names are the stable technical ids.
        var drivers = string.Join(" or ", compatibleDrivers.Select(name =>
            driverPlugins.FirstOrDefault(driver =>
                driver.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } driver
                && !string.IsNullOrWhiteSpace(driver.Label)
                ? driver.Label
                : name));
        return $"Not compatible with the selected driver — designed for the {drivers} driver.";
    }

    private static string DescribeTransport(Type transport)
    {
        if (transport == typeof(byte[]))
        {
            return "raw bytes";
        }

        if (transport == typeof(CanFrame))
        {
            return "CAN frames";
        }

        if (transport == typeof(string))
        {
            return "text data";
        }

        if (transport == typeof(DataLogRecord))
        {
            return "data-log records";
        }

        if (transport == typeof(IProtocolVariable))
        {
            return "live variable values";
        }

        return transport.Name;
    }

    private void RemoveProtocol()
    {
        if (SelectedDriver == null || SelectedProtocol == null)
        {
            return;
        }

        var protocol = SelectedProtocol;
        foreach (var communicatedVariable in CommunicatedVariables
                     .Where(communicatedVariable => ReferenceEquals(communicatedVariable.SelectedSource.Protocol, protocol.Protocol))
                     .ToList())
        {
            RemoveCommunicatedVariable(communicatedVariable);
        }

        protocol.PropertyChanged -= OnProtocolPropertyChanged;
        SelectedDriver.Driver.RemoveProtocol(protocol.Protocol);
        SelectedDriver.Protocols.Remove(protocol);
        RemoveProtocolFromSourceOptions(protocol.Protocol);

        var addedProtocol = addedProtocols.FirstOrDefault(addedProtocol => addedProtocol.Protocol == protocol);
        if (addedProtocol != null)
        {
            addedProtocols.Remove(addedProtocol);
        }
        else
        {
            removedProtocols.Add(new RemovedProtocol(SelectedDriver, protocol));
        }

        SelectedProtocol = SelectedDriver.Protocols.FirstOrDefault();
        NotifySourceOptionsChanged();
        NotifyHasChangesChanged();
    }

    private void AddProtocolToSourceOptions(ProjectConfigurationDriverWrapper driver, IProtocolBase protocol)
    {
        if (!IsSourceDriver(driver.Driver))
        {
            return;
        }

        if (SourceOptions.Any(option => ReferenceEquals(option.Protocol, protocol)))
        {
            return;
        }

        SourceOptions.Add(new ProjectConfigurationProtocolOption(driver.Driver, protocol));
    }

    private void RemoveProtocolFromSourceOptions(IProtocolBase protocol)
    {
        foreach (var sourceOption in SourceOptions
                     .Where(option => ReferenceEquals(option.Protocol, protocol))
                     .ToList())
        {
            SourceOptions.Remove(sourceOption);
        }

        if (SelectedSourceOption != null && ReferenceEquals(SelectedSourceOption.Protocol, protocol))
        {
            SelectedSourceOption = SourceOptions.FirstOrDefault();
        }
    }

    private void NotifySourceOptionsChanged()
    {
        OnPropertyChanged(nameof(SourceOptions));
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            communicatedVariable.RefreshSourceOptions();
        }
    }

    /// <summary>Variable type created by the + button; the combo next to it selects the type.</summary>
    public IReadOnlyList<string> NewVariableTypeOptions { get; } = ["Scalar", "Matrix"];

    public string NewVariableType
    {
        get;
        set { field = value; OnPropertyChanged(); }
    } = "Scalar";

    private void AddVariable()
    {
        if (!CanAddVariable())
        {
            return;
        }

        IVariableBase variable = NewVariableType == "Matrix"
            ? CreateNewMatrixVariable()
            : CreateNewScalarVariable();
        AddVariableWrapper(variable);
    }

    private ScalarVariable CreateNewScalarVariable()
    {
        var values = ValuesGlobal.CreateInstance(ValuesGlobal.ValueDataType.Int);
        values.ValPresentation = Presentations.First().Presentation;
        return new ScalarVariable
        {
            Id = CreateUniqueVariableId(),
            Namespace = "/",
            Name = CreateUniqueVariableName(),
            Label = "Variable",
            Description = string.Empty,
            Size = values.Size,
            Values = values
        };
    }

    private MatrixVariable CreateNewMatrixVariable()
    {
        // A small valid value block to start from; the Layout field shapes it into a curve/map.
        return new MatrixVariable
        {
            Id = CreateUniqueVariableId(),
            Namespace = "/",
            Name = CreateUniqueVariableName(),
            Label = "Variable",
            Description = string.Empty,
            DefaultDataType = ValuesGlobal.ValueDataType.Byte,
            Data = new MatrixSection { Count = 4 }
        };
    }

    private bool CanAddVariable()
    {
        return Presentations.Count > 0;
    }

    private void AddVariableWrapper(IVariableBase variable)
    {
        var wrapper = new ProjectConfigurationVariableWrapper(
            variable,
            Presentations.Select(presentation => presentation.Presentation),
            isNew: true);
        wrapper.PropertyChanged += OnVariablePropertyChanged;
        Variables.Add(wrapper);
        addedVariables.Add(wrapper);
        SelectedVariable = wrapper;

        ExportVariablesCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private void CopyVariable()
    {
        if (SelectedVariable == null)
        {
            return;
        }

        try
        {
            var copy = SelectedVariable.CreateVariableSnapshot(
                CreateUniqueVariableId(),
                MakeUniqueVariableName($"{SelectedVariable.Name}_copy"));
            AddVariableWrapper(copy);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Variable could not be copied: {e.Message}";
        }
    }

    private void ExportVariables()
    {
        // All variables are exported — only their definitions; protocol bindings are
        // not part of a standalone variables file.
        if (Variables.Count == 0)
        {
            ErrorMessage = "There are no variables to export.";
            return;
        }

        if (!TryPickExportFile("variables.xml", out var filePath))
        {
            return;
        }

        try
        {
            var snapshots = Variables
                .Select(variable => variable.CreateVariableSnapshot(variable.Id, variable.Name))
                .ToList();
            var variablesFile = new XmlVariablesFile
            {
                Variables = XmlVariableMapper.ToXmlVariables(snapshots)
            };
            XmlInOut<XmlVariablesFile>.SaveToFile(filePath, variablesFile);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Variables could not be exported: {e.Message}";
        }
    }

    private void ImportVariables()
    {
        if (!TryPickImportFile(out var filePath))
        {
            return;
        }

        try
        {
            var variablesFile = XmlInOut<XmlVariablesFile>.LoadFromFile(filePath);
            var xmlVariables = variablesFile.Variables ?? [];
            if (xmlVariables.Count == 0)
            {
                ErrorMessage = "No variables were found in the selected file.";
                return;
            }

            var existingNames = Variables
                .Select(variable => variable.Name)
                .Concat(variables.Select(variable => variable.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var presentationObjects = Presentations
                .Select(presentation => presentation.Presentation)
                .ToList();
            // Exact match — the mapper resolves the presentation reference the same way.
            var presentationNames = presentationObjects
                .Select(presentation => presentation.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);

            var skipped = new List<string>();
            var importedCount = 0;
            foreach (var xmlVariable in xmlVariables)
            {
                if (string.IsNullOrWhiteSpace(xmlVariable.Name))
                {
                    skipped.Add("(record without a name)");
                    continue;
                }

                // An already existing variable must not be imported (no overwrite, no rename).
                if (existingNames.Contains(xmlVariable.Name))
                {
                    skipped.Add($"{xmlVariable.Name} — already exists in the project");
                    continue;
                }

                // A variable requiring a presentation the project does not have is not imported.
                var missingPresentation = GetVariablePresentationRefs(xmlVariable)
                    .FirstOrDefault(reference => !string.IsNullOrEmpty(reference) && !presentationNames.Contains(reference));
                if (missingPresentation != null)
                {
                    skipped.Add($"{xmlVariable.Name} — presentation \"{missingPresentation}\" not found in the project");
                    continue;
                }

                var variable = XmlVariableMapper.FromXmlVariables([xmlVariable], presentationObjects).FirstOrDefault();
                if (variable == null)
                {
                    skipped.Add($"{xmlVariable.Name} — unsupported record type");
                    continue;
                }

                variable.Id = CreateUniqueVariableId();
                AddVariableWrapper(variable);
                existingNames.Add(variable.Name);
                importedCount++;
            }

            ReportImportResult("variables", importedCount, skipped);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Variables could not be imported: {e.Message}";
        }
    }

    private static bool VariableUsesPresentation(IVariableBase variable, IReadOnlySet<IPresentation> presentations)
    {
        if (presentations.Count == 0)
        {
            return false;
        }

        return variable switch
        {
            ScalarVariable scalar => scalar.Values?.ValPresentation != null &&
                                     presentations.Contains(scalar.Values.ValPresentation),
            MatrixVariable matrix =>
                (matrix.XAxis?.Presentation != null && presentations.Contains(matrix.XAxis.Presentation)) ||
                (matrix.YAxis?.Presentation != null && presentations.Contains(matrix.YAxis.Presentation)) ||
                (matrix.Data.Presentation != null && presentations.Contains(matrix.Data.Presentation)),
            _ => false
        };
    }

    private static IEnumerable<string?> GetVariablePresentationRefs(XmlVariable xmlVariable)
    {
        return xmlVariable switch
        {
            XmlScalarVariable scalar => [scalar.Values?.PresentationReference?.Ref],
            XmlMatrixVariable matrix =>
            [
                matrix.XAxis?.PresentationReference?.Ref,
                matrix.YAxis?.PresentationReference?.Ref,
                matrix.Data?.PresentationReference?.Ref
            ],
            _ => []
        };
    }

    // Import results do not go to the footer message strip: a long skip list would
    // inflate the footer and squeeze the section content. Every skipped item is written
    // to the application log and a summary window is shown on top of the dialog.
    private void ReportImportResult(string what, int importedCount, IReadOnlyCollection<string> skipped)
    {
        ErrorMessage = string.Empty;
        if (skipped.Count == 0)
        {
            return;
        }

        foreach (var item in skipped)
        {
            Logger?.Log(LogLevel.Warn, $"Import of {what}: skipped {item}");
        }

        ShowSummaryWindow("Import", $"Imported {importedCount} {what}, skipped {skipped.Count}:", skipped);
    }

    private void ShowSummaryWindow(string header, string headline, IReadOnlyCollection<string> items)
    {
        const int maxListedItems = 15;
        var summary = headline
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, items.Take(maxListedItems).Select(item => $"• {item}"));
        if (items.Count > maxListedItems)
        {
            summary += Environment.NewLine
                + $"… and {items.Count - maxListedItems} more (see the application log).";
        }

        RadWindow.Alert(new DialogParameters
        {
            Header = header,
            Content = summary,
            Owner = App.Current.MainWindow,
            DialogStartupLocation = WindowStartupLocation.CenterOwner
        });
    }

    private ILogger? Logger => (module as ModuleBase)?.Logger;

    private List<string> ValidateNamesBeforeApply()
    {
        var problems = new List<string>();

        AddNameProblems(problems, "variable name", Variables.Select(variable => variable.Name));
        AddNameProblems(problems, "conversion name", Conversions.Select(conversion => conversion.Name));
        AddNameProblems(problems, "presentation name", Presentations.Select(presentation => presentation.Name));
        AddNameProblems(problems, "event name", Events.Select(variableEvent => variableEvent.Name));
        AddNameProblems(problems, "script file name", Scripts.Select(script => script.FileName));

        problems.AddRange(Variables
            .GroupBy(variable => variable.Id)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate variable Id {group.Key}: "
                + string.Join(", ", group.Select(variable => $"\"{variable.Name}\""))));

        return problems;
    }

    private static void AddNameProblems(List<string> problems, string what, IEnumerable<string> names)
    {
        var nameList = names.ToList();
        var blankCount = nameList.Count(string.IsNullOrWhiteSpace);
        if (blankCount > 0)
        {
            problems.Add($"empty {what} ({blankCount}x)");
        }

        problems.AddRange(nameList
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate {what}: \"{group.Key}\" ({group.Count()}x)"));
    }

    private void ReportValidationProblems(IReadOnlyCollection<string> problems)
    {
        foreach (var problem in problems)
        {
            Logger?.Log(LogLevel.Warn, $"Project configuration cannot be applied: {problem}");
        }

        ShowSummaryWindow("Apply", "The configuration cannot be applied — fix the following first:", problems);
    }

    private bool TryPickExportFile(string defaultFileName, out string filePath)
    {
        var dialog = new RadSaveFileDialog
        {
            Owner = App.Current.MainWindow,
            Filter = XmlFileFilter,
            FileName = defaultFileName,
            InitialDirectory = lastXmlDialogDirectory ?? string.Empty
        };
        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            filePath = string.Empty;
            return false;
        }

        filePath = dialog.FileName;
        lastXmlDialogDirectory = Path.GetDirectoryName(dialog.FileName);
        return true;
    }

    private bool TryPickImportFile(out string filePath)
    {
        var dialog = new RadOpenFileDialog
        {
            Owner = App.Current.MainWindow,
            Multiselect = false,
            Filter = XmlFileFilter,
            InitialDirectory = lastXmlDialogDirectory ?? string.Empty
        };
        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            filePath = string.Empty;
            return false;
        }

        filePath = dialog.FileName;
        lastXmlDialogDirectory = Path.GetDirectoryName(dialog.FileName);
        return true;
    }

    private void RemoveVariable()
    {
        if (SelectedVariable == null)
        {
            return;
        }

        var usages = QueryVariableUsage(SelectedVariable.Variable);
        if (usages.Count > 0)
        {
            ErrorMessage = BuildVariableInUseMessage(SelectedVariable, usages);
            return;
        }

        var variable = SelectedVariable;
        foreach (var communicatedVariable in CommunicatedVariables
                     .Where(communicatedVariable => communicatedVariable.Variable.Id == variable.Id)
                     .ToList())
        {
            RemoveCommunicatedVariable(communicatedVariable);
        }

        var removedIndex = Variables.IndexOf(variable);
        variable.PropertyChanged -= OnVariablePropertyChanged;
        Variables.Remove(variable);

        if ((!variable.IsNew || !addedVariables.Remove(variable)) && !removedVariables.Contains(variable))
        {
            removedVariables.Add(variable);
        }

        SelectedVariable = NeighbourAfterRemoval(Variables, removedIndex);
        ExportVariablesCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    // A variable still assigned to a driver/protocol must not be deletable: remove it from the source
    // (left arrow) first. The "-" button stays disabled while the variable is communicated anywhere.
    private bool CanRemoveVariable()
    {
        return SelectedVariable != null && !IsVariableAssignedToSource(SelectedVariable);
    }

    private bool IsVariableAssignedToSource(ProjectConfigurationVariableWrapper variable)
    {
        return CommunicatedVariables.Any(communicated => communicated.Variable.Id == variable.Id);
    }

    private List<VariableUsage> QueryVariableUsage(IVariableBase variable)
    {
        var query = new VariableUsageQuery { Variable = variable };
        eventAggregator?.Publish(query);
        return query.Usages;
    }

    private static string BuildVariableInUseMessage(
        ProjectConfigurationVariableWrapper variable,
        IReadOnlyCollection<VariableUsage> usages)
    {
        return $"Variable '{variable.DisplayName}' cannot be deleted because it is still used:"
               + Environment.NewLine
               + FormatVariableUsageLines(usages)
               + Environment.NewLine
               + "Remove it from these controls first, then delete the variable.";
    }

    private static string BuildCommunicatedVariableInUseMessage(
        ProjectConfigurationProtocolVariableWrapper communicatedVariable,
        IReadOnlyCollection<VariableUsage> usages)
    {
        return $"Variable '{communicatedVariable.DisplayName}' cannot be removed from {communicatedVariable.SourceText} because it is still used:"
               + Environment.NewLine
               + FormatVariableUsageLines(usages)
               + Environment.NewLine
               + "Remove it from these controls first, then remove it from the driver/protocol.";
    }

    private static string FormatVariableUsageLines(IReadOnlyCollection<VariableUsage> usages)
    {
        return string.Join(
            Environment.NewLine,
            usages.Select(usage => $"  • {usage.WorkspaceName}/{usage.ControlLabel}"));
    }

    private int CreateUniqueVariableId()
    {
        var usedIds = Variables
            .Select(variable => variable.Id)
            .Concat(variables.Select(variable => variable.Id))
            .ToHashSet();

        var id = usedIds.Count == 0 ? 1 : usedIds.Max() + 1;
        while (usedIds.Contains(id))
        {
            id++;
        }

        return id;
    }

    private string CreateUniqueVariableName()
    {
        return MakeUniqueVariableName("variable");
    }

    private string MakeUniqueVariableName(string desiredName)
    {
        var existingNames = Variables
            .Select(variable => variable.Name)
            .Concat(variables.Select(variable => variable.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return MakeUniqueName(desiredName, existingNames);
    }

    private void AddConversion()
    {
        var conversion = ProjectConfigurationConversionWrapper.CreateConversion(
            CreateUniqueConversionName(),
            ConversionsGlobal.ConversionType.Linear);
        AddConversionWrapper(conversion);
    }

    private void AddConversionWrapper(IValConversion conversion)
    {
        var wrapper = new ProjectConfigurationConversionWrapper(conversion, isNew: true);
        wrapper.PropertyChanged += OnConversionPropertyChanged;
        Conversions.Add(wrapper);
        addedConversions.Add(wrapper);
        SelectedConversion = wrapper;

        ExportConversionsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private void CopyConversion()
    {
        if (SelectedConversion == null)
        {
            return;
        }

        try
        {
            var copy = SelectedConversion.CreateConversionSnapshot(
                MakeUniqueConversionName($"{SelectedConversion.Name}_copy"));
            AddConversionWrapper(copy);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Conversion could not be copied: {e.Message}";
        }
    }

    private void ExportConversions()
    {
        if (Conversions.Count == 0)
        {
            ErrorMessage = "There are no conversions to export.";
            return;
        }

        if (!TryPickExportFile("conversions.xml", out var filePath))
        {
            return;
        }

        try
        {
            var snapshots = Conversions
                .Select(conversion => conversion.CreateConversionSnapshot(conversion.Name))
                .ToList();
            var conversionsFile = new XmlConversionsFile
            {
                Conversions = XmlComponentMapper.ToXmlConversions(snapshots)
            };
            XmlInOut<XmlConversionsFile>.SaveToFile(filePath, conversionsFile);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Conversions could not be exported: {e.Message}";
        }
    }

    private void ImportConversions()
    {
        if (!TryPickImportFile(out var filePath))
        {
            return;
        }

        try
        {
            var conversionsFile = XmlInOut<XmlConversionsFile>.LoadFromFile(filePath);
            var xmlConversions = conversionsFile.Conversions ?? [];
            if (xmlConversions.Count == 0)
            {
                ErrorMessage = "No conversions were found in the selected file.";
                return;
            }

            var existingNames = Conversions
                .Select(conversion => conversion.Name)
                .Concat(conversions.Select(conversion => conversion.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var skipped = new List<string>();
            var importedCount = 0;
            foreach (var xmlConversion in xmlConversions)
            {
                if (string.IsNullOrWhiteSpace(xmlConversion.Name))
                {
                    skipped.Add("(record without a name)");
                    continue;
                }

                if (existingNames.Contains(xmlConversion.Name))
                {
                    skipped.Add($"{xmlConversion.Name} — already exists in the project");
                    continue;
                }

                var conversion = XmlComponentMapper.FromXmlConversions([xmlConversion]).FirstOrDefault();
                if (conversion == null)
                {
                    skipped.Add($"{xmlConversion.Name} — unsupported record type");
                    continue;
                }

                AddConversionWrapper(conversion);
                existingNames.Add(conversion.Name);
                importedCount++;
            }

            ReportImportResult("conversions", importedCount, skipped);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Conversions could not be imported: {e.Message}";
        }
    }

    private void RemoveConversion()
    {
        if (SelectedConversion == null)
        {
            return;
        }

        var conversion = SelectedConversion;
        conversion.PropertyChanged -= OnConversionPropertyChanged;
        Conversions.Remove(conversion);

        if (conversion.IsNew)
        {
            addedConversions.Remove(conversion);
        }
        else if (!removedConversions.Contains(conversion))
        {
            removedConversions.Add(conversion);
        }

        SelectedConversion = Conversions.FirstOrDefault();

        RefreshPresentationConverterOptions();

        ExportConversionsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    // A conversion type change produces a new conversion object. It takes the place of the previous
    // one in the project and every presentation that pointed to the previous object is re-pointed,
    // otherwise the presentations would keep converting with an object the project no longer holds.
    private void ReplaceConversionInstance(IValConversion previousConversion, IValConversion appliedConversion)
    {
        var index = conversions.IndexOf(previousConversion);
        if (index >= 0)
        {
            conversions[index] = appliedConversion;
        }

        foreach (var presentation in presentations
                     .Concat(Presentations.Select(presentation => presentation.Presentation))
                     .Distinct()
                     .Where(presentation => ReferenceEquals(presentation.Conversion, previousConversion)))
        {
            presentation.Conversion = appliedConversion;
        }
    }

    private string CreateUniqueConversionName()
    {
        return MakeUniqueConversionName("conversion");
    }

    private string MakeUniqueConversionName(string desiredName)
    {
        var existingNames = Conversions
            .Select(conversion => conversion.Name)
            .Concat(conversions.Select(conversion => conversion.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return MakeUniqueName(desiredName, existingNames);
    }

    private static string MakeUniqueName(string desiredName, ISet<string> existingNames)
    {
        var name = desiredName;
        var index = 1;
        while (existingNames.Contains(name))
        {
            name = $"{desiredName}_{index}";
            index++;
        }

        return name;
    }

    private ProjectConfigurationPresentationWrapper CreatePresentationWrapper(IPresentation presentation, bool isNew = false)
    {
        return new ProjectConfigurationPresentationWrapper(
            presentation,
            Conversions.Select(conversion => conversion.Conversion),
            isNew);
    }

    private void AddPresentation()
    {
        if (!CanAddPresentation())
        {
            return;
        }

        var conversion = Conversions.First().Conversion;
        var presentation = new Presentation
        {
            Name = CreateUniquePresentationName(),
            Min = 0,
            Max = 1,
            PrintFormat = "{0}",
            Unit = string.Empty,
            Conversion = conversion
        };
        AddPresentationWrapper(presentation);
    }

    private void AddPresentationWrapper(IPresentation presentation)
    {
        var wrapper = CreatePresentationWrapper(presentation, isNew: true);
        wrapper.PropertyChanged += OnPresentationPropertyChanged;
        Presentations.Add(wrapper);
        addedPresentations.Add(wrapper);
        SelectedPresentation = wrapper;

        // Refresh nabidky prezentaci u promennych se NEdela zde - nove pridana prezentace
        // neni potvrzena. Do nabidky u promennych se dostane az po Apply (viz ApplyChanges).
        ExportPresentationsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private void CopyPresentation()
    {
        if (SelectedPresentation == null)
        {
            return;
        }

        try
        {
            var copy = SelectedPresentation.CreatePresentationSnapshot(
                MakeUniquePresentationName($"{SelectedPresentation.Name}_copy"));
            AddPresentationWrapper(copy);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Presentation could not be copied: {e.Message}";
        }
    }

    private void ExportPresentations()
    {
        if (Presentations.Count == 0)
        {
            ErrorMessage = "There are no presentations to export.";
            return;
        }

        if (!TryPickExportFile("presentations.xml", out var filePath))
        {
            return;
        }

        try
        {
            var snapshots = Presentations
                .Select(presentation => presentation.CreatePresentationSnapshot(presentation.Name))
                .ToList();
            var presentationsFile = new XmlPresentationsFile
            {
                Presentations = XmlComponentMapper.ToXmlPresentations(snapshots)
            };
            XmlInOut<XmlPresentationsFile>.SaveToFile(filePath, presentationsFile);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Presentations could not be exported: {e.Message}";
        }
    }

    private void ImportPresentations()
    {
        if (!TryPickImportFile(out var filePath))
        {
            return;
        }

        try
        {
            var presentationsFile = XmlInOut<XmlPresentationsFile>.LoadFromFile(filePath);
            var xmlPresentations = presentationsFile.Presentations ?? [];
            if (xmlPresentations.Count == 0)
            {
                ErrorMessage = "No presentations were found in the selected file.";
                return;
            }

            var existingNames = Presentations
                .Select(presentation => presentation.Name)
                .Concat(presentations.Select(presentation => presentation.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Conversions are referenced by name and must already exist in the project —
            // import conversions first if the file relies on them.
            var conversionObjects = Conversions
                .Select(conversion => conversion.Conversion)
                .ToList();

            var skipped = new List<string>();
            var importedCount = 0;
            foreach (var xmlPresentation in xmlPresentations)
            {
                if (string.IsNullOrWhiteSpace(xmlPresentation.Name))
                {
                    skipped.Add("(record without a name)");
                    continue;
                }

                if (existingNames.Contains(xmlPresentation.Name))
                {
                    skipped.Add($"{xmlPresentation.Name} — already exists in the project");
                    continue;
                }

                var presentation = XmlComponentMapper
                    .FromXmlPresentations([xmlPresentation], conversionObjects)
                    .FirstOrDefault();
                if (presentation == null)
                {
                    skipped.Add($"{xmlPresentation.Name} — conversion "
                        + $"\"{xmlPresentation.ConversionReference?.Ref}\" not found in the project");
                    continue;
                }

                AddPresentationWrapper(presentation);
                existingNames.Add(presentation.Name);
                importedCount++;
            }

            ReportImportResult("presentations", importedCount, skipped);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Presentations could not be imported: {e.Message}";
        }
    }

    private bool CanAddPresentation()
    {
        return Conversions.Count > 0;
    }

    private void RemovePresentation()
    {
        if (SelectedPresentation == null)
        {
            return;
        }

        if (IsPresentationUsed(SelectedPresentation))
        {
            ErrorMessage = $"Presentation \"{SelectedPresentation.Name}\" is used by a variable.";
            return;
        }

        ErrorMessage = string.Empty;
        var presentation = SelectedPresentation;
        presentation.PropertyChanged -= OnPresentationPropertyChanged;
        Presentations.Remove(presentation);

        if (presentation.IsNew)
        {
            addedPresentations.Remove(presentation);
        }
        else if (!removedPresentations.Contains(presentation))
        {
            removedPresentations.Add(presentation);
        }

        SelectedPresentation = Presentations.FirstOrDefault();

        RefreshVariablePresentationOptions();

        ExportPresentationsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private bool CanRemovePresentation()
    {
        return SelectedPresentation != null && !IsPresentationUsed(SelectedPresentation);
    }

    private bool IsPresentationUsed(ProjectConfigurationPresentationWrapper presentation)
    {
        return Variables.Any(variable =>
            variable.Variable is ScalarVariable scalarVariable
            && ReferenceEquals(scalarVariable.Values.ValPresentation, presentation.Presentation));
    }

    private string CreateUniquePresentationName()
    {
        return MakeUniquePresentationName("presentation");
    }

    private string MakeUniquePresentationName(string desiredName)
    {
        var existingNames = Presentations
            .Select(presentation => presentation.Name)
            .Concat(presentations.Select(presentation => presentation.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return MakeUniqueName(desiredName, existingNames);
    }

    private void AddEvent()
    {
        var variableEvent = ProjectConfigurationEventWrapper.CreateEvent(
            CreateUniqueEventName(),
            EventsGlobal.VariableEventType.Periodic);
        AddEventWrapper(variableEvent);
    }

    private void AddEventWrapper(IVarEvent variableEvent)
    {
        var wrapper = new ProjectConfigurationEventWrapper(variableEvent, isNew: true);
        wrapper.PropertyChanged += OnEventPropertyChanged;
        Events.Add(wrapper);
        addedEvents.Add(wrapper);
        SelectedEvent = wrapper;

        RefreshCommunicatedVariableEventOptions();
        ExportEventsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private void CopyEvent()
    {
        if (SelectedEvent == null)
        {
            return;
        }

        try
        {
            var copy = SelectedEvent.CreateEventSnapshot(
                MakeUniqueEventName($"{SelectedEvent.Name}_copy"));
            AddEventWrapper(copy);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Event could not be copied: {e.Message}";
        }
    }

    private void ExportEvents()
    {
        if (Events.Count == 0)
        {
            ErrorMessage = "There are no events to export.";
            return;
        }

        if (!TryPickExportFile("events.xml", out var filePath))
        {
            return;
        }

        try
        {
            var snapshots = Events
                .Select(variableEvent => variableEvent.CreateEventSnapshot(variableEvent.Name))
                .ToList();
            var eventsFile = new XmlVarEventsFile
            {
                Events = XmlComponentMapper.ToXmlVarEvents(snapshots)
            };
            XmlInOut<XmlVarEventsFile>.SaveToFile(filePath, eventsFile);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Events could not be exported: {e.Message}";
        }
    }

    private void ImportEvents()
    {
        if (!TryPickImportFile(out var filePath))
        {
            return;
        }

        try
        {
            var eventsFile = XmlInOut<XmlVarEventsFile>.LoadFromFile(filePath);
            var xmlEvents = eventsFile.Events ?? [];
            if (xmlEvents.Count == 0)
            {
                ErrorMessage = "No events were found in the selected file.";
                return;
            }

            var existingNames = Events
                .Select(variableEvent => variableEvent.Name)
                .Concat(variableEvents.Select(variableEvent => variableEvent.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var skipped = new List<string>();
            var importedCount = 0;
            foreach (var xmlEvent in xmlEvents)
            {
                if (string.IsNullOrWhiteSpace(xmlEvent.Name))
                {
                    skipped.Add("(record without a name)");
                    continue;
                }

                if (existingNames.Contains(xmlEvent.Name))
                {
                    skipped.Add($"{xmlEvent.Name} — already exists in the project");
                    continue;
                }

                var variableEvent = XmlComponentMapper.FromXmlVarEvents([xmlEvent]).FirstOrDefault();
                if (variableEvent == null)
                {
                    skipped.Add($"{xmlEvent.Name} — unsupported record type");
                    continue;
                }

                AddEventWrapper(variableEvent);
                existingNames.Add(variableEvent.Name);
                importedCount++;
            }

            ReportImportResult("events", importedCount, skipped);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Events could not be imported: {e.Message}";
        }
    }

    private void RemoveEvent()
    {
        if (SelectedEvent == null)
        {
            return;
        }

        if (IsEventUsed(SelectedEvent))
        {
            ErrorMessage = $"Event \"{SelectedEvent.Name}\" is used by a communicated variable.";
            return;
        }

        ErrorMessage = string.Empty;
        var variableEvent = SelectedEvent;
        variableEvent.PropertyChanged -= OnEventPropertyChanged;
        Events.Remove(variableEvent);

        if (variableEvent.IsNew)
        {
            addedEvents.Remove(variableEvent);
        }
        else if (!removedEvents.Contains(variableEvent))
        {
            removedEvents.Add(variableEvent);
        }

        SelectedEvent = Events.FirstOrDefault();
        RefreshCommunicatedVariableEventOptions();
        ExportEventsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private bool CanRemoveEvent()
    {
        return SelectedEvent != null && !IsEventUsed(SelectedEvent);
    }

    private bool IsEventUsed(ProjectConfigurationEventWrapper variableEvent)
    {
        var eventNames = new[] { variableEvent.Name, variableEvent.OriginalName, variableEvent.VariableEvent.Name }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return CommunicatedVariables.Any(variable => eventNames.Contains(variable.SelectedVariableEventName));
    }

    private void SynchronizeRenamedEventReferences()
    {
        foreach (var variableEvent in Events.Where(variableEvent =>
                     !string.Equals(variableEvent.OriginalName, variableEvent.Name, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var communicatedVariable in CommunicatedVariables.Where(communicatedVariable =>
                         string.Equals(communicatedVariable.SelectedVariableEventName, variableEvent.OriginalName, StringComparison.OrdinalIgnoreCase)))
            {
                communicatedVariable.SelectedVariableEventName = variableEvent.Name;
            }
        }
    }

    // An event type change produces a new event object. It takes the place of the previous one in
    // the project; protocol variables hold the event object itself, so those bound to this event
    // are rebuilt on their Apply (names are already synchronized at this point).
    private void ReplaceEventInstance(IVarEvent previousEvent, IVarEvent appliedEvent)
    {
        var index = variableEvents.IndexOf(previousEvent);
        if (index >= 0)
        {
            variableEvents[index] = appliedEvent;
        }

        foreach (var communicatedVariable in CommunicatedVariables.Where(communicatedVariable =>
                     string.Equals(communicatedVariable.SelectedVariableEventName, appliedEvent.Name, StringComparison.OrdinalIgnoreCase)))
        {
            communicatedVariable.RequestRebuild();
        }
    }

    private string CreateUniqueEventName()
    {
        return MakeUniqueEventName("event");
    }

    private string MakeUniqueEventName(string desiredName)
    {
        var existingNames = Events
            .Select(variableEvent => variableEvent.Name)
            .Concat(variableEvents.Select(variableEvent => variableEvent.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return MakeUniqueName(desiredName, existingNames);
    }

    private void AddScript()
    {
        var script = new PyScript
        {
            FileName = CreateUniqueScriptFileName(),
            IsEnabled = true,
            IsReplayEnabled = false,
            ExecutionMode = ScriptExecutionMode.Manual,
            Blocking = true
        };
        AddScriptWrapper(script);
    }

    private void AddScriptWrapper(IScriptBase script)
    {
        var wrapper = new ProjectConfigurationScriptWrapper(script, isNew: true);
        wrapper.PropertyChanged += OnScriptPropertyChanged;
        Scripts.Add(wrapper);
        addedScripts.Add(wrapper);
        SelectedScript = wrapper;

        RefreshCommunicatedVariableScriptOptions();
        ExportAllScriptsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    // Scripts are exported/imported as plain .py files — the script content is what
    // matters; execution settings stay project-specific.
    private void ExportSelectedScript()
    {
        if (SelectedScript == null)
        {
            return;
        }

        var defaultFileName = Path.GetFileName(SelectedScript.FileName);
        if (string.IsNullOrWhiteSpace(defaultFileName))
        {
            ErrorMessage = "The selected script has no file name.";
            return;
        }

        var dialog = new RadSaveFileDialog
        {
            Owner = App.Current.MainWindow,
            Filter = "Python scripts (*.py)|*.py|All files (*.*)|*.*",
            FileName = defaultFileName,
            InitialDirectory = lastXmlDialogDirectory ?? string.Empty
        };
        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, SelectedScript.Script.Content ?? string.Empty);
            lastXmlDialogDirectory = Path.GetDirectoryName(dialog.FileName);
            Logger?.Log(LogLevel.Info, $"Exported script \"{defaultFileName}\" to \"{dialog.FileName}\".");
        }
        catch (Exception e)
        {
            ErrorMessage = $"Script could not be exported: {e.Message}";
        }
    }

    private void ExportAllScripts()
    {
        if (Scripts.Count == 0)
        {
            ErrorMessage = "There are no scripts to export.";
            return;
        }

        var dialog = new RadOpenFolderDialog
        {
            Owner = App.Current.MainWindow,
            InitialDirectory = lastXmlDialogDirectory ?? string.Empty
        };
        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        dialog.ShowDialog();
        if (dialog.DialogResult != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var directory = dialog.FileName;
        lastXmlDialogDirectory = directory;

        var failed = new List<string>();
        var exportedCount = 0;
        foreach (var script in Scripts)
        {
            var fileName = Path.GetFileName(script.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                failed.Add("(script without a file name)");
                continue;
            }

            try
            {
                File.WriteAllText(Path.Combine(directory, fileName), script.Script.Content ?? string.Empty);
                exportedCount++;
            }
            catch (Exception e)
            {
                failed.Add($"{fileName} — {e.Message}");
            }
        }

        Logger?.Log(LogLevel.Info, $"Exported {exportedCount} script(s) to \"{directory}\".");
        if (failed.Count > 0)
        {
            foreach (var item in failed)
            {
                Logger?.Log(LogLevel.Warn, $"Export of scripts: {item}");
            }

            ShowSummaryWindow("Export", $"Exported {exportedCount} scripts, failed {failed.Count}:", failed);
        }
    }

    private void ImportScripts()
    {
        var dialog = new RadOpenFileDialog
        {
            Owner = App.Current.MainWindow,
            Multiselect = true,
            Filter = "Python scripts (*.py)|*.py|All files (*.*)|*.*",
            InitialDirectory = lastXmlDialogDirectory ?? string.Empty
        };
        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            return;
        }

        var filePaths = dialog.FileNames?.ToList() ?? [];
        if (filePaths.Count == 0)
        {
            return;
        }

        lastXmlDialogDirectory = Path.GetDirectoryName(filePaths[0]);

        var existingFileNames = Scripts
            .Select(script => script.FileName)
            .Concat(scripts.Select(script => script.FileName))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var skipped = new List<string>();
        var importedCount = 0;
        foreach (var filePath in filePaths)
        {
            var fileName = Path.GetFileName(filePath);
            if (existingFileNames.Contains(fileName))
            {
                skipped.Add($"{fileName} — already exists in the project");
                continue;
            }

            try
            {
                var script = new PyScript
                {
                    FileName = fileName,
                    Content = File.ReadAllText(filePath),
                    IsEnabled = true,
                    IsReplayEnabled = false,
                    ExecutionMode = ScriptExecutionMode.Manual,
                    Blocking = true
                };
                AddScriptWrapper(script);
                existingFileNames.Add(fileName);
                importedCount++;
            }
            catch (Exception e)
            {
                skipped.Add($"{fileName} — {e.Message}");
            }
        }

        ReportImportResult("scripts", importedCount, skipped);
    }

    private void SelectScript(object? parameter)
    {
        if (parameter is ProjectConfigurationScriptWrapper script)
        {
            SelectedScript = script;
        }
    }

    private void RemoveScript()
    {
        if (SelectedScript == null)
        {
            return;
        }

        var script = SelectedScript;
        script.PropertyChanged -= OnScriptPropertyChanged;
        Scripts.Remove(script);

        if (script.IsNew)
        {
            addedScripts.Remove(script);
        }
        else if (!removedScripts.Contains(script))
        {
            removedScripts.Add(script);
        }

        ClearRemovedScriptReferences(script);
        SelectedScript = Scripts.FirstOrDefault();

        RefreshCommunicatedVariableScriptOptions();
        ExportAllScriptsCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
    }

    private string CreateUniqueScriptFileName()
    {
        return MakeUniqueScriptFileName("script.py");
    }

    private string MakeUniqueScriptFileName(string desiredFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(desiredFileName);
        var extension = Path.GetExtension(desiredFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".py";
        }

        var existingNames = Scripts
            .Select(script => script.FileName)
            .Concat(scripts.Select(script => script.FileName))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fileName = $"{baseName}{extension}";
        var index = 1;
        while (existingNames.Contains(fileName))
        {
            fileName = $"{baseName}_{index}{extension}";
            index++;
        }

        return fileName;
    }

    private void ClearRemovedScriptReferences(ProjectConfigurationScriptWrapper removedScript)
    {
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            communicatedVariable.ClearTriggersForScript(removedScript.FileName, removedScript.OriginalFileName);
        }
    }

    private static bool IsRemovedScriptReference(string scriptFileName, ProjectConfigurationScriptWrapper removedScript)
    {
        return string.Equals(scriptFileName, removedScript.FileName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(scriptFileName, removedScript.OriginalFileName, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveScriptTriggers(ProjectConfigurationScriptWrapper removedScript)
    {
        foreach (var trigger in onValueChangedScriptTriggers
                     .Where(trigger => IsRemovedScriptReference(trigger.ScriptFileName, removedScript))
                     .ToList())
        {
            onValueChangedScriptTriggers.Remove(trigger);
        }
    }

    private void AddVariableToSource()
    {
        if (SelectedVariable == null || SelectedSourceOption == null)
        {
            return;
        }

        try
        {
            ErrorMessage = string.Empty;
            if (addedVariables.Contains(SelectedVariable))
            {
                ErrorMessage = "Variable must be applied first. Click Apply to create the variable, then assign it to a driver/protocol.";
                return;
            }

            if (SelectedSourceOption.Protocol.Variables.Any(variable => variable.Variable.Id == SelectedVariable.Variable.Id))
            {
                ErrorMessage = "Variable is already assigned to the selected source protocol.";
                return;
            }

            var protocolVariable = ProjectConfigurationProtocolVariableFactory.CreateProtocolVariable(
                SelectedSourceOption.Protocol,
                SelectedVariable.Variable,
                GetConfigurationVariableEvents(),
                SelectedSourceOption.Protocol.CreateDefaultCommParam(
                    SelectedVariable.Variable,
                    GetConfigurationVariableEvents()),
                true);
            SelectedSourceOption.Protocol.AddVariable(protocolVariable);

            var wrapper = CreateCommunicatedVariableWrapper(protocolVariable, SelectedSourceOption);
            wrapper.PropertyChanged += OnCommunicatedVariablePropertyChanged;
            CommunicatedVariables.Add(wrapper);
            addedCommunicatedVariables.Add(wrapper);
            SelectedCommunicatedVariable = wrapper;
            RemoveVariableCommand?.OnCanExecuteChanged();
            NotifyHasChangesChanged();
            NotifyCommunicatedSignalsWarningChanged();
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    private bool CanAddVariableToSource()
    {
        return SelectedVariable != null
               && SelectedSourceOption != null
               && !addedVariables.Contains(SelectedVariable);
    }

    private void RemoveCommunicatedVariable()
    {
        if (SelectedCommunicatedVariable == null)
        {
            return;
        }

        // A communicated variable bound to a Control must not be detached from its driver/protocol:
        // that would break the control's binding. The arrow stays enabled so the user gets an
        // explanation of where the variable is used instead of a silently disabled button.
        var usages = QueryVariableUsage(SelectedCommunicatedVariable.Variable);
        if (usages.Count > 0)
        {
            ErrorMessage = BuildCommunicatedVariableInUseMessage(SelectedCommunicatedVariable, usages);
            return;
        }

        RemoveCommunicatedVariable(SelectedCommunicatedVariable);
    }

    private bool CanRemoveCommunicatedVariable()
    {
        return SelectedCommunicatedVariable != null;
    }

    private void RemoveCommunicatedVariable(ProjectConfigurationProtocolVariableWrapper communicatedVariable)
    {
        // Captured before Remove: the list clears its selection (pushes null) when the selected item leaves.
        var wasSelected = ReferenceEquals(SelectedCommunicatedVariable, communicatedVariable);
        var removedIndex = CommunicatedVariables.IndexOf(communicatedVariable);
        communicatedVariable.PropertyChanged -= OnCommunicatedVariablePropertyChanged;
        CommunicatedVariables.Remove(communicatedVariable);
        communicatedVariable.SelectedSource.Protocol.RemoveProtocolVariable(communicatedVariable.ProtocolVariable);

        if (!addedCommunicatedVariables.Remove(communicatedVariable))
        {
            removedCommunicatedVariables.Add(new RemovedCommunicatedVariable(
                communicatedVariable.ProtocolVariable,
                communicatedVariable.SelectedSource));
        }

        if (wasSelected)
        {
            SelectedCommunicatedVariable = NeighbourAfterRemoval(CommunicatedVariables, removedIndex);
        }

        RemoveVariableCommand?.OnCanExecuteChanged();
        NotifyHasChangesChanged();
        NotifyCommunicatedSignalsWarningChanged();
    }

    private ProjectConfigurationProtocolVariableWrapper CreateCommunicatedVariableWrapper(
        IProtocolVariable protocolVariable,
        ProjectConfigurationProtocolOption source)
    {
        return new ProjectConfigurationProtocolVariableWrapper(
            protocolVariable,
            source,
            SourceOptions,
            GetConfigurationVariableEvents(),
            Scripts,
            onValueChangedScriptTriggers.Where(trigger => trigger.VariableId == protocolVariable.Variable.Id),
            IsFileLogEnabled(protocolVariable.Variable),
            () => Events.Select(variableEvent => variableEvent.Name));
    }

    private IEnumerable<IVarEvent> GetConfigurationVariableEvents()
    {
        return Events.Select(variableEvent => variableEvent.VariableEvent);
    }

    private void SynchronizeFileLogAndReplayVariables()
    {
        var loggedVariables = CommunicatedVariables
            .Where(variable => variable.IsCommunicated && variable.IsFileLogEnabled)
            .Select(variable => variable.Variable)
            .DistinctBy(variable => variable.Id)
            .ToList();

        var fileLogDriver = drivers.FirstOrDefault(IsFileLogDriver);
        if (fileLogDriver != null)
        {
            EnsureSinkProtocol(fileLogDriver);
            SynchronizeSinkDriverVariables(fileLogDriver, loggedVariables);
        }
        else if (loggedVariables.Count > 0)
        {
            // The checkbox is greyed out without the driver, but a project may carry the flags from
            // before the driver was removed — say so instead of logging nothing silently.
            Logger?.Log(LogLevel.Info,
                $"{loggedVariables.Count} variable(s) are marked \"Log to FileDataLogger\", but the project has no File data logger driver — nothing is logged until the driver is added.");
        }

        var replayDriver = drivers.FirstOrDefault(IsReplayDriver);
        if (replayDriver != null)
        {
            SynchronizeReplayDriverVariables(replayDriver, loggedVariables);
        }
    }

    private void SynchronizeSinkDriverVariables(IDriverBase sinkDriver, IReadOnlyCollection<IVariableBase> loggedVariables)
    {
        var loggedIds = loggedVariables.Select(variable => variable.Id).ToHashSet();
        foreach (var protocol in sinkDriver.Protocols.Where(protocol => protocol is IProtocolVariableSinkProtocol))
        {
            foreach (var protocolVariable in protocol.Variables.Where(variable => !loggedIds.Contains(variable.Variable.Id)).ToList())
            {
                protocol.RemoveProtocolVariable(protocolVariable);
            }

            foreach (var variable in loggedVariables.Where(variable => protocol.Variables.All(protocolVariable => protocolVariable.Variable.Id != variable.Id)))
            {
                var protocolVariable = ProjectConfigurationProtocolVariableFactory.CreateProtocolVariable(
                    protocol,
                    variable,
                    GetConfigurationVariableEvents(),
                    string.Empty,
                    true);
                protocol.AddVariable(protocolVariable);
            }
        }
    }

    private void SynchronizeReplayDriverVariables(IDriverBase replayDriver, IReadOnlyCollection<IVariableBase> loggedVariables)
    {
        var loggedIds = loggedVariables.Select(variable => variable.Id).ToHashSet();
        foreach (var protocol in replayDriver.Protocols)
        {
            foreach (var protocolVariable in protocol.Variables.Where(variable => !loggedIds.Contains(variable.Variable.Id)).ToList())
            {
                protocol.RemoveProtocolVariable(protocolVariable);
            }

            foreach (var variable in loggedVariables.Where(variable => protocol.Variables.All(protocolVariable => protocolVariable.Variable.Id != variable.Id)))
            {
                var protocolVariable = ProjectConfigurationProtocolVariableFactory.CreateProtocolVariable(
                    protocol,
                    variable,
                    GetConfigurationVariableEvents(),
                    string.Empty,
                    true);
                protocol.AddVariable(protocolVariable);
            }
        }
    }

    private bool IsFileLogEnabled(IVariableBase variable)
    {
        return drivers
            .Where(IsFileLogDriver)
            .SelectMany(driver => driver.Protocols)
            .SelectMany(protocol => protocol.Variables)
            .Any(protocolVariable => protocolVariable.Variable.Id == variable.Id);
    }

    private static IEnumerable<ProjectConfigurationProtocolOption> CreateSourceOptions(IEnumerable<IDriverBase> drivers)
    {
        return drivers
            .Where(IsSourceDriver)
            .SelectMany(driver => driver.Protocols.Select(protocol => new ProjectConfigurationProtocolOption(driver, protocol)));
    }

    private static bool IsSourceDriver(IDriverBase driver)
    {
        // Shared with the Free-tier signal counting so the limit and this page always agree.
        return CommunicatedSignals.IsCommunicationDriver(driver);
    }

    private static bool IsFileLogDriver(IDriverBase driver)
    {
        return driver.Specification.Name.Equals("FileDataLoggerDriver", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReplayDriver(IDriverBase driver)
    {
        return driver.Specification.Name.Equals("FileDataReplayDriver", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDriverPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationDriverWrapper.HasChanges)
            && e.PropertyName != nameof(ProjectConfigurationDriverWrapper.IsEnabled))
        {
            return;
        }

        if (e.PropertyName == nameof(ProjectConfigurationDriverWrapper.IsEnabled))
        {
            // A disabled File data logger driver logs nothing either — grey the checkbox out.
            NotifyFileLoggingAvailabilityChanged();
        }

        NotifyHasChangesChanged();
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationProjectWrapper.HasChanges)
            && e.PropertyName != nameof(ProjectConfigurationProjectWrapper.HasError))
        {
            return;
        }

        NotifyHasChangesChanged();
    }

    private void OnProtocolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationLoadedProtocolWrapper.HasChanges)
            && e.PropertyName != nameof(ProjectConfigurationLoadedProtocolWrapper.IsEnabled))
        {
            return;
        }

        if (e.PropertyName == nameof(ProjectConfigurationLoadedProtocolWrapper.IsEnabled))
        {
            // A disabled Data Log Pass-Through protocol logs nothing — grey the checkbox out.
            NotifyFileLoggingAvailabilityChanged();
        }

        NotifyHasChangesChanged();
    }

    private void OnScriptPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationScriptWrapper.HasChanges))
        {
            return;
        }

        RefreshCommunicatedVariableScriptOptions();

        NotifyHasChangesChanged();
    }

    private void RefreshCommunicatedVariableScriptOptions()
    {
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            communicatedVariable.RefreshScriptOptions();
        }
    }

    private void RefreshCommunicatedVariableEventOptions()
    {
        foreach (var communicatedVariable in CommunicatedVariables)
        {
            communicatedVariable.RefreshVariableEventOptions();
        }
    }

    private void RefreshVariablePresentationOptions()
    {
        // Do nabidky u promennych patri jen POTVRZENE prezentace. Nove pridana a jeste
        // nepotvrzena prezentace (stale v addedPresentations) ma placeholder nazev a do
        // nabidky se dostane az po Apply.
        var presentationNames = Presentations
            .Where(presentation => !addedPresentations.Contains(presentation))
            .Select(presentation => presentation.Name)
            .ToList();
        foreach (var variable in Variables)
        {
            variable.RefreshPresentationOptions(presentationNames);
        }
    }

    private void RefreshPresentationConverterOptions()
    {
        // Do nabidky converteru u prezentaci patri jen POTVRZENE convertery. Nove pridany
        // a jeste nepotvrzeny converter (stale v addedConversions) ma placeholder nazev a do
        // nabidky se dostane az po Apply. Predavame objekty (ne jen nazvy), aby prezentace
        // mela pro Apply aktualni seznam i po prejmenovani/odebrani converteru.
        var converters = Conversions
            .Where(conversion => !addedConversions.Contains(conversion))
            .Select(conversion => conversion.Conversion)
            .ToList();
        foreach (var presentation in Presentations)
        {
            presentation.RefreshConverterOptions(converters);
        }
    }

    private void OnVariablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationVariableWrapper.HasChanges))
        {
            return;
        }

        NotifyHasChangesChanged();
    }

    private void OnConversionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationConversionWrapper.HasChanges))
        {
            return;
        }

        NotifyHasChangesChanged();
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationPresentationWrapper.HasChanges))
        {
            return;
        }

        NotifyHasChangesChanged();
    }

    private void OnEventPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectConfigurationEventWrapper.HasChanges)
            && e.PropertyName != nameof(ProjectConfigurationEventWrapper.Name))
        {
            return;
        }

        RefreshCommunicatedVariableEventOptions();
        NotifyHasChangesChanged();
    }

    private void OnCommunicatedVariablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectConfigurationProtocolVariableWrapper.IsCommunicated))
        {
            NotifyCommunicatedSignalsWarningChanged();
            return;
        }

        if (e.PropertyName != nameof(ProjectConfigurationProtocolVariableWrapper.HasChanges))
        {
            return;
        }

        NotifyHasChangesChanged();
    }

    private void NotifyCommunicatedSignalsWarningChanged()
    {
        OnPropertyChanged(nameof(HasCommunicatedSignalsWarning));
        OnPropertyChanged(nameof(CommunicatedSignalsWarning));
    }

    private void NotifyHasChangesChanged()
    {
        OnPropertyChanged(nameof(HasChanges));
        ApplyCommand.OnCanExecuteChanged();
        AddDriverCommand.OnCanExecuteChanged();
        RemoveDriverCommand.OnCanExecuteChanged();
        AddProtocolCommand.OnCanExecuteChanged();
        RemoveProtocolCommand.OnCanExecuteChanged();
        AddVariableCommand.OnCanExecuteChanged();
        RemoveVariableCommand.OnCanExecuteChanged();
        AddVariableToSourceCommand.OnCanExecuteChanged();
        RemoveConversionCommand.OnCanExecuteChanged();
        AddPresentationCommand.OnCanExecuteChanged();
        RemovePresentationCommand.OnCanExecuteChanged();
        RemoveEventCommand.OnCanExecuteChanged();
        RemoveScriptCommand.OnCanExecuteChanged();
    }

    private sealed record RemovedCommunicatedVariable(
        IProtocolVariable ProtocolVariable,
        ProjectConfigurationProtocolOption Source);

    private sealed record AddedProtocol(
        ProjectConfigurationDriverWrapper Driver,
        ProjectConfigurationLoadedProtocolWrapper Protocol);

    private sealed record RemovedProtocol(
        ProjectConfigurationDriverWrapper Driver,
        ProjectConfigurationLoadedProtocolWrapper Protocol);
}

public sealed record ProjectConfigurationNavigationItem(ProjectConfigurationSection Section, string Label);

public sealed record ProjectConfigurationDriverPluginOption(PluginDetails Plugin)
{
    // Show the user-facing Label; Name is the technical id stored in .qproj.
    public string DisplayName => string.IsNullOrWhiteSpace(Plugin.Label)
        ? $"{Plugin.Name} ({Plugin.Version.ToDisplayString()})"
        : $"{Plugin.Label} ({Plugin.Version.ToDisplayString()})";
}

public sealed record ProjectConfigurationProtocolPluginOption(
    PluginDetails Plugin,
    bool IsCompatible = true,
    string? CompatibilityHint = null)
{
    // Show the user-facing Label; Name is the technical id stored in .qproj.
    public string DisplayName => string.IsNullOrWhiteSpace(Plugin.Label)
        ? $"{Plugin.Name} ({Plugin.Version.ToDisplayString()})"
        : $"{Plugin.Label} ({Plugin.Version.ToDisplayString()})";
}

public enum ProjectConfigurationSection
{
    Project,
    CommunicationDrivers,
    Variables,
    Conversions,
    Presentations,
    Events,
    Scripts
}
