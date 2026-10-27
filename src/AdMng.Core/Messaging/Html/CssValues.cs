using System.Globalization;
using System.Text;

namespace AdMng.Core.Messaging.Html;

/// <summary>Outcome of filtering one CSS declaration.</summary>
internal enum CssVerdict
{
    /// <summary>Allowed; the value is normalized.</summary>
    Keep,

    /// <summary>Not allowed (property or value) – a cosmetic difference.</summary>
    Drop,

    /// <summary>Not allowed and a security finding (active content, remote resources, hiding or overlaying content).</summary>
    DropSecurity,
}

/// <summary>
/// Value rules of the allowed CSS properties (design note section 4.3): colours (<c>#hex</c>, <c>rgb()</c>/<c>rgba()</c>
/// with numbers, CSS names; <c>windowtext</c> → <c>#000000</c>, <c>window</c> → <c>#FFFFFF</c>), lengths in
/// <c>px pt em rem % cm mm in</c> (≥ 0 except <c>text-indent</c> and <c>letter-spacing</c>, which are clamped to −1in and
/// −2px – further they move or squeeze text out of sight; no negative margins), keywords,
/// font families as identifiers or strings (re-serialized with single quotes). Numbers are re-serialized invariantly
/// (<c>12.0pt</c> → <c>12pt</c>, <c>.5in</c> → <c>0.5in</c>), keywords in lower case. Anything containing <c>url(</c>,
/// <c>expression(</c>, <c>\</c>, <c>/*</c>, <c>@</c>, <c>&lt;</c>, <c>javascript:</c> or a control character is dropped,
/// so are <c>var()</c>, <c>calc()</c>, <c>attr()</c>.
/// </summary>
internal static class CssValues
{
    /// <summary>Text smaller than this (in px) cannot be read: hiding it is a security finding (the sanitizer checks
    /// elements with text, so Outlook's 1pt spacer paragraphs stay).</summary>
    public const double MinReadableFontPixels = 4;

    /// <summary>The CSS pixels of a unit (<c>%</c> is the caller's base; <c>em</c>/<c>rem</c> at the default 16px).</summary>
    private static readonly (string Unit, double Pixels)[] UnitPixels =
        [("px", 1), ("pt", 96d / 72), ("rem", 16), ("em", 16), ("cm", 96 / 2.54), ("mm", 96 / 25.4), ("in", 96)];

    private static readonly HashSet<string> AllowedProperties = new(HtmlPolicy.CssProperties, StringComparer.Ordinal);

    /// <summary>Properties whose presence is a security finding whatever the value (they hide, overlay or bind code).</summary>
    private static readonly HashSet<string> SecurityProperties = new(StringComparer.Ordinal)
    {
        "behavior", "-moz-binding", "content", "transform", "filter", "clip", "clip-path", "opacity", "mask", "mask-image",
    };

    private static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "transparent", "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanchedalmond", "blue",
        "blueviolet", "brown", "burlywood", "cadetblue", "chartreuse", "chocolate", "coral", "cornflowerblue", "cornsilk", "crimson",
        "cyan", "darkblue", "darkcyan", "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki", "darkmagenta",
        "darkolivegreen", "darkorange", "darkorchid", "darkred", "darksalmon", "darkseagreen", "darkslateblue", "darkslategray",
        "darkslategrey", "darkturquoise", "darkviolet", "deeppink", "deepskyblue", "dimgray", "dimgrey", "dodgerblue", "firebrick",
        "floralwhite", "forestgreen", "fuchsia", "gainsboro", "ghostwhite", "gold", "goldenrod", "gray", "green", "greenyellow", "grey",
        "honeydew", "hotpink", "indianred", "indigo", "ivory", "khaki", "lavender", "lavenderblush", "lawngreen", "lemonchiffon",
        "lightblue", "lightcoral", "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink",
        "lightsalmon", "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "lime",
        "limegreen", "linen", "magenta", "maroon", "mediumaquamarine", "mediumblue", "mediumorchid", "mediumpurple",
        "mediumseagreen", "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue", "mintcream",
        "mistyrose", "moccasin", "navajowhite", "navy", "oldlace", "olive", "olivedrab", "orange", "orangered", "orchid",
        "palegoldenrod", "palegreen", "paleturquoise", "palevioletred", "papayawhip", "peachpuff", "peru", "pink", "plum",
        "powderblue", "purple", "rebeccapurple", "red", "rosybrown", "royalblue", "saddlebrown", "salmon", "sandybrown", "seagreen",
        "seashell", "sienna", "silver", "skyblue", "slateblue", "slategray", "slategrey", "snow", "springgreen", "steelblue", "tan",
        "teal", "thistle", "tomato", "turquoise", "violet", "wheat", "white", "whitesmoke", "yellow", "yellowgreen",
    };

    private static readonly string[] LengthUnits = ["px", "pt", "em", "rem", "%", "cm", "mm", "in"];

    private static readonly HashSet<string> BorderStyles = Words("none", "hidden", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset");
    private static readonly HashSet<string> BorderWidths = Words("thin", "medium", "thick");
    private static readonly HashSet<string> FontStyles = Words("normal", "italic", "oblique");
    private static readonly HashSet<string> FontVariants = Words("normal", "small-caps");
    private static readonly HashSet<string> FontWeights = Words("normal", "bold", "bolder", "lighter", "100", "200", "300", "400", "500", "600", "700", "800", "900");
    private static readonly HashSet<string> FontSizes = Words("xx-small", "x-small", "small", "medium", "large", "x-large", "xx-large", "smaller", "larger");
    private static readonly HashSet<string> GenericFamilies = Words("serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui");
    private static readonly HashSet<string> DecorationLines = Words("none", "underline", "overline", "line-through");
    private static readonly HashSet<string> DecorationStyles = Words("solid", "double", "dotted", "dashed", "wavy");
    private static readonly HashSet<string> VerticalAligns = Words("baseline", "sub", "super", "top", "text-top", "middle", "bottom", "text-bottom");

    private static readonly Dictionary<string, HashSet<string>> KeywordProperties = new(StringComparer.Ordinal)
    {
        ["font-style"] = FontStyles,
        ["font-variant"] = FontVariants,
        ["font-weight"] = FontWeights,
        ["text-transform"] = Words("none", "capitalize", "uppercase", "lowercase"),
        ["text-align"] = Words("left", "right", "center", "justify", "start", "end"),
        ["white-space"] = Words("normal", "nowrap", "pre", "pre-wrap", "pre-line", "break-spaces"),
        ["direction"] = Words("ltr", "rtl"),
        ["text-decoration-style"] = DecorationStyles,
        ["border-collapse"] = Words("collapse", "separate"),
        ["list-style-type"] = Words("disc", "circle", "square", "decimal", "decimal-leading-zero", "lower-alpha", "upper-alpha",
            "lower-latin", "upper-latin", "lower-roman", "upper-roman", "lower-greek", "none"),
        ["list-style-position"] = Words("inside", "outside"),
        ["table-layout"] = Words("auto", "fixed"),
        ["caption-side"] = Words("top", "bottom"),
        ["empty-cells"] = Words("show", "hide"),
        ["word-wrap"] = Words("normal", "break-word", "anywhere"),
        ["overflow-wrap"] = Words("normal", "break-word", "anywhere"),
        ["word-break"] = Words("normal", "break-all", "keep-all", "break-word"),
    };

    /// <summary>True when the cascaded declarations hide the element (it is removed with its content).</summary>
    public static bool Hides(IReadOnlyDictionary<string, string> declarations) =>
        (declarations.TryGetValue("display", out var display) && display.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        || (declarations.TryGetValue("mso-hide", out var hide) && hide.Trim().Equals("all", StringComparison.OrdinalIgnoreCase));

    /// <summary>Filters one declaration: the verdict and, for <see cref="CssVerdict.Keep"/>, the normalized value.</summary>
    public static CssVerdict Filter(string property, string value, out string normalized)
    {
        normalized = string.Empty;
        var raw = value.Trim();
        if (raw.Length == 0)
        {
            return CssVerdict.Drop;
        }

        if (IsDangerousValue(raw))
        {
            return CssVerdict.DropSecurity;
        }

        if (raw.Contains('\\', StringComparison.Ordinal) || raw.Contains("/*", StringComparison.Ordinal) || raw.Contains('@', StringComparison.Ordinal)
            || raw.Contains('<', StringComparison.Ordinal) || raw.Any(char.IsControl)
            || raw.Contains("var(", StringComparison.OrdinalIgnoreCase) || raw.Contains("calc(", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("attr(", StringComparison.OrdinalIgnoreCase))
        {
            return CssVerdict.Drop;
        }

        if (!AllowedProperties.Contains(property))
        {
            return IsSecurityProperty(property, raw) ? CssVerdict.DropSecurity : CssVerdict.Drop;
        }

        string? result = property switch
        {
            "color" or "background-color" or "text-decoration-color" or "border-top-color" or "border-right-color"
                or "border-bottom-color" or "border-left-color" => Color(raw),
            "border-color" => Repeat(raw, 4, Color),
            "font-family" => FontFamily(raw),
            "font" => Font(raw),
            "font-size" => FontSize(raw),
            "line-height" => LineHeight(raw),
            "letter-spacing" => raw.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "normal" : AtLeast(Length(raw, allowNegative: true), -2, "-2px", 0.16),
            "word-spacing" => raw.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "normal" : Length(raw, allowNegative: false),
            "text-indent" => AtLeast(Length(raw, allowNegative: true), -96, "-1in", 6),
            "vertical-align" => VerticalAligns.Contains(raw) ? raw.ToLowerInvariant() : Length(raw, allowNegative: false),
            "text-decoration" => TextDecoration(raw),
            "text-decoration-line" => Repeat(raw, 3, t => DecorationLines.Contains(t) ? t.ToLowerInvariant() : null),
            "margin" => Repeat(raw, 4, t => AutoOr(t, allowAuto: true)),
            "margin-top" or "margin-right" or "margin-bottom" or "margin-left" => AutoOr(raw, allowAuto: true),
            "padding" => Repeat(raw, 4, t => Length(t, allowNegative: false)),
            "padding-top" or "padding-right" or "padding-bottom" or "padding-left" => Length(raw, allowNegative: false),
            "border" or "border-top" or "border-right" or "border-bottom" or "border-left" => Border(raw),
            "border-width" => Repeat(raw, 4, BorderWidth),
            "border-style" => Repeat(raw, 4, t => BorderStyles.Contains(t) ? t.ToLowerInvariant() : null),
            "border-top-width" or "border-right-width" or "border-bottom-width" or "border-left-width" => BorderWidth(raw),
            "border-top-style" or "border-right-style" or "border-bottom-style" or "border-left-style" =>
                BorderStyles.Contains(raw) ? raw.ToLowerInvariant() : null,
            "border-spacing" => Repeat(raw, 2, t => Length(t, allowNegative: false)),
            "border-radius" => Repeat(raw, 4, t => Length(t, allowNegative: false)),
            "width" or "height" or "min-width" or "min-height" => AutoOr(raw, allowAuto: true),
            "max-width" => raw.Equals("none", StringComparison.OrdinalIgnoreCase) ? "none" : Length(raw, allowNegative: false),
            _ => KeywordProperties.TryGetValue(property, out var words) && words.Contains(raw) ? raw.ToLowerInvariant() : null,
        };
        if (result is null)
        {
            // A negative margin can pull content over the fixed footer; a zero font size hides text.
            return IsNegativeMargin(property, raw) || (property is "font-size" && IsZero(raw)) ? CssVerdict.DropSecurity : CssVerdict.Drop;
        }

        normalized = result;
        return CssVerdict.Keep;
    }

    /// <summary>Conversions before filtering: <c>mso-highlight</c> → <c>background-color</c>, a <c>background</c> shorthand
    /// with only a colour → <c>background-color</c>.</summary>
    public static void ApplyConversions(Dictionary<string, string> declarations)
    {
        if (!declarations.ContainsKey("background-color"))
        {
            if (declarations.TryGetValue("mso-highlight", out var highlight) && Color(highlight.Trim()) is { } h)
            {
                declarations["background-color"] = h;
            }
            else if (declarations.TryGetValue("background", out var background) && Color(background.Trim()) is { } b)
            {
                declarations["background-color"] = b;
            }
        }

        // The converted source is not a removal worth reporting.
        if (declarations.TryGetValue("background", out var source) && Color(source.Trim()) is not null)
        {
            declarations.Remove("background");
        }

        declarations.Remove("mso-highlight");
    }

    /// <summary>A colour value, normalized, or null.</summary>
    public static string? Color(string value)
    {
        var v = value.Trim();
        if (v.Length == 0)
        {
            return null;
        }

        if (v[0] == '#')
        {
            var hex = v[1..];
            return hex.Length is 3 or 6 && hex.All(char.IsAsciiHexDigit) ? v : null;
        }

        if (v.Equals("windowtext", StringComparison.OrdinalIgnoreCase))
        {
            return "#000000";
        }

        if (v.Equals("window", StringComparison.OrdinalIgnoreCase))
        {
            return "#FFFFFF";
        }

        if (NamedColors.Contains(v))
        {
            return v.ToLowerInvariant();
        }

        var open = v.IndexOf('(', StringComparison.Ordinal);
        if (open > 0 && v.EndsWith(')'))
        {
            var function = v[..open].Trim().ToLowerInvariant();
            var parts = v[(open + 1)..^1].Split(',').Select(p => p.Trim()).ToArray();
            if (function == "rgb" && parts.Length == 3 && parts.All(IsColorComponent))
            {
                return "rgb(" + string.Join(',', parts.Select(NormalizeComponent)) + ")";
            }

            if (function == "rgba" && parts.Length == 4 && parts.Take(3).All(IsColorComponent) && IsAlpha(parts[3]))
            {
                return "rgba(" + string.Join(',', parts.Take(3).Select(NormalizeComponent)) + "," + Number(parts[3])! + ")";
            }
        }

        return null;
    }

    /// <summary>A length (<c>12pt</c>, <c>0</c>, <c>50%</c>) normalized, or null.</summary>
    public static string? Length(string value, bool allowNegative)
    {
        var v = value.Trim().ToLowerInvariant();
        foreach (var unit in LengthUnits)
        {
            if (v.EndsWith(unit, StringComparison.Ordinal) && Number(v[..^unit.Length]) is { } number)
            {
                return !allowNegative && number.StartsWith('-') ? null : number + unit;
            }
        }

        return Number(v) is "0" or "-0" ? "0" : null;
    }

    /// <summary>The CSS pixels of a normalized length (<paramref name="percentBase"/> pixels per <c>%</c>), or null.</summary>
    public static double? Pixels(string length, double percentBase)
    {
        var v = length.Trim().ToLowerInvariant();
        if (v.EndsWith('%'))
        {
            return Parse(v[..^1]) * percentBase;
        }

        foreach (var (unit, pixels) in UnitPixels)
        {
            if (v.EndsWith(unit, StringComparison.Ordinal))
            {
                return Parse(v[..^unit.Length]) * pixels;
            }
        }

        return Parse(v) is 0 ? 0 : null;

        static double? Parse(string number) =>
            double.TryParse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The font size of filtered declarations in pixels (<c>font-size</c>, else the size of the <c>font</c>
    /// shorthand; a keyword such as <c>small</c> is readable), or null.</summary>
    public static double? FontSizePixels(IReadOnlyDictionary<string, string> declarations)
    {
        if (declarations.TryGetValue("font-size", out var size))
        {
            return Pixels(size, 0.16);
        }

        return declarations.TryGetValue("font", out var font)
            ? font.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => char.IsAsciiDigit(t[0]) || t[0] == '.')
                .Select(t => Pixels(t.Split('/')[0], 0.16)).FirstOrDefault(p => p is not null)
            : null;
    }

    /// <summary>A length, or <paramref name="floor"/> when it is below <paramref name="minimumPixels"/>.</summary>
    private static string? AtLeast(string? length, double minimumPixels, string floor, double percentBase) =>
        length is not null && Pixels(length, percentBase) < minimumPixels ? floor : length;

    /// <summary>A plain number normalized ("12.0" → "12", ".5" → "0.5"), or null.</summary>
    public static string? Number(string value)
    {
        var v = value.Trim();
        if (v.Length == 0 || v.Length > 16 || !v.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or '+') || v.Count(c => c == '.') > 1
            || v.LastIndexOfAny(['-', '+']) > 0)
        {
            return null;
        }

        return decimal.TryParse(v, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.######", CultureInfo.InvariantCulture)
            : null;
    }

    private static bool IsDangerousValue(string value)
    {
        var compact = new string([.. value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))]).ToLowerInvariant();
        return compact.Contains("url(", StringComparison.Ordinal) || compact.Contains("expression(", StringComparison.Ordinal)
            || compact.Contains("javascript:", StringComparison.Ordinal) || compact.Contains("vbscript:", StringComparison.Ordinal)
            || compact.Contains("image-set(", StringComparison.Ordinal) || compact.Contains("@import", StringComparison.Ordinal)
            || compact.Contains("-moz-binding", StringComparison.Ordinal) || compact.Contains("behavior", StringComparison.Ordinal);
    }

    private static bool IsSecurityProperty(string property, string value)
    {
        if (SecurityProperties.Contains(property))
        {
            return true;
        }

        var v = value.Trim().ToLowerInvariant();
        return property switch
        {
            "position" => v is "fixed" or "absolute" or "sticky",
            "display" => v == "none",
            "visibility" => v is "hidden" or "collapse",
            "mso-hide" => v == "all",
            _ => false,
        };
    }

    private static bool IsNegativeMargin(string property, string value) =>
        property.StartsWith("margin", StringComparison.Ordinal)
        && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => t.TrimStart().StartsWith('-') && Length(t, allowNegative: true) is not null);

    private static bool IsZero(string value) => Length(value, allowNegative: true) is { } length && IsZeroLength(length);

    private static string? AutoOr(string value, bool allowAuto) =>
        allowAuto && value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : Length(value, allowNegative: false);

    private static string? BorderWidth(string value) =>
        BorderWidths.Contains(value.Trim()) ? value.Trim().ToLowerInvariant() : Length(value, allowNegative: false);

    private static string? FontSize(string value)
    {
        var v = value.Trim();
        if (FontSizes.Contains(v))
        {
            return v.ToLowerInvariant();
        }

        var length = Length(v, allowNegative: false);
        return length is null || IsZeroLength(length) ? null : length;
    }

    private static bool IsZeroLength(string length) => Number(length.TrimEnd('p', 't', 'x', 'e', 'm', 'r', 'c', 'i', 'n', '%')) is "0" or "-0";

    private static string? LineHeight(string value)
    {
        var v = value.Trim();
        if (v.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return "normal";
        }

        return Number(v) is { } number && !number.StartsWith('-') ? number : Length(v, allowNegative: false);
    }

    private static string? TextDecoration(string value)
    {
        var tokens = Tokens(value);
        if (tokens.Count is 0 or > 4)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var token in tokens)
        {
            if (DecorationLines.Contains(token) || DecorationStyles.Contains(token))
            {
                result.Add(token.ToLowerInvariant());
            }
            else if (Color(token) is { } color)
            {
                result.Add(color);
            }
            else
            {
                return null;
            }
        }

        return string.Join(' ', result);
    }

    private static string? Border(string value)
    {
        var tokens = Tokens(value);
        if (tokens.Count is 0 or > 3)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var token in tokens)
        {
            if (BorderStyles.Contains(token))
            {
                result.Add(token.ToLowerInvariant());
            }
            else if (BorderWidth(token) is { } width)
            {
                result.Add(width);
            }
            else if (Color(token) is { } color)
            {
                result.Add(color);
            }
            else
            {
                return null;
            }
        }

        return string.Join(' ', result);
    }

    private static string? FontFamily(string value)
    {
        var families = CssParser.SplitTopLevel(value, ',').Select(f => f.Trim()).ToList();
        if (families.Count is 0 or > 10)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var family in families)
        {
            if (family.Length >= 2 && family[0] is '"' or '\'' && family[^1] == family[0])
            {
                var name = family[1..^1].Trim();
                if (name.Length is 0 or > 100 || name.Any(c => c is '"' or '\'' or '\\' or '<' or '>' or '@' or ';' || char.IsControl(c)))
                {
                    return null;
                }

                result.Add("'" + name + "'");
                continue;
            }

            var words = family.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Any(w => !w.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')))
            {
                return null;
            }

            result.Add(words.Length == 1 && GenericFamilies.Contains(words[0]) ? words[0].ToLowerInvariant() : string.Join(' ', words));
        }

        return string.Join(',', result);
    }

    /// <summary>The <c>font</c> shorthand, kept only when fully allowed: [style] [variant] [weight] size[/line-height] family.</summary>
    private static string? Font(string value)
    {
        var tokens = Tokens(value);
        var prefix = new List<string>();
        var i = 0;
        while (i < tokens.Count && (FontStyles.Contains(tokens[i]) || FontVariants.Contains(tokens[i]) || FontWeights.Contains(tokens[i])))
        {
            prefix.Add(tokens[i].ToLowerInvariant());
            i++;
        }

        if (i >= tokens.Count || prefix.Count > 3)
        {
            return null;
        }

        var sizeToken = tokens[i++];
        string? lineHeight = null;
        var slash = sizeToken.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            lineHeight = sizeToken[(slash + 1)..];
            sizeToken = sizeToken[..slash];
            if (lineHeight.Length == 0 && i < tokens.Count)
            {
                lineHeight = tokens[i++];
            }
        }
        else if (i < tokens.Count && tokens[i].StartsWith('/'))
        {
            lineHeight = tokens[i][1..];
            i++;
            if (lineHeight.Length == 0 && i < tokens.Count)
            {
                lineHeight = tokens[i++];
            }
        }

        var size = FontSize(sizeToken);
        var height = lineHeight is null ? null : LineHeight(lineHeight);
        var family = i < tokens.Count ? FontFamily(string.Join(' ', tokens.Skip(i))) : null;
        if (size is null || (lineHeight is not null && height is null) || family is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var p in prefix)
        {
            builder.Append(p).Append(' ');
        }

        builder.Append(size);
        if (height is not null)
        {
            builder.Append('/').Append(height);
        }

        return builder.Append(' ').Append(family).ToString();
    }

    /// <summary>One to <paramref name="max"/> space-separated values, each normalized by <paramref name="normalize"/>.</summary>
    private static string? Repeat(string value, int max, Func<string, string?> normalize)
    {
        var tokens = Tokens(value);
        if (tokens.Count == 0 || tokens.Count > max)
        {
            return null;
        }

        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (normalize(token) is not { } normalized)
            {
                return null;
            }

            result.Add(normalized);
        }

        return string.Join(' ', result);
    }

    /// <summary>Space-separated tokens outside quotes and parentheses (a comma inside <c>rgb(…)</c> stays in its token;
    /// a family list keeps its commas).</summary>
    private static List<string> Tokens(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        char quote = '\0';
        foreach (var c in value.Trim())
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }

            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool IsColorComponent(string part) =>
        part.EndsWith('%') ? Number(part[..^1]) is { } p && !p.StartsWith('-') && decimal.Parse(p, CultureInfo.InvariantCulture) <= 100
        : Number(part) is { } n && !n.StartsWith('-') && decimal.Parse(n, CultureInfo.InvariantCulture) <= 255;

    private static string NormalizeComponent(string part) => part.EndsWith('%') ? Number(part[..^1]) + "%" : Number(part)!;

    private static bool IsAlpha(string part) => Number(part) is { } n && !n.StartsWith('-') && decimal.Parse(n, CultureInfo.InvariantCulture) <= 1;

    private static HashSet<string> Words(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);
}
