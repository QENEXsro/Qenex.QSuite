using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// One cell of the matrix table: an axis breakpoint or a data element. The top-left corner
/// of a map is a placeholder cell with no section binding. Runtime only — the grid is
/// rebuilt from the bound MatrixVariable, nothing here is persisted.
/// </summary>
public class MatrixCellViewModel : INotifyPropertyChanged
{
    private readonly MatrixControlViewModel owner;
    private bool suppressDirty;

    public MatrixCellViewModel(MatrixControlViewModel owner, MatrixSectionKind kind, int index, bool isPlaceholder = false)
    {
        this.owner = owner;
        Kind = kind;
        Index = index;
        IsPlaceholder = isPlaceholder;
    }

    public static MatrixCellViewModel Placeholder(MatrixControlViewModel owner)
    {
        return new MatrixCellViewModel(owner, MatrixSectionKind.Data, -1, isPlaceholder: true);
    }

    public MatrixSectionKind Kind { get; }
    public int Index { get; }
    public bool IsPlaceholder { get; }
    public bool IsAxis => !IsPlaceholder && Kind != MatrixSectionKind.Data;

    /// <summary>Read-mode display text (per-section presentation of the last value read from the device).</summary>
    public string Text { get; set { field = value; OnPropertyChanged(); } } = string.Empty;

    /// <summary>Write-mode edit text; user edits mark the cell dirty.</summary>
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
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
            owner.OnCellStateChanged();
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
        }
    }

    /// <summary>Enter in the edit box: immediate write when Write on Enter is ticked.</summary>
    public ICommand CommitCommand => field ??= new RelayCommand<object>(_ => owner.CommitCell(this));

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
