using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AdMng.App.Behaviors;

namespace AdMng.App.Controls;

/// <summary>
/// Placeholder rows for the first read of a list (track V, stage V2b-5b; docs/dev/v2-status-controls.md; design note 5.2 row 10 and
/// 6.7): rounded grey bars in the shape of rows - a picture and three bars of different lengths - that stand where the rows will
/// be, instead of blank space, so the operator sees that rows are coming and not that the list is empty. At most
/// <see cref="MaxRows"/> rows; <see cref="RowCount"/> and <see cref="RowHeight"/> say how many and how high (the height of a row
/// of the list it stands in for).
/// </summary>
/// <remarks>
/// <para>The control is shown while <see cref="IsActive"/> is true: the view binds it to the "first read" state of its view model
/// and the control is collapsed otherwise, so a list that has its rows has no placeholder in its tree and no clock. The bars come
/// after <c>Motion.WorkingDelay</c> (about 150 ms) - the delay the status line has before "Working" (owner's decision 5): a read
/// that ends at once never flashes placeholder rows. <see cref="IsShown"/> is what is drawn; a test replaces
/// <see cref="Scheduler"/> to move that time by hand.</para>
/// <para>Without motion the placeholder rows are a calm static picture. With motion they breathe: the opacity of the bars goes
/// down and up over <c>Motion.Shimmer</c> (<see cref="Motion.Pulse"/>) and only while they are visible. The brushes are Fluent
/// keys read as dynamic resources, so a theme switch and High Contrast follow. The control is decorative: it is not an element of
/// the UI Automation tree - the status line says "Reading ..." - takes no focus and no pointer, and clips to its bounds.</para>
/// <para>The look is the style <c>Ad.Skeleton</c> of Themes/Controls.xaml, which the control takes by itself; the style has only
/// the host panel <c>PART_Rows</c>, and the rows are made by the control, deterministically (the same bars for the same row),
/// when <see cref="RowCount"/> or <see cref="RowHeight"/> change.</para>
/// </remarks>
public class Skeleton : Control
{
    /// <summary>The resource key of the style in Themes/Controls.xaml.</summary>
    public const string StyleKey = "Ad.Skeleton";

    /// <summary>The template part that holds the rows.</summary>
    public const string RowsPartName = "PART_Rows";

    /// <summary>The most rows a list shows as placeholders (design note 6.7).</summary>
    public const int MaxRows = 12;

    /// <summary>Widths of the bars of the three text columns, row after row; the shape of names, types and descriptions.</summary>
    private static readonly double[] NameWidths = [150, 112, 176, 132, 96, 164, 120, 144, 104, 168, 126, 138];

    private static readonly double[] TypeWidths = [72, 96, 64, 88, 80, 60, 92, 70, 84, 66, 90, 76];

    private static readonly double[] DescriptionWidths = [210, 140, 180, 96, 230, 160, 120, 200, 150, 110, 190, 130];

    /// <summary>How many placeholder rows, 1 to <see cref="MaxRows"/> (8 by default).</summary>
    public static readonly DependencyProperty RowCountProperty = DependencyProperty.Register(
        nameof(RowCount), typeof(int), typeof(Skeleton), new PropertyMetadata(8, OnRowsChanged, CoerceRowCount));

    /// <summary>The height of a row: the height of a row of the list the placeholders stand in for (26 by default).</summary>
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(Skeleton), new PropertyMetadata(26d, OnRowsChanged),
        value => value is double d && d >= 8 && d <= 200);

    /// <summary>The first read runs and the rows are expected: the control is shown (after the delay).</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Skeleton), new PropertyMetadata(false, OnIsActiveChanged));

    private static readonly DependencyPropertyKey IsShownPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsShown), typeof(bool), typeof(Skeleton), new PropertyMetadata(false));

    /// <summary>The placeholder rows are drawn: the control is active and its delay has run out.</summary>
    public static readonly DependencyProperty IsShownProperty = IsShownPropertyKey.DependencyProperty;

    private Panel? _rows;
    private IStatusScheduler? _scheduler;
    private IDisposable? _delay;

    static Skeleton()
    {
        FocusableProperty.OverrideMetadata(typeof(Skeleton), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Skeleton), new FrameworkPropertyMetadata(false));
    }

    public Skeleton() => SetResourceReference(StyleProperty, StyleKey);

    public int RowCount
    {
        get => (int)GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsShown => (bool)GetValue(IsShownProperty);

    /// <summary>The rows that are built now (the template part holds them); zero before the template is applied.</summary>
    internal int BuiltRows => _rows?.Children.Count ?? 0;

    /// <summary>A pulse clock runs on the rows right now: shown, visible and motion is on.</summary>
    internal bool IsPulsing { get; private set; }

    /// <summary>The clock of the delay; a test replaces it before the control is made active.</summary>
    internal IStatusScheduler Scheduler
    {
        get => _scheduler ??= new DispatcherStatusScheduler(Dispatcher);
        set => _scheduler = value;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_rows is not null)
        {
            _rows.IsVisibleChanged -= OnRowsVisibleChanged;
        }

        _rows = GetTemplateChild(RowsPartName) as Panel;
        if (_rows is not null)
        {
            // The rows' own event, not this control's: a control raises its IsVisibleChanged before its children know that they
            // became visible too, and a pulse that asks the rows would find them hidden.
            _rows.IsVisibleChanged += OnRowsVisibleChanged;
        }

        BuildRows();
        UpdatePulse();
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    private static object CoerceRowCount(DependencyObject d, object? value) => Math.Clamp(value is int count ? count : 1, 1, MaxRows);

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Skeleton)d).BuildRows();

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Skeleton)d).Activate((bool)e.NewValue);

    /// <summary>The first read begins or ends: the rows come after the delay, and go at once.</summary>
    private void Activate(bool active)
    {
        _delay?.Dispose();
        _delay = null;
        if (!active)
        {
            SetValue(IsShownPropertyKey, false);
            UpdatePulse();
            return;
        }

        if (Motion.TimeOf(Motion.WorkingDelayKey) is { } wait && wait > TimeSpan.Zero)
        {
            _delay = Scheduler.Schedule(wait, Show);
        }
        else
        {
            Show();
        }
    }

    private void Show()
    {
        _delay = null;
        SetValue(IsShownPropertyKey, true);
        UpdatePulse();
    }

    private void OnRowsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePulse();

    /// <summary>The breathing of the rows: only while they are drawn and visible (in a window nobody hides), and only with motion.</summary>
    private void UpdatePulse()
    {
        if (_rows is null)
        {
            IsPulsing = false;
            return;
        }

        var shouldPulse = IsShown && _rows.IsVisible && Motion.IsEnabled;
        if (shouldPulse != IsPulsing)
        {
            IsPulsing = Motion.Pulse(_rows, shouldPulse);
        }
    }

    /// <summary>The rows of the placeholders: a picture and three bars, the bars of a different length from row to row.</summary>
    private void BuildRows()
    {
        if (_rows is null)
        {
            return;
        }

        _rows.Children.Clear();
        for (var index = 0; index < RowCount; index++)
        {
            _rows.Children.Add(BuildRow(index));
        }
    }

    private Grid BuildRow(int index)
    {
        var row = new Grid { Height = RowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 60 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 40 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 60 });

        var icon = Bar(16, 16, new Thickness(10, 0, 8, 0));
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var widths = new[] { NameWidths[index % NameWidths.Length], TypeWidths[index % TypeWidths.Length], DescriptionWidths[index % DescriptionWidths.Length] };
        for (var column = 0; column < widths.Length; column++)
        {
            var bar = Bar(widths[column], 10, new Thickness(0, 0, 12, 0));
            Grid.SetColumn(bar, column + 1);
            row.Children.Add(bar);
        }

        return row;
    }

    private static Border Bar(double width, double height, Thickness margin)
    {
        var bar = new Border
        {
            Width = width,
            Height = height,
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(height >= 16 ? 3 : 4),
            SnapsToDevicePixels = true,
        };

        // A theme brush as a resource reference: the bar follows light, dark and High Contrast while the application runs.
        bar.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");
        return bar;
    }
}
