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
///   starting at the top-left cell of the selection (RadGridView's own paste is off);
/// - Ctrl+C / Ctrl+Insert: copy the selected rectangle as tab / line separated text (view model).
/// Everything else is the default RadGridView behaviour (arrows, F2, ...).
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

        if (!editing &&
            ((key == Key.C && modifiers.HasFlag(ModifierKeys.Control)) ||
             (key == Key.Insert && modifiers.HasFlag(ModifierKeys.Control))))
        {
            return [new RelayCommand<object>(_ => Copy())];
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

    /// <summary>Ctrl+C: the rectangle of the selected cells as tab / line separated text (the
    /// view model formats it), so Excel gets exactly the block, whatever RadGridView would copy.</summary>
    private void Copy()
    {
        if (grid.DataContext is not MatrixControlViewModel viewModel || !TrySelectionRectangle(out var top, out var left, out var bottom, out var right))
        {
            return;
        }

        var text = viewModel.CopyText(top, left, bottom - top + 1, right - left + 1);
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private void Paste()
    {
        if (grid.DataContext is not MatrixControlViewModel viewModel || !Clipboard.ContainsText() ||
            !TrySelectionRectangle(out var top, out var left, out var bottom, out var right))
        {
            return;
        }

        viewModel.PasteCommand.Execute(new MatrixPasteRequest(top, left, bottom - top + 1, right - left + 1, Clipboard.GetText()));
    }

    /// <summary>Rectangle of the selected cells (or the current cell) in table coordinates.</summary>
    private bool TrySelectionRectangle(out int top, out int left, out int bottom, out int right)
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
