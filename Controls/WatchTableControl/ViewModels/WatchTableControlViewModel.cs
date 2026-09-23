using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Serialization;
using System.Windows;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Controls.WatchTableControl.Models;
using System.Windows.Media.Imaging;
using Qenex.QSuite.Variables.QVariables;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Qenex.QSuite.Controls.WatchTableControl.ViewModels;

[DataContract]
public class WatchTableControlViewModel : ControlBase, IVariableWriteControl, IVariableReadControl
{
	public WatchTableControlViewModel()
	{
		Width = 280;
		Height = 180;
	}

	#region Properties (not serialized - lazy, keeps working after deserialization)

	/// <summary>Table rows (one per bound variable). Lazy - DataContractSerializer
	/// does not run ctors/initializers, hence `field ??=`.</summary>
	[IgnoreDataMember]
	public ObservableCollection<WatchRow> Rows => field ??= [];

	[IgnoreDataMember]
	public WatchRow? SelectedRow
	{
		get;
		set
		{
			field = value;
			OnPropertyChanged();
			RemoveSelectedCommand.OnCanExecuteChanged();
		}
	}

	[IgnoreDataMember]
	public RelayCommand<object> RemoveSelectedCommand =>
		field ??= new RelayCommand<object>(_ => RemoveSelected(), _ => SelectedRow != null);

	// Column visibility flags bound by the context menu and (via BindingProxy) by the grid
	// columns. Runtime state only - persisted through ColumnLayout, which the layout behavior
	// re-applies to the columns after load. Nullable backing defaults to visible because
	// DataContractSerializer does not run initializers.
	private bool? isNameColumnVisible;
	private bool? isValueColumnVisible;
	private bool? isUnitColumnVisible;
	private bool? isTimeColumnVisible;
	private bool? isWriteColumnVisible;
	private bool? isReadColumnVisible;

	[IgnoreDataMember]
	public bool IsNameColumnVisible
	{
		get => isNameColumnVisible ?? true;
		set { isNameColumnVisible = value; OnPropertyChanged(); }
	}

	[IgnoreDataMember]
	public bool IsValueColumnVisible
	{
		get => isValueColumnVisible ?? true;
		set { isValueColumnVisible = value; OnPropertyChanged(); }
	}

	[IgnoreDataMember]
	public bool IsUnitColumnVisible
	{
		get => isUnitColumnVisible ?? true;
		set { isUnitColumnVisible = value; OnPropertyChanged(); }
	}

	[IgnoreDataMember]
	public bool IsTimeColumnVisible
	{
		get => isTimeColumnVisible ?? true;
		set { isTimeColumnVisible = value; OnPropertyChanged(); }
	}

	[IgnoreDataMember]
	public bool IsWriteColumnVisible
	{
		get => isWriteColumnVisible ?? true;
		set { isWriteColumnVisible = value; OnPropertyChanged(); }
	}

	[IgnoreDataMember]
	public bool IsReadColumnVisible
	{
		get => isReadColumnVisible ?? true;
		set { isReadColumnVisible = value; OnPropertyChanged(); }
	}

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

	[DataMember]
	public string Title { get; set { field = value; OnPropertyChanged(); } } = string.Empty;

	/// <summary>Column layout (order + width + visibility) captured from the grid by
	/// ColumnLayoutBehavior. Serialized into the project and re-applied after load.</summary>
	[DataMember]
	public List<GridColumnLayout>? ColumnLayout
	{
		get;
		set
		{
			field = value;
			OnPropertyChanged();
		}
	}

	#endregion

	#region Write mode (IVariableWriteControl, per row)

	[IgnoreDataMember]
	public Func<IVariableBase, bool>? CanWriteVariableProvider { get; set; }

	[IgnoreDataMember]
	public Func<IVariableBase, double, Task<bool>>? WriteVariableEngValueAsync { get; set; }

	/// <summary>References of rows with write mode turned on - persisted per-row state.
	/// Lazy - DataContractSerializer does not run initializers.</summary>
	[DataMember]
	public List<string> WriteModeReferences
	{
		get => field ??= [];
		set;
	}

	public void RefreshWriteCapability()
	{
		foreach (var row in Rows)
		{
			RefreshRowWriteCapability(row);
		}
	}

	private void RefreshRowWriteCapability(WatchRow row)
	{
		// Only scalar rows are writable - the write path parses an engineering value
		// (string variables stay read-only in the table).
		var variable = FindVariable(row.Reference);
		row.CanWrite = variable is ScalarVariable && (CanWriteVariableProvider?.Invoke(variable) ?? false);
	}

	private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (sender is not WatchRow row || e.PropertyName != nameof(WatchRow.IsWriteMode))
		{
			return;
		}

		if (row.IsWriteMode)
		{
			if (!WriteModeReferences.Contains(row.Reference))
			{
				WriteModeReferences.Add(row.Reference);
			}

			PrefillEditValue(row);
		}
		else
		{
			WriteModeReferences.Remove(row.Reference);
			row.IsWriteError = false;

			// Leaving write mode: the row display was frozen meanwhile, so show the variable's
			// current value (after a write it is the written one) — an On Request variable gets
			// no poll that would refresh it otherwise.
			var text = FindVariable(row.Reference) switch
			{
				ScalarVariable scalar => scalar.GetPresentationText(),
				StringVariable str => str.Values,
				_ => null
			};
			if (text != null)
			{
				row.Value = text;
			}
		}
	}

	private void PrefillEditValue(WatchRow row)
	{
		row.EditValue = FindVariable(row.Reference) is ScalarVariable scalar
			? scalar.GetEngValue().ToString(CultureInfo.InvariantCulture)
			: string.Empty;
		row.IsWriteError = false;
	}

	private async Task WriteRowAsync(WatchRow row)
	{
		// Writes are allowed only in the Run mode (outside Run the command drivers
		// are not subscribed and a SetValue would only confuse the UI).
		if (!row.IsWriteActive || !IsRun || WriteVariableEngValueAsync == null)
		{
			row.IsWriteError = true;
			return;
		}

		var variable = FindVariable(row.Reference);
		if (variable == null || !TryParseEngValue(row.EditValue, out var engValue))
		{
			row.IsWriteError = true;
			return;
		}

		try
		{
			var written = await WriteVariableEngValueAsync(variable, engValue);
			row.IsWriteError = !written;
		}
		catch
		{
			row.IsWriteError = true;
		}
	}

	private static bool TryParseEngValue(string text, out double engValue)
	{
		return double.TryParse((text ?? string.Empty).Replace(',', '.'),
			NumberStyles.Float, CultureInfo.InvariantCulture, out engValue);
	}

	private IVariableBase? FindVariable(string reference)
	{
		return Variables.FirstOrDefault(v => GetVariableReference(v) == reference);
	}

	#endregion

	#region Read on request (IVariableReadControl, per row)

	[IgnoreDataMember]
	public Func<IVariableBase, bool>? CanReadVariableProvider { get; set; }

	[IgnoreDataMember]
	public Func<IVariableBase, Task<bool>>? ReadVariableAsync { get; set; }

	public void RefreshReadCapability()
	{
		foreach (var row in Rows)
		{
			RefreshRowReadCapability(row);
		}
	}

	private void RefreshRowReadCapability(WatchRow row)
	{
		// Only a variable bound to an On Request event is readable on request (the protocol
		// decides through the host provider); periodically polled rows keep Read disabled.
		var variable = FindVariable(row.Reference);
		row.CanRead = variable != null && (CanReadVariableProvider?.Invoke(variable) ?? false);
	}

	private async Task ReadRowAsync(WatchRow row)
	{
		// Reads go to the device only in the Run mode (outside Run the command drivers are not
		// subscribed, so there is nobody to serve the request).
		var variable = FindVariable(row.Reference);
		if (!row.CanReadNow || !IsRun || ReadVariableAsync == null || variable == null)
		{
			row.IsReadError = true;
			return;
		}

		row.IsReadBusy = true;
		try
		{
			// The value arrives through UpdateVariableValueAsync during the read; reset the
			// row throttle so it is shown even when the last refresh was a moment ago.
			row.LastUpdate = DateTime.MinValue;
			var read = await ReadVariableAsync(variable);
			row.IsReadError = !read;
		}
		catch
		{
			row.IsReadError = true;
		}
		finally
		{
			row.IsReadBusy = false;
		}
	}

	#endregion

	#region Derived properties

	public override string ControlName => "WatchTableControl";
	public override string Label => "Watch Table";
	public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/WatchTable.png");
	public override string Description => "Watch table - several variables at once (name, value, unit, time).";

	#endregion

	#region Variable binding

	// Table rows are single-value: scalar or string (matrices have their own control)
	public override bool CanBindVariable(IVariableBase variable) => variable is ScalarVariable or StringVariable;

	public override void BindVariable(IVariableBase protVariable)
	{
		if (!WatchSealValid()) return;

		if (!CanBindVariable(protVariable))
		{
			return;
		}

		RememberVariableBinding(protVariable);
		var reference = GetVariableReference(protVariable);
		if (Rows.Any(row => row.Reference == reference))
		{
			return;
		}

		if (!Variables.Any(v => v.Equals(protVariable)))
		{
			Variables.Add(protVariable);
		}

		var row = new WatchRow
		{
			Reference = reference,
			Name = protVariable.Label,
			Unit = protVariable is ScalarVariable scalar ? scalar.Values.ValPresentation.Unit : string.Empty,
			// Restore the persisted per-row write mode before the change handler is attached.
			IsWriteMode = WriteModeReferences.Contains(reference),
			WriteRequested = r => _ = WriteRowAsync(r),
			ReadRequested = r => _ = ReadRowAsync(r)
		};
		row.PropertyChanged += OnRowPropertyChanged;
		Rows.Add(row);

		// Writability and on-request readability must be re-evaluated on every (re)bind.
		RefreshRowWriteCapability(row);
		RefreshRowReadCapability(row);
		if (row.IsWriteActive)
		{
			PrefillEditValue(row);
		}
	}

	public override void RefreshVariableBinding(IVariableBase variable)
	{
		base.RefreshVariableBinding(variable);
		var row = Rows.FirstOrDefault(r => r.Reference == GetVariableReference(variable));
		if (row == null)
		{
			return;
		}

		row.Name = variable.Label;
		row.Unit = variable is ScalarVariable scalar ? scalar.Values.ValPresentation.Unit : string.Empty;
		RefreshRowReadCapability(row);
	}

	public override async Task UpdateVariableValueAsync(IVariableBase protVariable)
	{
		if (!(watchLiveSealOk ??= WatchSealValid())) return;

		var raw = protVariable switch
		{
			ScalarVariable scalar => scalar.GetPresentationText(),
			StringVariable str => str.Values,
			_ => null
		};

		if (raw == null) return;

		var row = Rows.FirstOrDefault(r => r.Reference == GetVariableReference(protVariable));
		if (row == null) return;

		// In write mode only this row's display freezes (communication keeps running,
		// other rows and controls showing the same variable update normally).
		if (row.IsWriteActive) return;

		if (protVariable.Timestamp < row.LastUpdate) row.LastUpdate = DateTime.MinValue;
		if ((protVariable.Timestamp - row.LastUpdate).TotalMilliseconds < RefreshTime) return;
		row.LastUpdate = protVariable.Timestamp;

		var timestamp = protVariable.Timestamp;
		_ = Application.Current.Dispatcher.BeginInvoke(() =>
		{
			row.Value = raw;
			row.Time = timestamp;
			row.IsReadError = false;
		});
	}

	private void RemoveSelected()
	{
		var row = SelectedRow;
		if (row == null) return;

		row.PropertyChanged -= OnRowPropertyChanged;
		WriteModeReferences.Remove(row.Reference);
		Rows.Remove(row);
		LinkedVariables.RemoveAll(reference => reference == row.Reference);
		var variable = Variables.FirstOrDefault(v => GetVariableReference(v) == row.Reference);
		if (variable != null)
		{
			Variables.Remove(variable);
		}

		RaiseVariableBindingsChanged();

		SelectedRow = null;
	}

	#endregion

    private bool? watchLiveSealOk;

    // LICENSE-SEAL COPY v1 — sync z _LicenseGuard/LicenseSealTemplate.cs
    // Nezavisly licencni check (necte licenseService). true=licencovano/nejasne (fail-open),
    // false=jiste nelicencovano. Reakce (nenastartovat) je v StartAsym.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool WatchSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open
        }
    }
}
