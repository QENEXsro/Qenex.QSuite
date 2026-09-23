using System.ComponentModel;
using System.Runtime.CompilerServices;
using Qenex.QLibs.QUI;

namespace Qenex.QSuite.Controls.WatchTableControl.Models;

/// <summary>One watch table row - one bound variable.</summary>
public sealed class WatchRow : INotifyPropertyChanged
{
	/// <summary>Variable reference (ControlBase.GetVariableReference) - identifies the row.</summary>
	public string Reference { get; init; } = string.Empty;

	public string Name { get; set { field = value; OnChanged(); } } = string.Empty;
	public string Unit { get; set { field = value; OnChanged(); } } = string.Empty;
	public string Value { get; set { field = value; OnChanged(); } } = string.Empty;
	public DateTime Time { get; set { field = value; OnChanged(); } }

	/// <summary>Last update time - used for throttling (RefreshTime).</summary>
	internal DateTime LastUpdate { get; set; } = DateTime.MinValue;

	#region Write mode (per row)

	/// <summary>Set by the view model: writes EditValue of this row to the device.</summary>
	public Action<WatchRow>? WriteRequested { get; set; }

	/// <summary>Write capability of the bound variable (decided by the protocol via the host
	/// provider); re-evaluated on every (re)bind by RefreshWriteCapability.</summary>
	public bool CanWrite
	{
		get;
		set { field = value; OnChanged(); OnChanged(nameof(IsWriteActive)); }
	}

	/// <summary>User toggle (Write column checkbox); persisted through
	/// WatchTableControlViewModel.WriteModeReferences.</summary>
	public bool IsWriteMode
	{
		get;
		set { field = value; OnChanged(); OnChanged(nameof(IsWriteActive)); }
	}

	/// <summary>While active, incoming updates do not overwrite this row's display.</summary>
	public bool IsWriteActive => IsWriteMode && CanWrite;

	/// <summary>Edited engineering value; any edit clears the error state.</summary>
	public string EditValue
	{
		get;
		set { field = value; OnChanged(); IsWriteError = false; }
	} = string.Empty;

	public bool IsWriteError { get; set { field = value; OnChanged(); } }

	public RelayCommand<object> WriteCommand => field ??= new RelayCommand<object>(_ => WriteRequested?.Invoke(this));

	#endregion

	#region Read on request (per row)

	/// <summary>Set by the view model: reads this row's variable from the device once.</summary>
	public Action<WatchRow>? ReadRequested { get; set; }

	/// <summary>Read-on-request capability of the bound variable (On Request event, decided by the
	/// protocol via the host provider); re-evaluated on every (re)bind by RefreshReadCapability.
	/// Periodically polled rows keep Read disabled.</summary>
	public bool CanRead
	{
		get;
		set { field = value; OnChanged(); OnChanged(nameof(CanReadNow)); ReadCommand.OnCanExecuteChanged(); }
	}

	public bool IsReadBusy
	{
		get;
		set { field = value; OnChanged(); OnChanged(nameof(CanReadNow)); ReadCommand.OnCanExecuteChanged(); }
	}

	public bool CanReadNow => CanRead && !IsReadBusy;

	/// <summary>Last on-request read of this row failed; cleared by the next value update.</summary>
	public bool IsReadError { get; set { field = value; OnChanged(); } }

	public RelayCommand<object> ReadCommand =>
		field ??= new RelayCommand<object>(_ => ReadRequested?.Invoke(this), _ => CanReadNow);

	#endregion

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnChanged([CallerMemberName] string? propertyName = null)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
