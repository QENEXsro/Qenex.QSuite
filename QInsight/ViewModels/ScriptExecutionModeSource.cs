using Qenex.QSuite.Scripting.Script;
using Telerik.Windows.Data;

namespace Qenex.QInsight.ViewModels;

/// <summary>
/// Single source of the script execution modes offered in the UI.
/// The <see cref="ScriptExecutionMode"/> enum intentionally keeps values that
/// are reserved for future versions (currently <see cref="ScriptExecutionMode.EventTriggered"/>:
/// planned triggers are protocol events such as a command packet from a remote PC,
/// and clock events such as "run at 03:00"). Those values stay in the enum and in the
/// .qproj format, but are not selectable until the scripting engine can dispatch them.
/// To enable a mode again, remove it from <see cref="HiddenModes"/>.
/// </summary>
public static class ScriptExecutionModeSource
{
    private static readonly HashSet<ScriptExecutionMode> HiddenModes =
    [
        ScriptExecutionMode.EventTriggered
    ];

    /// <summary>Execution modes that are implemented and therefore offered to the user.</summary>
    public static IEnumerable<EnumMemberViewModel> Visible { get; } =
        EnumDataSource.FromType<ScriptExecutionMode>()
            .Where(member => member.Value is ScriptExecutionMode mode && !HiddenModes.Contains(mode))
            .ToList();
}
