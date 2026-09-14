using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

/// <summary>
/// Single source of the user-facing variable type texts. The variable kind (scalar, matrix,
/// string) is otherwise invisible in Project Configuration: lists show only "Label (Id)" and
/// the detail rows differ per kind without naming it.
/// </summary>
public static class VariableTypeDisplay
{
    public const string Scalar = "Scalar";
    public const string Matrix = "Matrix";
    public const string String = "String";

    /// <summary>Type name shown in the read-only "Type" row of the variable detail.</summary>
    public static string TypeName(IVariableBase variable) => variable switch
    {
        MatrixVariable => Matrix,
        StringVariable => String,
        _ => Scalar
    };

    /// <summary>
    /// Suffix appended to list entries. Scalars are the common case and stay unmarked so the
    /// lists remain clean; only the other kinds get a tag.
    /// </summary>
    public static string ListTag(IVariableBase variable) => variable switch
    {
        MatrixVariable => " [Matrix]",
        StringVariable => " [String]",
        _ => string.Empty
    };

    /// <summary>"Label (Id)" plus the list tag, used by every variable list and combo.</summary>
    public static string ListName(IVariableBase variable, string label, int id)
        => $"{label} ({id}){ListTag(variable)}";
}
