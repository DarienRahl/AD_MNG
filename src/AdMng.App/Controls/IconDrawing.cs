using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AdMng.Presentation.Icons;

namespace AdMng.App.Controls;

/// <summary>
/// Shared drawing code of <see cref="ObjectIconView"/> and <see cref="AreaIconView"/>: the application resource that
/// holds the effective icon style, the colour pictures (one shared frozen image per icon) and the glyphs of the icon font.
/// </summary>
internal static class IconDrawing
{
    /// <summary>
    /// Key of the application resource with the effective icon style (a boxed
    /// <see cref="AdMng.Presentation.Settings.IconStylePreference"/>), set by <c>IconStyleService</c>. The icon elements
    /// read it through a resource reference, so a change re-renders every icon of every open window; without the
    /// resource they draw the colour icons.
    /// </summary>
    public const string StyleResourceKey = "Ad.IconStyle";

    private static readonly Typeface GlyphTypeface = new(new FontFamily(Glyphs.FontFamilyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>The pictures of the object icons by enum value; filled on first use, frozen images only.</summary>
    private static readonly ImageSource?[] ObjectImages = new ImageSource?[Enum.GetValues<ObjectIcon>().Length];

    /// <summary>
    /// The colour picture of an object icon: the frozen image of the application resources, the same instance for every
    /// element that shows it. Null when there is no application or no such picture (the caller draws the glyph).
    /// </summary>
    public static ImageSource? ObjectImage(ObjectIcon icon)
    {
        var index = (int)icon;
        var cacheable = (uint)index < (uint)ObjectImages.Length;
        if (cacheable && ObjectImages[index] is { } cached)
        {
            return cached;
        }

        var image = AppIcons.Find(AppIcons.KeyFor(icon));
        if (cacheable && image is { IsFrozen: true })
        {
            ObjectImages[index] = image;
        }

        return image;
    }

    /// <summary>
    /// Draws a picture into the square of <paramref name="size"/> centred in the element. The image is drawn as it is
    /// (a vector drawing scaled by the drawing context; nothing is cloned), and the square starts on a device pixel
    /// of the element, so a 16 px icon is not blurred by a half-pixel offset in a wider cell.
    /// </summary>
    public static void DrawImage(DrawingContext context, ImageSource image, Size renderSize, double size, DpiScale dpi)
    {
        var x = SnapToDevice((renderSize.Width - size) / 2, dpi.DpiScaleX);
        var y = SnapToDevice((renderSize.Height - size) / 2, dpi.DpiScaleY);
        context.DrawImage(image, new Rect(x, y, size, size));
    }

    /// <summary>Draws a glyph of the icon font centred in the square of <paramref name="size"/> at <paramref name="topLeft"/>.</summary>
    public static void DrawGlyph(DrawingContext context, string glyph, double size, Brush brush, Point topLeft, double pixelsPerDip)
    {
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GlyphTypeface, size, brush, pixelsPerDip)
        {
            TextAlignment = TextAlignment.Left,
        };

        // Icon fonts have no descenders: centre the ink box in the target square.
        var x = topLeft.X + ((size - text.Width) / 2);
        var y = topLeft.Y + ((size - text.Height) / 2);
        context.DrawText(text, new Point(x, y));
    }

    private static double SnapToDevice(double value, double scale) =>
        scale > 0 ? Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale : value;
}
