using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AdMng.Core.Messaging.Html;

namespace AdMng.App.Correspondence;

// The layout an HTML body keeps in the editor (design note 9.2): margins of blocks, borders and paddings of cells, the
// attributes of tables and the CSS the editor cannot show, kept on the element they came from and written back.
public static partial class FlowDocumentHtml
{
    /// <summary>Properties read into the text format (runs, alignment, preformatting) – never kept as they are.</summary>
    private static readonly HashSet<string> TextProperties = new(StringComparer.Ordinal)
    {
        "font", "font-style", "font-weight", "font-size", "font-family", "color", "background-color", "text-align", "text-decoration",
        "text-decoration-line", "white-space",
    };

    /// <summary>Inherited properties of a <c>div</c> the blocks inside it take over when the <c>div</c> is dissolved.</summary>
    private static readonly HashSet<string> InheritedLayoutProperties = new(StringComparer.Ordinal)
    {
        "line-height", "letter-spacing", "word-spacing", "text-transform", "text-indent", "direction", "font-variant", "word-wrap",
        "overflow-wrap", "word-break",
    };

    /// <summary>The style of a link the editor keeps on the link (its runs do not take them over).</summary>
    private static readonly HashSet<string> LinkProperties = new(StringComparer.Ordinal)
    {
        "color", "text-decoration", "text-decoration-line", "text-decoration-style", "text-decoration-color",
    };

    /// <summary>Attributes of a table written back as they were.</summary>
    private static readonly string[] TableAttributes = ["border", "cellpadding", "cellspacing", "width", "align", "bgcolor"];

    /// <summary>Attributes of a cell written back as they were (spans and background are the cell's own properties).</summary>
    private static readonly string[] CellAttributes = ["width", "height", "align", "valign", "nowrap"];

    /// <summary>The editor's font of a CSS family list (the base font of a message): its first family, a generic one as
    /// Windows shows it (<c>sans-serif</c> → Arial, <c>serif</c> → Times New Roman, <c>monospace</c> → Consolas); Calibri
    /// when the list has no usable name. Only the first family: WPF falls back to Arial itself when it is not installed,
    /// as the mail client falls back to the next family of the list.</summary>
    public static FontFamily EditorFontFamily(string? cssFamilies) => new(EditorFamilyName(cssFamilies));

    /// <summary>The name of <see cref="EditorFontFamily"/>.</summary>
    internal static string EditorFamilyName(string? cssFamilies)
    {
        foreach (var part in SplitOutsideQuotes(cssFamilies ?? string.Empty, ','))
        {
            var name = part.Trim().Trim('"', '\'').Trim();
            if (name.Length is 0 or > 100 || !name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'))
            {
                continue;
            }

            return name.ToLowerInvariant() switch
            {
                "sans-serif" => "Arial",
                "serif" => "Times New Roman",
                "monospace" => "Consolas",
                "cursive" => "Comic Sans MS",
                "fantasy" => "Impact",
                "system-ui" => "Segoe UI",
                _ => name,
            };
        }

        return FontFamilyName;
    }

    /// <summary>The first family of a WPF font family list ("Aptos, sans-serif" → "Aptos").</summary>
    private static string FirstFamily(FontFamily? family) =>
        SplitOutsideQuotes(family?.Source ?? string.Empty, ',').Select(n => n.Trim().Trim('"', '\'').Trim()).FirstOrDefault(n => n.Length > 0)
        ?? FontFamilyName;

    /// <summary>A length in device-independent pixels as CSS: <c>0</c> or points with up to two decimals (<c>8pt</c>,
    /// <c>10.5pt</c>).</summary>
    internal static string PointsCss(double pixels)
    {
        if (double.IsNaN(pixels) || double.IsInfinity(pixels) || Math.Abs(pixels) < 0.005)
        {
            return "0";
        }

        var points = Math.Round(pixels * 72 / 96, 2);
        return points == 0 ? "0" : points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";
    }

    /// <summary>A border width in device-independent pixels as CSS pixels (<c>1px</c>, <c>1.33px</c>).</summary>
    private static string PixelsCss(double pixels) => Math.Round(pixels, 2).ToString("0.##", CultureInfo.InvariantCulture) + "px";

    /// <summary>The device-independent pixels of a margin or padding value (<c>auto</c> and percentages, which depend on
    /// the width around the block, show as 0; <c>em</c> of <paramref name="fontPixels"/>).</summary>
    private static double SidePixels(string css, double fontPixels)
    {
        var v = css.Trim().ToLowerInvariant();
        if (v == "auto" || v.EndsWith('%'))
        {
            return 0;
        }

        return Length(v, fontPixels) is { } pixels && pixels > 0 ? Math.Min(pixels, 10_000) : 0;
    }

    /// <summary>The sides (top, right, bottom, left) a <c>margin</c> or <c>padding</c> shorthand and its longhands set,
    /// in declaration order (null: not set; null array: no declaration).</summary>
    private static string?[]? Sides(IEnumerable<CssDeclaration> declarations, string property)
    {
        string?[]? sides = null;
        foreach (var declaration in declarations)
        {
            if (declaration.Property == property)
            {
                var values = declaration.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (values.Length is >= 1 and <= 4)
                {
                    sides ??= new string?[4];
                    sides[0] = values[0];
                    sides[1] = values.Length > 1 ? values[1] : values[0];
                    sides[2] = values.Length > 2 ? values[2] : values[0];
                    sides[3] = values.Length > 3 ? values[3] : sides[1];
                }
            }
            else if (declaration.Property.StartsWith(property + "-", StringComparison.Ordinal) && SideIndex(declaration.Property[(property.Length + 1)..]) is { } index)
            {
                sides ??= new string?[4];
                sides[index] = declaration.Value.Trim();
            }
        }

        return sides;
    }

    private static int? SideIndex(string side) => side switch
    {
        "top" => 0,
        "right" => 1,
        "bottom" => 2,
        "left" => 3,
        _ => null,
    };

    /// <summary>A box of the given sides; the sides the HTML did not set take <paramref name="defaults"/>.</summary>
    private static CssBox Box(string?[]? sides, Thickness defaults, double fontPixels)
    {
        CssSide Side(int index, double fallback) =>
            sides?[index] is { } css ? new CssSide(css, SidePixels(css, fontPixels), Specified: true) : CssSide.Default(fallback);

        return new CssBox(Side(0, defaults.Top), Side(1, defaults.Right), Side(2, defaults.Bottom), Side(3, defaults.Left));
    }

    /// <summary>The declarations of an element the editor keeps as they are: all but those <paramref name="modeled"/>
    /// says the editor holds itself (the text format, margins, a cell's borders …).</summary>
    private static List<KeyValuePair<string, string>> Extras(IEnumerable<CssDeclaration> declarations, Func<string, bool> modeled) =>
        [.. declarations.Where(d => !modeled(d.Property)).Select(d => new KeyValuePair<string, string>(d.Property, d.Value.Trim()))];

    /// <summary>Kept declarations of an outer element under those of an inner one (the inner one's own win).</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> MergeExtras(IReadOnlyList<KeyValuePair<string, string>> inner, IEnumerable<KeyValuePair<string, string>> outer)
    {
        var merged = new List<KeyValuePair<string, string>>(inner);
        foreach (var declaration in outer)
        {
            if (!merged.Any(d => d.Key == declaration.Key))
            {
                merged.Add(declaration);
            }
        }

        return merged;
    }

    private static bool IsMargin(string property) => property is "margin" or "margin-top" or "margin-right" or "margin-bottom" or "margin-left";

    private static bool IsPadding(string property) => property is "padding" or "padding-top" or "padding-right" or "padding-bottom" or "padding-left";

    /// <summary>A border property of a cell's box (not the table's <c>border-collapse</c>, <c>border-spacing</c>, nor
    /// <c>border-radius</c>, which the editor cannot draw).</summary>
    private static bool IsBorder(string property) =>
        property.StartsWith("border", StringComparison.Ordinal) && property is not ("border-collapse" or "border-spacing" or "border-radius");

    /// <summary>
    /// The border of a cell from its border declarations as a browser reads them (the shorthand resets a side, longhands
    /// set one value; in order): per side the width when its style is visible, else 0; one colour for the cell (WPF has
    /// one brush per cell): the one of the widest visible side, the left side first on a tie.
    /// </summary>
    private static (Thickness Thickness, Color? Color) Borders(IEnumerable<CssDeclaration> declarations, double fontPixels)
    {
        var widths = new double[] { 3, 3, 3, 3 };
        var styles = new[] { "none", "none", "none", "none" };
        var colors = new Color?[4];
        foreach (var declaration in declarations)
        {
            var value = declaration.Value.Trim();
            switch (declaration.Property)
            {
                case "border":
                    for (var side = 0; side < 4; side++)
                    {
                        SetSide(side, value);
                    }

                    break;
                case "border-top" or "border-right" or "border-bottom" or "border-left":
                    SetSide(SideIndex(declaration.Property[7..])!.Value, value);
                    break;
                case "border-width":
                    Expand(value, (side, token) => widths[side] = Width(token) ?? widths[side]);
                    break;
                case "border-style":
                    Expand(value, (side, token) => styles[side] = token.ToLowerInvariant());
                    break;
                case "border-color":
                    Expand(value, (side, token) => colors[side] = ParseColor(token));
                    break;
                default:
                    var parts = declaration.Property.Split('-');
                    if (parts is ["border", var name, var part] && SideIndex(name) is { } index)
                    {
                        switch (part)
                        {
                            case "width":
                                widths[index] = Width(value) ?? widths[index];
                                break;
                            case "style":
                                styles[index] = value.ToLowerInvariant();
                                break;
                            case "color":
                                colors[index] = ParseColor(value);
                                break;
                        }
                    }

                    break;
            }
        }

        var visible = new double[4];
        for (var side = 0; side < 4; side++)
        {
            visible[side] = styles[side] is "none" or "hidden" ? 0 : Math.Min(widths[side], 1_000);
        }

        Color? color = null;
        var widest = 0d;
        foreach (var side in new[] { 3, 0, 1, 2 })
        {
            if (visible[side] > widest)
            {
                widest = visible[side];
                color = colors[side] ?? Colors.Black;
            }
        }

        return (new Thickness(visible[3], visible[0], visible[1], visible[2]), color);

        void SetSide(int side, string shorthand)
        {
            widths[side] = 3;
            styles[side] = "none";
            colors[side] = null;
            foreach (var token in shorthand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var lower = token.ToLowerInvariant();
                if (lower is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset")
                {
                    styles[side] = lower;
                }
                else if (Width(lower) is { } width)
                {
                    widths[side] = width;
                }
                else if (ParseColor(token) is { } parsed)
                {
                    colors[side] = parsed;
                }
            }
        }

        double? Width(string token)
        {
            var lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "thin" => 1,
                "medium" => 3,
                "thick" => 5,
                _ => lower.EndsWith('%') ? null : Length(lower, fontPixels) is { } pixels && pixels >= 0 ? pixels : null,
            };
        }

        static void Expand(string value, Action<int, string> set)
        {
            var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length is < 1 or > 4)
            {
                return;
            }

            set(0, tokens[0]);
            set(1, tokens.Length > 1 ? tokens[1] : tokens[0]);
            set(2, tokens.Length > 2 ? tokens[2] : tokens[0]);
            set(3, tokens.Length > 3 ? tokens[3] : tokens.Length > 1 ? tokens[1] : tokens[0]);
        }
    }

    /// <summary>A cell border written from the editor's values: per side <c>none</c> or the width in pixels, solid, in the
    /// cell's colour; one <c>border</c> when the sides are the same.</summary>
    private static string BorderCss(Thickness thickness, Color? color)
    {
        var hex = Hex(color ?? Colors.Black);
        string Side(double width) => width > 0 && !double.IsNaN(width) ? PixelsCss(width) + " solid " + hex : "none";
        var top = Side(thickness.Top);
        var right = Side(thickness.Right);
        var bottom = Side(thickness.Bottom);
        var left = Side(thickness.Left);
        return top == right && right == bottom && bottom == left
            ? "border:" + top
            : "border-top:" + top + ";border-right:" + right + ";border-bottom:" + bottom + ";border-left:" + left;
    }

    /// <summary>The declarations as CSS text (<c>property:value</c> joined by <c>;</c>).</summary>
    private static string Css(IEnumerable<KeyValuePair<string, string>> declarations) => string.Join(';', declarations.Select(d => d.Key + ":" + d.Value));

    /// <summary>One side of a margin: its CSS (as the HTML wrote it, or the editor's default), its device-independent
    /// pixels and whether the HTML set it.</summary>
    internal readonly record struct CssSide(string Css, double Pixels, bool Specified)
    {
        /// <summary>A side the HTML did not set: the editor's value.</summary>
        public static CssSide Default(double pixels) => new(PointsCss(pixels), double.IsNaN(pixels) ? 0 : pixels, Specified: false);

        /// <summary>This side and the same side of an element around it as a browser stacks vertical margins: they
        /// collapse into the larger one; a side this element did not set takes the outer one.</summary>
        public CssSide Collapse(CssSide outer) => !Specified || outer.Pixels > Pixels ? outer with { Specified = Specified || outer.Specified } : this;

        /// <summary>This side and the same side of an element around it as a browser adds horizontal margins.</summary>
        public CssSide Add(CssSide outer) =>
            outer.Pixels <= 0 ? this
            : Pixels <= 0 ? outer with { Specified = true }
            : new CssSide(PointsCss(Pixels + outer.Pixels), Pixels + outer.Pixels, Specified: true);
    }

    /// <summary>The four sides of a margin (top, right, bottom, left).</summary>
    internal sealed record CssBox(CssSide Top, CssSide Right, CssSide Bottom, CssSide Left)
    {
        /// <summary>The WPF thickness of the box.</summary>
        public Thickness Thickness => new(Left.Pixels, Top.Pixels, Right.Pixels, Bottom.Pixels);

        /// <summary>The value of the shorthand: one value when the four are the same, else four.</summary>
        public string Css => Top.Css == Right.Css && Right.Css == Bottom.Css && Bottom.Css == Left.Css
            ? Top.Css
            : string.Join(' ', Top.Css, Right.Css, Bottom.Css, Left.Css);

        /// <summary>A box of the editor's values (no side set by the HTML).</summary>
        public static CssBox Of(Thickness thickness) =>
            new(CssSide.Default(thickness.Top), CssSide.Default(thickness.Right), CssSide.Default(thickness.Bottom), CssSide.Default(thickness.Left));
    }

    /// <summary>
    /// What the HTML said about an element that the editor keeps when it reads a body with its layout
    /// (<see cref="ToDocument(string, IReadOnlyDictionary{string, AdMng.Core.Messaging.Images.MessageImage}, bool)"/>), in
    /// the <c>Tag</c> of the paragraph, list, list item, table, cell or link: written back by <see cref="ToHtml"/> while
    /// the editor has not changed the property it describes. WPF copies it with the other values when Enter splits a
    /// paragraph, so the new paragraph keeps the look of the one it came from.
    /// </summary>
    internal sealed record HtmlLayout
    {
        /// <summary>The margin as read (blocks), written while the block's margin is still its thickness.</summary>
        public CssBox? Margin { get; init; }

        /// <summary>Declarations the editor cannot show (<c>mso-margin-top-alt</c>, <c>line-height</c>, <c>max-width</c>,
        /// <c>border-spacing</c>, a link's colour …), written back as they were, in their order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Extra { get; init; } = [];

        /// <summary>Attributes of a table or cell written back as they were (<c>border</c>, <c>cellpadding</c>,
        /// <c>width</c> …).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];

        /// <summary>A table with <c>border="0"</c> (an e-mail layout): no grid and no margin of the editor's own.</summary>
        public bool Borderless { get; init; }

        /// <summary>The padding declarations of a cell as read (null: none), written while the padding is
        /// <see cref="AppliedPadding"/>.</summary>
        public string? PaddingCss { get; init; }

        public Thickness AppliedPadding { get; init; }

        /// <summary>The border declarations of a cell as read (null: none), written while the border is
        /// <see cref="AppliedBorder"/> in <see cref="AppliedBorderColor"/>.</summary>
        public string? BorderCss { get; init; }

        public Thickness AppliedBorder { get; init; }

        public Color? AppliedBorderColor { get; init; }
    }
}
