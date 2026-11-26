using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AdMng.App.Behaviors;

namespace AdMng.App.Controls;

/// <summary>
/// A small progress ring (track V, stage V2; design note docs/dev/v-interface-renewal.md 5.2, row 11) - the Fluent
/// theme of WPF has none. While <see cref="IsActive"/> it draws a faint full circle and on it an arc in the text colour:
/// a quarter that turns once per <c>Motion.Ring</c> when the progress is not known (<see cref="Fraction"/> is not a
/// number), or the part done, standing still. Used in the action button and in the status line.
/// </summary>
/// <remarks>
/// <para>Progress is information, not motion: the ring turns also when motion is switched off
/// (<c>Behaviors/Motion.Turn</c> is not behind the switch). It turns only while it is active <b>and</b> visible - a
/// ring in a hidden tab, a collapsed row or a closed window has no clock (<see cref="IsTurning"/>).</para>
/// <para>What turns is the element's own render transform; the drawing is made once. Like the icon elements it is a
/// light element without template, decorative and outside the UI Automation tree: the text next to it says what runs.
/// The colour is the inherited text colour, so the ring reads on a plain and on an accent button, in light, dark and
/// High Contrast, without a brush of its own.</para>
/// </remarks>
public sealed class ProgressRing : FrameworkElement
{
    /// <summary>The share of the circle the turning arc covers while the progress is not known.</summary>
    private const double IndeterminateShare = 0.25;

    /// <summary>How strong the full circle under the arc is drawn, relative to the arc.</summary>
    private const double TrackOpacity = 0.25;

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    /// <summary>The part done, 0 to 1; not a number (the default) = not known: the ring turns.</summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double d && d > 0 && !double.IsInfinity(d));

    /// <summary>The colour of the ring (inherits the text colour).</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform _rotation = new();

    public ProgressRing()
    {
        // The ring turns around its centre; the transform is the element's own and stays for its whole life.
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        SnapsToDevicePixels = true;
        Focusable = false;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateTurning();
    }

    /// <summary>True while the operation runs: the ring is drawn (and turns when the progress is not known).</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The part done, 0 to 1; not a number = not known.</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>Edge length in device independent pixels.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>
    /// A clock turns the ring right now: it is active, visible and its progress is not known. False for a ring nobody
    /// sees - it keeps no clock running.
    /// </summary>
    public bool IsTurning { get; private set; }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (!IsActive)
        {
            return;
        }

        var size = Size;
        var thickness = Math.Max(1.5, size / 8);
        var radius = (size - thickness) / 2;
        var centre = new Point(RenderSize.Width / 2, RenderSize.Height / 2);

        // The pen is not frozen: a brush of the theme cannot be (its colour is a resource reference that follows the
        // theme and the accent colour), and a pen is only as freezable as its brush.
        var pen = new Pen(Foreground, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        drawingContext.PushOpacity(TrackOpacity);
        drawingContext.DrawEllipse(null, pen, centre, radius, radius);
        drawingContext.Pop();

        var share = double.IsNaN(Fraction) ? IndeterminateShare : Math.Clamp(Fraction, 0d, 1d);
        if (share >= 1d)
        {
            drawingContext.DrawEllipse(null, pen, centre, radius, radius);
        }
        else if (share > 0d)
        {
            drawingContext.DrawGeometry(null, pen, Arc(centre, radius, share));
        }
    }

    /// <summary>An arc from the top of the circle clockwise over <paramref name="share"/> of it (between 0 and 1).</summary>
    private static Geometry Arc(Point centre, double radius, double share)
    {
        var angle = share * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + (radius * Math.Sin(angle)), centre.Y - (radius * Math.Cos(angle)));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, isLargeArc: share > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProgressRing)d).UpdateTurning();

    /// <summary>Starts or stops the clock: it runs only for a ring that is active, visible and without a known progress.</summary>
    private void UpdateTurning()
    {
        var turn = IsActive && IsVisible && double.IsNaN(Fraction);
        if (turn != IsTurning)
        {
            IsTurning = Motion.Turn(_rotation, turn);
        }
    }
}
