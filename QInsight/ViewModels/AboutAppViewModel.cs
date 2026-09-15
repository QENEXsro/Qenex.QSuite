using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Qenex.QLibs.QUI;
using Telerik.Windows.Controls;

namespace Qenex.QInsight.ViewModels;

public class AboutAppViewModel()
{
    private RadWindow parentWindow = null!;
    // Version of the QInsight assembly (major.minor.build, the same three-number form the plugins show),
    // so the About dialog can never drift from what was actually built.
    public string Version => (typeof(AboutAppViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0)).ToString(3);
    public string Copyright => $"© {DateTime.Now.Year}";
    
    public RelayCommand<object> CloseCommand => new RelayCommand<object>((o) =>
    {
        parentWindow?.Close();
    });
    
    public void SetParentWindow(RadWindow window)
    {
        parentWindow = window;
    }
}
