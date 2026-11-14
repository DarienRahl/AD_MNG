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
    /// Text colours at least this opaque are full-strength text: the primary text colour of the Fluent theme has an
    /// alpha of 0.89 on the light and 1 on the dark theme, the secondary one 0.62 and 0.77.
    /// </summary>
    internal const double FullStrengthAlpha = 0.85;

    /// <summary>A picture never fades further than this, so it stays recognisable next to disabled text.</summary>
    internal const double MinimumPictureOpacity = 0.3;

    /// <summary>
    /// How strong a colour picture is drawn next to text of the colour <paramref name="textColour"/> (the inherited
    /// foreground): 1 next to full-strength text, and the alpha of the text colour where the theme or a row style dims
    /// the text (secondary, tertiary and disabled text: a disabled account in the object list, a disabled or pressed
    /// row or button, an unselected tab). The monochrome glyph is dimmed there because it is drawn in that colour; the
    /// picture has its own colours, so it follows through its opacity. Brushes that are not a solid colour count as
    /// full strength.
    /// </summary>
    public static double PictureOpacity(Brush? textColour)
    {
        if (textColour is not SolidColorBrush solid)
        {
            return 1;
        }

        var alpha = solid.Color.A / 255d * solid.Opacity;
        if (double.IsNaN(alpha) || alpha >= FullStrengthAlpha)
        {
            return 1;
        }

        return Math.Max(MinimumPictureOpacity, alpha);
    }

    /// <summary>
    /// Draws a picture into the square of <paramref name="size"/> centred in the element. The image is drawn as it is
    /// (a vector drawing scaled by the drawing context; nothing is cloned), and the square starts on a device pixel
    /// of the element, so a 16 px icon is not blurred by a half-pixel offset in a wider cell. Next to dimmed text
    /// (<paramref name="textColour"/>, see <see cref="PictureOpacity"/>) the picture is dimmed as well.
    /// </summary>
    public static void DrawImage(DrawingContext context, ImageSource image, Size renderSize, double size, DpiScale dpi, Brush? textColour)
    {
        var x = SnapToDevice((renderSize.Width - size) / 2, dpi.DpiScaleX);
        var y = SnapToDevice((renderSize.Height - size) / 2, dpi.DpiScaleY);
        var opacity = PictureOpacity(textColour);
        if (opacity < 1)
        {
            context.PushOpacity(opacity);
            context.DrawImage(image, new Rect(x, y, size, size));
            context.Pop();
            return;
        }

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
