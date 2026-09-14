using HarfBuzzSharp;
using Qenex.QInsight.ViewModels;
using Qenex.QInsight.ViewModels.ModelWrappers;
using Qenex.QInsight.ViewModels.SolutionExplorerWrappers;
using Qenex.QLibs.QUI;
using Qenex.QLibs.QUI.TelerikDocking;
using Qenex.QSuite.Scripting.Script;
using Telerik.Windows.Data;

namespace Qenex.QInsight.ViewModels;

public class ScriptPropertiesViewModel : PropertyChangedBaseWithValidation, IPropertiesViewModel
{
	public ScriptPropertiesViewModel(EventAggregator ea, ScriptWrapper scriptWrapper)
	{
		ScriptWrapper = scriptWrapper;
	}
	public IEnumerable<EnumMemberViewModel> ExecutionModes { get; }	= ScriptExecutionModeSource.Visible;

	public ScriptWrapper ScriptWrapper { get; set; }

	public void RefreshScript()
	{
		ScriptWrapper.RefreshProperties();
	}
}
