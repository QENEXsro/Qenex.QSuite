using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Telerik.Windows.Controls;
using GridViewLength = Telerik.Windows.Controls.GridViewLength;

namespace Qenex.QSuite.Controls.MatrixControl.Behaviors;

/// <summary>
/// Attached behavior of the matrix RadGridView (view-layer plumbing only, no application logic):
/// creates one column per table column (bound to Cells[i] of the row, shared cell / edit
/// templates and cell style from XAML), keeps the column widths at the CellWidth setting and
/// installs the keyboard command provider (Tab = next cell in edit, Enter = commit the cell,
/// Ctrl+V = paste through the view model). Pattern: WatchTableControl\Behaviors\ColumnLayoutBehavior.
/// </summary>
public static class MatrixGridBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MatrixGridBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty ColumnCountProperty = DependencyProperty.RegisterAttached(
        "ColumnCount", typeof(int), typeof(MatrixGridBehavior), new PropertyMetadata(0, OnColumnsChanged));

    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.RegisterAttached(
        "CellWidth", typeof(double), typeof(MatrixGridBehavior), new PropertyMetadata(60d, OnCellWidthChanged));

    public static readonly DependencyProperty CellTemplateProperty = DependencyProperty.RegisterAttached(
        "CellTemplate", typeof(DataTemplate), typeof(MatrixGridBehavior), new PropertyMetadata(null, OnColumnsChanged));

    public static readonly DependencyProperty CellEditTemplateProperty = DependencyProperty.RegisterAttached(
        "CellEditTemplate", typeof(DataTemplate), typeof(MatrixGridBehavior), new PropertyMetadata(null, OnColumnsChanged));

    public static readonly DependencyProperty CellStyleProperty = DependencyProperty.RegisterAttached(
        "CellStyle", typeof(Style), typeof(MatrixGridBehavior), new PropertyMetadata(null, OnColumnsChanged));

    /// <summary>Editor text box: select the whole text when it receives the keyboard focus
    /// (typing replaces the value, as in the former inline edit boxes / Excel).</summary>
    public static readonly DependencyProperty SelectAllOnFocusProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnFocus", typeof(bool), typeof(MatrixGridBehavior), new PropertyMetadata(false, OnSelectAllOnFocusChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static int GetColumnCount(DependencyObject obj) => (int)obj.GetValue(ColumnCountProperty);
    public static void SetColumnCount(DependencyObject obj, int value) => obj.SetValue(ColumnCountProperty, value);

    public static double GetCellWidth(DependencyObject obj) => (double)obj.GetValue(CellWidthProperty);
    public static void SetCellWidth(DependencyObject obj, double value) => obj.SetValue(CellWidthProperty, value);

    public static DataTemplate? GetCellTemplate(DependencyObject obj) => (DataTemplate?)obj.GetValue(CellTemplateProperty);
    public static void SetCellTemplate(DependencyObject obj, DataTemplate? value) => obj.SetValue(CellTemplateProperty, value);

    public static DataTemplate? GetCellEditTemplate(DependencyObject obj) => (DataTemplate?)obj.GetValue(CellEditTemplateProperty);
    public static void SetCellEditTemplate(DependencyObject obj, DataTemplate? value) => obj.SetValue(CellEditTemplateProperty, value);

    public static Style? GetCellStyle(DependencyObject obj) => (Style?)obj.GetValue(CellStyleProperty);
    public static void SetCellStyle(DependencyObject obj, Style? value) => obj.SetValue(CellStyleProperty, value);

    public static bool GetSelectAllOnFocus(DependencyObject obj) => (bool)obj.GetValue(SelectAllOnFocusProperty);
    public static void SetSelectAllOnFocus(DependencyObject obj, bool value) => obj.SetValue(SelectAllOnFocusProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RadGridView grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            grid.KeyboardCommandProvider = new MatrixKeyboardCommandProvider(grid);
            RebuildColumns(grid);
        }
        else
        {
            grid.Columns.Clear();
        }
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RadGridView grid && GetIsEnabled(grid))
        {
            RebuildColumns(grid);
        }
    }

    private static void OnCellWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RadGridView grid || !GetIsEnabled(grid))
        {
            return;
        }

        var width = new GridViewLength(ColumnWidth(grid));
        foreach (var column in grid.Columns)
        {
            column.Width = width;
        }
    }

    private static void RebuildColumns(RadGridView grid)
    {
        var count = GetColumnCount(grid);
        var width = new GridViewLength(ColumnWidth(grid));
        var cellTemplate = GetCellTemplate(grid);
        var editTemplate = GetCellEditTemplate(grid);
        var cellStyle = GetCellStyle(grid);

        grid.Columns.Clear();
        for (var i = 0; i < count; i++)
        {
            grid.Columns.Add(new GridViewDataColumn
            {
                UniqueName = $"C{i}",
                DataMemberBinding = new Binding($"Cells[{i}]"),
                Width = width,
                IsSortable = false,
                IsFilterable = false,
                IsGroupable = false,
                IsResizable = false,
                IsReorderable = false,
                TextAlignment = TextAlignment.Right,
                CellTemplate = cellTemplate,
                CellEditTemplate = editTemplate,
                CellStyle = cellStyle
            });
        }
    }

    private static double ColumnWidth(RadGridView grid)
    {
        var width = GetCellWidth(grid);
        return double.IsNaN(width) || width < 20 ? 20 : width;
    }

    private static void OnSelectAllOnFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            textBox.GotKeyboardFocus += SelectAllOnFocus;
        }
        else
        {
            textBox.GotKeyboardFocus -= SelectAllOnFocus;
        }
    }

    private static void SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ((TextBox)sender).SelectAll();
    }
}
