using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Media;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// One tab of the Matrix control = one MatrixVariable (value block / curve / map) with its own
/// table, settings and edit state. Read/write behaviour is the same as in the Single-Signal and
/// Watch Table controls: the table shows only values read from the device (periodic event: every
/// poll; On Request event: the Read button). Write mode freezes the display and makes the cells
/// editable: with Write on Enter ticked (control setting), Enter writes the cell immediately;
/// otherwise edits accumulate as dirty cells until the Write button sends them in one batch.
/// A written value shows up only once the device returns it (next poll / next Read).
/// The table is a virtualised RadGridView (rows = <see cref="Rows"/>, one column per table
/// column bound to Cells[i]). Clipboard: copy is native (cell ToString = shown text), paste comes
/// in as <see cref="MatrixPasteRequest"/>. Optional colour scale (min/max/spectrum) tints the data
/// cells by their engineering value. Persisted: the variable reference and the settings; the
/// grid is rebuilt when the host binds the variable (Radek 2026-09-27: tabs per map).
/// </summary>
[DataContract]
public class MatrixPageViewModel : INotifyPropertyChanged
{
    private DateTime previousUpdateTime = DateTime.MinValue;

    [IgnoreDataMember]
    private List<MatrixCellViewModel> allCells = [];

    /// <summary>The control this page belongs to (host delegates, Write on Enter, IsRun, Logger).</summary>
    [IgnoreDataMember]
    internal MatrixControlViewModel? Owner { get; set; }

    /// <summary>"id|namespace|name" of the bound variable (ControlBase.GetVariableReference).</summary>
    [DataMember]
    public string VariableReference { get; set; } = string.Empty;

    [IgnoreDataMember]
    public MatrixVariable? Variable { get; private set; }

    #region Tab header

    /// <summary>Tab title: the variable label (the reference name until the variable is bound).</summary>
    [IgnoreDataMember]
    public string Title
    {
        get
        {
            if (!string.IsNullOrEmpty(Variable?.Label))
            {
                return Variable.Label;
            }

            var parts = (VariableReference ?? string.Empty).Split('|');
            return parts.Length > 0 ? parts[^1] : string.Empty;
        }
    }

    /// <summary>Edited cells not written to the device yet - shown in the tab title so a hidden
    /// tab with pending edits is not forgotten (Radek 2026-09-27).</summary>
    [IgnoreDataMember]
    public bool IsModified => HasDirtyCells;

    #endregion

    #region Display properties

    [IgnoreDataMember]
    public string VariableLabel { get; set { field = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } } = "----------";

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

    /// <summary>The Y axis column is frozen: it stays visible while the map is scrolled horizontally.</summary>
    [IgnoreDataMember]
    public int FrozenColumnCount => Rows.Count > 0 && Rows[0].Count > 0 && (Rows[0][0].Kind == MatrixSectionKind.YAxis || Rows[0][0].IsPlaceholder) ? 1 : 0;

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

    /// <summary>Write mode: Enter writes the edited cell immediately (control-level setting).</summary>
    [IgnoreDataMember]
    public bool WriteOnEnter => Owner?.WriteOnEnter ?? false;

    internal void NotifyWriteOnEnterChanged() => OnPropertyChanged(nameof(IsWriteButtonVisible));

    [IgnoreDataMember]
    private bool IsRun => Owner?.IsRun ?? false;

    [IgnoreDataMember]
    private ILogger? Logger => Owner?.Logger;

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

    #region Write mode

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
    public bool HasDirtyCells
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanWriteDirty));
            OnPropertyChanged(nameof(IsModified));
        }
    }

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
        CanWrite = Variable != null && (Owner?.CanWriteVariableProvider?.Invoke(Variable) ?? false);
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

    #region Read on request

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
        CanRead = Variable != null && (Owner?.CanReadVariableProvider?.Invoke(Variable) ?? false);
    }

    /// <summary>
    /// Read button: one read of the matrix from the device (On Request event). On success the
    /// table is re-rendered from the values just read, even in write mode (the display is frozen
    /// there): an explicit Read means the user wants the fresh data; pending edits are discarded.
    /// </summary>
    private async Task ReadFromDeviceAsync()
    {
        var read = Owner?.ReadVariableAsync;
        if (!CanReadNow || !IsRun || read == null || Variable is not { } matrixVariable)
        {
            IsReadError = true;
            return;
        }

        IsReadBusy = true;
        try
        {
            var ok = await read(matrixVariable);
            IsReadError = !ok;
            if (ok)
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

    #endregion

    #region Cell edits, writes, clipboard

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

    /// <summary>
    /// Write button: all dirty cells in ONE batch (the protocol merges neighbouring cells into
    /// windows - a pasted row is one transfer, not one per cell). Cells whose text is not a
    /// number are marked as errors and left out. When the batch fails, every cell of it stays
    /// dirty and is marked red so Write can be repeated. Without a batch delegate (older host)
    /// the cells are written one by one.
    /// </summary>
    private async Task WriteDirtyCellsAsync()
    {
        var dirty = allCells.Where(c => c.IsDirty).ToList();
        if (dirty.Count == 0 || !IsWriteActive || !IsRun)
        {
            return;
        }

        var writeBatch = Owner?.WriteMatrixElementsEngValueAsync;
        if (writeBatch == null || Variable is not { } matrixVariable)
        {
            foreach (var cell in dirty)
            {
                // Leaving write mode (or the runtime) while a long write runs cancels the rest
                // silently: the pending edits were discarded, they are no errors.
                if (!IsWriteActive || !IsRun)
                {
                    return;
                }

                await WriteCellAsync(cell);
            }

            return;
        }

        var batch = new List<(MatrixCellViewModel cell, double engValue)>();
        foreach (var cell in dirty)
        {
            if (TryParseEngValue(cell.EditText, out var engValue))
            {
                batch.Add((cell, engValue));
            }
            else
            {
                cell.IsWriteError = true;
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        bool written;
        try
        {
            written = await writeBatch(matrixVariable,
                batch.Select(b => new MatrixElementWrite(b.cell.Kind, b.cell.Index, b.engValue)).ToList());
        }
        catch
        {
            written = false;
        }

        foreach (var (cell, engValue) in batch)
        {
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
        }
    }

    private async Task<bool> WriteCellAsync(MatrixCellViewModel cell)
    {
        if (!IsWriteActive || !IsRun)
        {
            return false;
        }

        var write = Owner?.WriteMatrixElementEngValueAsync;
        if (write == null || Variable is not { } matrixVariable || !TryParseEngValue(cell.EditText, out var engValue))
        {
            cell.IsWriteError = true;
            return false;
        }

        bool written;
        try
        {
            written = await write(matrixVariable, cell.Kind, cell.Index, engValue);
        }
        catch
        {
            written = false;
        }

        if (written)
        {
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
            if (field.Length > 0)
            {
                // Same message into the application log (host-injected ILogger, Calibro pattern)
                Logger?.Log(LogLevel.Warn, $"Matrix '{VariableLabel}': {field}");
            }
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
        if (Variable is not { } matrixVariable || allCells == null)
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

    #region Binding and updates (called by the control)

    /// <summary>Host bound (or re-bound) the variable of this page: header, empty grid, capabilities.</summary>
    internal void Attach(MatrixVariable variable)
    {
        Variable = variable;
        previousUpdateTime = DateTime.MinValue;
        ApplyHeader();
        RebuildGrid();
        // Nothing from the variable memory (zeros after a project load): the cells are filled
        // only by data from the device (poll / Read), rule of Radek 2026-09-25/26.
        ClearCells();
        RefreshWriteCapability();
        RefreshReadCapability();
        IsReadError = false;
        if (IsWriteMode)
        {
            PrefillEditTexts();
        }
    }

    /// <summary>Configuration of the variable changed (sections, types): rebuild the (empty) table.</summary>
    internal void RefreshBinding()
    {
        ApplyHeader();
        RebuildGrid();
        ClearCells();
        RefreshReadCapability();
    }

    /// <summary>Value update from the bus (poll / read / DAQ) - same rules as before the tabs.</summary>
    internal void Update(MatrixVariable protVariable)
    {
        // V rezimu write se automaticky neprepisuje ZADNA bunka (rozhodnuti Radka 2026-07-17);
        // komunikace bezi dal, obnova dalsim pollem po opusteni rezimu nebo rucnim Read.
        if (IsWriteActive)
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
            ApplyVariable(protVariable);
            IsReadError = false;
        });
    }

    /// <summary>Edit -> Run: nothing has been read from the device yet, so the cells show nothing
    /// until the first poll / Read; a runtime start also ends write mode (its pending edits are
    /// gone with the cleared cells, and a table left in write mode would ignore the polls).</summary>
    internal void OnRunStart()
    {
        ClearCells();
        if (IsWriteMode)
        {
            IsWriteMode = false;
        }
    }

    /// <summary>Empties all cells (display, edit boxes, colour scale, flags); the grid shape and
    /// the axis captions stay.</summary>
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

    private void ApplyHeader()
    {
        VariableLabel = Variable?.Label ?? "----------";
        DataUnit = Variable?.Data.Presentation?.Unit ?? string.Empty;
        XAxisLabel = Variable?.XAxis?.Label ?? string.Empty;
        YAxisLabel = Variable?.YAxis?.Label ?? string.Empty;
    }

    private void RebuildGrid()
    {
        allCells = [];
        var rows = new List<MatrixRowViewModel>();

        if (Variable is { } matrixVariable && matrixVariable.ValidateLayout() == null)
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
        if (Variable is not { } matrixVariable)
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
        VariableReference ??= string.Empty;
        Rows ??= [];
        allCells ??= [];
        if (HeatMaximum == 0 && HeatMinimum == 0)
        {
            HeatMaximum = 100;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
