using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Qenex.QInsight.Views;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.External;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using Qenex.QInsight.AppConfig;

namespace Qenex.QInsight
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	public partial class App : Application
	{
		protected override void OnStartup(StartupEventArgs e)
		{
			// Telerik UI Automation peers off: with them on, every layout change inside a control
			// hosted in the workspace RadDiagram rebuilt the peer tree of the whole shape (all cells
			// of a RadGridView) - one scroll step of a 64x64 map blocked the UI for 0.5 s; without
			// them it is ~1 ms (probe + QInsight measurement 2026-09-27). Telerik's own performance
			// recommendation; QInsight has no UI-automation / screen-reader requirement.
			Telerik.Windows.Automation.Peers.AutomationManager.AutomationMode = Telerik.Windows.Automation.Peers.AutomationMode.Disabled;

			// Drivers resolve relative data paths (e.g. the default DataLogs directory) against
			// this root — the installation directory is not writable under Program Files.
			Qenex.QSuite.Drivers.Driver.DriverEnvironment.DataRootDirectory = AppDataPaths.Root;
			base.OnStartup(e);
		}
	}
}
