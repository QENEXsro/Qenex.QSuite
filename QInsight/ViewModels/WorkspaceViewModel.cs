using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Qenex.QInsight.AppConfig;
using Qenex.QInsight.DragDrop;
using Qenex.QInsight.EventAggregatorMsgs;
using Qenex.QInsight.Views;
using Telerik.Windows.Diagrams.Core;
using Qenex.QLibs.QUI;
using Qenex.QSuite.LogSystems.LogSystem;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.ColorEditor.ColorSchemas;
using Telerik.Windows.Controls.Diagrams;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Telerik.Windows.DragDrop;
using Telerik.Windows.Controls.FileDialogs;

namespace Qenex.QInsight.ViewModels;

public class WorkspaceViewModel : WorkspaceViewModelBase
{
    #region  Fields
    
    private static readonly Brush DarkGridLineColor = new SolidColorBrush(Color.FromRgb(47,47,47));
    private static readonly Brush LightGridLineColor = new SolidColorBrush(Colors.WhiteSmoke);
    private static readonly Brush DarkBackgroundColor = new SolidColorBrush(Color.FromRgb(40, 40, 40));
    private static readonly Brush LightBackgroundColor = new SolidColorBrush(Colors.White);
    
    private const int SamplePumpIntervalMs = 33;

    private bool isViewLoaded;
    private List<ControlBase> controlsToLoad = [];
    private List<IProtocolVariable> activeProtocolVariables = [];
    private readonly List<ControlVariableFeed> loadedVariableSubscriptions = [];
    private DispatcherTimer? samplePumpTimer;
    private bool samplePumpDraining;

    #endregion
    
    #region Constructors

    public WorkspaceViewModel(EventAggregator ea) : base(ea)
    {
        GridLineColor = ShellWindow.IsDarkTheme ? DarkGridLineColor : LightGridLineColor;
        BackgroundColor = ShellWindow.IsDarkTheme ? DarkBackgroundColor : LightBackgroundColor;
        //WorkspaceViewLoadedCommand = new RelayCommand<UserControl>(OnWorkspaceViewLoaded);
        WorkspaceViewLoadedCommand = new RelayCommand<RadDiagram>(OnWorkspaceViewLoaded);
        EventAggregator.SubscribeAction<VariablePropertiesChangedMsg>(OnVariablePropertiesChanged);
        EventAggregator.SubscribeAction<VariableUsageQuery>(CollectVariableUsage);
    }

    // Named handler (not a lambda) so it can be unsubscribed in Clean() — otherwise a closed/reloaded
    // workspace stays subscribed and keeps answering queries from its stale state (zombie workspace).
    private void OnVariablePropertiesChanged(VariablePropertiesChangedMsg msg)
    {
        RefreshControlVariableBindings(msg.Variable);
    }

    #endregion

    #region Properties

    public Brush BackgroundColor { get; set { field = value; OnPropertyChanged(); }}
    public Brush GridLineColor { get; set { field = value; OnPropertyChanged(); } }

    public bool IsGridVisible { get; set { field = value; OnPropertyChanged(); } } = true;
    public bool IsPageGridVisible { get; set { field = value; OnPropertyChanged(); } } = false;
    public double GridCellSize { get; set { field = value; OnPropertyChanged(); } } = 20;
    public bool IsSnapToGridEnabled { get; set { field = value; OnPropertyChanged(); } } = true;

    //public RelayCommand<UserControl> WorkspaceViewLoadedCommand { get; set; }
    public RelayCommand<RadDiagram> WorkspaceViewLoadedCommand { get; set; }

    public Action<DialogWindowBase>? ConfigureGraphControlSaveDialog { get; set; }
    public Func<string?>? GraphControlSaveDialogInitialDirectoryProvider { get; set; }
    public Action<string>? GraphControlSaveDialogDirectoryChanged { get; set; }

    /// <summary>Injektuje Shell: protokolova capability zapisu pro protocol variable.</summary>
    public Func<IProtocolVariable, bool>? CanWriteProtocolVariable { get; set; }

    /// <summary>Host-provided: true when a running-capable protocol can read the variable on
    /// request (On Request event). Mirror of CanWriteProtocolVariable.</summary>
    public Func<IProtocolVariable, bool>? CanReadProtocolVariable { get; set; }

    #endregion
    
    #region ViewModelBase implementation

    public override string Header { get; set; } = "Workspace";
    public override string Name { get; set; } = $"WorkspaceViewModel_WsControls_{Guid.NewGuid().ToString().Replace("-", "_")}";
    public override string WinTitle { get; set { field = value; OnPropertyChanged(); } } = "Workspace" + Random.Shared.Next(1, 9999);
    public override DockingPosition DockPosition { get; set; } = DockingPosition.Workspace;
    public override bool IsDocument => true;
    

    #endregion

    public override void OnIsVisibleChanged(bool isVisible)
    {
        //EventAggregator.Publish(new LogMessage(LogLevel.Info, $"{Name} visibility changed to {isVisible}"));
    }

	#region Commands methods

	private RadDiagram diagram;
	 private void OnWorkspaceViewLoaded(RadDiagram radDiagram)
	 {
        if (isViewLoaded && ReferenceEquals(diagram, radDiagram)) return;
        var controlsToRestore = isViewLoaded
	        ? GetControlProjectData()
	        : controlsToLoad.ToList();

         isViewLoaded = true;
         if (diagram != null)
         {
	         diagram.SelectionChanged -= OnDiagramSelectionChanged;
         }

         diagram = radDiagram;
         diagram.SelectionChanged += OnDiagramSelectionChanged;
         GridCellSize = Telerik.Windows.Controls.Diagrams.Primitives.BackgroundGrid.GetCellSize(diagram).Height;

         var diagramAlreadyContainsControls = GetDiagramControls(diagram).Any();
         if (!diagramAlreadyContainsControls)
         {
	         foreach (var controlData in controlsToRestore)
	         {
		         AddControlToDiagram(controlData);
	         }
         }

         controlsToLoad.Clear();

         if (activeProtocolVariables.Count > 0)
         {
	         BindLoadedControlVariables(activeProtocolVariables);
         }
	}

     public List<ControlBase> GetControlProjectData()
     {
	     if (!isViewLoaded)
	     {
		     foreach (var control in controlsToLoad)
		     {
			     SynchronizeSavedVariableBindings(control);
		     }

		     return controlsToLoad.ToList();
	     }

	     return GetDiagramControls()
		     .Select(item =>
		     {
			     item.Control.X = (int)Math.Round(item.Shape.Position.X);
			     item.Control.Y = (int)Math.Round(item.Shape.Position.Y);
			     item.Control.Width = (int)Math.Round(item.Shape.Width);
			     item.Control.Height = (int)Math.Round(item.Shape.Height);
			     SynchronizeSavedVariableBindings(item.Control);
			     return item.Control;
		     })
		     .ToList();
     }

     public void SetControlProjectData(IEnumerable<ControlBase> controls)
     {
	     controlsToLoad = controls.ToList();
	     RenumberDuplicateControlIds(controlsToLoad);

	     if (!isViewLoaded)
	     {
		     return;
	     }

	     foreach (var controlData in controlsToLoad)
	     {
		     AddControlToDiagram(controlData);
	     }

	     controlsToLoad.Clear();
     }

     public void AddControlToDiagram(IControlBase iControl, double x, double y)
     {
	     var controlType = iControl.GetType();
	     var viewTypeName = controlType.AssemblyQualifiedName?.Replace("Model", string.Empty);
	     if (viewTypeName == null)
	     {
		     return;
	     }

	     // Plugin controls (nactene pres Assembly.LoadFrom) nemusi byt dohledatelne pres Type.GetType,
	     // proto fallback na assembly daneho controlu (View je ve stejne assembly jako ViewModel).
	     var viewType = Type.GetType(viewTypeName)
		     ?? controlType.Assembly.GetType(controlType.FullName!.Replace("Model", string.Empty));

	     if (viewType == null)
	     {
		     return;
	     }
	     
	     var iControlView = (UserControl) Activator.CreateInstance(viewType)!;
	     iControlView.DataContext = iControl;
	     
	     AddControlToDiagram(iControl, iControlView, x, y);
     }

    private void AddControlToDiagram(IControlBase controlVm, UserControl control, double x, double y)
    {
	    ConfigureControl(controlVm);

	    if (controlVm is ControlBase controlBase)
	    {
		    controlBase.VariableBindingsChanged -= OnControlVariableBindingsChanged;
		    controlBase.VariableBindingsChanged += OnControlVariableBindingsChanged;
	    }

	    var fgColor = ShellWindow.ForegroundColor;
	    var bgColor = ShellWindow.BackgroundColor;
	    var fontSize = ShellWindow.MainAppSettings.Design.FontSize;
	    
		var userControl = new RadDiagramShape();
		controlVm.DiagramShape = userControl;
		controlVm.BackgroundColor = bgColor;
		controlVm.ForegroundColor = fgColor;
		controlVm.UpdateThemeSettingsControl(bgColor, fgColor, fontSize > 0 ? fontSize : 12);
		controlVm.X = (int)Math.Round(x);
		controlVm.Y = (int)Math.Round(y);
		
		userControl.Position = new Point(x, y);
		userControl.Width = controlVm.Width;
		userControl.Height = controlVm.Height;
		userControl.Content = control;
		userControl.Background = new SolidColorBrush(controlVm.BackgroundColor);
		userControl.BorderBrush = new SolidColorBrush(Colors.Black);
		userControl.BorderThickness = new Thickness(1);
		userControl.UseGlidingConnector = true;
		userControl.HorizontalContentAlignment = HorizontalAlignment.Stretch;
		userControl.VerticalContentAlignment = VerticalAlignment.Stretch;
		userControl.AllowDrop = true;
		userControl.IsManipulationEnabled = !controlVm.IsLocked;
		userControl.IsManipulationAdornerVisible = !controlVm.IsLocked;
		userControl.IsManipulationAdornerVisible = !controlVm.IsLocked;
		userControl.IsDraggingEnabled = !controlVm.IsLocked;
		userControl.IsRotationEnabled = !controlVm.IsLocked;
		userControl.IsResizingEnabled = !controlVm.IsLocked;

		userControl.Connectors.Clear();
		var leftCon = new RadDiagramConnector()
		{
			Offset = new Point(0, 0.5),
			Name = "LeftConnector"
		};
		var rightCon = new RadDiagramConnector()
		{
			Offset = new Point(1, 0.5),
			Name = "RightConnector"
		};
		var topCon = new RadDiagramConnector()
		{
			Offset = new Point(0.5, 0),
			Name = "TopConnector"
		};
		var bottomCon = new RadDiagramConnector()
		{
			Offset = new Point(0.5, 1),
			Name = "BottomConnector"
		};

		if (controlVm.AreConnectorsEnabled)
		{
			userControl.Connectors.Add(leftCon);
			userControl.Connectors.Add(rightCon);
			userControl.Connectors.Add(topCon);
			userControl.Connectors.Add(bottomCon);
		}
		
		DragDropManager.AddPreviewDragOverHandler(userControl, OnVariableDragOver);
		DragDropManager.AddDragLeaveHandler(userControl, OnVariableDragLeave);
		DragDropManager.AddDropHandler(userControl, OnVariableDrop);

		diagram.AddShape(userControl);
	}

    private void ConfigureControl(IControlBase controlVm)
    {
	    if (controlVm is ILogAwareControl logAwareControl && controlVm is ControlBase logControlBase)
	    {
		    logAwareControl.LogInfo = message =>
			    EventAggregator.Publish(new LogMessage(LogLevel.Info, $"{GetControlDisplayName(logControlBase)}: {message}"));
		    logAwareControl.LogWarn = message =>
			    EventAggregator.Publish(new LogMessage(LogLevel.Warn, $"{GetControlDisplayName(logControlBase)}: {message}"));
	    }

	    if (controlVm is IFileDialogAwareControl dialogAwareControl)
	    {
		    dialogAwareControl.ConfigureSaveFileDialog = ConfigureGraphControlSaveDialog;
		    dialogAwareControl.SaveDialogInitialDirectoryProvider = GraphControlSaveDialogInitialDirectoryProvider;
		    dialogAwareControl.SaveDialogDirectoryChanged = GraphControlSaveDialogDirectoryChanged;
	    }

	    if (controlVm is IVariableWriteControl writeControl)
	    {
		    writeControl.CanWriteVariableProvider = variable =>
			    ResolveProtocolVariable(variable) is { } protocolVariable
			    && (CanWriteProtocolVariable?.Invoke(protocolVariable) ?? false);
		    writeControl.WriteVariableEngValueAsync = WriteControlVariableEngValueAsync;

		    if (writeControl is IMatrixVariableWriteControl matrixWriteControl)
		    {
			    matrixWriteControl.WriteMatrixElementEngValueAsync = WriteControlMatrixElementEngValueAsync;
		    }

		    writeControl.RefreshWriteCapability();
	    }

	    if (controlVm is IVariableReadControl readControl)
	    {
		    readControl.CanReadVariableProvider = variable =>
			    ResolveProtocolVariable(variable) is { } protocolVariable
			    && (CanReadProtocolVariable?.Invoke(protocolVariable) ?? false);
		    readControl.ReadVariableAsync = ReadControlVariableAsync;
		    readControl.RefreshReadCapability();
	    }
    }

    /// <summary>
    /// On-request read from a control: the read-requested notification goes through the module's
    /// command-driver wiring to the owning protocol (IProtocolVariableReadProtocol), which reads
    /// the device once and applies the value like a poll (value-changed → all controls). Returns
    /// false on any failure; the protocol has already logged the reason.
    /// </summary>
    private async Task<bool> ReadControlVariableAsync(IVariableBase variable)
    {
	    var protocolVariable = ResolveProtocolVariable(variable);
	    if (protocolVariable == null || CanReadProtocolVariable?.Invoke(protocolVariable) != true)
	    {
		    return false;
	    }

	    try
	    {
		    await protocolVariable.RequestReadAsync();
		    return true;
	    }
	    catch (Exception e)
	    {
		    EventAggregator.Publish(new LogMessage(LogLevel.Warn, $"Read of '{variable.Name}' failed: {e.Message}"));
		    return false;
	    }
    }

    /// <summary>
    /// Najde zivou protocol variable pro promennou drzenou controlem. Primarne identitou
    /// (controly dostavaji v BindVariable zivou promennou), fallback pres referenci
    /// (namespace/name) pro pripad rebindu po vymene protokolu.
    /// </summary>
    private IProtocolVariable? ResolveProtocolVariable(IVariableBase? variable)
    {
	    if (variable == null)
	    {
		    return null;
	    }

	    var reference = ControlBase.GetVariableReference(variable);
	    return activeProtocolVariables.FirstOrDefault(pv => ReferenceEquals(pv.Variable, variable))
	           ?? activeProtocolVariables.FirstOrDefault(pv => ControlBase.IsVariableReferenceMatch(reference, pv.Variable));
    }

    private async Task<bool> WriteControlVariableEngValueAsync(IVariableBase variable, double engValue)
    {
	    var protocolVariable = ResolveProtocolVariable(variable);
	    if (protocolVariable == null
	        || CanWriteProtocolVariable?.Invoke(protocolVariable) != true
	        || protocolVariable.Variable is not ScalarVariable scalarVariable
	        || !scalarVariable.TrySetEngValue(engValue))
	    {
		    return false;
	    }

	    // Na notifikaci je prihlaseny command driver (zapis do zarizeni) i ctecí handlery
	    // ostatnich controlu (okamzity feedback nove hodnoty)
	    await protocolVariable.NotifyValueChangedAsync();
	    return true;
    }

    /// <summary>
    /// Zapis jednoho prvku (bunky) matice: inverzni konverze do raw bufferu + fronta zapisu
    /// s presnymi byty prvku (protokol z ni zapise jen dotcene registry; nesene byty prezijou
    /// i prepis bufferu soubeznym pollem).
    /// </summary>
    private async Task<bool> WriteControlMatrixElementEngValueAsync(IVariableBase variable,
	    MatrixSectionKind kind, int index, double engValue)
    {
	    var protocolVariable = ResolveProtocolVariable(variable);
	    if (protocolVariable == null
	        || CanWriteProtocolVariable?.Invoke(protocolVariable) != true
	        || protocolVariable.Variable is not MatrixVariable matrixVariable
	        || !matrixVariable.TrySetEngValue(kind, index, engValue))
	    {
		    return false;
	    }

	    var elementSize = matrixVariable.GetElementSize(kind);
	    var byteOffset = matrixVariable.GetSectionOffset(kind) + index * elementSize;
	    var bytes = matrixVariable.RawData.AsSpan(byteOffset, elementSize).ToArray();
	    matrixVariable.EnqueuePendingWrite(new MatrixWriteRequest(byteOffset, bytes));

	    await protocolVariable.NotifyValueChangedAsync();
	    return true;
    }

    public void BindLoadedControlVariables(IEnumerable<IProtocolVariable> protocolVariables)
    {
	    UnsubscribeLoadedControlVariables();
	    activeProtocolVariables = protocolVariables.ToList();
	    var projectProtocolVariables = activeProtocolVariables;
	    var controls = GetWorkspaceControls();

	    foreach (var control in controls)
	    {
		    foreach (var variableReference in GetControlVariableReferences(control))
		    {
			    var protocolVariable = projectProtocolVariables.FirstOrDefault(v =>
				    ControlBase.IsVariableReferenceMatch(variableReference, v.Variable));
			    if (protocolVariable != null && control.CanBindVariable(protocolVariable.Variable))
			    {
				    control.BindVariable(protocolVariable.Variable);
				    SubscribeControlVariable(control, protocolVariable);
			    }
		    }

		    (control as IVariableWriteControl)?.RefreshWriteCapability();
		    (control as IVariableReadControl)?.RefreshReadCapability();
	    }
    }

    public void SetControlsRunState(bool isRun)
    {
	    foreach (var control in GetWorkspaceControls())
	    {
		    control.IsRun = isRun;
	    }
    }

    /// <summary>
    /// Replay seek: drops the samples still queued for every control (they belong to the
    /// position before the seek) and resets the timeline of the controls that accumulate
    /// samples (ITimelineResetControl). Must complete before the replay driver re-sends the
    /// history up to the new position, otherwise the stale samples would land in the cleared
    /// chart first. Runs on the UI thread — the controls' data belong to it, and the sample
    /// pump delivers there, so no drain can interleave with the reset.
    /// </summary>
    public void ResetControlTimelines()
    {
	    var uiDispatcher = Application.Current?.Dispatcher;
	    if (uiDispatcher != null && !uiDispatcher.CheckAccess())
	    {
		    uiDispatcher.Invoke(ResetControlTimelines);
		    return;
	    }

	    ControlVariableFeed[] feeds;
	    lock (loadedVariableSubscriptions)
	    {
		    feeds = loadedVariableSubscriptions.ToArray();
	    }

	    foreach (var feed in feeds)
	    {
		    feed.Discard();
	    }

	    foreach (var control in GetWorkspaceControls().OfType<ITimelineResetControl>())
	    {
		    control.ResetTimeline();
	    }
    }

    private static IVariableBase CreateVariableSnapshot(IVariableBase variable)
    {
	    return variable switch
	    {
		    ScalarVariable scalarVariable => CreateScalarVariableSnapshot(scalarVariable),
		    MatrixVariable matrixVariable => CreateMatrixVariableSnapshot(matrixVariable),
		    StringVariable stringVariable => CreateStringVariableSnapshot(stringVariable),
		    _ => variable
	    };
    }

    private static MatrixVariable CreateMatrixVariableSnapshot(MatrixVariable variable)
    {
	    // Sekce (layout + presentation reference) se sdileji, kopiruje se jen datovy buffer.
	    var snapshot = new MatrixVariable
	    {
		    Id = variable.Id,
		    Namespace = variable.Namespace,
		    Name = variable.Name,
		    Label = variable.Label,
		    Description = variable.Description,
		    Timestamp = variable.Timestamp,
		    CommComponents = variable.CommComponents,
		    DefaultDataType = variable.DefaultDataType,
		    Endianness = variable.Endianness,
		    XAxis = variable.XAxis,
		    YAxis = variable.YAxis,
		    Data = variable.Data
	    };

	    snapshot.SetValue(variable.RawData.ToArray());
	    return snapshot;
    }

    private static ScalarVariable CreateScalarVariableSnapshot(ScalarVariable variable)
    {
	    var values = ValuesGlobal.CreateInstance(variable.Values.ValueType);
	    values.ValPresentation = variable.Values.ValPresentation;
	    values.SetValue(variable.GetValue());

	    return new ScalarVariable
	    {
		    Id = variable.Id,
		    Namespace = variable.Namespace,
		    Name = variable.Name,
		    Label = variable.Label,
		    Description = variable.Description,
		    Timestamp = variable.Timestamp,
		    CommComponents = variable.CommComponents,
		    Size = variable.Size,
		    BitShift = variable.BitShift,
		    BitMask = variable.BitMask,
		    Values = values
	    };
    }

    private static StringVariable CreateStringVariableSnapshot(StringVariable variable)
    {
	    return new StringVariable
	    {
		    Id = variable.Id,
		    Namespace = variable.Namespace,
		    Name = variable.Name,
		    Label = variable.Label,
		    Description = variable.Description,
		    Timestamp = variable.Timestamp,
		    CommComponents = variable.CommComponents,
		    Values = variable.Values
	    };
    }

    // Named handler so it can be unsubscribed on diagram reload and in Clean() (zombie rule).
    private void OnDiagramSelectionChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
	    var shape = diagram?.SelectedItems?.OfType<RadDiagramShape>().FirstOrDefault();
	    var control = (shape?.Content as UserControl)?.DataContext as ControlBase;
	    EventAggregator.Publish(new WorkspaceControlSelectedMsg
	    {
		    WorkspaceName = string.IsNullOrWhiteSpace(WinTitle) ? Name : WinTitle,
		    Control = control
	    });
    }

    public override void Clean()
    {
	    UnsubscribeLoadedControlVariables();
	    activeProtocolVariables.Clear();

	    if (diagram != null)
	    {
		    diagram.SelectionChanged -= OnDiagramSelectionChanged;
	    }

	    // Release EventAggregator subscriptions taken in the constructor; without this a removed
	    // workspace lingers as a subscriber (zombie) and still answers VariableUsageQuery from stale state.
	    EventAggregator.UnsubscribeAction<VariablePropertiesChangedMsg>(OnVariablePropertiesChanged);
	    EventAggregator.UnsubscribeAction<VariableUsageQuery>(CollectVariableUsage);
    }

    private void RefreshControlVariableBindings(IVariableBase variable)
    {
	    foreach (var control in GetWorkspaceControls())
	    {
		    control.RefreshVariableBinding(variable);
		    SynchronizeSavedVariableBindings(control);
		    // The event binding (periodic ↔ On Request) may have changed with the configuration.
		    (control as IVariableWriteControl)?.RefreshWriteCapability();
		    (control as IVariableReadControl)?.RefreshReadCapability();
	    }
    }

    private void CollectVariableUsage(VariableUsageQuery query)
    {
	    foreach (var control in GetWorkspaceControls())
	    {
		    if (control.IsVariableUsed(query.Variable))
		    {
			    query.Usages.Add(new VariableUsage
			    {
				    WorkspaceName = string.IsNullOrWhiteSpace(WinTitle) ? (string.IsNullOrWhiteSpace(Name) ? "(unnamed workspace)" : Name) : WinTitle,
				    ControlLabel = $"{GetControlDisplayName(control)} (id: {control.Id})"
			    });
		    }
	    }
    }

    // User-facing control name: the control kind as the user knows it from the toolbox
    // ("Graph", "Gauge", ...; ControlBase.Label is the plugin's fixed kind name — controls have
    // no per-instance name today). Type-name fallback covers a plugin returning an empty Label.
    internal static string GetControlDisplayName(ControlBase control)
    {
	    if (!string.IsNullOrWhiteSpace(control.Label))
	    {
		    return control.Label;
	    }

	    const string typeNameSuffix = "ControlViewModel";
	    var typeName = control.GetType().Name;
	    return typeName.EndsWith(typeNameSuffix, StringComparison.Ordinal)
		    ? typeName[..^typeNameSuffix.Length]
		    : typeName;
    }

    public override Task CleanAsync(CancellationToken ct = default)
    {
	    Clean();
	    return Task.CompletedTask;
    }

    private void AddControlToDiagram(ControlBase control)
    {
	    AddControlToDiagram(control, control.X, control.Y);
    }

    private IEnumerable<(RadDiagramShape Shape, ControlBase Control)> GetDiagramControls()
    {
	    return GetDiagramControls(diagram);
    }

    private static IEnumerable<(RadDiagramShape Shape, ControlBase Control)> GetDiagramControls(RadDiagram sourceDiagram)
    {
	    return sourceDiagram
		    .Shapes
		    .OfType<RadDiagramShape>()
		    .Select(shape => (Shape: shape, Control: (shape.Content as UserControl)?.DataContext as ControlBase))
		    .Where(item => item.Control != null)!;
    }

    private IEnumerable<ControlBase> GetWorkspaceControls()
    {
	    // Dedup by logical control identity (type + Id), not reference: the same control can appear as
	    // separate instances across controlsToLoad/diagram (or as duplicate diagram shapes), and reference
	    // equality would let those slip through and be counted/reported multiple times.
	    return GetAllControlInstances().DistinctBy(control => (control.GetType(), control.Id));
    }

    private IEnumerable<ControlBase> GetAllControlInstances()
    {
	    return isViewLoaded
		    ? controlsToLoad.Concat(GetDiagramControls().Select(item => item.Control))
		    : controlsToLoad;
    }

    /// <summary>
    /// Next free Id for a new control of the given type, derived from the controls actually
    /// present in this workspace. Ids must be unique per control type — a control whose
    /// (type, Id) collides with another one is invisible to GetWorkspaceControls and would
    /// silently lose its variable bindings on the next project open.
    /// </summary>
    public int CreateUniqueControlId(Type controlType)
    {
	    return GetAllControlInstances()
		    .Where(control => control.GetType() == controlType)
		    .Select(control => control.Id)
		    .DefaultIfEmpty(0)
		    .Max() + 1;
    }

    // Older builds assigned control Ids from a per-session counter, so saved projects can carry
    // duplicate (type, Id) pairs; the duplicates would be dropped by GetWorkspaceControls and
    // never get their variables bound. Heal such projects while loading.
    private static void RenumberDuplicateControlIds(List<ControlBase> controls)
    {
	    foreach (var typeGroup in controls.GroupBy(control => control.GetType()))
	    {
		    var usedIds = new HashSet<int>();
		    foreach (var control in typeGroup)
		    {
			    if (usedIds.Add(control.Id))
			    {
				    continue;
			    }

			    var renumberedId = usedIds.Max() + 1;
			    Trace.TraceWarning(
				    $"Workspace: duplicate {typeGroup.Key.Name} Id {control.Id} ('{control.Label}') renumbered to {renumberedId}.");
			    control.Id = renumberedId;
			    usedIds.Add(renumberedId);
		    }
	    }
    }

    private static IEnumerable<string> GetControlVariableReferences(ControlBase control)
    {
	    var references = control.LinkedVariables.ToList();
	    references.AddRange(control.Variables.Select(ControlBase.GetVariableReference));

	    if (control is IVariableReferenceProvider referenceProvider)
	    {
		    references.AddRange(referenceProvider.GetAdditionalVariableReferences());
	    }

	    return references
		    .Where(reference => !string.IsNullOrWhiteSpace(reference))
		    .Distinct();
    }

    private static void SynchronizeSavedVariableBindings(ControlBase control)
    {
	    foreach (var variable in control.Variables)
	    {
		    control.RememberVariableBinding(variable);
	    }
    }

    private void UnsubscribeLoadedControlVariables()
    {
	    StopSamplePump();
	    // The subscription list is mutated from bind/unbind paths (UI or runtime start) while
	    // the sample pump tick snapshots it on the UI thread — hence the lock on every access.
	    lock (loadedVariableSubscriptions)
	    {
		    foreach (var subscription in loadedVariableSubscriptions)
		    {
			    subscription.ProtocolVariable.UnsubscribeAsyncValueChanged(subscription.Handler);
		    }

		    loadedVariableSubscriptions.Clear();
	    }
    }

    private void SubscribeControlVariable(ControlBase control, IProtocolVariable protocolVariable)
    {
	    lock (loadedVariableSubscriptions)
	    {
		    if (loadedVariableSubscriptions.Any(s =>
			    ReferenceEquals(s.Control, control) && ReferenceEquals(s.ProtocolVariable, protocolVariable)))
		    {
			    return;
		    }
	    }

	    ControlVariableFeed feed = null!;
	    // The protocol side must never wait for the UI. Off the UI thread the handler only
	    // freezes the sample (snapshot) and enqueues it for the sample pump; the returned task
	    // is already completed, so DAQ/poll loops run at their own pace whatever the UI does.
	    // On the UI thread (operator writes, headless tests without a dispatcher) it updates
	    // directly so the immediate write feedback stays.
	    Func<IProtocolVariable, Task> handler = changedProtocolVariable =>
	    {
		    var variableSnapshot = CreateVariableSnapshot(changedProtocolVariable.Variable);
		    if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.CheckAccess())
		    {
			    return control.UpdateVariableValueAsync(variableSnapshot);
		    }

		    feed.Enqueue(variableSnapshot);
		    return Task.CompletedTask;
	    };

	    feed = new ControlVariableFeed(keepHistory: control is ISampleHistoryControl)
	    {
		    Control = control,
		    ProtocolVariable = protocolVariable,
		    Handler = handler
	    };

	    protocolVariable.SubscribeAsyncValueChanged(handler);
	    lock (loadedVariableSubscriptions)
	    {
		    loadedVariableSubscriptions.Add(feed);
	    }

	    EnsureSamplePumpRunning();
    }

    private void EnsureSamplePumpRunning()
    {
	    if (samplePumpTimer != null)
	    {
		    return;
	    }

	    // No dispatcher = headless run (tests); the subscribe handler then updates directly.
	    var uiDispatcher = Application.Current?.Dispatcher;
	    if (uiDispatcher == null)
	    {
		    return;
	    }

	    // Explicitly bound to the APPLICATION dispatcher: subscriptions are also created from
	    // runtime-start paths off the UI thread, and a timer created there would silently bind
	    // to a worker dispatcher that never pumps — no tick, no updates. This ctor overload is
	    // safe to call from any thread and starts the timer itself.
	    // Background priority: queued samples never outrank user input — that inversion (one
	    // dispatcher operation per sample at Normal priority) is what froze the UI when two
	    // boards streamed 10ms DAQ at once.
	    samplePumpTimer = new DispatcherTimer(
		    TimeSpan.FromMilliseconds(SamplePumpIntervalMs),
		    DispatcherPriority.Background,
		    OnSamplePumpTick,
		    uiDispatcher);
    }

    private void StopSamplePump()
    {
	    var timer = samplePumpTimer;
	    if (timer == null)
	    {
		    return;
	    }

	    samplePumpTimer = null;
	    // Stop on the timer's own dispatcher — this method is also reached from runtime
	    // start/stop paths off the UI thread and DispatcherTimer is not thread-safe.
	    if (timer.Dispatcher.CheckAccess())
	    {
		    timer.Stop();
		    timer.Tick -= OnSamplePumpTick;
	    }
	    else
	    {
		    timer.Dispatcher.InvokeAsync(() =>
		    {
			    timer.Stop();
			    timer.Tick -= OnSamplePumpTick;
		    });
	    }
    }

    private async void OnSamplePumpTick(object? sender, EventArgs e)
    {
	    if (samplePumpDraining)
	    {
		    return; // a slow previous drain is still awaiting; skip this tick instead of nesting
	    }

	    samplePumpDraining = true;
	    try
	    {
		    // Copy under the lock: bind/unbind may mutate the list from another thread, and an
		    // awaited control update may pump nested messages that rebind controls mid-loop.
		    ControlVariableFeed[] feeds;
		    lock (loadedVariableSubscriptions)
		    {
			    feeds = loadedVariableSubscriptions.ToArray();
		    }

		    foreach (var feed in feeds)
		    {
			    var dropped = feed.TakeDroppedSamples();
			    if (dropped > 0)
			    {
				    EventAggregator.Publish(new LogMessage(LogLevel.Warn,
					    $"{GetControlDisplayName(feed.Control)}: UI stalled — dropped {dropped} oldest sample(s) of '{feed.ProtocolVariable.Variable.Name}'."));
			    }

			    await feed.DrainAsync();
		    }
	    }
	    catch (Exception exception)
	    {
		    EventAggregator.Publish(new LogMessage(LogLevel.Error, $"Workspace sample pump failed: {exception.Message}"));
	    }
	    finally
	    {
		    samplePumpDraining = false;
	    }
    }

    /// <summary>
    /// Per (control, variable) feed decoupling protocols from the UI: the protocol thread only
    /// enqueues a frozen snapshot, the sample pump delivers on its UI tick. History feeds
    /// (ISampleHistoryControl — graphs, XY, signal peaks) keep every sample in order, bounded
    /// with drop-oldest; other controls keep only the latest pending sample.
    /// </summary>
    private sealed class ControlVariableFeed(bool keepHistory)
    {
	    // ~200 s of one 100 Hz DAQ signal; past this the UI has been stalled for minutes and
	    // dropping the oldest samples beats growing without bound.
	    private const int HistoryCapacity = 20_000;

	    public required ControlBase Control { get; init; }
	    public required IProtocolVariable ProtocolVariable { get; init; }
	    public required Func<IProtocolVariable, Task> Handler { get; init; }

	    private readonly ConcurrentQueue<IVariableBase>? history = keepHistory ? new ConcurrentQueue<IVariableBase>() : null;
	    private IVariableBase? latest;
	    private int historyCount;
	    private int droppedSamples;

	    public void Enqueue(IVariableBase snapshot)
	    {
		    if (history == null)
		    {
			    Volatile.Write(ref latest, snapshot);
			    return;
		    }

		    history.Enqueue(snapshot);
		    if (Interlocked.Increment(ref historyCount) > HistoryCapacity && history.TryDequeue(out _))
		    {
			    Interlocked.Decrement(ref historyCount);
			    Interlocked.Increment(ref droppedSamples);
		    }
	    }

	    public async Task DrainAsync()
	    {
		    if (history == null)
		    {
			    var snapshot = Interlocked.Exchange(ref latest, null);
			    if (snapshot != null)
			    {
				    await Control.UpdateVariableValueAsync(snapshot);
			    }

			    return;
		    }

		    while (history.TryDequeue(out var snapshot))
		    {
			    Interlocked.Decrement(ref historyCount);
			    await Control.UpdateVariableValueAsync(snapshot);
		    }
	    }

	    public int TakeDroppedSamples() => Interlocked.Exchange(ref droppedSamples, 0);

	    /// <summary>
	    /// Drops every pending sample (replay seek: they precede the new position). Not counted
	    /// as dropped — nothing was lost, the driver re-sends the history.
	    /// </summary>
	    public void Discard()
	    {
		    Interlocked.Exchange(ref latest, null);
		    if (history == null)
		    {
			    return;
		    }

		    while (history.TryDequeue(out _))
		    {
			    Interlocked.Decrement(ref historyCount);
		    }
	    }
    }

    /// <summary>
    /// Unsubscribes and drops this control's value subscriptions whose variable is no longer
    /// bound to the control (e.g. a single-variable control that replaced its variable).
    /// </summary>
    private void PruneControlSubscriptions(ControlBase control)
    {
	    var currentReferences = GetControlVariableReferences(control).ToHashSet();
	    List<ControlVariableFeed> staleSubscriptions;
	    lock (loadedVariableSubscriptions)
	    {
		    staleSubscriptions = loadedVariableSubscriptions
			    .Where(s => ReferenceEquals(s.Control, control)
			        && !currentReferences.Contains(ControlBase.GetVariableReference(s.ProtocolVariable.Variable)))
			    .ToList();
	    }

	    foreach (var subscription in staleSubscriptions)
	    {
		    subscription.ProtocolVariable.UnsubscribeAsyncValueChanged(subscription.Handler);
		    lock (loadedVariableSubscriptions)
		    {
			    loadedVariableSubscriptions.Remove(subscription);
		    }
	    }
    }

    private void OnControlVariableBindingsChanged(ControlBase control) => PruneControlSubscriptions(control);
    
    private void OnVariableDragOver(object sender, Telerik.Windows.DragDrop.DragEventArgs e)
    {
	    var variable = DragDropPayloadManager.GetDataFromObject(e.Data, "DraggedVariable");
	    if (variable is not IVariableBase variableBase)
	    {
		    return;
	    }

	    // Kazdy control umi jen sve typy promennych (matice nepatri na Signal/WatchTable
	    // a naopak) - nekompatibilni cil ukaze zakazany kurzor a drop se nekona.
	    var canBind = ResolveDropTargetControl(sender)?.CanBindVariable(variableBase) ?? false;
	    VariableDragAndDropBehavior.IsOverValidTarget = canBind;
	    e.Effects = canBind ? DragDropEffects.All : DragDropEffects.None;
	    e.Handled = true;
    }

    private static ControlBase? ResolveDropTargetControl(object sender)
    {
	    return sender is RadDiagramShape { Content: UserControl { DataContext: ControlBase control } }
		    ? control
		    : null;
    }
    
    private void OnVariableDragLeave(object sender, Telerik.Windows.DragDrop.DragEventArgs e)
    {
	    VariableDragAndDropBehavior.IsOverValidTarget = false;
    }
    
    private void OnVariableDrop(object sender, Telerik.Windows.DragDrop.DragEventArgs e)
    {
	    if (DragDropPayloadManager.GetDataFromObject(e.Data, "DraggedProtocolVariable") is not IProtocolVariable protocolVariable) return;

	    if (ResolveDropTargetControl(sender) is not { } control) return;
	    if (!control.CanBindVariable(protocolVariable.Variable))
	    {
		    VariableDragAndDropBehavior.IsOverValidTarget = false;
		    e.Handled = true;
		    return;
	    }

	    // Bind first (single-variable controls replace, multi-variable add), then make the host-owned
	    // subscriptions match: drop the ones the control no longer holds, subscribe the dropped one.
	    control.BindVariable(protocolVariable.Variable);
	    PruneControlSubscriptions(control);
	    SubscribeControlVariable(control, protocolVariable);
	    (control as IVariableWriteControl)?.RefreshWriteCapability();
	    (control as IVariableReadControl)?.RefreshReadCapability();

	    VariableDragAndDropBehavior.IsOverValidTarget = false;
	    e.Handled = true;
    }

	#endregion
}
