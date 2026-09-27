using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.Primitives;

namespace Qenex.QSuite.Controls.MatrixControl.Behaviors;

/// <summary>
/// Attached behavior of the tab control (view-layer plumbing only): the strip's scroll viewer
/// is left-aligned so it is only as wide as the tabs. The empty part of the strip right of the
/// last tab then belongs to the control underneath (WPF ScrollViewer hit-tests its whole area
/// even without a background): the shape can be dragged in the workspace there and the
/// control's context menu opens there, while the tabs keep their own menu (Radek 2026-09-27).
/// </summary>
public static class MatrixTabsBehavior
{
    public static readonly DependencyProperty StripFitsTabsProperty = DependencyProperty.RegisterAttached(
        "StripFitsTabs", typeof(bool), typeof(MatrixTabsBehavior), new PropertyMetadata(false, OnStripFitsTabsChanged));

    public static bool GetStripFitsTabs(DependencyObject obj) => (bool)obj.GetValue(StripFitsTabsProperty);
    public static void SetStripFitsTabs(DependencyObject obj, bool value) => obj.SetValue(StripFitsTabsProperty, value);

    private static void OnStripFitsTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RadTabControl tabs)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            tabs.Loaded += OnTabsLoaded;
            if (tabs.IsLoaded)
            {
                FitStrip(tabs);
            }
        }
        else
        {
            tabs.Loaded -= OnTabsLoaded;
        }
    }

    private static void OnTabsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadTabControl tabs)
        {
            FitStrip(tabs);
        }
    }

    private static void FitStrip(RadTabControl tabs)
    {
        tabs.ApplyTemplate();
        var panel = FindVisual<TabWrapPanel>(tabs);
        DependencyObject? node = panel;
        while (node != null && node is not ScrollViewer)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is ScrollViewer stripViewer)
        {
            stripViewer.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found)
        {
            return found;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = FindVisual<T>(VisualTreeHelper.GetChild(root, i));
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }
}
