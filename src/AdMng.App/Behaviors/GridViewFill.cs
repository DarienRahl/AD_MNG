using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdMng.App.Behaviors;

/// <summary>
/// Gives one column of a <see cref="ListView"/>'s <see cref="GridView"/> the width the list has left to the right of the
/// columns before it, between <see cref="MinWidthProperty"/> and <see cref="MaxWidthProperty"/> – a star column for the
/// column that matters most (the event title of the Watchdog feed: <c>b:GridViewFill.Column="2"</c>). The width follows
/// the visible width of the list (window resized, splitter dragged, vertical scroll bar shown); once the operator drags
/// that column's header gripper, their width stays.
/// </summary>
internal static class GridViewFill
{
    /// <summary>Index of the column that fills (-1: none).</summary>
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "Column",
        typeof(int),
        typeof(GridViewFill),
        new PropertyMetadata(-1, OnSettingChanged));

    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.RegisterAttached(
        "MinWidth",
        typeof(double),
        typeof(GridViewFill),
        new PropertyMetadata(120d, OnSettingChanged));

    public static readonly DependencyProperty MaxWidthProperty = DependencyProperty.RegisterAttached(
        "MaxWidth",
        typeof(double),
        typeof(GridViewFill),
        new PropertyMetadata(double.PositiveInfinity, OnSettingChanged));

    /// <summary>The width set last (NaN: none yet); a different column width means the operator resized it.</summary>
    private static readonly DependencyProperty AppliedWidthProperty = DependencyProperty.RegisterAttached(
        "AppliedWidth",
        typeof(double),
        typeof(GridViewFill),
        new PropertyMetadata(double.NaN));

    /// <summary>Room kept free right of the column (row padding, rounding) so it never starts a horizontal scroll alone.</summary>
    private const double Reserve = 4;

    public static int GetColumn(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)element.GetValue(ColumnProperty);
    }

    public static void SetColumn(DependencyObject element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ColumnProperty, value);
    }

    public static double GetMinWidth(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(MinWidthProperty);
    }

    public static void SetMinWidth(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(MinWidthProperty, value);
    }

    public static double GetMaxWidth(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(MaxWidthProperty);
    }

    public static void SetMaxWidth(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(MaxWidthProperty, value);
    }

    /// <summary>The fill width for a visible width and the widths of the columns before the fill column.</summary>
    public static double FillWidth(double visibleWidth, double widthBefore, double minWidth, double maxWidth) =>
        Math.Floor(Math.Clamp(visibleWidth - widthBefore - Reserve, minWidth, Math.Max(minWidth, maxWidth)));

    private static void OnSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        list.Loaded -= OnLoaded;
        list.SizeChanged -= OnSizeChanged;
        list.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
        if (GetColumn(list) < 0)
        {
            return;
        }

        list.Loaded += OnLoaded;
        list.SizeChanged += OnSizeChanged;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
        Apply(list);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((ListView)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((ListView)sender);

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The vertical scroll bar appeared or disappeared (rows loaded or removed): the visible width changed.
        if (e.ViewportWidthChange != 0 && ReferenceEquals(e.OriginalSource, FindScrollViewer((ListView)sender)))
        {
            Apply((ListView)sender);
        }
    }

    private static void Apply(ListView list)
    {
        var index = GetColumn(list);
        if (index < 0 || list.View is not GridView view || index >= view.Columns.Count || !list.IsLoaded)
        {
            return;
        }

        var column = view.Columns[index];
        var applied = (double)list.GetValue(AppliedWidthProperty);
        if (!double.IsNaN(applied) && Math.Abs(column.Width - applied) > 0.5)
        {
            // The operator resized the column: their width wins from now on.
            return;
        }

        var visible = FindScrollViewer(list) is { ViewportWidth: > 0 } viewer ? viewer.ViewportWidth : list.ActualWidth;
        if (visible <= 0)
        {
            return;
        }

        var before = view.Columns.Take(index).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        var width = FillWidth(visible, before, GetMinWidth(list), GetMaxWidth(list));
        list.SetValue(AppliedWidthProperty, width);
        if (Math.Abs(column.Width - width) > 0.5 || double.IsNaN(column.Width))
        {
            column.Width = width;
        }
    }

    /// <summary>The list's own scroll viewer (the first one in its template).</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
