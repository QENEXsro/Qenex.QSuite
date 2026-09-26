namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// Clipboard paste into the table: the text (tab separated cells, line separated rows — what
/// Excel puts on the clipboard) and the target block: top-left cell plus the size of the
/// selection (a single pasted value fills the whole selection, like Excel).
/// </summary>
public sealed record MatrixPasteRequest(int Row, int Column, int RowCount, int ColumnCount, string Text);
