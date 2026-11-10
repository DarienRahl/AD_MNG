using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AdMng.Presentation.Settings;

namespace AdMng.App.Controls;

/// <summary>
/// Draws the icon of an application area or another picture of the icon set that has no <c>ObjectIcon</c> value
/// (<see cref="IconKey"/>: a key of <see cref="AppIcons"/>). In the colour style it draws the shared frozen picture
/// scaled to <see cref="Size"/>; in the monochrome style (the setting, and always in Windows High Contrast) the
/// <see cref="Glyph"/> of the icon font in the text colour. Like <see cref="ObjectIconView"/> a light element without
/// template, decorative and outside the UI Automation tree.
/// </summary>
public sealed class AreaIconView : FrameworkElement
{
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(
        nameof(IconKey), typeof(string), typeof(AreaIconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(AreaIconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AreaIconView),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double d && d > 0 && !double.IsInfinity(d));

    /// <summary>Glyph colour of the monochrome style (inherits the text colour); its alpha dims the picture next to dimmed text.</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(AreaIconView),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The effective icon style, from the application resource (colour when the resource is not set).</summary>
    private static readonly DependencyProperty IconStyleProperty = DependencyProperty.Register(
        "IconStyle", typeof(IconStylePreference), typeof(AreaIconView),
        new FrameworkPropertyMetadata(IconStylePreference.Colour, FrameworkPropertyMetadataOptions.AffectsRender));

    public AreaIconView()
    {
        SetResourceReference(IconStyleProperty, IconDrawing.StyleResourceKey);
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    /// <summary>Resource key of the picture (<c>Icon.&lt;Name&gt;</c>, a constant of <see cref="AppIcons"/>).</summary>
    public string? IconKey
    {
        get => (string?)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    /// <summary>Glyph of the icon font for the monochrome style (a constant of <see cref="Glyphs"/>).</summary>
    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Edge length in device independent pixels (picture edge, glyph font size).</summary>
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
        var size = Size;
        var dpi = VisualTreeHelper.GetDpi(this);
        if ((IconStylePreference)GetValue(IconStyleProperty) == IconStylePreference.Colour
            && IconKey is { Length: > 0 } key
            && AppIcons.Find(key) is { } image)
        {
            IconDrawing.DrawImage(drawingContext, image, RenderSize, size, dpi, Foreground);
            return;
        }

        if (Glyph is { Length: > 0 } glyph)
        {
            var origin = new Point((RenderSize.Width - size) / 2, (RenderSize.Height - size) / 2);
            IconDrawing.DrawGlyph(drawingContext, glyph, size, Foreground, origin, dpi.PixelsPerDip);
        }
    }
}
