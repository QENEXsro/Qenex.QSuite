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
using Qenex.QSuite.Controls.XYGraphControl.Helpers;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.QVariables.Values;
using Qenex.QSuite.Controls.XYGraphControl.Models;
using ScottPlot;
using ScottPlot.AxisPanels;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.FileDialogs;

namespace Qenex.QSuite.Controls.XYGraphControl.ViewModels;

[DataContract]
public class XYGraphControlViewModel : ControlBase, IFileDialogAwareControl, IVariableReferenceProvider, ILogAwareControl, ISampleHistoryControl, ITimelineResetControl
{
    #region Const

    private const double PlotFontSizeMultiplier = 0.9;

    // Minimum interval between plot redraws, so a fast sample stream does not repaint on every point.
    private const double RefreshThrottleMs = 50;

    #endregion
    
    #region Fields

    // XY pairing: X-source sample history (ascending timestamps). Each Y sample looks up /
    // interpolates its X value by TIMESTAMP; Y samples newer than the X stream wait in the
    // series' PendingY queue. Never pair by arrival order — network transports deliver
    // per-variable bursts and arrival-order pairing draws staircases from clean signals.
    private List<DateTime> xTimes = [];
    private List<double> xValues = [];
    private DateTime lastUpdateTime;
    private int currentColorIndex;
    private Color backgroundColor;
    private Color foregroundColor;
    // Guards ApplyXRowToPlot from painting the bottom axis with the default (transparent)
    // color before the first theme update arrives.
    private bool isThemeApplied;
    private int axesFontSize;
    private int plotFontSize;

    #endregion
    
    #region Constructor

    public XYGraphControlViewModel()
    {
        ChartVariables = [];
        VerticalAxes = [];
        
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

    // Drives the Remove item in the axes grid context menu: fixed rows (X + first Y) and axes
    // with signals assigned are not removable — the item is disabled, no message box.
    [IgnoreDataMember]
    public bool CanRemoveSelectedAxis { get; private set { field = value; OnPropertyChanged(); } }

    // Tooltip on the (disabled) Remove item explaining why the axis cannot be removed.
    [IgnoreDataMember]
    public string? RemoveAxisBlockReason { get; private set { field = value; OnPropertyChanged(); } }

    // 1-based cisla Y os pro combobox v Signals gridu (2..n; cislo 1 je pevny radek X).
    // Drzi se v AddAxis/RemoveAxis.
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

    // How long (milliseconds) a plotted point stays visible before it is dropped — the rolling
    // trail that makes a live Lissajous/hysteresis loop instead of an ever-growing curve.
    [DataMember]
    public int PersistenceMs { get; set { if (value < 1) value = 1; field = value; OnPropertyChanged(); } } = 5000;

    #endregion
    
    #region Derived properties

    public override string ControlName => "XYGraphControl";
    public override string Label => "XY Graph";
    public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/XYGraph.png");
    public override string Description => "Graph Control for displaying data in a graphical format.";

    #endregion    
    
    #region Overrides of ControlBase
    
    // Graf kresli ciselne prubehy: jen skalarni promenne
    public override bool CanBindVariable(IVariableBase variable) => variable is ScalarVariable;

    public override void BindVariable(IVariableBase variable)
    {
        if (!CanBindVariable(variable)) return;

        var ev = Variables.FirstOrDefault(v => v.Equals(variable));
        if (ev != null) return;

        RememberVariableBinding(variable);
        Variables.Add(variable);

        var savedBinding = GetSavedChartVariableBinding(variable);
        // Snapshot the persisted X-source flag now: RememberChartVariableBinding below writes the
        // (still false) chartVariable.IsXAxis back onto savedBinding, so reading it afterwards
        // would always be false — the reason the X selection was lost on reload.
        var savedIsXAxis = savedBinding?.IsXAxis == true;
        var c = GetSavedChartColor(savedBinding) ?? GetNextChartColor();
        RestoreVerticalAxes();
        var chartVariable = new ChartVariable();
        ChartVariables.Add(chartVariable);
        
        // XY graph: Scatter (X and Y both arbitrary, the curve may loop back) instead of SignalXY
        // (which needs X ascending). XVal now holds the X-source value per sample, not time.
        var signal = PlotControl.Plot.Add.Scatter(chartVariable.XVal, chartVariable.YVal, ChartVariable.ToScottPlotColor(c));
        signal.LegendText = GetVariableLegendText(variable);
        // Line only: Scatter draws a marker on every point by default, which makes a dense
        // live curve look beaded/rough (SignalXY in GraphControl has no markers either).
        signal.MarkerSize = 0;
        chartVariable.ChartSignal = signal;
        chartVariable.ChartColor = c;
        chartVariable.Variable = variable;
        chartVariable.ChangeAxisAction += axisIndex =>
        {
            // The X-source is pinned to the X row (index 0). Its hidden scatter still gets a
            // registered Y axis so render/autoscale never touches the bare frame axis.
            if (chartVariable.IsXAxis)
            {
                if (VerticalAxes.Count > 1)
                {
                    signal.Axes.YAxis = VerticalAxes[1];
                }

                return 0;
            }

            var retIndex = GetValidVerticalAxisIndex(axisIndex);
            if (retIndex >= 0)
            {
                signal.Axes.YAxis = VerticalAxes[retIndex];
            }

            PlotControl.Refresh();
            UpdateRemoveAxisState();
            return retIndex;
        };
        chartVariable.XAxisSelectedAction = SetXSource;
        chartVariable.XAxisClearedAction = ClearXSource;

        PlotControl.Refresh();
        chartVariable.LineWidth = GetSavedLineWidth(savedBinding);
        chartVariable.LineStyle = savedBinding?.LineStyle ?? ChartLineStyle.Solid;
        chartVariable.AxisIndex = savedBinding?.AxisIndex ?? 1;
        RememberChartVariableBinding(chartVariable);

        // Restore the persisted X-source selection (raises SetXSource via the setter).
        if (savedIsXAxis)
        {
            chartVariable.IsXAxis = true;
        }
        else if (savedBinding == null && ChartVariables.Count == 1)
        {
            // The first signal dropped on a fresh graph becomes the X-source automatically;
            // the following ones are Y series (default: first Y axis, row 2).
            chartVariable.IsXAxis = true;
        }

        UpdateRemoveAxisState();
    }

    public override Task UpdateVariableValueAsync(IVariableBase variable)
    {
        // Only handle scalar variables contained in ChartVariables
        if (variable is not ScalarVariable scalarVariable) return Task.CompletedTask;
        var chartVariable = ChartVariables.FirstOrDefault(v => v.Variable.Name == scalarVariable.Name);
        if (chartVariable == null) return Task.CompletedTask;

        var timestamp = variable.Timestamp == default ? DateTime.UtcNow : variable.Timestamp;
        var value = scalarVariable.GetEngValue();

        // The X-source series only provides the X coordinate for the others; it is not plotted.
        if (chartVariable.IsXAxis)
        {
            AppendXSample(timestamp, value);
            return Task.CompletedTask;
        }

        // A Y series can only be plotted once an X-source has produced a value to pair with.
        if (xTimes.Count == 0) return Task.CompletedTask;

        // Pair by TIMESTAMP, never by arrival order: network transports (eth/XCP) deliver
        // samples in per-variable bursts, and pairing each Y with the latest received X turned
        // two clean sines into a staircase (a whole Y burst shared one X value).
        if (timestamp <= xTimes[^1])
        {
            AppendPoint(chartVariable, timestamp, GetXValueAt(timestamp), value);
            RefreshThrottled(timestamp);
        }
        else
        {
            // Y is ahead of the X stream — hold it until an X sample covers its time.
            chartVariable.PendingY.Add((timestamp, value));
        }

        return Task.CompletedTask;
    }

    /// <summary>Records an X-source sample into the pairing history (ascending timestamps;
    /// a backward jump means restart and starts a fresh history) and releases any waiting
    /// Y samples the X stream now covers.</summary>
    private void AppendXSample(DateTime timestamp, double value)
    {
        if (xTimes.Count > 0 && timestamp < xTimes[^1])
        {
            xTimes.Clear();
            xValues.Clear();
        }

        xTimes.Add(timestamp);
        xValues.Add(value);

        // History only needs to cover the persistence window (plus slack for late Y bursts).
        var cutoff = timestamp.AddMilliseconds(-PersistenceMs - 1000);
        var drop = 0;
        while (drop < xTimes.Count - 1 && xTimes[drop] < cutoff)
        {
            drop++;
        }

        if (drop > 0)
        {
            xTimes.RemoveRange(0, drop);
            xValues.RemoveRange(0, drop);
        }

        FlushPendingY(timestamp);
    }

    /// <summary>Emits the buffered Y samples whose timestamps the X stream has reached.</summary>
    private void FlushPendingY(DateTime now)
    {
        var flushed = false;
        foreach (var cv in ChartVariables)
        {
            if (cv.IsXAxis || cv.PendingY.Count == 0)
            {
                continue;
            }

            var take = 0;
            while (take < cv.PendingY.Count && cv.PendingY[take].Timestamp <= now)
            {
                var (t, v) = cv.PendingY[take];
                AppendPoint(cv, t, GetXValueAt(t), v);
                take++;
            }

            if (take > 0)
            {
                cv.PendingY.RemoveRange(0, take);
                flushed = true;
            }
        }

        if (flushed)
        {
            RefreshThrottled(now);
        }
    }

    /// <summary>X value at the given time: exact sample when one exists, otherwise linear
    /// interpolation between the surrounding X samples (clamped at the history edges).</summary>
    private double GetXValueAt(DateTime timestamp)
    {
        var index = xTimes.BinarySearch(timestamp);
        if (index >= 0)
        {
            // Equal timestamps are legal (the DAQ timestamp mapper clamps backward steps to
            // keep the axis monotonic) — take the newest of the duplicates.
            while (index + 1 < xTimes.Count && xTimes[index + 1] == xTimes[index])
            {
                index++;
            }

            return xValues[index];
        }

        index = ~index;
        if (index == 0)
        {
            return xValues[0];
        }

        if (index >= xTimes.Count)
        {
            return xValues[^1];
        }

        var t0 = xTimes[index - 1];
        var t1 = xTimes[index];
        var fraction = (timestamp - t0).TotalMilliseconds / (t1 - t0).TotalMilliseconds;
        return xValues[index - 1] + (xValues[index] - xValues[index - 1]) * fraction;
    }

    private void AppendPoint(ChartVariable chartVariable, DateTime timestamp, double xVal, double yVal)
    {
        chartVariable.XDateTimeVal.Add(timestamp);
        chartVariable.XVal.Add(xVal);
        chartVariable.YVal.Add(yVal);
        PrunePersistence(chartVariable, timestamp);
    }

    /// <summary>Forgets the X history and all waiting Y samples (X-source changed/removed,
    /// graph cleared) — pairing starts over with the next X sample.</summary>
    private void ResetPairingState()
    {
        xTimes.Clear();
        xValues.Clear();
        foreach (var cv in ChartVariables)
        {
            cv.PendingY.Clear();
        }
    }

    /// <summary>
    /// Radio behaviour for the X-source: exactly one series provides the X coordinate. Clears the
    /// other selections, hides this series (it is the X axis, not a Y curve), labels the X axis and
    /// drops the paired points (they were plotted against a different or no X).
    /// </summary>
    private void SetXSource(ChartVariable xSource)
    {
        // Clearing IsXAxis raises XAxisClearedAction -> ClearXSource turns the former X-source
        // back into a Y series (visible, first Y axis) and wipes the paired points.
        foreach (var other in ChartVariables.Where(cv => !ReferenceEquals(cv, xSource) && cv.IsXAxis).ToList())
        {
            other.IsXAxis = false;
        }

        xSource.IsVisible = false;
        xSource.AxisIndex = 0;

        ResetPairingState();
        foreach (var cv in ChartVariables)
        {
            cv.XDateTimeVal.Clear();
            cv.XVal.Clear();
            cv.YVal.Clear();
        }

        // The X row governs the bottom axis; its label falls back to the X-source signal name.
        ApplyXRowToPlot();

        RememberChartVariableBinding(xSource);
        UpdateRemoveAxisState();
        PlotControl?.Refresh();
    }

    /// <summary>
    /// The series stopped being the X-source (unchecked, or another series took over): it becomes
    /// a normal Y series on the first Y axis again. Paired points are dropped and the graph waits
    /// for a manual X choice — no other series is silently promoted to X.
    /// </summary>
    private void ClearXSource(ChartVariable former)
    {
        former.IsVisible = true;
        former.AxisIndex = 1;

        ResetPairingState();
        foreach (var cv in ChartVariables)
        {
            cv.XDateTimeVal.Clear();
            cv.XVal.Clear();
            cv.YVal.Clear();
        }

        ApplyXRowToPlot();
        RememberChartVariableBinding(former);
        UpdateRemoveAxisState();
        PlotControl?.Refresh();
    }

    /// <summary>Drops points older than <see cref="PersistenceMs"/> from the front of the buffers.</summary>
    private void PrunePersistence(ChartVariable chartVariable, DateTime now)
    {
        var cutoff = now.AddMilliseconds(-PersistenceMs);
        var drop = 0;
        while (drop < chartVariable.XDateTimeVal.Count && chartVariable.XDateTimeVal[drop] < cutoff)
        {
            drop++;
        }

        if (drop > 0)
        {
            chartVariable.XDateTimeVal.RemoveRange(0, drop);
            chartVariable.XVal.RemoveRange(0, drop);
            chartVariable.YVal.RemoveRange(0, drop);
        }
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
    /// Replay seek (ITimelineResetControl): same as the operator's Clear — drops the paired
    /// points, the X pairing history and the waiting Y samples. Without it the points of the
    /// old position would stay in the persistence window until the re-sent history passed
    /// them again, and Y samples buffered before the seek would pair with re-sent X values.
    /// </summary>
    public void ResetTimeline()
    {
        ClearGraph(null!);
    }

    // Throttle the redraw so a fast sample stream does not repaint on every point. The axis
    // fit runs on the same throttle — never per sample (no unthrottled redraw storm).
    private void RefreshThrottled(DateTime timestamp)
    {
        if (lastUpdateTime == DateTime.MinValue
            || timestamp < lastUpdateTime
            || (timestamp - lastUpdateTime).TotalMilliseconds > RefreshThrottleMs)
        {
            UpdateAxesFromData();
            PlotControl.Refresh();
            lastUpdateTime = timestamp;
        }
    }

    /// <summary>
    /// Applies the axes settings to the plot from the live data window. Autoscale = FIT: the
    /// axis follows the min/max of the currently buffered (persistence-window) points, shrinking
    /// as well as growing — an XY graph is a value-vs-value view, an oscilloscope-style fit, not
    /// the grow-only expansion a running time graph uses. Manual axes hold their Min/Max (the X
    /// row is re-applied to the bottom axis here, so runtime edits and toggles take effect
    /// immediately).
    /// </summary>
    private void UpdateAxesFromData()
    {
        // Only an autoscale CHANGE touches the bottom axis. A manual X row is applied when the
        // user edits it (RefreshAction -> ApplyXRowToPlot) and never re-asserted here — a
        // per-refresh slam made runtime mouse zoom/pan and range changes impossible.
        var xRow = GetXAxisRow();
        if (xRow != null
            && xRow.IsAutoScale
            && TryGetDataRange(cv => cv.XVal, _ => true, out var xMin, out var xMax))
        {
            PadRange(ref xMin, ref xMax);
            if (xRow.SetRangeQuiet(xMin, xMax))
            {
                PlotControl.Plot.Axes.SetLimitsX(xRow.Min, xRow.Max);
            }
        }

        // Y axes are real plot axes — writing Min/Max is enough, no SetLimitsY needed.
        for (var i = 1; i < VerticalAxes.Count; i++)
        {
            if (VerticalAxes[i] is not VerticalAxis yAxis || !yAxis.IsAutoScale)
            {
                continue;
            }

            var axisIndex = i;
            if (TryGetDataRange(cv => cv.YVal, cv => cv.AxisIndex == axisIndex, out var yMin, out var yMax))
            {
                PadRange(ref yMin, ref yMax);
                yAxis.SetRangeQuiet(yMin, yMax);
            }
        }
    }

    /// <summary>Min/max over the buffered points of all visible Y series matching the filter.
    /// False when no such points exist (the axis then keeps its current limits).</summary>
    private bool TryGetDataRange(Func<ChartVariable, List<double>> values, Func<ChartVariable, bool> filter, out double min, out double max)
    {
        min = double.MaxValue;
        max = double.MinValue;
        var found = false;

        foreach (var chartVariable in ChartVariables)
        {
            if (chartVariable.IsXAxis || !chartVariable.IsVisible || !filter(chartVariable))
            {
                continue;
            }

            var list = values(chartVariable);
            for (var i = 0; i < list.Count; i++)
            {
                var val = list[i];
                if (val < min) min = val;
                if (val > max) max = val;
                found = true;
            }
        }

        return found;
    }

    /// <summary>5% margin around the fitted range; a degenerate (single-value) range gets a
    /// fixed band so the axis never collapses to zero span. The result is rounded to a tidy
    /// value (the margin absorbs the rounding), so the axes grid shows e.g. 5.25 instead of
    /// 5.2500000000000003 — long raw doubles also blew up the auto-sized Min/Max columns.</summary>
    private static void PadRange(ref double min, ref double max)
    {
        var span = max - min;
        var pad = span > 0
            ? span * 0.05
            : Math.Max(Math.Abs(max) * 0.05, 1.0);
        min -= pad;
        max += pad;

        var digits = Math.Clamp(2 - (int)Math.Floor(Math.Log10(max - min)), 0, 15);
        min = Math.Round(min, digits);
        max = Math.Round(max, digits);
    }

    public override void UpdateThemeSettingsControl(System.Windows.Media.Color bgColor, System.Windows.Media.Color fgColor, int fontSize)
    {
        base.UpdateThemeSettingsControl(bgColor, fgColor, fontSize);

        var scale = PlotControl.DisplayScale;
        plotFontSize = (int)Math.Round(PlotFontSizeMultiplier * fontSize * scale);
        axesFontSize = (int)Math.Round(PlotFontSizeMultiplier * fontSize * scale);
        backgroundColor = bgColor.ToScottPlotColor();
        foregroundColor = fgColor.ToScottPlotColor();
        isThemeApplied = true;

        // Legend
        PlotControl.Plot.Legend.FontSize = axesFontSize;
        PlotControl.Plot.Legend.BackgroundColor = backgroundColor;
        PlotControl.Plot.Legend.FontColor = foregroundColor;
        PlotControl.Plot.Legend.Alignment = Alignment.LowerLeft;

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
        
        
        // Set horizontal axes. In an XY graph X is a chosen signal, not time — label, limits,
        // visibility and custom color come from the X row (ApplyXRowToPlot below); only the
        // theme-driven styling is set here.
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
        // Row 0 is the X row (not a plot axis) — the grid follows the first Y axis.
        PlotControl.Plot.Grid.YAxis = VerticalAxes[1];
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

        // Re-apply the X row on top of the theme styling (custom color, label, limits).
        ApplyXRowToPlot();

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
        // Field initializers are skipped by DataContract deserialization.
        xTimes ??= [];
        xValues ??= [];
        currentColorIndex = Math.Max(currentColorIndex, 0);

        RemoveChartVariableCommand = new RelayCommand<object>(RemoveChartVariable);
        ClearGraphCommand = new RelayCommand<object>(ClearGraph);
        AddAxisCommand = new RelayCommand<object>(AddAxis);
        RemoveAxisCommand = new RelayCommand<object>(RemoveAxis);
        ExportImageCommand = new RelayCommand<object>(ExportImage);
        ExportCsvCommand = new RelayCommand<object>(ExportCsv);
        ZoomToFitCommand = new RelayCommand<object>((i) => PlotControl?.Plot.Axes.AutoScale());

        PlotControl = new WpfPlot();
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

        // The two fixed rows (X + first Y) exist from the moment the control lands on the
        // workspace — the axes are configurable before any signal is bound.
        RestoreVerticalAxes();
        UpdateRemoveAxisState();
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
                chartVariable.LineStyle) { IsXAxis = chartVariable.IsXAxis });
            return;
        }

        binding.SetChartColor(chartVariable.ChartColor);
        binding.LineWidth = chartVariable.LineWidth;
        binding.LineStyle = chartVariable.LineStyle;
        binding.AxisIndex = chartVariable.AxisIndex;
        binding.IsXAxis = chartVariable.IsXAxis;
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
                chartVariable.LineStyle) { IsXAxis = chartVariable.IsXAxis })
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

    // The axes grid always holds at least two fixed rows: row 0 = the X row (configuration
    // driving the bottom axis, never a plot Y axis), row 1 = the first Y axis.
    private void RestoreVerticalAxes()
    {
        if (VerticalAxes.Count > 0)
        {
            return;
        }

        if (VerticalAxisBindings.Count == 0)
        {
            AddXAxisRow(null);
            AddAxis(Edge.Left, 1);
            return;
        }

        // The X row is persisted as the first binding, marked by Edge.Bottom. Older saves
        // (pre X-row format) contain only Y rows — prepend a default X row in front of them.
        var startIndex = 0;
        if (VerticalAxisBindings[0].Edge == Edge.Bottom)
        {
            AddXAxisRow(VerticalAxisBindings[0]);
            startIndex = 1;
        }
        else
        {
            AddXAxisRow(null);
        }

        for (var i = startIndex; i < VerticalAxisBindings.Count; i++)
        {
            AddAxis(VerticalAxisBindings[i], VerticalAxes.Count);
        }

        if (VerticalAxes.Count < 2)
        {
            AddAxis(Edge.Left, 1);
        }
    }

    private int GetValidVerticalAxisIndex(int axisIndex)
    {
        RestoreVerticalAxes();
        if (VerticalAxes.Count < 2)
        {
            return -1;
        }

        // Y series live on rows 1.. — row 0 is the fixed X row.
        return axisIndex >= 1 && axisIndex < VerticalAxes.Count
            ? axisIndex
            : 1;
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

    private VerticalAxis? GetXAxisRow()
    {
        RestoreVerticalAxes();
        return VerticalAxes.OfType<VerticalAxis>().FirstOrDefault(a => a.IsXAxisRow);
    }

    /// <summary>
    /// Propagates the X row onto the bottom axis: label (custom, or the X-source signal name),
    /// visibility, custom color (theme color when unset) and limits.
    /// </summary>
    private void ApplyXRowToPlot()
    {
        if (PlotControl == null)
        {
            return;
        }

        var xRow = GetXAxisRow();
        if (xRow == null)
        {
            return;
        }

        var bottom = PlotControl.Plot.Axes.Bottom;
        var xSource = ChartVariables.FirstOrDefault(cv => cv.IsXAxis);
        bottom.Label.Text = !string.IsNullOrWhiteSpace(xRow.AxisLabel)
            ? xRow.AxisLabel
            : xSource?.Variable != null
                ? GetVariableLegendText(xSource.Variable)
                : string.Empty;
        bottom.IsVisible = xRow.IsVisible;

        if (xRow.AxisColor.HasValue || isThemeApplied)
        {
            var color = xRow.AxisColor?.ToScottPlotColor() ?? foregroundColor;
            bottom.FrameLineStyle.Color = color;
            bottom.MajorTickStyle.Color = color;
            bottom.MinorTickStyle.Color = color;
            bottom.TickLabelStyle.ForeColor = color;
            bottom.Label.ForeColor = color;
        }

        PlotControl.Plot.Axes.SetLimitsX(xRow.Min, xRow.Max);
    }

    private static string GetVariableLegendText(IVariableBase variable)
    {
        var label = string.IsNullOrWhiteSpace(variable.Label)
            ? variable.Name
            : variable.Label;

        return $"{label} ({variable.Id})";
    }


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

        if (selected.IsXAxis)
        {
            // The X-source was removed: no silent promotion of another series — clear the
            // paired points and wait for a manual X choice.
            ResetPairingState();
            foreach (var cv in ChartVariables)
            {
                cv.XDateTimeVal.Clear();
                cv.XVal.Clear();
                cv.YVal.Clear();
            }

            ApplyXRowToPlot();
        }

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
        lastUpdateTime = DateTime.MinValue;

        ResetPairingState();
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

    /// <summary>
    /// Creates the fixed X row (row 0 of the axes grid). Configuration only — never registered
    /// in the plot (no vertical ruler); its values drive the bottom axis via ApplyXRowToPlot.
    /// Persisted as the first VerticalAxisBinding, marked by Edge.Bottom.
    /// </summary>
    private void AddXAxisRow(VerticalAxisBinding? axisBinding)
    {
        var xRow = new VerticalAxis(Edge.Bottom)
        {
            IsXAxisRow = true,
            Name = string.IsNullOrWhiteSpace(axisBinding?.Name) ? "X" : axisBinding.Name,
            AxisLabel = axisBinding?.Label ?? string.Empty,
            IsVisible = axisBinding?.IsVisible ?? true,
            IsAutoScale = axisBinding?.IsAutoScale ?? true,
        };

        if (axisBinding != null && axisBinding.TryGetAxisColor(out var axisColor))
        {
            xRow.AxisColor = axisColor;
        }

        VerticalAxes.Add(xRow);

        xRow.RefreshAction = () =>
        {
            ApplyXRowToPlot();
            PlotControl.Refresh();
        };

        xRow.Minimum = axisBinding?.Minimum ?? -10.0;
        xRow.Maximum = axisBinding?.Maximum ?? 10.0;
        ApplyXRowToPlot();
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
        if (SelectedVerticalAxis is not VerticalAxis axis)
        {
            return;
        }

        // Rows 0 (X) and 1 (first Y) are fixed, and an axis with signals assigned stays too —
        // the menu item is disabled with a tooltip explaining why (UpdateRemoveAxisState);
        // this guard just mirrors that rule for safety.
        var index = VerticalAxes.IndexOf(axis);
        if (index < 2 || ChartVariables.Any(cv => !cv.IsXAxis && cv.AxisIndex == index))
        {
            return;
        }

        PlotControl.Plot.Axes.Remove(axis);
        VerticalAxes.Remove(axis);
        if (AxisNumbers.Count > 0)
        {
            AxisNumbers.RemoveAt(AxisNumbers.Count - 1);
        }

        // Rows above the removed one shifted down by one — re-point their signals (the
        // AxisIndex setter re-assigns ChartSignal.Axes.YAxis via ChangeAxisAction).
        foreach (var chartVariable in ChartVariables.Where(cv => !cv.IsXAxis && cv.AxisIndex > index).ToList())
        {
            chartVariable.AxisIndex--;
        }

        SelectedVerticalAxis = VerticalAxes[Math.Min(index, VerticalAxes.Count - 1)];
        PlotControl.Refresh();
    }

    private void UpdateRemoveAxisState()
    {
        if (SelectedVerticalAxis is not VerticalAxis axis || VerticalAxes.Count == 0)
        {
            CanRemoveSelectedAxis = false;
            RemoveAxisBlockReason = "No axis selected.";
            return;
        }

        var index = VerticalAxes.IndexOf(axis);
        if (index < 2)
        {
            CanRemoveSelectedAxis = false;
            RemoveAxisBlockReason = "The X axis and the first Y axis are fixed and cannot be removed.";
            return;
        }

        var users = ChartVariables
            .Where(cv => !cv.IsXAxis && cv.AxisIndex == index && cv.Variable != null)
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
}
