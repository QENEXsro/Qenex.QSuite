using System.Globalization;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// Table view of a MatrixVariable (value block / curve / map): X axis breakpoints as the first
/// row, Y axis breakpoints as the first column, data cells row-major. Read/write behaviour is
/// the same as in the Single-Signal and Watch Table controls: the table shows only values read
/// from the device (periodic event: every poll; On Request event: the Read button). Write mode
/// freezes the display and makes the cells editable: with Write on Enter ticked, Enter writes the
/// cell immediately; otherwise edits accumulate as dirty cells until the Write button sends them.
/// A written value shows up only once the device returns it (next poll / next Read).
/// The view is a virtualised RadGridView (rows = <see cref="Rows"/>, one column per table
/// column bound to Cells[i]), so a 64x64 map costs no more UI than the visible cells. Clipboard:
/// copy is native (cell ToString = shown text), paste comes in as <see cref="MatrixPasteRequest"/>.
/// Optional colour scale (min/max/spectrum) tints the data cells by their engineering value.
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
    public IReadOnlyList<MatrixRowViewModel> Rows
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColumnCount));
            OnPropertyChanged(nameof(FrozenColumnCount));
        }
    } = [];

    /// <summary>Number of table columns (Y axis column + X count, or the data count of a value block).</summary>
    [IgnoreDataMember]
    public int ColumnCount => Rows.Count > 0 ? Rows[0].Count : 0;

    /// <summary>The Y axis column is frozen: the grid draws its splitter as the separator from the
    /// data and the axis stays visible while the map is scrolled horizontally.</summary>
    [IgnoreDataMember]
    public int FrozenColumnCount => Rows.Count > 0 && Rows[0].Count > 0 && Rows[0][0].Kind == MatrixSectionKind.YAxis ||
                                    Rows.Count > 0 && Rows[0].Count > 0 && Rows[0][0].IsPlaceholder ? 1 : 0;

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

    #region Colour scale (heat map)

    /// <summary>Tint the data cells by value between <see cref="HeatMinimum"/> and <see cref="HeatMaximum"/>.</summary>
    [DataMember]
    public bool IsHeatmapEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            RefreshAllBackgrounds();
        }
    }

    [DataMember]
    public double HeatMinimum
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            RefreshAllBackgrounds();
        }
    }

    [DataMember]
    public double HeatMaximum
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            RefreshAllBackgrounds();
        }
    } = 100;

    [DataMember]
    public MatrixSpectrum HeatSpectrum
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            RefreshAllBackgrounds();
        }
    }

    // Static: the DataContractSerializer does not run the constructor / field initialisers, so an
    // instance initialiser would leave the combo box of a loaded project without items.
    private static readonly MatrixSpectrum[] AllSpectrums = Enum.GetValues<MatrixSpectrum>();

    [IgnoreDataMember]
    public IReadOnlyList<MatrixSpectrum> Spectrums => AllSpectrums;

    /// <summary>
    /// Background of a cell: write error > dirty > failed read > write mode > colour scale > none.
    /// One place for the whole table so the view only binds the resulting brush.
    /// </summary>
    internal Brush GetCellBackground(MatrixCellViewModel cell)
    {
        if (cell.IsPlaceholder)
        {
            return MatrixCellBrushes.None;
        }

        if (cell.IsWriteError)
        {
            return MatrixCellBrushes.Error;
        }

        if (cell.IsDirty)
        {
            return MatrixCellBrushes.Dirty;
        }

        if (IsWriteActive)
        {
            return MatrixCellBrushes.WriteMode;
        }

        if (IsReadError)
        {
            return MatrixCellBrushes.Error;
        }

        if (IsHeatmapEnabled && cell.Kind == MatrixSectionKind.Data && cell.HeatValue is { } value)
        {
            var span = HeatMaximum - HeatMinimum;
            return MatrixCellBrushes.Heat(HeatSpectrum, span > 0 ? (value - HeatMinimum) / span : 0.5);
        }

        return MatrixCellBrushes.None;
    }

    private void RefreshAllBackgrounds()
    {
        // allCells is null while the DataContractSerializer sets the members (no constructor).
        if (allCells == null)
        {
            return;
        }

        foreach (var cell in allCells)
        {
            cell.RefreshBackground();
        }
    }

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
                PasteMessage = string.Empty;
            }

            NotifyWriteStateChanged();
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

    /// <summary>Clipboard paste (Ctrl+V / Shift+Insert in the grid): parameter <see cref="MatrixPasteRequest"/>.</summary>
    [IgnoreDataMember]
    public RelayCommand<object> PasteCommand => field ??= new RelayCommand<object>(p =>
    {
        if (p is MatrixPasteRequest request)
        {
            Paste(request);
        }
    });

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

        // The table shows the edit texts in write mode and the read values otherwise; the
        // backgrounds switch to the orange write tint (or back to the colour scale).
        if (allCells == null)
        {
            return;
        }

        foreach (var cell in allCells)
        {
            cell.NotifyWriteModeChanged();
        }
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
    public bool IsReadError
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
            RefreshAllBackgrounds();
        }
    }

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

    /// <summary>Why the last paste was refused (shown red in the header); empty after a successful
    /// paste or when write mode is left.</summary>
    [IgnoreDataMember]
    public string PasteMessage
    {
        get;
        private set
        {
            field = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPasteMessage));
        }
    } = string.Empty;

    [IgnoreDataMember]
    public bool HasPasteMessage => !string.IsNullOrEmpty(PasteMessage);

    /// <summary>
    /// Paste from the clipboard (Excel format: cells separated by tabs, rows by line breaks)
    /// starting at the top-left cell of the selection; only in write mode. Like the Calibro
    /// ParamControl: the block must fit into the table from that cell, otherwise nothing is
    /// pasted and the reason is shown in the header. Every pasted cell becomes dirty (yellow)
    /// like an edited one, even when its value did not change; the Write button / Enter sends
    /// them. A single value pasted into a multi-cell selection fills the selection; the map
    /// corner is skipped; empty clipboard cells leave the cell untouched.
    /// </summary>
    internal void Paste(MatrixPasteRequest request)
    {
        if (!IsWriteActive)
        {
            return;
        }

        if (string.IsNullOrEmpty(request.Text) || request.Row < 0 || request.Column < 0 || request.Row >= Rows.Count)
        {
            PasteMessage = "Paste: nothing to paste here.";
            return;
        }

        var lines = request.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            PasteMessage = "Paste: the clipboard is empty.";
            return;
        }

        var block = lines.Select(line => line.Split('\t')).ToList();
        var single = block.Count == 1 && block[0].Length == 1;
        var rowCount = single ? Math.Max(1, request.RowCount) : block.Count;
        var columnCount = single ? Math.Max(1, request.ColumnCount) : block.Max(r => r.Length);
        var tableColumns = Rows[request.Row].Count;

        if (request.Row + rowCount > Rows.Count || request.Column + columnCount > tableColumns)
        {
            PasteMessage = $"Paste refused: {rowCount} x {columnCount} cells do not fit at row {request.Row + 1}, column {request.Column + 1} " +
                           $"(table {Rows.Count} x {tableColumns}).";
            return;
        }

        for (var i = 0; i < rowCount; i++)
        {
            var row = Rows[request.Row + i];
            var values = single ? block[0] : block[i];
            for (var j = 0; j < columnCount; j++)
            {
                var text = (single ? values[0] : j < values.Length ? values[j] : string.Empty).Trim();
                var cell = row[request.Column + j];
                if (text.Length == 0 || cell.IsPlaceholder)
                {
                    continue;
                }

                cell.SetPastedText(text);
            }
        }

        PasteMessage = string.Empty;
    }

    /// <summary>Clipboard text of a rectangular block of cells (tabs between cells, line breaks
    /// between rows - the format Excel pastes); the map corner is an empty cell.</summary>
    public string CopyText(int top, int left, int rowCount, int columnCount)
    {
        var lines = new List<string>();
        for (var i = 0; i < rowCount; i++)
        {
            var rowIndex = top + i;
            if (rowIndex < 0 || rowIndex >= Rows.Count)
            {
                continue;
            }

            var row = Rows[rowIndex];
            var values = new List<string>();
            for (var j = 0; j < columnCount; j++)
            {
                var columnIndex = left + j;
                values.Add(columnIndex >= 0 && columnIndex < row.Count && !row[columnIndex].IsPlaceholder
                    ? row[columnIndex].DisplayText
                    : string.Empty);
            }

            lines.Add(string.Join("\t", values));
        }

        return string.Join("\r\n", lines) + (lines.Count > 0 ? "\r\n" : string.Empty);
    }

    /// <summary>Sets the edit boxes to the variable's current values without marking them dirty.</summary>
    private void PrefillEditTexts()
    {
        if (Variables?.FirstOrDefault() is not MatrixVariable matrixVariable || allCells == null)
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
        // Nothing from the variable memory (zeros after a project load): the cells are filled
        // only by data from the device (poll / Read), rule of Radek 2026-09-25/26.
        ClearCells();

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

        // Layout (sekce, typy) se mohl v konfiguraci zmenit — prestav tabulku (prazdnou, viz BindVariable).
        ApplyHeader(variable);
        RebuildGrid();
        ClearCells();
        RefreshReadCapability();
    }

    /// <summary>Edit -> Run: nothing has been read from the device yet, so the cells show
    /// nothing (the grid shape and axis captions stay) until the first poll / Read fills them
    /// (rule "truth is what comes from the device", Radek 2026-09-25). The write-mode edit
    /// boxes are emptied as well.</summary>
    protected override void OnEditToRun()
    {
        ClearCells();
    }

    /// <summary>Empties all cells (display, edit boxes, colour scale, flags); the grid shape and
    /// the axis captions stay. Used on bind, configuration change and Edit -> Run.</summary>
    private void ClearCells()
    {
        previousUpdateTime = DateTime.MinValue;
        IsReadError = false;
        foreach (var cell in allCells)
        {
            cell.Text = string.Empty;
            cell.SetEditTextSilently(string.Empty);
            cell.HeatValue = null;
            cell.IsDirty = false;
            cell.IsWriteError = false;
            cell.RefreshBackground();
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
        var rows = new List<MatrixRowViewModel>();

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
                    headerRow.Add(MatrixCellViewModel.Placeholder(this, rows.Count, 0));
                }

                for (var x = 0; x < xCount; x++)
                {
                    headerRow.Add(new MatrixCellViewModel(this, MatrixSectionKind.XAxis, x, rows.Count, headerRow.Count));
                }

                rows.Add(new MatrixRowViewModel(rows.Count, headerRow, isAxisRow: true));
            }

            if (hasY)
            {
                for (var y = 0; y < matrixVariable.YCount; y++)
                {
                    var row = new List<MatrixCellViewModel> { new(this, MatrixSectionKind.YAxis, y, rows.Count, 0) };
                    for (var x = 0; x < xCount; x++)
                    {
                        row.Add(new MatrixCellViewModel(this, MatrixSectionKind.Data, y * xCount + x, rows.Count, row.Count));
                    }

                    rows.Add(new MatrixRowViewModel(rows.Count, row, isAxisRow: false));
                }
            }
            else
            {
                var row = new List<MatrixCellViewModel>();
                for (var i = 0; i < matrixVariable.DataCount; i++)
                {
                    row.Add(new MatrixCellViewModel(this, MatrixSectionKind.Data, i, rows.Count, i));
                }

                rows.Add(new MatrixRowViewModel(rows.Count, row, isAxisRow: false));
            }

            allCells = rows.SelectMany(r => r.Cells).Where(c => !c.IsPlaceholder).ToList();
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

    /// <summary>Applies the values of a (snapshot) variable to the display texts and the colour scale.</summary>
    private void ApplyVariable(MatrixVariable matrixVariable)
    {
        if (!IsGridShapeCurrent(matrixVariable))
        {
            RebuildGrid();
        }

        foreach (var cell in allCells)
        {
            cell.Text = matrixVariable.GetPresentationText(cell.Kind, cell.Index);
            if (cell.Kind == MatrixSectionKind.Data)
            {
                cell.HeatValue = matrixVariable.GetEngValue(cell.Kind, cell.Index);
            }

            if (IsHeatmapEnabled)
            {
                cell.RefreshBackground();
            }
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
        if (HeatMaximum == 0 && HeatMinimum == 0)
        {
            // Project saved before the colour scale existed: keep the defaults of a new control.
            HeatMaximum = 100;
        }
    }
}
