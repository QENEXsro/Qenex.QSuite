using System.Globalization;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// Table view of a MatrixVariable (value block / curve / map): X axis breakpoints as the column
/// header, Y axis breakpoints as the row header, data cells row-major. Read/write behaviour is
/// the same as in the Single-Signal and Watch Table controls: the table shows only values read
/// from the device (periodic event: every poll; On Request event: the Read button). Write mode
/// freezes the display and makes the cells editable: with Write on Enter ticked, Enter writes the
/// cell immediately; otherwise edits accumulate as dirty cells until the Write button sends them.
/// A written value shows up only once the device returns it (next poll / next Read).
/// </summary>
[DataContract]
public class MatrixControlViewModel : ControlBase, IMatrixVariableWriteControl, IVariableReadControl
{
    private DateTime previousUpdateTime = DateTime.MinValue;

    public MatrixControlViewModel()
    {
        Width = 320;
        Height = 180;
        VariableLabel = "----------";
    }

    #region Display properties

    [IgnoreDataMember]
    public string VariableLabel { get; set { field = value; OnPropertyChanged(); } }

    [IgnoreDataMember]
    public string DataUnit { get; set { field = value; OnPropertyChanged(); } } = string.Empty;

    /// <summary>Axis captions from the variable layout; data is captioned by the variable label.</summary>
    [IgnoreDataMember]
    public string XAxisLabel { get; set { field = value; OnPropertyChanged(); } } = string.Empty;

    [IgnoreDataMember]
    public string YAxisLabel { get; set { field = value; OnPropertyChanged(); } } = string.Empty;

    /// <summary>Rows of the table, first row is the X axis header when the matrix has one.</summary>
    [IgnoreDataMember]
    public IReadOnlyList<IReadOnlyList<MatrixCellViewModel>> Rows
    {
        get;
        private set { field = value; OnPropertyChanged(); }
    } = [];

    [IgnoreDataMember]
    private List<MatrixCellViewModel> allCells = [];

    [DataMember]
    public int RefreshTime
    {
        get;
        set
        {
            if (value < 0) value = 0;
            field = value; OnPropertyChanged();
        }
    } = 250;

    [DataMember]
    public int CellWidth
    {
        get;
        set
        {
            if (value < 20) value = 20;
            field = value; OnPropertyChanged();
        }
    } = 60;

    /// <summary>Write mode: Enter writes the edited cell immediately instead of leaving it pending
    /// for the Write button (same option as in the Single-Signal and Watch Table controls).</summary>
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

    // "Auto Read" (older .qproj files): a leftover of the time when Read meant "redraw from
    // memory"; removed 2026-09-25 (decision of Radek) - the table always follows the device.
    // Read from old projects and dropped, never serialized back.
    [DataMember(Name = "AutoRead", EmitDefaultValue = false)]
    private bool LegacyAutoRead { get => false; set { } }

    #endregion

    #region Write mode (IMatrixVariableWriteControl)

    [IgnoreDataMember]
    public Func<IVariableBase, bool>? CanWriteVariableProvider { get; set; }

    /// <summary>Scalar write delegate of the base contract; unused by this control.</summary>
    [IgnoreDataMember]
    public Func<IVariableBase, double, Task<bool>>? WriteVariableEngValueAsync { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, MatrixSectionKind, int, double, Task<bool>>? WriteMatrixElementEngValueAsync { get; set; }

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
                PrefillEditTexts();
            }
            else
            {
                // Leaving write mode discards pending edits. The display keeps the last values
                // read from the device (it was frozen meanwhile): a written value is shown only
                // once the device returns it - next poll, or next Read for an On Request matrix.
                ClearPendingEdits();
            }
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

    [IgnoreDataMember]
    public bool HasDirtyCells { get; private set { field = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanWriteDirty)); } }

    /// <summary>Some cell failed to write (red tint of the Write button, same as the cell).</summary>
    [IgnoreDataMember]
    public bool HasWriteErrorCells { get; private set { field = value; OnPropertyChanged(); } }

    /// <summary>Write button: shown in write mode when Enter does not write (Write on Enter off).</summary>
    [IgnoreDataMember]
    public bool IsWriteButtonVisible => IsWriteActive && !WriteOnEnter;

    /// <summary>Write button enablement: greys out until some cell is edited, greys back after the write.</summary>
    [IgnoreDataMember]
    public bool CanWriteDirty => IsWriteActive && HasDirtyCells;

    // Lazy kvuli deserializaci (DataContractSerializer nevola konstruktor)
    [IgnoreDataMember]
    public RelayCommand<object> WriteDirtyCommand => field ??= new RelayCommand<object>(_ => _ = WriteDirtyCellsAsync());

    public void RefreshWriteCapability()
    {
        var variable = Variables?.FirstOrDefault();
        CanWrite = variable != null && (CanWriteVariableProvider?.Invoke(variable) ?? false);
    }

    private void NotifyWriteStateChanged()
    {
        OnPropertyChanged(nameof(IsWriteActive));
        OnPropertyChanged(nameof(IsWriteButtonVisible));
        OnPropertyChanged(nameof(CanWriteDirty));
    }

    #endregion

    #region Read on request (IVariableReadControl)

    [IgnoreDataMember]
    public Func<IVariableBase, bool>? CanReadVariableProvider { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, Task<bool>>? ReadVariableAsync { get; set; }

    /// <summary>True only for a matrix bound to an On Request event (decided by the protocol
    /// through the host provider). The Read button asks the device for one read; for a
    /// periodically polled matrix it is not shown at all (the table follows every poll).</summary>
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

    /// <summary>Last on-request read failed (timeout, device error, not running): red tint of the
    /// Read button and of the value cells, cleared by the next successful read or bus update.</summary>
    [IgnoreDataMember]
    public bool IsReadError { get; private set { field = value; OnPropertyChanged(); } }

    // Lazy kvuli deserializaci (DataContractSerializer nevola konstruktor)
    [IgnoreDataMember]
    public RelayCommand<object> ReadCommand => field ??= new RelayCommand<object>(_ => _ = ReadFromDeviceAsync(), _ => CanReadNow);

    public void RefreshReadCapability()
    {
        var variable = Variables?.FirstOrDefault();
        CanRead = variable != null && (CanReadVariableProvider?.Invoke(variable) ?? false);
    }

    /// <summary>
    /// Read button: one read of the matrix from the device (On Request event). On success the
    /// table is re-rendered from the values just read, even in write mode (the display is frozen
    /// there): an explicit Read means the user wants the fresh data; pending edits are discarded.
    /// </summary>
    private async Task ReadFromDeviceAsync()
    {
        if (!CanReadNow || !IsRun || ReadVariableAsync == null ||
            Variables?.FirstOrDefault() is not MatrixVariable matrixVariable)
        {
            IsReadError = true;
            return;
        }

        IsReadBusy = true;
        try
        {
            var read = await ReadVariableAsync(matrixVariable);
            IsReadError = !read;
            if (read)
            {
                previousUpdateTime = DateTime.MinValue;
                _ = Application.Current.Dispatcher.BeginInvoke(RefreshFromVariable);
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

    internal void OnCellStateChanged()
    {
        HasDirtyCells = allCells.Any(c => c.IsDirty);
        HasWriteErrorCells = allCells.Any(c => c.IsWriteError);
    }

    internal void CommitCell(MatrixCellViewModel cell)
    {
        if (WriteOnEnter)
        {
            _ = WriteCellAsync(cell);
        }
        // Without Write on Enter the edit already marked the cell dirty; the Write button sends it.
    }

    private async Task WriteDirtyCellsAsync()
    {
        foreach (var cell in allCells.Where(c => c.IsDirty).ToList())
        {
            await WriteCellAsync(cell);
        }
    }

    private async Task<bool> WriteCellAsync(MatrixCellViewModel cell)
    {
        if (!IsWriteActive || !IsRun || WriteMatrixElementEngValueAsync == null ||
            Variables?.FirstOrDefault() is not MatrixVariable matrixVariable ||
            !TryParseEngValue(cell.EditText, out var engValue))
        {
            cell.IsWriteError = true;
            return false;
        }

        bool written;
        try
        {
            written = await WriteMatrixElementEngValueAsync(matrixVariable, cell.Kind, cell.Index, engValue);
        }
        catch
        {
            written = false;
        }

        if (written)
        {
            // The display text is NOT touched: it shows the last value read from the device and
            // the written one appears only once the device returns it (poll / Read).
            cell.IsWriteError = false;
            cell.IsDirty = false;
            cell.SetEditTextSilently(engValue.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            cell.IsWriteError = true;
        }

        return written;
    }

    private static bool TryParseEngValue(string text, out double engValue)
    {
        return double.TryParse((text ?? string.Empty).Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out engValue);
    }

    /// <summary>Sets the edit boxes to the variable's current values without marking them dirty.</summary>
    private void PrefillEditTexts()
    {
        if (Variables?.FirstOrDefault() is not MatrixVariable matrixVariable)
        {
            return;
        }

        foreach (var cell in allCells)
        {
            cell.SetEditTextSilently(matrixVariable.GetEngValue(cell.Kind, cell.Index).ToString(CultureInfo.InvariantCulture));
            cell.IsDirty = false;
            cell.IsWriteError = false;
        }
    }

    /// <summary>Discards pending edits and write errors; the display texts stay as read.</summary>
    private void ClearPendingEdits()
    {
        // allCells is null while the DataContractSerializer sets IsWriteMode (no constructor,
        // OnDeserialized runs later); nothing to clear before the grid exists.
        if (allCells == null)
        {
            return;
        }

        foreach (var cell in allCells)
        {
            cell.IsDirty = false;
            cell.IsWriteError = false;
        }
    }

    #endregion

    #region Derived properties

    public override string ControlName => "MatrixControl";
    public override string Label => "Matrix";
    public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/MatrixControl.png");
    public override string Description => "Table control for a matrix variable: value block, curve or calibration map with editable cells.";

    #endregion

    #region Public methods

    public override async Task UpdateVariableValueAsync(IVariableBase protVariable)
    {
        // V rezimu write se automaticky neprepisuje ZADNA bunka (rozhodnuti Radka 2026-07-17);
        // komunikace bezi dal, obnova dalsim pollem po opusteni rezimu nebo rucnim Read.
        if (IsWriteActive)
        {
            return;
        }

        if (protVariable is not MatrixVariable matrixVariable)
        {
            return;
        }

        if (protVariable.Timestamp < previousUpdateTime)
        {
            previousUpdateTime = DateTime.MinValue;
        }

        if ((protVariable.Timestamp - previousUpdateTime).TotalMilliseconds < RefreshTime)
        {
            return;
        }

        previousUpdateTime = protVariable.Timestamp;

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ApplyVariable(matrixVariable);
            IsReadError = false;
        });
    }

    // Tabulka zobrazuje vyhradne matrix promenne (skalary patri Signal/Gauge/WatchTable)
    public override bool CanBindVariable(IVariableBase variable) => variable is MatrixVariable;

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
        previousUpdateTime = DateTime.MinValue;

        ApplyHeader(protVariable);
        RebuildGrid();
        RefreshFromVariable();

        // Zapisovatelnost i citelnost na vyzadani se musi prehodnotit pri kazdem (re)bindu
        RefreshWriteCapability();
        RefreshReadCapability();
        IsReadError = false;
        if (IsWriteMode)
        {
            PrefillEditTexts();
        }
    }

    public override void RefreshVariableBinding(IVariableBase variable)
    {
        base.RefreshVariableBinding(variable);

        if (!IsVariableBound(variable))
        {
            return;
        }

        // Layout (sekce, typy) se mohl v konfiguraci zmenit — prestav tabulku.
        ApplyHeader(variable);
        RebuildGrid();
        RefreshFromVariable();
        RefreshReadCapability();
    }

    /// <summary>Edit -> Run: nothing has been read from the device yet, so the cells show
    /// nothing (the grid shape and axis captions stay) until the first poll / Read fills them
    /// (rule "truth is what comes from the device", Radek 2026-09-25). The write-mode edit
    /// boxes are emptied as well.</summary>
    protected override void OnEditToRun()
    {
        previousUpdateTime = DateTime.MinValue;
        IsReadError = false;
        foreach (var cell in allCells)
        {
            cell.Text = string.Empty;
            cell.SetEditTextSilently(string.Empty);
            cell.IsDirty = false;
            cell.IsWriteError = false;
        }
    }

    #endregion

    #region Grid building & refresh

    private void ApplyHeader(IVariableBase variable)
    {
        VariableLabel = variable.Label;
        var matrixVariable = variable as MatrixVariable;
        DataUnit = matrixVariable?.Data.Presentation?.Unit ?? string.Empty;
        XAxisLabel = matrixVariable?.XAxis?.Label ?? string.Empty;
        YAxisLabel = matrixVariable?.YAxis?.Label ?? string.Empty;
    }

    private void RebuildGrid()
    {
        allCells = [];
        var rows = new List<IReadOnlyList<MatrixCellViewModel>>();

        if (Variables?.FirstOrDefault() is MatrixVariable matrixVariable && matrixVariable.ValidateLayout() == null)
        {
            var hasX = matrixVariable.XAxis != null;
            var hasY = matrixVariable.YAxis != null;
            var xCount = matrixVariable.XCount;

            if (hasX)
            {
                var headerRow = new List<MatrixCellViewModel>();
                if (hasY)
                {
                    headerRow.Add(MatrixCellViewModel.Placeholder(this));
                }

                for (var x = 0; x < xCount; x++)
                {
                    headerRow.Add(new MatrixCellViewModel(this, MatrixSectionKind.XAxis, x));
                }

                rows.Add(headerRow);
            }

            if (hasY)
            {
                for (var y = 0; y < matrixVariable.YCount; y++)
                {
                    var row = new List<MatrixCellViewModel> { new(this, MatrixSectionKind.YAxis, y) };
                    for (var x = 0; x < xCount; x++)
                    {
                        row.Add(new MatrixCellViewModel(this, MatrixSectionKind.Data, y * xCount + x));
                    }

                    rows.Add(row);
                }
            }
            else
            {
                var row = new List<MatrixCellViewModel>();
                for (var i = 0; i < matrixVariable.DataCount; i++)
                {
                    row.Add(new MatrixCellViewModel(this, MatrixSectionKind.Data, i));
                }

                rows.Add(row);
            }

            allCells = rows.SelectMany(r => r).Where(c => !c.IsPlaceholder).ToList();
        }

        Rows = rows;
        HasDirtyCells = false;
        HasWriteErrorCells = false;
    }

    /// <summary>Re-renders all cells from the bound variable; clears pending edits and errors.</summary>
    private void RefreshFromVariable()
    {
        if (Variables?.FirstOrDefault() is not MatrixVariable matrixVariable)
        {
            return;
        }

        if (!IsGridShapeCurrent(matrixVariable))
        {
            RebuildGrid();
        }

        ApplyVariable(matrixVariable);

        foreach (var cell in allCells)
        {
            cell.SetEditTextSilently(matrixVariable.GetEngValue(cell.Kind, cell.Index).ToString(CultureInfo.InvariantCulture));
            cell.IsDirty = false;
            cell.IsWriteError = false;
        }
    }

    /// <summary>Applies the values of a (snapshot) variable to the display texts.</summary>
    private void ApplyVariable(MatrixVariable matrixVariable)
    {
        if (!IsGridShapeCurrent(matrixVariable))
        {
            RebuildGrid();
        }

        foreach (var cell in allCells)
        {
            cell.Text = matrixVariable.GetPresentationText(cell.Kind, cell.Index);
        }
    }

    private bool IsGridShapeCurrent(MatrixVariable matrixVariable)
    {
        var expectedCells = matrixVariable.ValidateLayout() == null
            ? matrixVariable.XCount + matrixVariable.YCount + matrixVariable.DataCount
            : 0;
        return allCells.Count == expectedCells && expectedCells > 0
            ? allCells.Count(c => c.Kind == MatrixSectionKind.XAxis) == matrixVariable.XCount &&
              allCells.Count(c => c.Kind == MatrixSectionKind.YAxis) == matrixVariable.YCount
            : expectedCells == 0 && allCells.Count == 0;
    }

    #endregion

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        VariableLabel ??= "----------";
        DataUnit ??= string.Empty;
        XAxisLabel ??= string.Empty;
        YAxisLabel ??= string.Empty;
        Variables ??= [];
        LinkedVariables ??= [];
        Rows ??= [];
        allCells ??= [];
    }
}
