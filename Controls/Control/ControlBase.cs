
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Qenex.QLibs.QUI;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Telerik.Windows.Controls;

namespace Qenex.QSuite.Controls.Control;

[DataContract]
public abstract class ControlBase : PropertyChangedBaseWithValidation, IControlBase
{
	private bool isRun;

	protected ControlBase()
	{
		Width = MinWidth;
		Height = MinHeight;
		AreConnectorsEnabled = false;
		Variables = [];
		LinkedVariables = [];
	}

	#region Properties

	/// <summary>
	/// Component specification used for plugin discovery (PluginLoader). Built lazily
	/// from the control's own metadata so derived (abstract) members are available.
	/// </summary>
	[IgnoreDataMember]
	public ISpecification Specification =>
		field ??= new SpecificationBase
		{
			Name = ControlName,
			Label = Label,
			Description = Description,
			Version = GetType().Assembly.GetName().Version ?? new Version(1, 0, 0)
		};

	[DataMember]
	public int Id { get; set; }
	
	[IgnoreDataMember]
	public RadDiagramShape? DiagramShape { get; set; }
	
	[IgnoreDataMember]
	public abstract string ControlName { get; }

	[IgnoreDataMember]
	public abstract string Label { get; }

	[IgnoreDataMember]
	public abstract BitmapImage Icon { get; }

	[IgnoreDataMember]
	public abstract string Description { get; }

	[IgnoreDataMember]
	public int MinHeight => 50;

	[IgnoreDataMember]
	public int MinWidth => 100;

	[DataMember]
	public int X { get; set { field = value; OnPropertyChanged(); } }
	[DataMember]
	public int Y { get; set { field = value; OnPropertyChanged(); } }
	[DataMember]
	public int Width { get; set { field = value; OnPropertyChanged(); } }
	[DataMember]
	public int Height { get; set { field = value; OnPropertyChanged(); } }
	[DataMember]
	public bool AreConnectorsEnabled { get; set { field = value; OnPropertyChanged(); } }

	[IgnoreDataMember]
	public Color BackgroundColor
	{
		get;
		set
		{
			field = value;
			OnPropertyChanged();
			DiagramShape?.Background = new SolidColorBrush(value);
		}
	} = Colors.White;//Color.FromRgb(90, 90, 90);

    [IgnoreDataMember]
    public Color ForegroundColor
	{
		get;
		set { field = value; OnPropertyChanged(); }
	}
	
    [IgnoreDataMember]
    public int FontSize
	{
		get;
		set { field = value; OnPropertyChanged(); }
	}

	/// <summary>Application log injected by the host after creation (see IControlBase.Logger).</summary>
	[IgnoreDataMember]
	public ILogger? Logger { get; set; }
	
	[DataMember]
	public bool IsRun
	{
		get => isRun;
		set
		{
			if (isRun == value)
			{
				return;
			}

			var wasRun = isRun;
			isRun = value;
			OnPropertyChanged();
			OnIsRunChanged(wasRun, isRun);

			if (!wasRun && isRun)
			{
				IsLocked = true;
				OnEditToRun();
			}
			else if (wasRun && !isRun)
			{
				IsLocked = EditModeLock;
				OnRunToEdit();
			}
		}
	}
	[IgnoreDataMember]
	public ObservableCollection<IVariableBase> Variables { get; set { field = value; OnPropertyChanged(); } }

	[DataMember]
	public List<string> LinkedVariables { get; set; }

	[IgnoreDataMember]
	public bool EditModeLock { get; set; }

	[DataMember]
	public bool IsLocked
	{
		get;
		set
		{
			field = value;
			if (!isRun)
			{
				EditModeLock = value;
			}
			OnPropertyChanged();
			DiagramShape?.AllowDelete = !value;
			DiagramShape?.AllowCut = !value;
			DiagramShape?.IsManipulationEnabled = !value;
			DiagramShape?.IsManipulationAdornerVisible = !value;
			DiagramShape?.IsManipulationAdornerVisible = !value;
			DiagramShape?.IsDraggingEnabled = !value;
			DiagramShape?.IsRotationEnabled = !value;
			DiagramShape?.IsResizingEnabled = !value;
		}
	} = false;

	#endregion

	#region Public methods

	public abstract Task UpdateVariableValueAsync(IVariableBase variable);

	/// <summary>
	/// Whether the control can display the given variable type; override to restrict
	/// (e.g. scalar-only controls refuse a matrix variable). Default accepts everything.
	/// </summary>
	public virtual bool CanBindVariable(IVariableBase variable) => true;

	public abstract void BindVariable(IVariableBase protVariable);

	protected virtual void OnIsRunChanged(bool wasRun, bool isRun)
	{
	}

	protected virtual void OnEditToRun()
	{
	}

	protected virtual void OnRunToEdit()
	{
	}

	public virtual void RefreshVariableBinding(IVariableBase variable)
	{
		if (IsVariableBound(variable))
		{
			OnPropertyChanged(nameof(Variables));
		}
	}

	/// <summary>
	/// Public query for whether this control uses the given variable (runtime binding
	/// or persisted reference). Used to prevent deleting a variable that is still in use.
	/// </summary>
	public bool IsVariableUsed(IVariableBase variable) => IsVariableBound(variable);

	protected bool IsVariableBound(IVariableBase variable)
	{
		return FindBoundVariable(variable) != null
		       || LinkedVariables.Any(reference => IsVariableReferenceMatch(reference, variable));
	}

	protected IVariableBase? FindBoundVariable(IVariableBase variable)
	{
		return Variables.FirstOrDefault(v =>
			ReferenceEquals(v, variable)
			|| IsVariableReferenceMatch(GetVariableReference(v), variable));
	}

	public void RememberVariableBinding(IVariableBase variable)
	{
		var variableReference = GetVariableReference(variable);
		if (!LinkedVariables.Contains(variableReference))
		{
			LinkedVariables.Add(variableReference);
		}
	}

	/// <summary>
	/// Raised when the control changes its own variable bindings at runtime (e.g. the user
	/// removed a variable), so the host can reconcile its value subscriptions.
	/// </summary>
	public event Action<ControlBase>? VariableBindingsChanged;

	/// <summary>Notifies the host that this control's variable bindings changed.</summary>
	protected void RaiseVariableBindingsChanged() => VariableBindingsChanged?.Invoke(this);

	public static string GetVariableReference(IVariableBase variable)
	{
		return $"{variable.Id}|{variable.Namespace}|{variable.Name}";
	}

	public static bool IsVariableReferenceMatch(string variableReference, IVariableBase variable)
	{
		if (variableReference == GetVariableReference(variable))
		{
			return true;
		}

		var parts = variableReference.Split('|');
		if (parts.Length == 3 && int.TryParse(parts[0], out var id))
		{
			return variable.Id == id || (variable.Namespace == parts[1] && variable.Name == parts[2]);
		}

		return variable.Name == variableReference;
	}
	
	public virtual void UpdateThemeSettingsControl(Color backgroundColor, Color foregroundColor, int fontSize)
	{
		BackgroundColor = backgroundColor;
		ForegroundColor = foregroundColor;
		FontSize = fontSize;
	}

	[OnDeserializing]
	private void OnDeserializing(StreamingContext context)
	{
		Variables = [];
		LinkedVariables = [];
	}

	[OnDeserialized]
	private void OnDeserialized(StreamingContext context)
	{
		EditModeLock = IsLocked;
	}

	#endregion
}
