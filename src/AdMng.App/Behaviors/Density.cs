using System.Windows;
using System.Windows.Controls;

namespace AdMng.App.Behaviors;

/// <summary>
/// Applies a density token of Themes/Tokens.xaml where the Fluent theme reads the size from a resource key of its own
/// (track V, stage V1): <c>b:Density.ColumnHeaderHeight="{StaticResource Ad.Size.ColumnHeader}"</c> on a
/// <see cref="ListView"/> writes the value under the theme's key into the resources of that element, where the dynamic
/// resource references of the column headers below it find it.
/// </summary>
/// <remarks>
/// The token cannot be put under the Fluent key in XAML: a number resource cannot refer to another resource, and a
/// keyed <c>StaticResource</c> element as a dictionary entry (an alias) is compiled into a wrong key table by the
/// markup compiler of the SDK. Set the property on an element that has no resources of its own in XAML - a
/// <c>Resources</c> property element would replace the dictionary this property wrote to.
/// </remarks>
internal static class Density
{
    /// <summary>The Fluent key of the minimum height of a <see cref="GridView"/> column header (the theme's value is 32).</summary>
    public const string ColumnHeaderHeightKey = "GridViewColumnHeaderMinHeight";

    /// <summary>Height of the column headers of the lists in and below the element; not a number = the theme's own.</summary>
    public static readonly DependencyProperty ColumnHeaderHeightProperty = DependencyProperty.RegisterAttached(
        "ColumnHeaderHeight",
        typeof(double),
        typeof(Density),
        new PropertyMetadata(double.NaN, OnColumnHeaderHeightChanged));

    public static double GetColumnHeaderHeight(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(ColumnHeaderHeightProperty);
    }

    public static void SetColumnHeaderHeight(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ColumnHeaderHeightProperty, value);
    }

    private static void OnColumnHeaderHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var height = (double)e.NewValue;
        if (double.IsFinite(height) && height >= 0)
        {
            element.Resources[ColumnHeaderHeightKey] = height;
        }
        else
        {
            element.Resources.Remove(ColumnHeaderHeightKey);
        }
    }
}
