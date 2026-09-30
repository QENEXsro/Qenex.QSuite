using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;
using Telerik.Windows.Controls;

namespace Qenex.QInsight.Behaviors;

/// <summary>
/// Scrolls the workspace with the mouse wheel: vertically, or horizontally with Shift held.
/// RadDiagram wires the wheel to zoom only (disabled in the workspace) and has no ScrollViewer —
/// just two standalone scrollbars — so the wheel moves the matching scrollbar, exactly as
/// dragging its thumb does. The diagram shows a scrollbar only when the content does not fit
/// in that direction, so nothing happens otherwise.
/// Controls that use the wheel themselves (tables, graphs) mark the event handled and keep it.
/// </summary>
public class DiagramMouseWheelScrollBehavior : Behavior<RadDiagram>
{
    // Template part names of the diagram's scrollbars.
    private const string VerticalScrollbarPartName = "VerticalScrollbar";
    private const string HorizontalScrollbarPartName = "HorizontalScrollbar";

    // Same distance per wheel notch as the WPF ScrollViewer.
    private const double PixelsPerNotch = 48;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.MouseWheel += OnMouseWheel;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.MouseWheel -= OnMouseWheel;
        base.OnDetaching();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Other modifiers are left alone (Ctrl + wheel is the usual zoom gesture).
        var scrollbarPartName = Keyboard.Modifiers switch
        {
            ModifierKeys.None => VerticalScrollbarPartName,
            ModifierKeys.Shift => HorizontalScrollbarPartName,
            _ => null
        };

        if (scrollbarPartName != null && Scroll(scrollbarPartName, e.Delta))
        {
            e.Handled = true;
        }
    }

    private bool Scroll(string scrollbarPartName, int wheelDelta)
    {
        if (AssociatedObject.Template?.FindName(scrollbarPartName, AssociatedObject)
            is not ScrollBar { Visibility: Visibility.Visible } scrollBar)
        {
            return false;
        }

        // The scrollbar clamps the value to its range, so the limits match dragging the thumb.
        scrollBar.Value -= wheelDelta / (double)Mouse.MouseWheelDeltaForOneLine * PixelsPerNotch;
        return true;
    }
}
