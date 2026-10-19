using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AdMng.Presentation.Infrastructure;

namespace AdMng.App.Behaviors;

/// <summary>
/// Fits the columns of a <see cref="ListView"/>'s <see cref="GridView"/> into the list's visible width with
/// <see cref="ColumnFit"/>, so the list does not scroll sideways: <c>b:GridViewColumnFit.IsEnabled="True"</c> on the list and,
/// on each <see cref="GridViewColumn"/>, its <see cref="MinWidthProperty"/>, a <see cref="StarProperty"/> weight for the
/// columns that share the rest (the event title) and a <see cref="PriorityProperty"/> for optional columns (shown in that
/// order while their minimum fits, hidden – removed from the view – otherwise). The column's XAML <c>Width</c> is its
/// preferred width. The fit follows the list's visible width: window resized, a neighbouring <see cref="GridSplitter"/>
/// dragged, the vertical scroll bar shown or hidden (it overlays the last column in the Fluent theme, so its width is kept
/// free). A column whose header gripper the operator dragged (or double-clicked: sized to its content) keeps the operator's
/// width from then on: the fit waits while the gripper is dragged (refitting under the pointer would move the gripper away
/// from it) and the star columns give way once it is released – or once the double-clicked column has its content width,
/// and again whenever that width grows.
/// </summary>
internal static class GridViewColumnFit
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(GridViewColumnFit),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>The narrowest width at which a column is still useful (default 40).</summary>
    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.RegisterAttached(
        "MinWidth",
        typeof(double),
        typeof(GridViewColumnFit),
        new PropertyMetadata(40d));

    /// <summary>Above zero: the column shares the width the other columns leave, by this weight.</summary>
    public static readonly DependencyProperty StarProperty = DependencyProperty.RegisterAttached(
        "Star",
        typeof(double),
        typeof(GridViewColumnFit),
        new PropertyMetadata(0d));

    /// <summary>0 (default): always shown; 1, 2, …: optional, shown in this order while the list has room for its minimum.</summary>
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority",
        typeof(int),
        typeof(GridViewColumnFit),
        new PropertyMetadata(0));

    /// <summary>Every column of the view in its XAML order (shown or not), taken when the list is first fitted.</summary>
    private static readonly DependencyProperty AllColumnsProperty = DependencyProperty.RegisterAttached(
        "AllColumns",
        typeof(IReadOnlyList<GridViewColumn>),
        typeof(GridViewColumnFit),
        new PropertyMetadata(null));

    /// <summary>A column header gripper is being dragged: the fit waits for its release.</summary>
    private static readonly DependencyProperty IsDraggingProperty = DependencyProperty.RegisterAttached(
        "IsDragging",
        typeof(bool),
        typeof(GridViewColumnFit),
        new PropertyMetadata(false));

    /// <summary>The column's XAML width.</summary>
    private static readonly DependencyProperty PreferredWidthProperty = DependencyProperty.RegisterAttached(
        "PreferredWidth",
        typeof(double),
        typeof(GridViewColumnFit),
        new PropertyMetadata(double.NaN));

    /// <summary>The width set last (NaN: none yet); a different column width means the operator resized it.</summary>
    private static readonly DependencyProperty AppliedWidthProperty = DependencyProperty.RegisterAttached(
        "AppliedWidth",
        typeof(double),
        typeof(GridViewColumnFit),
        new PropertyMetadata(double.NaN));

    /// <summary>The operator resized the column: its width is theirs.</summary>
    private static readonly DependencyProperty IsUserSizedProperty = DependencyProperty.RegisterAttached(
        "IsUserSized",
        typeof(bool),
        typeof(GridViewColumnFit),
        new PropertyMetadata(false));

    /// <summary>Row border and padding until a row is realized (the Watchdog rows: 1 + 2 px on either side).</summary>
    private const double DefaultRowChrome = 8;

    /// <summary>Kept free for layout rounding.</summary>
    private const double RoundingReserve = 1;

    private static readonly ScrollChangedEventHandler ScrollChangedHandler = OnScrollChanged;
    private static readonly DragStartedEventHandler DragStartedHandler = OnGripperDragStarted;
    private static readonly DragCompletedEventHandler DragCompletedHandler = OnGripperDragCompleted;

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
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

    public static double GetStar(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(StarProperty);
    }

    public static void SetStar(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(StarProperty, value);
    }

    public static int GetPriority(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)element.GetValue(PriorityProperty);
    }

    public static void SetPriority(DependencyObject element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PriorityProperty, value);
    }

    /// <summary>Every column of the fitted list in its XAML order, also the hidden ones (UI tests).</summary>
    public static IReadOnlyList<GridViewColumn> AllColumns(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.GetValue(AllColumnsProperty) as IReadOnlyList<GridViewColumn>
            ?? (list.View is GridView view ? [.. view.Columns] : []);
    }

    /// <summary>Fits the columns now (after a change the list cannot see, e.g. in tests).</summary>
    public static void Refresh(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (GetIsEnabled(list))
        {
            Apply(list);
        }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        list.Loaded -= OnLoaded;
        list.SizeChanged -= OnSizeChanged;
        list.RemoveHandler(ScrollViewer.ScrollChangedEvent, ScrollChangedHandler);
        list.RemoveHandler(Thumb.DragStartedEvent, DragStartedHandler);
        list.RemoveHandler(Thumb.DragCompletedEvent, DragCompletedHandler);
        if (e.NewValue is not true)
        {
            return;
        }

        list.Loaded += OnLoaded;
        list.SizeChanged += OnSizeChanged;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, ScrollChangedHandler);

        // The header marks the drag of its gripper handled.
        list.AddHandler(Thumb.DragStartedEvent, DragStartedHandler, handledEventsToo: true);
        list.AddHandler(Thumb.DragCompletedEvent, DragCompletedHandler, handledEventsToo: true);
        Apply(list);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((ListView)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((ListView)sender);

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The visible width changed, or rows came or went (the vertical scroll bar may have appeared or disappeared, the
        // first realized row tells the row chrome). Column widths changing the extent width are the fit's own doing –
        // unless a column's width changed without a gripper drag (a double click on the gripper sizes the column to its
        // content: no drag starts or completes) or an auto-sized column grew with the rows scrolled into view.
        var list = (ListView)sender;
        if ((e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0
                || (e.ExtentWidthChange != 0 && NeedsRefit(list)))
            && ReferenceEquals(e.OriginalSource, FindScrollViewer(list)))
        {
            Apply(list);
        }
    }

    /// <summary>
    /// A shown column's width is not the one the last fit saw: resized by the operator outside a gripper drag, or an
    /// operator's auto-sized column (gripper double click) whose content width changed.
    /// </summary>
    private static bool NeedsRefit(ListView list) =>
        list.View is GridView view
        && !(bool)list.GetValue(IsDraggingProperty)
        && view.Columns.Any(column => column.GetValue(AppliedWidthProperty) is double applied
            && !double.IsNaN(applied)
            && Math.Abs(CurrentWidth(column) - applied) > 0.5);

    /// <summary>The column's width as the operator set it: its <c>Width</c>, or its actual width while it sizes to content.</summary>
    private static double CurrentWidth(GridViewColumn column) => double.IsNaN(column.Width) ? column.ActualWidth : column.Width;

    private static void OnGripperDragStarted(object sender, DragStartedEventArgs e)
    {
        if (e.OriginalSource is Thumb { TemplatedParent: GridViewColumnHeader })
        {
            ((ListView)sender).SetValue(IsDraggingProperty, true);
        }
    }

    private static void OnGripperDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is Thumb { TemplatedParent: GridViewColumnHeader })
        {
            var list = (ListView)sender;
            list.SetValue(IsDraggingProperty, false);
            Apply(list);
        }
    }

    private static void Apply(ListView list)
    {
        if (!list.IsLoaded || list.View is not GridView view || (bool)list.GetValue(IsDraggingProperty))
        {
            return;
        }

        var all = ColumnsOf(list, view);
        var viewer = FindScrollViewer(list);
        var viewport = viewer is { ViewportWidth: > 0 } ? viewer.ViewportWidth : list.ActualWidth;
        if (viewport <= 0 || all.Count == 0)
        {
            return;
        }

        var available = viewport - RowChrome(viewer) - OverlayScrollBarWidth(viewer) - RoundingReserve;
        MarkUserSized(view.Columns);
        var widths = ColumnFit.Fit(available, [.. all.Select(Spec)]);
        Show(view, all, widths);
        for (var i = 0; i < all.Count; i++)
        {
            var column = all[i];
            if (widths[i] is not { } width)
            {
                continue;
            }

            // For an operator's column: its width as this fit saw it (an auto-sized one that grows refits the rest).
            column.SetValue(AppliedWidthProperty, width);
            if ((bool)column.GetValue(IsUserSizedProperty))
            {
                continue;
            }

            if (double.IsNaN(column.Width) || Math.Abs(column.Width - width) > 0.5)
            {
                column.Width = width;
            }
        }
    }

    /// <summary>All columns in XAML order; the first call remembers them and their XAML widths.</summary>
    private static IReadOnlyList<GridViewColumn> ColumnsOf(ListView list, GridView view)
    {
        if (list.GetValue(AllColumnsProperty) is IReadOnlyList<GridViewColumn> known)
        {
            return known;
        }

        List<GridViewColumn> all = [.. view.Columns];
        foreach (var column in all)
        {
            column.SetValue(PreferredWidthProperty, double.IsNaN(column.Width) ? Math.Max(GetMinWidth(column), column.ActualWidth) : column.Width);
        }

        list.SetValue(AllColumnsProperty, all);
        return all;
    }

    /// <summary>A shown column whose width is not the one set last was resized by the operator (gripper, double click).</summary>
    private static void MarkUserSized(IEnumerable<GridViewColumn> shown)
    {
        foreach (var column in shown)
        {
            var applied = (double)column.GetValue(AppliedWidthProperty);
            if (!double.IsNaN(applied) && (double.IsNaN(column.Width) || Math.Abs(column.Width - applied) > 0.5))
            {
                column.SetValue(IsUserSizedProperty, true);
            }
        }
    }

    private static FitColumn Spec(GridViewColumn column)
    {
        if ((bool)column.GetValue(IsUserSizedProperty))
        {
            return new FitColumn(double.IsNaN(column.Width) ? column.ActualWidth : column.Width, 0, IsFixed: true);
        }

        var minimum = GetMinWidth(column);
        var preferred = (double)column.GetValue(PreferredWidthProperty);
        return new FitColumn(
            double.IsNaN(preferred) ? minimum : preferred,
            minimum,
            GetStar(column),
            GetPriority(column));
    }

    /// <summary>Shows exactly the columns with a width, in their XAML order (as few collection changes as possible).</summary>
    private static void Show(GridView view, IReadOnlyList<GridViewColumn> all, IReadOnlyList<double?> widths)
    {
        var shown = all.Where((_, i) => widths[i] is not null).ToList();
        if (view.Columns.SequenceEqual(shown))
        {
            return;
        }

        for (var i = view.Columns.Count - 1; i >= 0; i--)
        {
            if (!shown.Contains(view.Columns[i]))
            {
                view.Columns.RemoveAt(i);
            }
        }

        for (var i = 0; i < shown.Count; i++)
        {
            if (i >= view.Columns.Count || !ReferenceEquals(view.Columns[i], shown[i]))
            {
                view.Columns.Insert(i, shown[i]);
            }
        }
    }

    /// <summary>Margin, border and padding of a row around its cells (a realized row, else the default).</summary>
    private static double RowChrome(ScrollViewer? viewer)
    {
        if (viewer is null || FindDescendant<ListViewItem>(viewer) is not { } row)
        {
            return DefaultRowChrome;
        }

        return row.Margin.Left + row.Margin.Right + row.BorderThickness.Left + row.BorderThickness.Right + row.Padding.Left + row.Padding.Right;
    }

    /// <summary>
    /// The width of the vertical scroll bar while it is shown over the rows (the Fluent template lays it over the last
    /// column); 0 when it is hidden or has its own column next to the rows.
    /// </summary>
    private static double OverlayScrollBarWidth(ScrollViewer? viewer)
    {
        if (viewer is null || viewer.ComputedVerticalScrollBarVisibility != Visibility.Visible)
        {
            return 0;
        }

        var width = viewer.Template?.FindName("PART_VerticalScrollBar", viewer) is ScrollBar { ActualWidth: > 0 } bar
            ? bar.ActualWidth
            : SystemParameters.VerticalScrollBarWidth;
        var overlays = viewer.ViewportWidth > viewer.ActualWidth - viewer.Padding.Left - viewer.Padding.Right - width + 0.5;
        return overlays ? width : 0;
    }

    /// <summary>The list's own scroll viewer (the first one in its template).</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject parent) => FindDescendant<ScrollViewer>(parent);

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
