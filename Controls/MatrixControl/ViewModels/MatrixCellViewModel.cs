using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// One cell of the matrix table: an axis breakpoint or a data element. The top-left corner
/// of a map is a placeholder cell with no section binding. Runtime only — the grid is
/// rebuilt from the bound MatrixVariable, nothing here is persisted. The grid (RadGridView)
/// binds the cell object itself (row.Cells[column]) and shows DisplayText / Background; the
/// cell therefore never touches UI elements, it only publishes its state.
/// </summary>
public class MatrixCellViewModel : INotifyPropertyChanged
{
    private readonly MatrixControlViewModel owner;
    private bool suppressDirty;

    public MatrixCellViewModel(MatrixControlViewModel owner, MatrixSectionKind kind, int index, int row, int column,
        bool isPlaceholder = false)
    {
        this.owner = owner;
        Kind = kind;
        Index = index;
        Row = row;
        Column = column;
        IsPlaceholder = isPlaceholder;
    }

    public static MatrixCellViewModel Placeholder(MatrixControlViewModel owner, int row, int column)
    {
        return new MatrixCellViewModel(owner, MatrixSectionKind.Data, -1, row, column, isPlaceholder: true);
    }

    public MatrixSectionKind Kind { get; }
    public int Index { get; }
    public bool IsPlaceholder { get; }
    public bool IsAxis => !IsPlaceholder && Kind != MatrixSectionKind.Data;

    /// <summary>Cell of the X axis row (breakpoint or the map corner): the view draws the
    /// separator line towards the data under it.</summary>
    public bool IsXAxisRow => IsPlaceholder || Kind == MatrixSectionKind.XAxis;

    /// <summary>Cell of the Y axis column (breakpoint or the map corner): the view draws the
    /// separator line towards the data at its right edge.</summary>
    public bool IsYAxisColumn => IsPlaceholder || Kind == MatrixSectionKind.YAxis;

    /// <summary>Position in the table (row 0 = X axis when present, column 0 = Y axis when present).</summary>
    public int Row { get; }
    public int Column { get; }

    /// <summary>Read-mode display text (per-section presentation of the last value read from the device).</summary>
    public string Text
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (!owner.IsWriteActive)
            {
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    } = string.Empty;

    /// <summary>Write-mode edit text; user edits (typing, paste) mark the cell dirty.</summary>
    public string EditText
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
            if (owner.IsWriteActive)
            {
                OnPropertyChanged(nameof(DisplayText));
            }

            if (!suppressDirty)
            {
                IsDirty = true;
                IsWriteError = false;
            }
        }
    } = string.Empty;

    /// <summary>What the table shows: the edit text in write mode (the display is frozen there),
    /// otherwise the last value read from the device. Also the clipboard text of the cell.</summary>
    public string DisplayText => owner.IsWriteActive ? EditText : Text;

    /// <summary>Engineering value of a data cell for the colour scale (null = not read yet / axis).</summary>
    public double? HeatValue { get; internal set; }

    /// <summary>Cell background decided by the owner: write error > dirty > read error > write mode
    /// > colour scale > none (theme). Frozen shared brushes, see <see cref="MatrixCellBrushes"/>.</summary>
    public Brush Background
    {
        get;
        private set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    } = MatrixCellBrushes.None;

    /// <summary>Edited value not written yet (yellow tint, enables the Write button).</summary>
    public bool IsDirty
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
            owner.OnCellStateChanged();
            RefreshBackground();
        }
    }

    /// <summary>Last write of this cell failed (red tint of the cell and of the Write button).</summary>
    public bool IsWriteError
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
            owner.OnCellStateChanged();
            RefreshBackground();
        }
    }

    /// <summary>Enter in the edit box: immediate write when Write on Enter is ticked.</summary>
    public ICommand CommitCommand => field ??= new RelayCommand<object>(_ => owner.CommitCell(this));

    /// <summary>Pasted text: becomes the edit text and the cell is marked dirty even when the
    /// value did not change (the pasted block is highlighted like edited cells, Radek 2026-09-26).</summary>
    public void SetPastedText(string text)
    {
        SetEditTextSilently(text);
        IsWriteError = false;
        IsDirty = true;
    }

    /// <summary>Sets the edit text without marking the cell dirty (prefill/refresh).</summary>
    public void SetEditTextSilently(string text)
    {
        suppressDirty = true;
        try
        {
            EditText = text;
        }
        finally
        {
            suppressDirty = false;
        }
    }

    internal void RefreshBackground()
    {
        Background = owner.GetCellBackground(this);
    }

    /// <summary>Owner switched write mode on/off: the shown text and the background change.</summary>
    internal void NotifyWriteModeChanged()
    {
        OnPropertyChanged(nameof(DisplayText));
        RefreshBackground();
    }

    /// <summary>Clipboard text of the cell (RadGridView copies the bound cell object).</summary>
    public override string ToString() => DisplayText;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
