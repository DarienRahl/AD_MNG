using System.Windows;

namespace AdMng.App.Behaviors;

/// <summary>
/// View-only state shared by all documents of a window, as inherited attached properties: set it on the window and
/// read it anywhere below with <c>{Binding (b:ViewState.ShowFilterChips), RelativeSource={RelativeSource Self}}</c>.
/// </summary>
public static class ViewState
{
    /// <summary>Whether the quick filter chips row of object lists is shown (toolbar toggle).</summary>
    public static readonly DependencyProperty ShowFilterChipsProperty = DependencyProperty.RegisterAttached(
        "ShowFilterChips", typeof(bool), typeof(ViewState),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Set on a tree item while dragged objects may be dropped on it (drop highlight).</summary>
    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsDropTarget", typeof(bool), typeof(ViewState), new FrameworkPropertyMetadata(false));

    public static bool GetShowFilterChips(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ShowFilterChipsProperty);
    }

    public static void SetShowFilterChips(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShowFilterChipsProperty, value);
    }

    public static bool GetIsDropTarget(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsDropTargetProperty);
    }

    public static void SetIsDropTarget(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsDropTargetProperty, value);
    }
}
