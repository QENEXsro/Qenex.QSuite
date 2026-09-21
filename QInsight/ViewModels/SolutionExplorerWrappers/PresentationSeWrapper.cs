using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QInsight.ViewModels.ViewableItem;

namespace Qenex.QInsight.ViewModels.SolutionExplorerWrappers;

public class PresentationSeWrapper : PropertyChangedBase, IViewableItem
{
    public PresentationSeWrapper(IPresentation presentation)
    {
        Presentation = presentation;
    }
    
    #region UI Properties
    
    public IPresentation Presentation { get => field; init { field = value; OnPropertyChanged(); } }

    public string Label
    {
        get => Presentation.Name;
        set { Presentation.Name = value; OnPropertyChanged(); }
    }
    
    public FontWeight LabelWeight => FontWeights.Normal;

    public Visibility ToolTipVisibility => Visibility.Visible;
    public string ToolTip => GetToolTip();
    
    public BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/SolutionExplorer/Presentation.png");
    public bool IsExpanded { get; set; }
    public ObservableCollection<IViewableItem> Children { get; set; } = [];

    public Dictionary<string, object>? CustomTags { get; set; } = new();

    #endregion

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ToolTip));
    }
    
    private string GetToolTip()
    {
        var sb = new StringBuilder();
        sb.Append("Presentation:");
        sb.Append(Environment.NewLine);
        sb.Append($"Name\t{Presentation.Name}");
        sb.Append(Environment.NewLine);
        sb.Append($"Conv.\t{Presentation.Conversion.ToString()}");
        sb.Append(Environment.NewLine);
        sb.Append($"Min.\t{Presentation.Min}");
        sb.Append(Environment.NewLine);
        sb.Append($"Max.\t{Presentation.Max}");
        sb.Append(Environment.NewLine);
        sb.Append($"Format\t{Presentation.PrintFormat}");
        sb.Append(Environment.NewLine);
        sb.Append($"Unit\t{Presentation.Unit}");
        
        return sb.ToString();
    }
}
