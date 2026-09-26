using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.Control;

/// <summary>
/// Control writing single elements (cells) of a matrix variable to the device. Extends the
/// scalar write contract: the host injects the element write delegate the same way, and the
/// control stays ignorant of protocols/drivers. The base IVariableWriteControl members keep
/// their meaning (CanWriteVariableProvider gates the write mode); the scalar
/// WriteVariableEngValueAsync delegate is unused by matrix controls.
/// </summary>
public interface IMatrixVariableWriteControl : IVariableWriteControl
{
    /// <summary>
    /// Host injektuje: zapise inzenyrskou hodnotu jednoho prvku matice (sekce + index) do
    /// zarizeni (inverzni konverze, fronta zapisu a notifikace command driveru na strane hosta).
    /// </summary>
    Func<IVariableBase, MatrixSectionKind, int, double, Task<bool>>? WriteMatrixElementEngValueAsync { get; set; }

    /// <summary>
    /// Host injektuje: zapise vice prvku matice najednou - vsechny do fronty zapisu proměnné a
    /// JEDNA notifikace protokolu, ktery sousedni prvky slouci do oken (XCP: SET_MTA + DOWNLOAD
    /// blok na okno; radek 64 bunek = 1 prenos misto 64). Tlacitko Write posila takto vsechny
    /// rozeditovane bunky; Write on Enter zapisuje jednu bunku pres WriteMatrixElementEngValueAsync.
    /// Vraci false, kdyz se davka nezapsala (zadny prvek se pak nepovazuje za zapsany).
    /// </summary>
    Func<IVariableBase, IReadOnlyList<MatrixElementWrite>, Task<bool>>? WriteMatrixElementsEngValueAsync { get; set; }
}

/// <summary>One element of a batch matrix write: section, index in the section, engineering value.</summary>
public sealed record MatrixElementWrite(MatrixSectionKind Kind, int Index, double EngValue);
