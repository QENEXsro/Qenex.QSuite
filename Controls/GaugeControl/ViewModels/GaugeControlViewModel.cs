using System.Globalization;
using System.Runtime.Serialization;
using System.Windows;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using System.Windows.Media.Imaging;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.GaugeControl.ViewModels;

/// <summary>Gauge type/orientation.</summary>
public enum GaugeKind
{
	Radial,
	LinearHorizontal,
	LinearVertical
}

/// <summary>Zone classification of the current value against the configured levels.</summary>
public enum GaugeZoneKind
{
	Normal,
	Warning,
	Error
}

[DataContract]
public class GaugeControlViewModel : ControlBase
{
	private DateTime previousUpdateTime = DateTime.MinValue;

	public GaugeControlViewModel()
	{
		Width = 200;
		Height = 200;
		VariableLabel = "----------";
		VariableUnit = "-";
	}

	#region Display properties (not serialized)

	[IgnoreDataMember]
	public string VariableLabel { get; set { field = value; OnPropertyChanged(); } }

	[IgnoreDataMember]
	public string VariableUnit { get; set { field = value; OnPropertyChanged(); } }

	[IgnoreDataMember]
	public double Value
	{
		get;
		set
		{
			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(DisplayValue));
			OnPropertyChanged(nameof(ValueText));
			UpdateAlarmState();
		}
	}

	/// <summary>False until the first value read from the device in this run: the needle rests
	/// at the scale minimum and the numeric caption is empty.</summary>
	[IgnoreDataMember]
	public bool HasValue
	{
		get;
		private set
		{
			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(ValueText));
		}
	}

	/// <summary>Numeric caption under the gauge; empty before the first value.</summary>
	[IgnoreDataMember]
	public string ValueText => HasValue ? Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

	/// <summary>Value shown by the needle/bar, clamped to the scale range - an out-of-range
	/// value must not draw the indicator beyond the scale ends.</summary>
	[IgnoreDataMember]
	public double DisplayValue => ClampToScale(Value);

	[IgnoreDataMember]
	public GaugeZoneKind AlarmState
	{
		get;
		private set { field = value; OnPropertyChanged(); }
	}

	private static readonly GaugeKind[] AllKinds = Enum.GetValues<GaugeKind>();

	/// <summary>Options for the type switcher in the control settings. Static - keeps working
	/// after deserialization (DataContractSerializer bypasses ctors and initializers).</summary>
	[IgnoreDataMember]
	public IReadOnlyList<GaugeKind> AvailableKinds => AllKinds;

	#endregion

	#region Configuration (serialized)

	[DataMember]
	public GaugeKind Kind { get; set { field = value; OnPropertyChanged(); } } = GaugeKind.Radial;

	[DataMember]
	public double Minimum { get; set { field = value; OnPropertyChanged(); OnScaleConfigChanged(); } }

	[DataMember]
	public double Maximum { get; set { field = value; OnPropertyChanged(); OnScaleConfigChanged(); } } = 100;

	[DataMember]
	public double? LowErrorLevel { get; set { field = value; OnPropertyChanged(); OnScaleConfigChanged(); } }

	[DataMember]
	public double? LowWarningLevel { get; set { field = value; OnPropertyChanged(); OnScaleConfigChanged(); } }

	[DataMember]
	public double? HighWarningLevel { get; set { field = value; OnPropertyChanged(); OnScaleConfigChanged(); } }

	[DataMember]
	public double? HighErrorLevel { get; set { field = value; OnPropertyChanged(); OnScaleConfigChanged(); } }

	[DataMember]
	public int RefreshTime
	{
		get;
		set
		{
			if (value < 0) value = 0;
			field = value;
			OnPropertyChanged();
		}
	} = 250;

	#endregion

	#region Derived properties

	public override string ControlName => "GaugeControl";
	public override string Label => "Gauge";
	public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/Gauge.png");
	public override string Description => "Gauge (radial / linear) with Warning and Error levels for both the low and high bound.";

	#endregion

	#region Levels / zones

	/// <summary>Classification of a value against the configured levels (4 levels, each optional).</summary>
	public GaugeZoneKind Classify(double value)
	{
		if (LowErrorLevel is { } lowError && value < lowError) return GaugeZoneKind.Error;
		if (HighErrorLevel is { } highError && value > highError) return GaugeZoneKind.Error;
		if (LowWarningLevel is { } lowWarning && value < lowWarning) return GaugeZoneKind.Warning;
		if (HighWarningLevel is { } highWarning && value > highWarning) return GaugeZoneKind.Warning;
		return GaugeZoneKind.Normal;
	}

	// Five fixed zones declared in the view (low error, low warning, normal, high warning,
	// high error), each bound to the clamped boundaries below. Boundaries are monotonically
	// non-decreasing and always inside [Minimum, Maximum], so a level outside the scale range
	// (or a missing level) collapses its zone to zero width instead of drawing outside the
	// scale - that was one source of leftover artifacts next to the gauge.

	public double LowErrorZoneMin => Minimum;
	public double LowErrorZoneMax => LowErrorBound;
	public double LowWarningZoneMin => LowErrorBound;
	public double LowWarningZoneMax => LowWarningBound;
	public double NormalZoneMin => LowWarningBound;
	public double NormalZoneMax => Math.Max(HighWarningBound, LowWarningBound);
	public double HighWarningZoneMin => NormalZoneMax;
	public double HighWarningZoneMax => Math.Max(HighErrorBound, NormalZoneMax);
	public double HighErrorZoneMin => HighWarningZoneMax;
	public double HighErrorZoneMax => Maximum;

	private double LowErrorBound => LowErrorLevel is { } level ? ClampToScale(level) : Minimum;
	private double LowWarningBound => Math.Max(LowWarningLevel is { } level ? ClampToScale(level) : LowErrorBound, LowErrorBound);
	private double HighErrorBound => HighErrorLevel is { } level ? ClampToScale(level) : Maximum;
	private double HighWarningBound => Math.Min(HighWarningLevel is { } level ? ClampToScale(level) : HighErrorBound, HighErrorBound);

	private double ClampToScale(double value) => Math.Min(Math.Max(value, Minimum), Maximum);

	private void OnScaleConfigChanged()
	{
		UpdateAlarmState();
		OnPropertyChanged(nameof(DisplayValue));
		OnPropertyChanged(nameof(LowErrorZoneMin));
		OnPropertyChanged(nameof(LowErrorZoneMax));
		OnPropertyChanged(nameof(LowWarningZoneMin));
		OnPropertyChanged(nameof(LowWarningZoneMax));
		OnPropertyChanged(nameof(NormalZoneMin));
		OnPropertyChanged(nameof(NormalZoneMax));
		OnPropertyChanged(nameof(HighWarningZoneMin));
		OnPropertyChanged(nameof(HighWarningZoneMax));
		OnPropertyChanged(nameof(HighErrorZoneMin));
		OnPropertyChanged(nameof(HighErrorZoneMax));
	}

	private void UpdateAlarmState() => AlarmState = Classify(Value);

	#endregion

	#region Variable binding

	// The needle shows a single numeric value: scalar variables only
	public override bool CanBindVariable(IVariableBase variable) => variable is ScalarVariable;

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
	}

	public override async Task UpdateVariableValueAsync(IVariableBase protVariable)
	{
		double engValue;
		switch (protVariable)
		{
			case ScalarVariable scalar:
				engValue = scalar.GetEngValue();
				break;
			case StringVariable str when double.TryParse(str.Values, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
				engValue = parsed;
				break;
			default:
				return;
		}

		if (protVariable.Timestamp < previousUpdateTime) previousUpdateTime = DateTime.MinValue;
		if ((protVariable.Timestamp - previousUpdateTime).TotalMilliseconds < RefreshTime) return;
		previousUpdateTime = protVariable.Timestamp;

		_ = Application.Current.Dispatcher.BeginInvoke(() =>
		{
			Value = engValue;
			HasValue = true;
		});
	}

	/// <summary>Edit -> Run: nothing has been read from the device yet, so the gauge shows nothing
	/// (needle at the scale minimum, empty caption) until the first poll / Read (Radek 2026-09-25).</summary>
	protected override void OnEditToRun()
	{
		previousUpdateTime = DateTime.MinValue;
		HasValue = false;
		Value = Minimum;
	}

	[OnDeserialized]
	private void OnDeserialized(StreamingContext context)
	{
		VariableLabel ??= "----------";
		VariableUnit ??= string.Empty;
		Variables ??= [];
		LinkedVariables ??= [];
		UpdateAlarmState();
	}

	#endregion
}
