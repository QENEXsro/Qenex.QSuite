using System.Windows;
using System.Windows.Input;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Controls.MatrixControl.ViewModels;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.GridView;

namespace Qenex.QSuite.Controls.MatrixControl.Behaviors;

/// <summary>
/// Keyboard behaviour of the matrix grid (view-layer only; the decisions live in the view model):
/// - Tab / Shift+Tab while editing: commit the cell and open the next / previous cell for editing
///   (Excel-like walk through the table);
/// - Enter while editing: commit the cell and run its CommitCommand (Write on Enter writes it,
///   otherwise it stays pending for the Write button);
/// Copy / paste are NOT keys here: RadGridView runs its own clipboard commands before any
/// keyboard provider; MatrixGridBehavior hooks the grid events (copy = shown text of the cell,
/// paste = cancelled and handed to <see cref="PasteFromClipboard"/>).
/// Everything else is the default RadGridView behaviour (arrows, F2, Ctrl+C, ...).
/// </summary>
public class MatrixKeyboardCommandProvider : DefaultKeyboardCommandProvider
{
    private readonly RadGridView grid;

    public MatrixKeyboardCommandProvider(RadGridView grid) : base(grid)
    {
        this.grid = grid;
    }

    public override IEnumerable<ICommand> ProvideCommandsForKey(Key key)
    {
        var editing = grid.CurrentCell?.IsInEditMode == true;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Tab && editing)
        {
            return
            [
                RadGridViewCommands.CommitEdit,
                modifiers.HasFlag(ModifierKeys.Shift) ? RadGridViewCommands.MovePrevious : RadGridViewCommands.MoveNext,
                RadGridViewCommands.BeginEdit
            ];
        }

        if (key == Key.Enter && editing)
        {
            return [RadGridViewCommands.CommitEdit, new RelayCommand<object>(_ => CommitCurrentCell())];
        }

        return base.ProvideCommandsForKey(key);
    }

    private void CommitCurrentCell()
    {
        if (grid.CurrentCellInfo is { Item: MatrixRowViewModel row, Column: { } column } &&
            column.DisplayIndex >= 0 && column.DisplayIndex < row.Count)
        {
            row[column.DisplayIndex].CommitCommand.Execute(null);
        }
    }

    /// <summary>Paste (RadGridView Pasting event, cancelled by the behavior): the clipboard text
    /// goes to the view model with the selected rectangle - fit check and dirty cells happen there.</summary>
    public static void PasteFromClipboard(RadGridView grid)
    {
        if (grid.DataContext is not MatrixPageViewModel viewModel || !Clipboard.ContainsText() ||
            !TrySelectionRectangle(grid, out var top, out var left, out var bottom, out var right))
        {
            return;
        }

        viewModel.PasteCommand.Execute(new MatrixPasteRequest(top, left, bottom - top + 1, right - left + 1, Clipboard.GetText()));
    }

    /// <summary>Rectangle of the selected cells (or the current cell) in table coordinates.</summary>
    private static bool TrySelectionRectangle(RadGridView grid, out int top, out int left, out int bottom, out int right)
    {
        top = left = bottom = right = -1;
        var selected = grid.SelectedCells
            .Where(c => c.Item is MatrixRowViewModel && c.Column != null)
            .Select(c => (row: ((MatrixRowViewModel)c.Item).RowIndex, column: c.Column.DisplayIndex))
            .ToList();
        if (selected.Count > 0)
        {
            top = selected.Min(c => c.row);
            bottom = selected.Max(c => c.row);
            left = selected.Min(c => c.column);
            right = selected.Max(c => c.column);
        }
        else if (grid.CurrentCellInfo is { Item: MatrixRowViewModel current, Column: { } column })
        {
            top = bottom = current.RowIndex;
            left = right = column.DisplayIndex;
        }
        else
        {
            return false;
        }

        return true;
    }
}
