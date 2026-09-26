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
/// - Ctrl+V / Shift+Insert: paste the clipboard text through the view model's PasteCommand
///   starting at the top-left cell of the selection (RadGridView's own paste is off).
/// Everything else is the default RadGridView behaviour (arrows, F2, Ctrl+C copy of the selection).
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

        if (!editing &&
            ((key == Key.V && modifiers.HasFlag(ModifierKeys.Control)) ||
             (key == Key.Insert && modifiers.HasFlag(ModifierKeys.Shift))))
        {
            return [new RelayCommand<object>(_ => Paste())];
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

    private void Paste()
    {
        if (grid.DataContext is not MatrixControlViewModel viewModel || !Clipboard.ContainsText())
        {
            return;
        }

        int top, left, bottom, right;
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
            return;
        }

        viewModel.PasteCommand.Execute(new MatrixPasteRequest(top, left, bottom - top + 1, right - left + 1, Clipboard.GetText()));
    }
}
