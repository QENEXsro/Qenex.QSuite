using System.Collections;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// One row of the matrix table for the RadGridView: the X axis header row (when the matrix has
/// an X axis) or a Y axis breakpoint followed by its data cells. The grid columns bind
/// Cells[column]; the row is also enumerable so tests can walk the cells.
/// </summary>
public class MatrixRowViewModel : IReadOnlyList<MatrixCellViewModel>
{
    public MatrixRowViewModel(int rowIndex, List<MatrixCellViewModel> cells, bool isAxisRow)
    {
        RowIndex = rowIndex;
        Cells = cells;
        IsAxisRow = isAxisRow;
    }

    public int RowIndex { get; }

    /// <summary>Cells by column; bound by the grid columns as Cells[i].</summary>
    public List<MatrixCellViewModel> Cells { get; }

    /// <summary>True for the X axis row (separator line below it).</summary>
    public bool IsAxisRow { get; }

    public MatrixCellViewModel this[int index] => Cells[index];
    public int Count => Cells.Count;
    public IEnumerator<MatrixCellViewModel> GetEnumerator() => Cells.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
