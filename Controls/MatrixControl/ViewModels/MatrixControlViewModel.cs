using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Windows.Media.Imaging;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.WpfComm;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.MatrixControl.ViewModels;

/// <summary>
/// Matrix control = tabs, one per MatrixVariable (value block / curve / map), each with its own
/// table, settings and edit state (<see cref="MatrixPageViewModel"/>; Calibro ParamControl
/// pattern, Radek 2026-09-27). Dropping another matrix variable adds a tab; the tab's context
/// menu moves or removes it. The control itself keeps only what is common: the host delegates
/// (read / write / logger), Write on Enter and the lock. Data of hidden tabs keep updating from
/// the polls; their grids are built when the tab is first shown.
/// Projects saved before the tabs (one variable, settings on the control) load as one tab.
/// </summary>
[DataContract]
public class MatrixControlViewModel : ControlBase, IMatrixVariableWriteControl, IVariableReadControl
{
    public MatrixControlViewModel()
    {
        Width = 320;
        Height = 180;
    }

    #region Pages

    /// <summary>Tabs in display order; persisted (variable reference + settings per tab).</summary>
    [DataMember]
    public ObservableCollection<MatrixPageViewModel> Pages { get; private set; } = [];

    [IgnoreDataMember]
    public MatrixPageViewModel? SelectedPage
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPages));
        }
    }

    [IgnoreDataMember]
    public bool HasPages => Pages.Count > 0;

    /// <summary>Index of the selected tab, persisted so the project opens on the same map.</summary>
    [DataMember]
    public int SelectedPageIndex
    {
        get => SelectedPage != null ? Pages.IndexOf(SelectedPage) : field;
        set => field = value;
    }

    // Lazy kvuli deserializaci (DataContractSerializer nevola konstruktor)
    [IgnoreDataMember]
    public RelayCommand<object> RemovePageCommand => field ??= new RelayCommand<object>(p => { if (p is MatrixPageViewModel page) RemovePage(page); });

    [IgnoreDataMember]
    public RelayCommand<object> MovePageLeftCommand => field ??= new RelayCommand<object>(p => { if (p is MatrixPageViewModel page) MovePage(page, -1); });

    [IgnoreDataMember]
    public RelayCommand<object> MovePageRightCommand => field ??= new RelayCommand<object>(p => { if (p is MatrixPageViewModel page) MovePage(page, +1); });

    private MatrixPageViewModel? FindPage(IVariableBase variable)
    {
        return Pages.FirstOrDefault(p => ReferenceEquals(p.Variable, variable))
               ?? Pages.FirstOrDefault(p => IsVariableReferenceMatch(p.VariableReference, variable));
    }

    /// <summary>Tab context menu "Remove": the variable leaves the control (host prunes its subscription).</summary>
    private void RemovePage(MatrixPageViewModel page)
    {
        var index = Pages.IndexOf(page);
        if (index < 0)
        {
            return;
        }

        Pages.RemoveAt(index);
        LinkedVariables.RemoveAll(reference => reference == page.VariableReference);
        foreach (var variable in Variables.Where(v => IsVariableReferenceMatch(page.VariableReference, v)).ToList())
        {
            Variables.Remove(variable);
        }

        if (ReferenceEquals(SelectedPage, page))
        {
            SelectedPage = Pages.Count > 0 ? Pages[Math.Min(index, Pages.Count - 1)] : null;
        }

        OnPropertyChanged(nameof(HasPages));
        RaiseVariableBindingsChanged();
    }

    /// <summary>Tab context menu "Move left / right": tab order and the persisted reference order.</summary>
    private void MovePage(MatrixPageViewModel page, int delta)
    {
        var index = Pages.IndexOf(page);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Pages.Count)
        {
            return;
        }

        Pages.Move(index, target);
        var referenceIndex = LinkedVariables.IndexOf(page.VariableReference);
        var referenceTarget = referenceIndex + delta;
        if (referenceIndex >= 0 && referenceTarget >= 0 && referenceTarget < LinkedVariables.Count)
        {
            LinkedVariables.RemoveAt(referenceIndex);
            LinkedVariables.Insert(referenceTarget, page.VariableReference);
        }
    }

    #endregion

    #region Control-level settings

    /// <summary>Write mode: Enter writes the edited cell immediately instead of leaving it pending
    /// for the Write button (same option as in the Single-Signal and Watch Table controls).</summary>
    [DataMember]
    public bool WriteOnEnter
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (Pages == null)
            {
                return;
            }

            foreach (var page in Pages)
            {
                page.NotifyWriteOnEnterChanged();
            }
        }
    }

    // Settings of projects saved before the tabs (one variable, settings on the control): read
    // once, applied to the first tab that is created for the loaded reference, never written
    // back (EmitDefaultValue = false with null getters). "AutoRead" is older still and dropped.
    [DataMember(Name = "RefreshTime", EmitDefaultValue = false)]
    private int? LegacyRefreshTime { get => null; set => legacyRefreshTime = value; }

    [DataMember(Name = "CellWidth", EmitDefaultValue = false)]
    private int? LegacyCellWidth { get => null; set => legacyCellWidth = value; }

    [DataMember(Name = "IsWriteMode", EmitDefaultValue = false)]
    private bool? LegacyIsWriteMode { get => null; set => legacyIsWriteMode = value; }

    [DataMember(Name = "IsHeatmapEnabled", EmitDefaultValue = false)]
    private bool? LegacyIsHeatmapEnabled { get => null; set => legacyIsHeatmapEnabled = value; }

    [DataMember(Name = "HeatMinimum", EmitDefaultValue = false)]
    private double? LegacyHeatMinimum { get => null; set => legacyHeatMinimum = value; }

    [DataMember(Name = "HeatMaximum", EmitDefaultValue = false)]
    private double? LegacyHeatMaximum { get => null; set => legacyHeatMaximum = value; }

    [DataMember(Name = "HeatSpectrum", EmitDefaultValue = false)]
    private MatrixSpectrum? LegacyHeatSpectrum { get => null; set => legacyHeatSpectrum = value; }

    [DataMember(Name = "AutoRead", EmitDefaultValue = false)]
    private bool? LegacyAutoRead { get => null; set { } }

    [IgnoreDataMember] private int? legacyRefreshTime;
    [IgnoreDataMember] private int? legacyCellWidth;
    [IgnoreDataMember] private bool? legacyIsWriteMode;
    [IgnoreDataMember] private bool? legacyIsHeatmapEnabled;
    [IgnoreDataMember] private double? legacyHeatMinimum;
    [IgnoreDataMember] private double? legacyHeatMaximum;
    [IgnoreDataMember] private MatrixSpectrum? legacyHeatSpectrum;

    private void ApplyLegacySettings(MatrixPageViewModel page)
    {
        if (legacyRefreshTime is { } refresh) page.RefreshTime = refresh;
        if (legacyCellWidth is { } width) page.CellWidth = width;
        if (legacyIsHeatmapEnabled is { } heat) page.IsHeatmapEnabled = heat;
        if (legacyHeatMinimum is { } min) page.HeatMinimum = min;
        if (legacyHeatMaximum is { } max) page.HeatMaximum = max;
        if (legacyHeatSpectrum is { } spectrum) page.HeatSpectrum = spectrum;
        if (legacyIsWriteMode is { } writeMode) page.IsWriteMode = writeMode;
        legacyRefreshTime = null;
        legacyCellWidth = null;
        legacyIsHeatmapEnabled = null;
        legacyHeatMinimum = null;
        legacyHeatMaximum = null;
        legacyHeatSpectrum = null;
        legacyIsWriteMode = null;
    }

    #endregion

    #region Host delegates (IMatrixVariableWriteControl, IVariableReadControl)

    [IgnoreDataMember]
    public Func<IVariableBase, bool>? CanWriteVariableProvider { get; set; }

    /// <summary>Scalar write delegate of the base contract; unused by this control.</summary>
    [IgnoreDataMember]
    public Func<IVariableBase, double, Task<bool>>? WriteVariableEngValueAsync { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, MatrixSectionKind, int, double, Task<bool>>? WriteMatrixElementEngValueAsync { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, IReadOnlyList<MatrixElementWrite>, Task<bool>>? WriteMatrixElementsEngValueAsync { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, bool>? CanReadVariableProvider { get; set; }

    [IgnoreDataMember]
    public Func<IVariableBase, Task<bool>>? ReadVariableAsync { get; set; }

    public void RefreshWriteCapability()
    {
        foreach (var page in Pages)
        {
            page.RefreshWriteCapability();
        }
    }

    public void RefreshReadCapability()
    {
        foreach (var page in Pages)
        {
            page.RefreshReadCapability();
        }
    }

    #endregion

    #region Derived properties

    public override string ControlName => "MatrixControl";
    public override string Label => "Matrix";
    public override BitmapImage Icon => ImageGetter.GetBitmapImage("Icons/MatrixControl.png");
    public override string Description => "Tabs of matrix variables: value blocks, curves and calibration maps with editable cells.";

    #endregion

    #region ControlBase

    public override Task UpdateVariableValueAsync(IVariableBase protVariable)
    {
        if (protVariable is MatrixVariable matrixVariable)
        {
            FindPage(protVariable)?.Update(matrixVariable);
        }

        return Task.CompletedTask;
    }

    // Tabulka zobrazuje vyhradne matrix promenne (skalary patri Signal/Gauge/WatchTable)
    public override bool CanBindVariable(IVariableBase variable) => variable is MatrixVariable;

    /// <summary>
    /// A dropped or loaded variable: its tab (existing one from the project, or a new one) gets
    /// the variable. Another matrix adds a tab instead of replacing the shown one.
    /// </summary>
    public override void BindVariable(IVariableBase protVariable)
    {
        if (protVariable is not MatrixVariable matrixVariable)
        {
            return;
        }

        var page = FindPage(protVariable);
        if (page != null && ReferenceEquals(page.Variable, protVariable))
        {
            return;
        }

        if (page == null)
        {
            page = new MatrixPageViewModel { Owner = this, VariableReference = GetVariableReference(protVariable) };
            ApplyLegacySettings(page);
            Pages.Add(page);
            OnPropertyChanged(nameof(HasPages));
        }

        // Rebind after a protocol change: the old variable object of the same reference goes.
        foreach (var old in Variables.Where(v => !ReferenceEquals(v, protVariable) && IsVariableReferenceMatch(page.VariableReference, v)).ToList())
        {
            Variables.Remove(old);
        }

        RememberVariableBinding(protVariable);
        Variables.Add(protVariable);
        page.Attach(matrixVariable);

        SelectedPage ??= Pages.Count > SelectedPageIndex && SelectedPageIndex >= 0 ? Pages[SelectedPageIndex] : page;
    }

    public override void RefreshVariableBinding(IVariableBase variable)
    {
        base.RefreshVariableBinding(variable);
        FindPage(variable)?.RefreshBinding();
    }

    protected override void OnEditToRun()
    {
        foreach (var page in Pages)
        {
            page.OnRunStart();
        }
    }

    #endregion

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        Variables ??= [];
        LinkedVariables ??= [];
        Pages ??= [];
        foreach (var page in Pages)
        {
            page.Owner = this;
        }
    }
}
