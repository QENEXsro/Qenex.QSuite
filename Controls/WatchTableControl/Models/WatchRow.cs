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

	private bool suppressDirty;

	/// <summary>Set by the view model: writes EditValue of this row to the device.</summary>
	public Action<WatchRow>? WriteRequested { get; set; }

	/// <summary>Table-wide "Write on Enter" option, pushed in by the view model: Enter writes
	/// immediately; unticked, the edit stays pending and the row's Write button sends it.</summary>
	public bool WriteOnEnter
	{
		get;
		set { field = value; OnChanged(); OnChanged(nameof(IsWriteButtonVisible)); }
	}

	/// <summary>Write capability of the bound variable (decided by the protocol via the host
	/// provider); re-evaluated on every (re)bind by RefreshWriteCapability.</summary>
	public bool CanWrite
	{
		get;
		set { field = value; OnChanged(); NotifyWriteStateChanged(); }
	}

	/// <summary>User toggle (Write column checkbox); persisted through
	/// WatchTableControlViewModel.WriteModeReferences.</summary>
	public bool IsWriteMode
	{
		get;
		set { field = value; OnChanged(); NotifyWriteStateChanged(); }
	}

	/// <summary>While active, incoming updates do not overwrite this row's display.</summary>
	public bool IsWriteActive => IsWriteMode && CanWrite;

	/// <summary>Edited engineering value; a user edit marks the row pending (dirty) and clears
	/// the error state.</summary>
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
			OnChanged();
			if (!suppressDirty)
			{
				IsDirty = true;
				IsWriteError = false;
			}
		}
	} = string.Empty;

	/// <summary>Edited value not written yet (yellow tint, enables the Write button).</summary>
	public bool IsDirty
	{
		get;
		set { field = value; OnChanged(); OnChanged(nameof(CanWriteDirty)); }
	}

	public bool IsWriteError { get; set { field = value; OnChanged(); } }

	/// <summary>Write button of the row: shown in write mode when Enter does not write.</summary>
	public bool IsWriteButtonVisible => IsWriteActive && !WriteOnEnter;

	/// <summary>Write button enablement: greys out until the value is edited, greys back after the write.</summary>
	public bool CanWriteDirty => IsWriteActive && IsDirty;

	/// <summary>Enter in the edit box: immediate write when Write on Enter is ticked, otherwise
	/// the edit stays pending for the Write button.</summary>
	public RelayCommand<object> CommitCommand => field ??= new RelayCommand<object>(_ =>
	{
		if (WriteOnEnter)
		{
			WriteRequested?.Invoke(this);
		}
	});

	/// <summary>Write button: writes the pending value.</summary>
	public RelayCommand<object> WriteCommand => field ??= new RelayCommand<object>(_ => WriteRequested?.Invoke(this));

	/// <summary>Sets the edit value without marking the row dirty (prefill/refresh).</summary>
	public void SetEditValueSilently(string text)
	{
		suppressDirty = true;
		try
		{
			EditValue = text;
		}
		finally
		{
			suppressDirty = false;
		}

		IsDirty = false;
		IsWriteError = false;
	}

	private void NotifyWriteStateChanged()
	{
		OnChanged(nameof(IsWriteActive));
		OnChanged(nameof(IsWriteButtonVisible));
		OnChanged(nameof(CanWriteDirty));
	}

	#endregion

	#region Read on request (per row)

	/// <summary>Set by the view model: reads this row's variable from the device once.</summary>
	public Action<WatchRow>? ReadRequested { get; set; }

	/// <summary>Read-on-request capability of the bound variable (On Request event, decided by the
	/// protocol via the host provider); re-evaluated on every (re)bind by RefreshReadCapability.
	/// Periodically polled rows have no Read button (Collapsed).</summary>
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
