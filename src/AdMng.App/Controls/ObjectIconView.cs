using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AdMng.Presentation.Icons;

namespace AdMng.App.Controls;

/// <summary>
/// Draws the glyph of an <see cref="ObjectIcon"/> with the ADUC overlay badges (down arrow for disabled accounts,
/// padlock for locked-out accounts). A lightweight element without template or bindings inside, so thousands of list
/// rows stay cheap. Colours come from the Fluent theme resources and follow light/dark/high contrast switches.
/// Decorative: it is not exposed to UI Automation (the row text carries the information).
/// </summary>
public sealed class ObjectIconView : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(ObjectIcon), typeof(ObjectIconView),
        new FrameworkPropertyMetadata(ObjectIcon.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ObjectIconView),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double d && d > 0 && !double.IsInfinity(d));

    /// <summary>Glyph colour of leaf objects (inherits the text colour, so selected/disabled rows look right).</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ObjectIconView),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ContainerBrushProperty = RegisterBrush("ContainerBrush");
    private static readonly DependencyProperty MutedBrushProperty = RegisterBrush("MutedBrush");
    private static readonly DependencyProperty BadgeBackgroundProperty = RegisterBrush("BadgeBackground");
    private static readonly DependencyProperty DisabledBadgeBrushProperty = RegisterBrush("DisabledBadgeBrush");
    private static readonly DependencyProperty LockedBadgeBrushProperty = RegisterBrush("LockedBadgeBrush");

    private static readonly Typeface IconTypeface = new(new FontFamily(Glyphs.FontFamilyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public ObjectIconView()
    {
        // Theme brushes as resource references: a theme switch re-renders the icon.
        SetResourceReference(ContainerBrushProperty, "AccentTextFillColorPrimaryBrush");
        SetResourceReference(MutedBrushProperty, "TextFillColorSecondaryBrush");
        SetResourceReference(BadgeBackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(DisabledBadgeBrushProperty, "TextFillColorSecondaryBrush");
        SetResourceReference(LockedBadgeBrushProperty, "SystemFillColorCautionBrush");
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    public ObjectIcon Icon
    {
        get => (ObjectIcon)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Edge length in device independent pixels (glyph font size).</summary>
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

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var icon = Icon;
        var size = Size;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var origin = new Point((RenderSize.Width - size) / 2, (RenderSize.Height - size) / 2);
        var overlay = Glyphs.OverlayFor(icon);
        var baseBrush = Glyphs.ToneFor(icon) switch
        {
            IconTone.Container => Brush(ContainerBrushProperty) ?? Foreground,
            IconTone.Muted => Brush(MutedBrushProperty) ?? Foreground,
            _ => Foreground,
        };

        if (overlay == IconOverlay.Disabled)
        {
            drawingContext.PushOpacity(0.55);
        }

        DrawGlyph(drawingContext, Glyphs.For(icon), size, baseBrush, origin, pixelsPerDip);
        if (overlay == IconOverlay.Disabled)
        {
            drawingContext.Pop();
        }

        if (overlay == IconOverlay.None)
        {
            return;
        }

        // Badge: a small disc in the bottom right corner with the overlay glyph.
        var badge = Math.Max(7, Math.Round(size * 0.6));
        var center = new Point(origin.X + size - (badge / 2) + 1, origin.Y + size - (badge / 2) + 1);
        drawingContext.DrawEllipse(Brush(BadgeBackgroundProperty) ?? Brushes.White, null, center, badge / 2, badge / 2);
        var badgeBrush = overlay == IconOverlay.Locked
            ? Brush(LockedBadgeBrushProperty) ?? Foreground
            : Brush(DisabledBadgeBrushProperty) ?? Foreground;
        var glyph = overlay == IconOverlay.Locked ? Glyphs.Lock : Glyphs.DownArrow;
        var glyphSize = badge * 0.8;
        DrawGlyph(drawingContext, glyph, glyphSize, badgeBrush, new Point(center.X - (glyphSize / 2), center.Y - (glyphSize / 2)), pixelsPerDip);
    }

    private static DependencyProperty RegisterBrush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(ObjectIconView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void DrawGlyph(DrawingContext context, string glyph, double size, Brush brush, Point topLeft, double pixelsPerDip)
    {
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconTypeface, size, brush, pixelsPerDip)
        {
            TextAlignment = TextAlignment.Left,
        };

        // Icon fonts have no descenders: centre the ink box in the target square.
        var x = topLeft.X + ((size - text.Width) / 2);
        var y = topLeft.Y + ((size - text.Height) / 2);
        context.DrawText(text, new Point(x, y));
    }

    private Brush? Brush(DependencyProperty property) => GetValue(property) as Brush;
}
