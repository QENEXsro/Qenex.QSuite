using System.Collections.ObjectModel;
using System.Windows;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Controls.GraphControl.Helpers;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using RtGraphControl.Models;
using ScottPlot;
using ScottPlot.AxisPanels;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.FileDialogs;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Qenex.QSuite.Controls.GraphControl.ViewModels;

[DataContract]
public class GraphControlViewModel : ControlBase, IHasMousePosition, IFileDialogAwareControl, IVariableReferenceProvider, ILogAwareControl, ISampleHistoryControl, ITimelineResetControl
{
    #region Const

    private const double PlotFontSizeMultiplier = 0.9;

    // Backward timestamps up to this tolerance merge into the last point (host clock
    // adjustments and timestamp source changes must not wipe the chart); a larger
    // rewind means a restart (replay, device reboot) and clears the chart as before
    private const double BackwardTimestampToleranceSeconds = 5.0;

    #endregion
    
    #region Fields

    private DateTime baseTime;
    private DateTime lastUpdateTime;
    private int currentColorIndex;
    private Crosshair cross = null!;
    private Annotation annotation = null!;
    private Color backgroundColor;
    private Color foregroundColor;
    private int axesFontSize;
    private int plotFontSize;

    #endregion
    
    #region Constructor

    public GraphControlViewModel()
    {
        ChartVariables = [];
        VerticalAxes = [];
        
        MouseMoveCommand = new RelayCommand<MouseEventArgs>(DisplayCursorBasedOnMouseMove);
        RemoveChartVariableCommand = new RelayCommand<object>(RemoveChartVariable);
        ClearGraphCommand = new RelayCommand<object>(ClearGraph);
        AddAxisCommand = new RelayCommand<object>(AddAxis);
        RemoveAxisCommand = new RelayCommand<object>(RemoveAxis);
        ExportImageCommand = new RelayCommand<object>(ExportImage);
        ExportCsvCommand = new RelayCommand<object>(ExportCsv);
        ZoomToFitCommand = new RelayCommand<object>((i) => PlotControl?.Plot.Axes.AutoScale()); 
        
        Width = 300;
        Height = 200;

        InitializeRuntimeState();
        //PlotControl.Plot.Axes.Remove(Edge.Right);
    }

    #endregion
    
    #region Properties
    
    [IgnoreDataMember]
    public ObservableCollection<IYAxis> VerticalAxes { get; set { field = value; OnPropertyChanged(); } }
    [IgnoreDataMember]
    public IYAxis SelectedVerticalAxis { get; set { field = value; OnPropertyChanged(); UpdateRemoveAxisState(); } }

    // Drives the Remove item in the axes grid context menu: the first axis and axes with
    // signals assigned are not removable — the item is disabled, no message box.
    [IgnoreDataMember]
    public bool CanRemoveSelectedAxis { get; private set { field = value; OnPropertyChanged(); } }

    // Tooltip on the (disabled) Remove item explaining why the axis cannot be removed.
    [IgnoreDataMember]
    public string? RemoveAxisBlockReason { get; private set { field = value; OnPropertyChanged(); } }

    // 1-based cisla os pro comboboxy v UI (drzi se v AddAxis/RemoveAxis)
    [IgnoreDataMember]
    public ObservableCollection<int> AxisNumbers { get; set { field = value; OnPropertyChanged(); } } = [];

    // Options for the Style combo box in the variables grid (ScottPlot default patterns).
    // Computed getter, NOT an initializer: DataContract deserialization skips constructors
    // and field initializers, an initialized property would come back null.
    [IgnoreDataMember]
    public IReadOnlyList<ChartLineStyle> LineStyles => Enum.GetValues<ChartLineStyle>();

    [IgnoreDataMember]
    public ChartVariable SelectedChartVariable { get; set { field = value; OnPropertyChanged(); } }

    [DataMember]
    public bool IsLegendHorizontal 
    {
        get;
        set
        {
            field = value; 
            OnPropertyChanged();
            if (PlotControl != null)
            {
                PlotControl.Plot.Legend.Orientation = value ? ScottPlot.Orientation.Horizontal : ScottPlot.Orientation.Vertical;
                PlotControl.Refresh();
            }
        } 
    }

    [IgnoreDataMember]
    public Point MousePosition { get; set; }

    [DataMember]
    public bool IsCrossEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (cross != null)
            {
                cross.IsVisible = value;
            }

            if (annotation != null)
            {
                annotation.IsVisible = false;
            }

            if (!value && ChartVariables != null)
            {
                ResetLegendTexts();
            }

            PlotControl?.Refresh();
        }
    } = false;

    [IgnoreDataMember]
    public RelayCommand<MouseEventArgs> MouseMoveCommand { get; set; }
    [IgnoreDataMember]
    public RelayCommand<object> ClearGraphCommand { get; set; }
    [IgnoreDataMember]
    public RelayCommand<object> RemoveChartVariableCommand { get; set; }
    [IgnoreDataMember]
    public RelayCommand<object> AddAxisCommand { get; set; }
    [IgnoreDataMember]
    public RelayCommand<object> RemoveAxisCommand { get; set; }
    [IgnoreDataMember]
    public RelayCommand<object> ExportImageCommand { get; set; }
    [IgnoreDataMember]
    public RelayCommand<object> ExportCsvCommand { get; set; }
    
    [IgnoreDataMember]
    public RelayCommand<object> ZoomToFitCommand { get; set; }
    
    
    
    [IgnoreDataMember]
    public ObservableCollection<ChartVariable> ChartVariables { get; set; }

    [DataMember]
    public List<ChartVariableBinding> ChartVariableBindings { get; set; } = [];

    [DataMember]
    public List<VerticalAxisBinding> VerticalAxisBindings { get; set; } = [];

    [DataMember]
    public string ChartTitle { get; set { field = value; OnPropertyChanged(); } } = string.Empty;
    
    [IgnoreDataMember]
    public WpfPlot PlotControl { get; private set; } = null!;

    [IgnoreDataMember]
    public Action<DialogWindowBase>? ConfigureSaveFileDialog { get; set; }

    [IgnoreDataMember]
    public Func<string?>? SaveDialogInitialDirectoryProvider { get; set; }

    [IgnoreDataMember]
    public Action<string>? SaveDialogDirectoryChanged { get; set; }

    [IgnoreDataMember]
    public Action<string>? LogInfo { get; set; }

    [IgnoreDataMember]
    public Action<string>? LogWarn { get; set; }

    /// <summary>
    /// Reference na promenne navazane v grafu (ChartVariableBindings + ChartVariables),
    /// nad ramec ControlBase.Variables/LinkedVariables. Pouziva host pri zjistovani pouziti promenne.
    /// </summary>
    public IEnumerable<string> GetAdditionalVariableReferences()
    {
        var references = ChartVariableBindings.Select(binding => binding.VariableReference).ToList();
        references.AddRange(ChartVariables
            .Where(chartVariable => chartVariable.Variable != null)
            .Select(chartVariable => GetVariableReference(chartVariable.Variable)));
        return references;
    }

    [DataMember]
    public int ChartTimeSpan { get; set { if (value < 1) value = 1; field = value; OnPropertyChanged(); } } = 10;
    
    [DataMember]
    public int ChartBuffer { get; set { if (value < 50) value = 50; field = value; OnPropertyChanged(); } } = 80;
    
    #endregion
    
    #region Derived properties

    public override string ControlName => "GraphControl";
    public override string Label => "Graph";
    public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/GraphControl.png");
    public override string Description => "Graph Control for displaying data in a graphical format.";

    #endregion    
    
    #region Overrides of ControlBase
    
    // Graf kresli ciselne prubehy: jen skalarni promenne
    public override bool CanBindVariable(IVariableBase variable) => variable is ScalarVariable;

    public override void BindVariable(IVariableBase variable)
    {
        if (!GraphSealValid()) return;

        if (!CanBindVariable(variable)) return;

        var ev = Variables.FirstOrDefault(v => v.Equals(variable));
        if (ev != null) return;

        RememberVariableBinding(variable);
        Variables.Add(variable);

        var savedBinding = GetSavedChartVariableBinding(variable);
        var c = GetSavedChartColor(savedBinding) ?? GetNextChartColor();
        RestoreVerticalAxes();
        var chartVariable = new ChartVariable();
        ChartVariables.Add(chartVariable);
        
        var signal = PlotControl.Plot.Add.SignalXY(chartVariable.XVal, chartVariable.YVal, ChartVariable.ToScottPlotColor(c));
        signal.LegendText = GetVariableLegendText(variable);
        // signal.MarkerShape = MarkerShape.Asterisk;
        // signal.MarkerSize = 50; 
        chartVariable.ChartSignal = signal;
        chartVariable.ChartColor = c;
        chartVariable.Variable = variable;
        chartVariable.ChangeAxisAction += axisIndex =>
        {
            var retIndex = GetValidVerticalAxisIndex(axisIndex);
            if (retIndex >= 0)
            {
                signal.Axes.YAxis = VerticalAxes[retIndex];
            }

            PlotControl.Refresh();
            UpdateRemoveAxisState();
            return retIndex;
        };

        PlotControl.Refresh();
        chartVariable.LineWidth = GetSavedLineWidth(savedBinding);
        chartVariable.LineStyle = savedBinding?.LineStyle ?? ChartLineStyle.Solid;
        chartVariable.AxisIndex = savedBinding?.AxisIndex ?? 0;
        RememberChartVariableBinding(chartVariable);
        UpdateRemoveAxisState();
    }

    public override Task UpdateVariableValueAsync(IVariableBase variable)
    {
        if (!(graphLiveSealOk ??= GraphSealValid())) return Task.CompletedTask;

        // Only handle scalar variables contained in ChartVariables
        if (variable is not ScalarVariable scalarVariable) return Task.CompletedTask;
        var chartVariable = ChartVariables.FirstOrDefault(v => v.Variable.Name == scalarVariable.Name);
        if (chartVariable == null) return Task.CompletedTask;

        var timestamp = variable.Timestamp == default
            ? DateTime.UtcNow
            : variable.Timestamp;

        // Initialize from the incoming data timestamp. Replay data can be historical,
        // so using DateTime.UtcNow here prevents refreshes from being triggered.
        if (baseTime == DateTime.MinValue)
        {
            baseTime = timestamp;
            lastUpdateTime = DateTime.MinValue;
        }

        var val = scalarVariable.GetEngValue();
        var xVal = (timestamp - baseTime).TotalSeconds;

        if (chartVariable.XDateTimeVal.Count > 0)
        {
            var lastChartVariableTimestamp = chartVariable.XDateTimeVal[^1];
            if (timestamp < lastChartVariableTimestamp
                && (lastChartVariableTimestamp - timestamp).TotalSeconds <= BackwardTimestampToleranceSeconds)
            {
                // A small backward step (host clock adjustment, timestamp source change,
                // replay seek re-sending history) folds into the last point; only a real
                // rewind below restarts the chart. Neither case is logged: it happens on
                // every replay seek and the operator cannot act on it.
                timestamp = lastChartVariableTimestamp;
                xVal = chartVariable.XVal[^1];
            }

            if (timestamp < lastChartVariableTimestamp)
            {
                ClearChartData(timestamp);
                xVal = 0;
            }
            else if (timestamp == lastChartVariableTimestamp)
            {
                chartVariable.YVal[^1] = val;
                RefreshPlotForValue(timestamp, xVal, val, chartVariable);
                return Task.CompletedTask;
            }
        }

        chartVariable.XDateTimeVal.Add(timestamp);
        chartVariable.XVal.Add(xVal);
        chartVariable.YVal.Add(val);

        RefreshPlotForValue(timestamp, xVal, val, chartVariable);

        return Task.CompletedTask;
    }

    public override void RefreshVariableBinding(IVariableBase variable)
    {
        base.RefreshVariableBinding(variable);

        foreach (var chartVariable in ChartVariables.Where(v =>
	                 v.Variable != null
	                 && (ReferenceEquals(v.Variable, variable)
	                     || ControlBase.IsVariableReferenceMatch(ControlBase.GetVariableReference(v.Variable), variable))))
        {
            chartVariable.Variable = variable;
            chartVariable.RefreshVariableLabel();
        }

        PlotControl?.Refresh();
    }

    protected override void OnEditToRun()
    {
        ClearGraph(null!);
    }

    /// <summary>
    /// Replay seek (ITimelineResetControl): same as the operator's Clear — drops the plotted
    /// samples and the time-axis origin, so the history the replay driver re-sends up to the
    /// new position starts a fresh timeline instead of folding into the last point (the
    /// backward-timestamp tolerance is meant for live clock drift, not for a seek).
    /// </summary>
    public void ResetTimeline()
    {
        ClearGraph(null!);
    }

    private void ClearChartData(DateTime newBaseTime)
    {
        foreach (var chartVariable in ChartVariables)
        {
            chartVariable.XDateTimeVal.Clear();
            chartVariable.XVal.Clear();
            chartVariable.YVal.Clear();
        }

        baseTime = newBaseTime;
        lastUpdateTime = DateTime.MinValue;
    }

    private void RefreshPlotForValue(DateTime timestamp, double xVal, double val, ChartVariable chartVariable)
    {
        var axisIndex = GetValidVerticalAxisIndex(chartVariable.AxisIndex);
        if (axisIndex < 0)
        {
            return;
        }

        if (axisIndex != chartVariable.AxisIndex)
        {
            chartVariable.AxisIndex = axisIndex;
        }

        if (((VerticalAxis)VerticalAxes[axisIndex]).IsAutoScale)
        {
            RecalculateVerticalAxisLimits(xVal, val, axisIndex);
        }

        PlotControl.Plot.Axes.SetLimitsX(xVal - ChartTimeSpan - 1,xVal + 1);

        if (lastUpdateTime == DateTime.MinValue
            || timestamp < lastUpdateTime
            || (timestamp - lastUpdateTime).TotalMilliseconds > ChartBuffer)
        {
            PlotControl.Refresh();
            lastUpdateTime = timestamp;
        }

        if (IsCrossEnabled)
        {
            ActualizeCrosshairAndAnnotation(MousePosition);
        }
    }
    
    public override void UpdateThemeSettingsControl(System.Windows.Media.Color bgColor, System.Windows.Media.Color fgColor, int fontSize)
    {
        base.UpdateThemeSettingsControl(bgColor, fgColor, fontSize);

        var scale = PlotControl.DisplayScale;
        plotFontSize = (int)Math.Round(PlotFontSizeMultiplier * fontSize * scale);
        axesFontSize = (int)Math.Round(PlotFontSizeMultiplier * fontSize * scale);
        backgroundColor = bgColor.ToScottPlotColor();
        foregroundColor = fgColor.ToScottPlotColor();
        
        // Crosshair
        cross.LineColor = foregroundColor;
        
        // Legend
        PlotControl.Plot.Legend.FontSize = axesFontSize;
        PlotControl.Plot.Legend.BackgroundColor = backgroundColor;
        PlotControl.Plot.Legend.FontColor = foregroundColor;
        PlotControl.Plot.Legend.Alignment = Alignment.LowerLeft;
        
        // Annotation
        annotation.LabelFontSize = plotFontSize;
        annotation.LabelFontColor = foregroundColor;
        annotation.LabelBackgroundColor = backgroundColor;
        
        // Background
        PlotControl.Plot.FigureBackground.Color = backgroundColor;
        PlotControl.Plot.DataBackground.Color = backgroundColor;
        PlotControl.Plot.DataBorder.Color = foregroundColor;
        
        // Set Title Axis <<
        PlotControl.Plot.Axes.Title.Label.FontSize = plotFontSize;
        PlotControl.Plot.Axes.Title.Label.Bold = false;
        PlotControl.Plot.Axes.Title.Label.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Title.Label.BackgroundColor = backgroundColor;
        PlotControl.Plot.Axes.Title.IsVisible = false;
        
        
        // Set horizontal axes
        PlotControl.Plot.Axes.SetLimitsX(0, 10);
        PlotControl.Plot.Axes.Bottom.Label.Text = "Time [s]";
        PlotControl.Plot.Axes.Bottom.Label.FontSize = plotFontSize;
        PlotControl.Plot.Axes.Bottom.Label.Bold = false;   
        PlotControl.Plot.Axes.Bottom.TickLabelStyle.FontSize = axesFontSize;
        PlotControl.Plot.Axes.Bottom.Label.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Bottom.Label.BackgroundColor = backgroundColor;
        PlotControl.Plot.Axes.Bottom.TickLabelStyle.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Bottom.TickLabelStyle.BackgroundColor = backgroundColor;

        PlotControl.Plot.Axes.Top.Label.FontSize = plotFontSize;
        PlotControl.Plot.Axes.Top.Label.Bold = false;
        PlotControl.Plot.Axes.Top.TickLabelStyle.FontSize = axesFontSize;    
        PlotControl.Plot.Axes.Top.Label.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Top.Label.BackgroundColor = backgroundColor;
        PlotControl.Plot.Axes.Top.TickLabelStyle.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Top.TickLabelStyle.BackgroundColor = backgroundColor; 
        
        // Set left empty axis as default (bare frame only - EmptyTickGenerator, see InitializeRuntimeState)
        PlotControl.Plot.Axes.Left.Label.IsVisible = false;

        // Set right empty axis as default
        PlotControl.Plot.Axes.Right.Label.IsVisible = false;
        PlotControl.Plot.Axes.Right.Label.FontSize = plotFontSize;
        PlotControl.Plot.Axes.Right.Label.Bold = false;
        PlotControl.Plot.Axes.Right.TickLabelStyle.FontSize = axesFontSize;    
        PlotControl.Plot.Axes.Right.Label.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Right.Label.BackgroundColor = backgroundColor;
        PlotControl.Plot.Axes.Right.TickLabelStyle.ForeColor = foregroundColor;
        PlotControl.Plot.Axes.Right.TickLabelStyle.BackgroundColor = backgroundColor; 
        
        RestoreVerticalAxes();
        UpdateVerticalAxisTheme();
        
        // Grid
        var gridColor = new Color((foregroundColor.R + backgroundColor.R)/2, (foregroundColor.G + backgroundColor.G)/2, (foregroundColor.B + backgroundColor.B)/2, 0.2f);
        PlotControl.Plot.Grid.LineColor = gridColor;
        PlotControl.Plot.Grid.YAxis = VerticalAxes[0];
        PlotControl.Plot.Grid.YAxisStyle.MajorLineStyle.IsVisible = true;
        PlotControl.Plot.Grid.YAxisStyle.MajorLineStyle.Color = gridColor;
     

        foreach (var axis in PlotControl.Plot.Axes.GetAxes())
        {
            if (axis is VerticalAxis)
            {
                continue;
            }

            axis.FrameLineStyle.Color = foregroundColor;
        }
        
        PlotControl.Plot.ShowLegend();
        PlotControl.Refresh();
    }

    #endregion

    #region Private Methods

    private void InitializeRuntimeState()
    {
        ChartVariables ??= [];
        ChartVariableBindings ??= [];
        VerticalAxisBindings ??= [];
        VerticalAxes ??= [];
        AxisNumbers ??= [];
        currentColorIndex = Math.Max(currentColorIndex, 0);

        MouseMoveCommand = new RelayCommand<MouseEventArgs>(DisplayCursorBasedOnMouseMove);
        RemoveChartVariableCommand = new RelayCommand<object>(RemoveChartVariable);
        ClearGraphCommand = new RelayCommand<object>(ClearGraph);
        AddAxisCommand = new RelayCommand<object>(AddAxis);
        RemoveAxisCommand = new RelayCommand<object>(RemoveAxis);
        ExportImageCommand = new RelayCommand<object>(ExportImage);
        ExportCsvCommand = new RelayCommand<object>(ExportCsv);
        ZoomToFitCommand = new RelayCommand<object>((i) => PlotControl?.Plot.Axes.AutoScale());

        PlotControl = new WpfPlot();
        cross = PlotControl.Plot.Add.Crosshair(0, 0);
        annotation = PlotControl.Plot.Add.Annotation("", Alignment.UpperLeft);
        annotation.IsVisible = false;
        cross.IsVisible = IsCrossEnabled;
        PlotControl.Plot.Legend.Orientation = IsLegendHorizontal ? ScottPlot.Orientation.Horizontal : ScottPlot.Orientation.Vertical;

        // Remove context menu
        PlotControl.Menu?.Clear();
        PlotControl.UserInputProcessor.UserActionResponses.RemoveAll(
            x => x is ScottPlot.Interactivity.UserActionResponses.SingleClickContextMenu);

        // Vychozi leva osa musi v plotu zustat (ScottPlot vyzaduje existenci Axes.Left -
        // render/GetCoordinates by spadly, kdyby uzivatel presunul vsechny osy doprava).
        // Zustava viditelna jako holy ramecek grafu: EmptyTickGenerator zaruci, ze nikdy
        // nedostane ticky - render action AutoScaleUnsetAxes by jinak ose bez limitu
        // nastavil -10..10 a vedle Y->0 by se vykreslila druha plnohodnotna osa.
        PlotControl.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.EmptyTickGenerator();
        // Prava vychozi osa: stejna pojistka (dnes ticky nema jen diky prazdnym limitum)
        PlotControl.Plot.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.EmptyTickGenerator();
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        InitializeRuntimeState();
    }

    [OnSerializing]
    private void OnSerializing(StreamingContext context)
    {
        SynchronizeVerticalAxisBindings();
        SynchronizeChartVariableBindings();
    }

    private System.Windows.Media.Color GetNextChartColor()
    {
        if (!ChartHelper.ChartColors.ContainsKey(currentColorIndex))
        {
            currentColorIndex = ChartHelper.ChartColors.Keys.Min();
        }

        var color = ChartHelper.ChartColors[currentColorIndex];
        currentColorIndex++;
        return color;
    }

    private ChartVariableBinding? GetSavedChartVariableBinding(IVariableBase variable)
    {
        return ChartVariableBindings.FirstOrDefault(b =>
            ControlBase.IsVariableReferenceMatch(b.VariableReference, variable));
    }

    private static System.Windows.Media.Color? GetSavedChartColor(ChartVariableBinding? binding)
    {
        if (binding?.TryGetChartColor(out var chartColor) == true)
        {
            return chartColor;
        }

        return null;
    }

    private static float GetSavedLineWidth(ChartVariableBinding? binding)
    {
        return binding?.LineWidth ?? 1.0f;
    }

    private void RememberChartVariableBinding(ChartVariable chartVariable)
    {
        if (chartVariable.Variable == null)
        {
            return;
        }

        var variableReference = GetVariableReference(chartVariable.Variable);
        var binding = ChartVariableBindings.FirstOrDefault(b => b.VariableReference == variableReference);
        if (binding == null)
        {
            ChartVariableBindings.Add(new ChartVariableBinding(
                variableReference,
                chartVariable.ChartColor,
                chartVariable.LineWidth,
                chartVariable.AxisIndex,
                chartVariable.LineStyle));
            return;
        }

        binding.SetChartColor(chartVariable.ChartColor);
        binding.LineWidth = chartVariable.LineWidth;
        binding.LineStyle = chartVariable.LineStyle;
        binding.AxisIndex = chartVariable.AxisIndex;
    }

    private void SynchronizeChartVariableBindings()
    {
        if (ChartVariables.Count == 0)
        {
            return;
        }

        ChartVariableBindings = ChartVariables
            .Where(chartVariable => chartVariable.Variable != null)
            .Select(chartVariable => new ChartVariableBinding(
                GetVariableReference(chartVariable.Variable),
                chartVariable.ChartColor,
                chartVariable.LineWidth,
                chartVariable.AxisIndex,
                chartVariable.LineStyle))
            .ToList();
        LinkedVariables = ChartVariableBindings
            .Select(binding => binding.VariableReference)
            .ToList();
    }

    private void SynchronizeVerticalAxisBindings()
    {
        if (VerticalAxes.Count == 0)
        {
            return;
        }

        VerticalAxisBindings = VerticalAxes
            .OfType<VerticalAxis>()
            .Select(VerticalAxisBinding.FromAxis)
            .ToList();
    }

    private void RestoreVerticalAxes()
    {
        if (VerticalAxes.Count > 0)
        {
            return;
        }

        if (VerticalAxisBindings.Count == 0)
        {
            AddAxis(Edge.Left, 0);
        }
        else
        {
            for (var i = 0; i < VerticalAxisBindings.Count; i++)
            {
                AddAxis(VerticalAxisBindings[i], i);
            }
        }

        // Crosshair a annotation nesmi zustat bez os - jinak by je ScottPlot navazal na
        // vychozi (ramovou) levou osu bez limitu a kreslily by se do NaN souradnic.
        cross.Axes.YAxis = VerticalAxes[0];
        annotation.Axes.YAxis = VerticalAxes[0];
    }

    private int GetValidVerticalAxisIndex(int axisIndex)
    {
        RestoreVerticalAxes();
        if (VerticalAxes.Count == 0)
        {
            return -1;
        }

        return axisIndex >= 0 && axisIndex < VerticalAxes.Count
            ? axisIndex
            : 0;
    }

    private void UpdateVerticalAxisTheme()
    {
        foreach (var axis in VerticalAxes.OfType<VerticalAxis>())
        {
            axis.LabelBackgroundColor = backgroundColor;
            axis.LabelFontSize = plotFontSize;
            axis.TickLabelStyle.BackgroundColor = backgroundColor;
            axis.TickLabelStyle.FontSize = axesFontSize;
            axis.ThemeForeColor = foregroundColor;
            axis.ApplyColor();
        }
    }

    private void RecalculateVerticalAxisLimits(double xVal, double yVal, int axisIndex)
    {
        // The axis is identified by its index, never by Name - the name is user-editable.
        if (VerticalAxes[axisIndex] is not VerticalAxis yAxis)
        {
            return;
        }

        var top = yAxis.Max;
        var bottom = yAxis.Min;

        // Written through Minimum/Maximum, not the raw ScottPlot range - the axes settings grid
        // binds to these properties, and a manual edit only reaches the axis when the entered
        // value differs from them.
        if (yVal > top)
        {
            yAxis.Maximum = yVal * 1.1;
        }
        else if (yVal < bottom && yVal > 0)
        {
            yAxis.Minimum = yVal * 0.7;
        }
        else if (yVal < bottom && yVal < 0)
        {
            yAxis.Minimum = yVal * 1.1;
        }
    }

    private void DisplayCursorBasedOnMouseMove(MouseEventArgs e)
    {
        if (!IsCrossEnabled) return;
        
        var p = e.GetPosition(PlotControl);
        MousePosition = p;
        ActualizeCrosshairAndAnnotation(p);
        PlotControl.Refresh();
    }
    
    private void ActualizeCrosshairAndAnnotation(Point p)
    {
        Pixel mousePixel = new(p.X * PlotControl.DisplayScale, p.Y * PlotControl.DisplayScale);
        // Y explicitne v prostoru prvni uzivatelske osy - Axes.Left je ramova osa bez limitu
        Coordinates mouseCoordinates = PlotControl.Plot.GetCoordinates(mousePixel,
            yAxis: VerticalAxes.Count > 0 ? VerticalAxes[0] : null);
        var cursorX = GetCursorX(mouseCoordinates.X);

        cross.Position = new Coordinates(cursorX, mouseCoordinates.Y);

        var cursorValues = GetCursorValues(cursorX);
        UpdateLegendTexts(cursorValues);
    }

    private double GetCursorX(double mouseX)
    {
        var chartVariable = SelectedChartVariable?.IsVisible == true && HasCursorData(SelectedChartVariable)
            ? SelectedChartVariable
            : ChartVariables.FirstOrDefault(v => v.IsVisible && HasCursorData(v));

        if (chartVariable == null)
        {
            return mouseX;
        }

        var index = GetNearestXIndex(chartVariable.XVal, mouseX);
        return chartVariable.XVal[index];
    }

    private List<CursorValue> GetCursorValues(double cursorX)
    {
        var cursorValues = new List<CursorValue>();

        foreach (var chartVariable in ChartVariables.Where(v => v.IsVisible && HasCursorData(v)))
        {
            var index = GetNearestXIndex(chartVariable.XVal, cursorX);
            cursorValues.Add(new CursorValue(chartVariable, chartVariable.XVal[index], chartVariable.YVal[index]));
        }

        return cursorValues;
    }

    private static bool HasCursorData(ChartVariable chartVariable)
    {
        return chartVariable.XVal.Count > 0 && chartVariable.XVal.Count == chartVariable.YVal.Count;
    }

    private static int GetNearestXIndex(List<double> values, double x)
    {
        var index = values.BinarySearch(x);
        if (index >= 0)
        {
            return index;
        }

        index = ~index;
        if (index <= 0)
        {
            return 0;
        }

        if (index >= values.Count)
        {
            return values.Count - 1;
        }

        return Math.Abs(values[index] - x) < Math.Abs(values[index - 1] - x)
            ? index
            : index - 1;
    }

    private static string GetCursorLabel(ChartVariable chartVariable)
    {
        return GetVariableLegendText(chartVariable.Variable);
    }

    private void UpdateLegendTexts(IReadOnlyCollection<CursorValue> cursorValues)
    {
        foreach (var chartVariable in ChartVariables)
        {
            if (chartVariable.ChartSignal == null)
            {
                continue;
            }

            var cursorValue = cursorValues.FirstOrDefault(v => ReferenceEquals(v.ChartVariable, chartVariable));
            chartVariable.ChartSignal.LegendText = cursorValue.ChartVariable == null
                ? GetCursorLabel(chartVariable)
                : $"{GetCursorLabel(chartVariable)} [{Math.Round(cursorValue.X, 3)}; {Math.Round(cursorValue.Y, 3)}]";
        }
    }

    private void ResetLegendTexts()
    {
        foreach (var chartVariable in ChartVariables.Where(v => v.Variable != null))
        {
            if (chartVariable.ChartSignal != null)
            {
                chartVariable.ChartSignal.LegendText = GetCursorLabel(chartVariable);
            }
        }
    }

    private static string GetVariableLegendText(IVariableBase variable)
    {
        var label = string.IsNullOrWhiteSpace(variable.Label)
            ? variable.Name
            : variable.Label;

        return $"{label} ({variable.Id})";
    }

    private readonly record struct CursorValue(ChartVariable ChartVariable, double X, double Y);

    private void ExportImage(object parameter)
    {
        var dialog = CreateSaveFileDialog(
            "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|BMP image (*.bmp)|*.bmp|WebP image (*.webp)|*.webp|SVG image (*.svg)|*.svg",
            "graph.png");

        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            return;
        }

        var filePath = EnsureFileExtension(dialog.FileName, ".png");
        RememberSaveDialogDirectory(filePath);
        var width = GetExportPixelWidth();
        var height = GetExportPixelHeight();

        switch (Path.GetExtension(filePath).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                PlotControl.Plot.SaveJpeg(filePath, width, height);
                break;
            case ".bmp":
                PlotControl.Plot.SaveBmp(filePath, width, height);
                break;
            case ".webp":
                PlotControl.Plot.SaveWebp(filePath, width, height);
                break;
            case ".svg":
                PlotControl.Plot.SaveSvg(filePath, width, height);
                break;
            default:
                PlotControl.Plot.SavePng(filePath, width, height);
                break;
        }
    }

    private void ExportCsv(object parameter)
    {
        var dialog = CreateSaveFileDialog("CSV files (*.csv)|*.csv", "graph-data.csv");

        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            return;
        }

        var filePath = EnsureFileExtension(dialog.FileName, ".csv");
        RememberSaveDialogDirectory(filePath);
        File.WriteAllText(filePath, CreateCsv(), Encoding.UTF8);
    }

    private string CreateCsv()
    {
        var csv = new StringBuilder();
        csv.AppendLine("VariableId,VariableName,Index,Timestamp,X,Y");

        foreach (var chartVariable in ChartVariables.Where(v => v.Variable != null))
        {
            var count = Math.Min(chartVariable.XVal.Count, chartVariable.YVal.Count);
            for (var i = 0; i < count; i++)
            {
                var timestamp = i < chartVariable.XDateTimeVal.Count
                    ? chartVariable.XDateTimeVal[i].ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty;

                csv.Append(chartVariable.Variable.Id.ToString(CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.Append(EscapeCsv(GetVariableLegendText(chartVariable.Variable)));
                csv.Append(',');
                csv.Append(i.ToString(CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.Append(EscapeCsv(timestamp));
                csv.Append(',');
                csv.Append(chartVariable.XVal[i].ToString("G17", CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.Append(chartVariable.YVal[i].ToString("G17", CultureInfo.InvariantCulture));
                csv.AppendLine();
            }
        }

        return csv.ToString();
    }

    private int GetExportPixelWidth()
    {
        var width = PlotControl.ActualWidth > 0 ? PlotControl.ActualWidth : Width;
        return Math.Max(1, (int)Math.Round(width * PlotControl.DisplayScale));
    }

    private int GetExportPixelHeight()
    {
        var height = PlotControl.ActualHeight > 0 ? PlotControl.ActualHeight : Height;
        return Math.Max(1, (int)Math.Round(height * PlotControl.DisplayScale));
    }

    private RadSaveFileDialog CreateSaveFileDialog(string filter, string fileName)
    {
        var dialog = new RadSaveFileDialog()
        {
            Owner = Application.Current?.MainWindow,
            Filter = filter,
            FileName = fileName,
            InitialDirectory = GetSaveDialogInitialDirectory()
        };

        ConfigureSaveFileDialog?.Invoke(dialog);
        return dialog;
    }

    private string GetSaveDialogInitialDirectory()
    {
        var initialDirectory = SaveDialogInitialDirectoryProvider?.Invoke();
        if (Directory.Exists(initialDirectory))
        {
            return initialDirectory;
        }

        // No working-directory fallback: when installed, the process may start in the
        // read-only application folder (Program Files) or wherever the opened .qproj lives.
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void RememberSaveDialogDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            SaveDialogDirectoryChanged?.Invoke(directory);
        }
    }

    private static string EnsureFileExtension(string filePath, string defaultExtension)
    {
        return string.IsNullOrWhiteSpace(Path.GetExtension(filePath))
            ? $"{filePath}{defaultExtension}"
            : filePath;
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
    
    private void RemoveChartVariable(object parameter)
    {
        var selected = SelectedChartVariable;
        if (selected == null) return;

        var index = ChartVariables.IndexOf(selected);
        var removedVariable = selected.Variable;

        if (selected.ChartSignal != null)
        {
            PlotControl?.Plot.Remove(selected.ChartSignal);
        }

        ChartVariables.Remove(selected);
        if (removedVariable != null)
        {
            Variables.Remove(removedVariable);
            RemoveChartVariableBinding(removedVariable);
        }

        SelectedChartVariable = ChartVariables.Count > 0
            ? ChartVariables[Math.Min(index, ChartVariables.Count - 1)]
            : null;

        UpdateRemoveAxisState();
        PlotControl?.Refresh();
        RaiseVariableBindingsChanged();
    }

    private void RemoveChartVariableBinding(IVariableBase variable)
    {
        var variableReference = GetVariableReference(variable);
        LinkedVariables.RemoveAll(v => v == variableReference || ControlBase.IsVariableReferenceMatch(v, variable));
        ChartVariableBindings.RemoveAll(v => v.VariableReference == variableReference || ControlBase.IsVariableReferenceMatch(v.VariableReference, variable));
    }
    
    private void ClearGraph(object parameter)
    {
        baseTime = DateTime.MinValue;
        lastUpdateTime = baseTime;

        foreach (var chartVariable in ChartVariables)
        {
            chartVariable.XDateTimeVal.Clear();
            chartVariable.XVal.Clear();
            chartVariable.YVal.Clear();
        }
        
        PlotControl.Refresh();
    }
    
    private void AddAxis(object parameter)
    {
        AddAxis(Edge.Right, VerticalAxes.Count);
        UpdateRemoveAxisState();
    }

    private void AddAxis(Edge edge, int index)
    {
        AddAxis(edge, index, null);
    }

    private void AddAxis(VerticalAxisBinding axisBinding, int index)
    {
        AddAxis(axisBinding.Edge, index, axisBinding);
    }

    private void AddAxis(Edge edge, int index, VerticalAxisBinding? axisBinding)
    {
        var newAxis = new VerticalAxis(edge)
        {
            Name = string.IsNullOrWhiteSpace(axisBinding?.Name) ? $"Y->{index}" : axisBinding.Name,
            AxisLabel = axisBinding?.Label ?? string.Empty,
            IsVisible = axisBinding?.IsVisible ?? true,
            IsAutoScale = axisBinding?.IsAutoScale ?? true,
            ThemeForeColor = foregroundColor,
            LabelBackgroundColor = backgroundColor,
            LabelFontSize = plotFontSize,
            TickLabelStyle = new LabelStyle()
            {
                BackgroundColor = backgroundColor,
                FontSize = axesFontSize,
            },

            MajorTickStyle = new TickMarkStyle(),
            MinorTickStyle = new TickMarkStyle()
        };

        if (axisBinding != null && axisBinding.TryGetAxisColor(out var axisColor))
        {
            newAxis.AxisColor = axisColor;
        }
        else
        {
            newAxis.ApplyColor();
        }

        newAxis.RefreshAction = () =>
        {
            PlotControl.Refresh();
        };

        PlotControl.Plot.Axes.AddYAxis(newAxis);
        VerticalAxes.Add(newAxis);
        AxisNumbers.Add(VerticalAxes.Count);

        newAxis.Minimum = axisBinding?.Minimum ?? -10.0;
        newAxis.Maximum = axisBinding?.Maximum ?? 10.0;
        PlotControl.Plot.Axes.SetLimitsY(bottom: newAxis.Minimum, top: newAxis.Maximum, yAxis: newAxis);
        PlotControl.Refresh();        
    }
    
    
    private void RemoveAxis(object parameter)
    {
        if (SelectedVerticalAxis == null)
        {
            return;
        }

        // The first axis is fixed and an axis with signals assigned stays too — the menu
        // item is disabled with a tooltip explaining why (UpdateRemoveAxisState); this
        // guard just mirrors that rule for safety.
        var index = VerticalAxes.IndexOf(SelectedVerticalAxis);
        if (index < 1 || ChartVariables.Any(cv => cv.AxisIndex == index))
        {
            return;
        }

        PlotControl.Plot.Axes.Remove(SelectedVerticalAxis);
        VerticalAxes.Remove(SelectedVerticalAxis);
        if (AxisNumbers.Count > 0)
        {
            AxisNumbers.RemoveAt(AxisNumbers.Count - 1);
        }

        // Axes above the removed one shifted down by one — re-point their signals to keep
        // them on the same axis object (the AxisIndex setter re-assigns
        // ChartSignal.Axes.YAxis via ChangeAxisAction).
        foreach (var chartVariable in ChartVariables.Where(cv => cv.AxisIndex > index).ToList())
        {
            chartVariable.AxisIndex--;
        }

        SelectedVerticalAxis = VerticalAxes[Math.Min(index, VerticalAxes.Count - 1)];
        PlotControl.Refresh();
    }

    private void UpdateRemoveAxisState()
    {
        if (SelectedVerticalAxis == null || VerticalAxes.Count == 0)
        {
            CanRemoveSelectedAxis = false;
            RemoveAxisBlockReason = "No axis selected.";
            return;
        }

        var index = VerticalAxes.IndexOf(SelectedVerticalAxis);
        if (index < 1)
        {
            CanRemoveSelectedAxis = false;
            RemoveAxisBlockReason = "The first axis is fixed and cannot be removed.";
            return;
        }

        var users = ChartVariables
            .Where(cv => cv.AxisIndex == index && cv.Variable != null)
            .Select(cv => GetVariableLegendText(cv.Variable))
            .ToList();
        if (users.Count > 0)
        {
            CanRemoveSelectedAxis = false;
            RemoveAxisBlockReason = $"The axis is used by: {string.Join(", ", users)}";
            return;
        }

        CanRemoveSelectedAxis = true;
        RemoveAxisBlockReason = null;
    }

    #endregion

    private bool? graphLiveSealOk;

    // LICENSE-SEAL COPY v1 — sync z _LicenseGuard/LicenseSealTemplate.cs
    // Nezavisly licencni check (necte licenseService). true=licencovano/nejasne (fail-open),
    // false=jiste nelicencovano. Reakce (nenastartovat) je v StartAsym.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool GraphSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open
        }
    }
}
