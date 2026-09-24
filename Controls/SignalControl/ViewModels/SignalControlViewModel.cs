using System.Globalization;
using System.Runtime.Serialization;
using System.Windows;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using System.Windows.Media.Imaging;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.SignalControl.ViewModels;

[DataContract]
public class SignalControlViewModel : ControlBase, IVariableWriteControl, IVariableReadControl, ISampleHistoryControl
{
	private DateTime previousUpdateTime = DateTime.MinValue;
	private double prevValue;
	private bool suppressDirty;
	
    public SignalControlViewModel()
    {
		Width = 140;
		Height = 60;
		
		VariableLabel = "----------";
		VariableValue = "";
		VariableUnit = "-";
    }

    #region Properties

    [IgnoreDataMember]
    public string VariableLabel { get; set { field = value; OnPropertyChanged(); } }

    [IgnoreDataMember]
    public string VariableValue { get; set { field = value; OnPropertyChanged(); } }

    [IgnoreDataMember]
    public string VariableUnit { get; set { field = value; OnPropertyChanged(); } }
    
    [DataMember]
    public int RefreshTime
    {
	    get;
	    set
	    {
		    if (value < 0) value = 0;
		    field = value; OnPropertyChanged();
	    }
    } = 500;

    // Change in % of the presentation Min-Max range that redraws immediately,
    // without waiting for RefreshTime (so short peaks are not lost);
    // 0 = every change redraws immediately.
    [DataMember]
    public int PeakThresholdPercentage
    {
	    get;
	    set
	    {
		    if (value < 0) value = 0;
		    field = value; OnPropertyChanged();
	    }
    } = 5;

    // Legacy member name in older .qproj files; alphabetical member order makes
    // the serializer read it before PeakThresholdPercentage. Never serialized
    // back (always 0 + EmitDefaultValue false).
    [DataMember(Name = "DeathBendPercentage", EmitDefaultValue = false)]
    private int LegacyDeathBendPercentage { get => 0; set => PeakThresholdPercentage = value; }

    #endregion

    #region Write mode (IVariableWriteControl)

    [IgnoreDataMember]
    public Func<IVariableBase, bool>? CanWriteVariableProvider { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, double, Task<bool>>? WriteVariableEngValueAsync { get; set; }

    [DataMember]
    public bool IsWriteMode
    {
	    get;
	    set
	    {
		    field = value;
		    OnPropertyChanged();
		    NotifyWriteStateChanged();
		    if (value)
		    {
			    PrefillEditValue();
		    }
		    else
		    {
			    // Leaving write mode discards the pending edit and re-follows the variable: the
			    // display was frozen meanwhile, so show its current value (after a write it is
			    // the written one) — an On Request variable gets no poll that would refresh it.
			    IsDirty = false;
			    RefreshDisplayFromVariable();
		    }
	    }
    }

    /// <summary>Write mode: Enter writes the edited value immediately instead of leaving it
    /// pending for the Write button (same option as in the Matrix control).</summary>
    [DataMember]
    public bool WriteOnEnter
    {
	    get;
	    set
	    {
		    field = value;
		    OnPropertyChanged();
		    OnPropertyChanged(nameof(IsWriteButtonVisible));
	    }
    }

    private void RefreshDisplayFromVariable()
    {
	    var text = Variables?.FirstOrDefault() switch
	    {
		    ScalarVariable scalarVariable => scalarVariable.GetPresentationText(),
		    StringVariable stringVariable => stringVariable.Values,
		    _ => null
	    };

	    if (text != null)
	    {
		    VariableValue = text;
	    }
    }

    [IgnoreDataMember]
    public bool CanWrite
    {
	    get;
	    private set
	    {
		    field = value;
		    OnPropertyChanged();
		    NotifyWriteStateChanged();
	    }
    }

    [IgnoreDataMember]
    public bool IsWriteActive => IsWriteMode && CanWrite;

    /// <summary>Edited value in write mode; a user edit marks it pending (dirty).</summary>
    [IgnoreDataMember]
    public string EditValue
    {
	    get;
	    set
	    {
		    if (field == value)
		    {
			    return;
		    }

		    field = value;
		    OnPropertyChanged();
		    if (!suppressDirty)
		    {
			    IsDirty = true;
			    IsWriteError = false;
		    }
	    }
    }

    /// <summary>Edited value not written yet (yellow tint, enables the Write button).</summary>
    [IgnoreDataMember]
    public bool IsDirty
    {
	    get;
	    private set
	    {
		    field = value;
		    OnPropertyChanged();
		    OnPropertyChanged(nameof(CanWriteDirty));
	    }
    }

    [IgnoreDataMember]
    public bool IsWriteError { get; set { field = value; OnPropertyChanged(); } }

    /// <summary>Write button: shown in write mode when Enter does not write (Write on Enter off).</summary>
    [IgnoreDataMember]
    public bool IsWriteButtonVisible => IsWriteActive && !WriteOnEnter;

    /// <summary>Write button enablement: greys out until the value is edited, greys back after the write.</summary>
    [IgnoreDataMember]
    public bool CanWriteDirty => IsWriteActive && IsDirty;

    // Lazy kvuli deserializaci (DataContractSerializer nevola konstruktor)
    /// <summary>Enter in the edit box: immediate write when Write on Enter is ticked, otherwise
    /// the edit stays pending for the Write button.</summary>
    [IgnoreDataMember]
    public RelayCommand<object> WriteValueCommand => field ??= new RelayCommand<object>(OnCommitEdit);

    /// <summary>Write button: writes the pending value.</summary>
    [IgnoreDataMember]
    public RelayCommand<object> WriteDirtyCommand => field ??= new RelayCommand<object>(_ => _ = WriteValueAsync());

    public void RefreshWriteCapability()
    {
	    // Numeric scalars only (a string variable has no engineering value to write).
	    var variable = Variables?.FirstOrDefault();
	    CanWrite = variable is ScalarVariable && (CanWriteVariableProvider?.Invoke(variable) ?? false);
    }

    private void NotifyWriteStateChanged()
    {
	    OnPropertyChanged(nameof(IsWriteActive));
	    OnPropertyChanged(nameof(IsWriteButtonVisible));
	    OnPropertyChanged(nameof(CanWriteDirty));
    }

    private void OnCommitEdit(object parameter)
    {
	    if (WriteOnEnter)
	    {
		    _ = WriteValueAsync();
	    }
	    // Without Write on Enter the edit already marked the value dirty; the Write button sends it.
    }

    private async Task WriteValueAsync()
    {
	    if (!IsWriteActive || !IsRun || WriteVariableEngValueAsync == null)
	    {
		    IsWriteError = true;
		    return;
	    }

	    var variable = Variables?.FirstOrDefault();
	    if (variable == null || !TryParseEditValue(out var engValue))
	    {
		    IsWriteError = true;
		    return;
	    }

	    bool written;
	    try
	    {
		    written = await WriteVariableEngValueAsync(variable, engValue);
	    }
	    catch
	    {
		    written = false;
	    }

	    IsWriteError = !written;
	    if (written)
	    {
		    IsDirty = false;
	    }
    }

    private bool TryParseEditValue(out double engValue)
    {
	    return double.TryParse((EditValue ?? string.Empty).Replace(',', '.'),
		    NumberStyles.Float, CultureInfo.InvariantCulture, out engValue);
    }

    /// <summary>Sets the edit box to the variable's current value without marking it dirty.</summary>
    private void PrefillEditValue()
    {
	    suppressDirty = true;
	    try
	    {
		    EditValue = Variables?.FirstOrDefault() is ScalarVariable scalarVariable
			    ? scalarVariable.GetEngValue().ToString(CultureInfo.InvariantCulture)
			    : string.Empty;
	    }
	    finally
	    {
		    suppressDirty = false;
	    }

	    IsDirty = false;
	    IsWriteError = false;
    }

    #endregion

    #region Read on request (IVariableReadControl)

    [IgnoreDataMember]
    public Func<IVariableBase, bool>? CanReadVariableProvider { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, Task<bool>>? ReadVariableAsync { get; set; }

    /// <summary>True only for a variable bound to an On Request event (decided by the protocol
    /// through the host provider); periodically polled variables keep Read disabled.</summary>
    [IgnoreDataMember]
    public bool CanRead
    {
	    get;
	    private set
	    {
		    field = value;
		    OnPropertyChanged();
		    OnPropertyChanged(nameof(CanReadNow));
		    ReadCommand.OnCanExecuteChanged();
	    }
    }

    [IgnoreDataMember]
    public bool IsReadBusy
    {
	    get;
	    private set
	    {
		    field = value;
		    OnPropertyChanged();
		    OnPropertyChanged(nameof(CanReadNow));
		    ReadCommand.OnCanExecuteChanged();
	    }
    }

    [IgnoreDataMember]
    public bool CanReadNow => CanRead && !IsReadBusy;

    /// <summary>Last on-request read failed (timeout, device error, not running); cleared by the
    /// next successful read or value update.</summary>
    [IgnoreDataMember]
    public bool IsReadError { get; private set { field = value; OnPropertyChanged(); } }

    // Lazy kvuli deserializaci (DataContractSerializer nevola konstruktor)
    [IgnoreDataMember]
    public RelayCommand<object> ReadCommand => field ??= new RelayCommand<object>(_ => _ = ReadValueAsync(), _ => CanReadNow);

    public void RefreshReadCapability()
    {
	    var variable = Variables?.FirstOrDefault();
	    CanRead = variable != null && (CanReadVariableProvider?.Invoke(variable) ?? false);
    }

    private async Task ReadValueAsync()
    {
	    var variable = Variables?.FirstOrDefault();
	    if (!CanReadNow || !IsRun || ReadVariableAsync == null || variable == null)
	    {
		    IsReadError = true;
		    return;
	    }

	    IsReadBusy = true;
	    try
	    {
		    // The value arrives through UpdateVariableValueAsync during the read; reset the
		    // throttle so it is shown even when the last refresh was a moment ago.
		    previousUpdateTime = DateTime.MinValue;
		    var read = await ReadVariableAsync(variable);
		    IsReadError = !read;
		    if (read && IsWriteActive)
		    {
			    // An explicit Read means the user wants the fresh value even in write mode
			    // (the display is frozen there): show it and discard the pending edit.
			    _ = Application.Current.Dispatcher.BeginInvoke(() =>
			    {
				    RefreshDisplayFromVariable();
				    PrefillEditValue();
			    });
		    }
	    }
	    catch
	    {
		    IsReadError = true;
	    }
	    finally
	    {
		    IsReadBusy = false;
	    }
    }

    #endregion

    #region Derived properties

    public override string ControlName => "SignalControl";
    public override string Label => "Single-Signal";
    public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/SingleSignalControl.png");
    public override string Description => "Signal control for displaying a single signal.";
    
    #endregion

    #region Public methods

    public override async Task UpdateVariableValueAsync(IVariableBase protVariable)
    {
	    // Ve write rezimu se displej tohoto controlu zmrazi (komunikace bezi dal,
	    // ostatni controly stejnou promennou zobrazuji normalne)
	    if (IsWriteActive) return;

	    var dataValue = protVariable switch
	    {
		    ScalarVariable scVar => scVar.GetPresentationText(),
		    StringVariable stVar => stVar.Values
	    };
	    
	    if (dataValue == null ) return;
	    if (protVariable.Timestamp < previousUpdateTime)
	    {
		    previousUpdateTime = DateTime.MinValue;
		    prevValue = 0;
	    }

	    // Regular redraw on the RefreshTime tick; a change exceeding
	    // PeakThresholdPercentage of the presentation range redraws immediately.
	    var tickElapsed = (protVariable.Timestamp - previousUpdateTime).TotalMilliseconds >= RefreshTime;
	    if (protVariable is ScalarVariable scalarVariable)
	    {
		    var engValue = scalarVariable.GetEngValue();
		    if (!tickElapsed && !ExceedsPeakThreshold(scalarVariable, engValue)) return;
		    prevValue = engValue;
	    }
	    else if (!tickElapsed) return;

	    previousUpdateTime = protVariable.Timestamp;

	    _ = Application.Current.Dispatcher.BeginInvoke(() =>
	    {
		    VariableValue = dataValue;
		    IsReadError = false;
	    });

    }

    private bool ExceedsPeakThreshold(ScalarVariable variable, double engValue)
    {
	    if (engValue == prevValue) return false;
	    if (PeakThresholdPercentage <= 0) return true;

	    // String/enum presentations have no numeric range -> tick only
	    var presentation = variable.Values.ValPresentation;
	    var range = presentation == null ? 0 : presentation.Max - presentation.Min;
	    if (range <= 0) return false;

	    return Math.Abs(engValue - prevValue) / range * 100 >= PeakThresholdPercentage;
    }

    // Zobrazuje jednu hodnotu: skalar nebo string (matice apod. patri specializovanym controlum)
    public override bool CanBindVariable(IVariableBase variable) => variable is ScalarVariable or StringVariable;

    public override void BindVariable(IVariableBase protVariable)
    {
	    if (!CanBindVariable(protVariable) || Variables.Any(v => v.Equals(protVariable)))
	    {
		    return;
	    }

	    // Single-variable control: replace the previous variable (host unsubscribes the old).
	    Variables.Clear();
	    LinkedVariables.Clear();

	    RememberVariableBinding(protVariable);
	    Variables.Add(protVariable);
	    VariableLabel = protVariable.Label;
	    VariableUnit = protVariable is ScalarVariable variable ? variable.Values.ValPresentation.Unit : string.Empty;
	    VariableValue = string.Empty;
	    previousUpdateTime = DateTime.MinValue;
	    prevValue = 0;

	    // Zapisovatelnost i citelnost na vyzadani se musi prehodnotit pri kazdem (re)bindu
	    RefreshWriteCapability();
	    RefreshReadCapability();
	    IsReadError = false;
	    if (IsWriteMode)
	    {
		    PrefillEditValue();
	    }
    }

    public override void RefreshVariableBinding(IVariableBase variable)
    {
	    base.RefreshVariableBinding(variable);

	    if (!IsVariableBound(variable))
	    {
		    return;
	    }

	    VariableLabel = variable.Label;
	    VariableUnit = variable is ScalarVariable scalarVariable
		    ? scalarVariable.Values.ValPresentation.Unit
		    : string.Empty;
	    RefreshReadCapability();
    }

    protected override void OnEditToRun()
    {
	    VariableValue = string.Empty;
	    previousUpdateTime = DateTime.MinValue;
	    prevValue = 0;
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
	    VariableLabel ??= "----------";
	    VariableValue ??= "----------";
	    VariableUnit ??= string.Empty;
	    Variables ??= [];
	    LinkedVariables ??= [];
	    EditValue ??= string.Empty;
    }

    #endregion
}
