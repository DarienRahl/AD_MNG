using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Messaging.Html;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// What a browser shows of the layout of an HTML body, for comparing two bodies semantically (the editor's round trip may
/// write the same look with other markup: a <c>div</c>'s margin on the paragraph inside it, <c>&lt;b&gt;</c> as a bold
/// span, colours as hex). One line per table (attributes and layout CSS), cell (padding, per-side borders, background),
/// list (kind, start, bottom margin), text block (<c>p</c>, <c>li</c>: margins with the margins of the <c>div</c>s around
/// it stacked as a browser stacks them, Word's spacing, the text in stretches of the same weight, colour, font and
/// underline) and link (address, colour, underline). Lengths in pixels, colours as <c>#RRGGBB</c> (black is the message's
/// colour: <c>default</c>).
/// </summary>
internal static class HtmlLayoutFacts
{
    /// <summary>The paragraph of the signature field: its margins do not matter (the renderer replaces the paragraph).</summary>
    private const string SignatureField = "{{podpis}}";

    public static IReadOnlyList<string> Of(string html)
    {
        var document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><head></head><body>" + html + "</body></html>");
        var facts = new List<string>();
        foreach (var element in document.Body!.Descendants<IElement>())
        {
            switch (element.LocalName)
            {
                case "table":
                    facts.Add(TableFact(element));
                    break;
                case "td" or "th":
                    facts.Add(CellFact(element));
                    break;
                case "ol" or "ul":
                    facts.Add(ListFact(element));
                    break;
                case "p" or "li":
                    facts.Add(BlockFact(element));
                    break;
                case "a":
                    facts.Add(LinkFact(element));
                    break;
            }
        }

        return facts;
    }

    private static string TableFact(IElement table)
    {
        var style = Style(table);
        var margin = Sides(style, "margin");
        return "table border=" + Attribute(table, "border") + " cellpadding=" + Attribute(table, "cellpadding") + " cellspacing=" + Attribute(table, "cellspacing")
            + " width=" + Attribute(table, "width") + " | collapse=" + (style.GetValueOrDefault("border-collapse") ?? "-")
            + " spacing=" + Length(style.GetValueOrDefault("border-spacing")) + " css-width=" + (style.GetValueOrDefault("width") ?? "-")
            + " max-width=" + Length(style.GetValueOrDefault("max-width"))
            + " margin=" + string.Join(' ', margin.Select(m => Length(m ?? "0")));
    }

    private static string CellFact(IElement cell)
    {
        var style = Style(cell);
        var table = cell.Closest("table");
        var cellPadding = table?.GetAttribute("cellpadding") ?? "1";
        var padding = Sides(style, "padding").Select(p => Length(p ?? cellPadding + "px"));
        return "cell padding=" + string.Join(' ', padding) + " border=" + string.Join(", ", Borders(style, table))
            + " background=" + Color(style.GetValueOrDefault("background-color") ?? cell.GetAttribute("bgcolor"));
    }

    private static string ListFact(IElement list)
    {
        var style = Style(list);
        var kind = style.GetValueOrDefault("list-style-type") ?? list.GetAttribute("type") switch
        {
            "a" => "lower-alpha",
            "A" => "upper-alpha",
            "i" => "lower-roman",
            "I" => "upper-roman",
            "circle" or "square" or "disc" => list.GetAttribute("type")!,
            _ => list.LocalName == "ol" ? "decimal" : "disc",
        };
        var start = list.GetAttribute("start") ?? "1";
        return list.LocalName + " " + kind + " start=" + start + " margin-bottom=" + Length(Sides(style, "margin")[2] ?? "auto");
    }

    private static string BlockFact(IElement block)
    {
        var text = Stretches(block);
        var style = Style(block);
        var builder = new StringBuilder(block.LocalName);
        if (block.TextContent.Trim() != SignatureField)
        {
            var (top, right, bottom, left) = StackedMargins(block);
            builder.Append(" margin=").Append(top).Append(' ').Append(right).Append(' ').Append(bottom).Append(' ').Append(left)
                .Append(" mso-top=").Append(style.GetValueOrDefault("mso-margin-top-alt") ?? "-")
                .Append(" mso-bottom=").Append(style.GetValueOrDefault("mso-margin-bottom-alt") ?? "-");
        }

        return builder.Append(" | ").Append(text).ToString();
    }

    private static string LinkFact(IElement link)
    {
        var style = Style(link);
        return "a " + link.GetAttribute("href") + " color=" + Color(style.GetValueOrDefault("color") ?? "link")
            + " decoration=" + (style.GetValueOrDefault("text-decoration") ?? "-");
    }

    /// <summary>The margins of a block with those of the <c>div</c>s it is the first (top) or last (bottom) block of:
    /// vertical ones collapse into the larger, horizontal ones add up.</summary>
    private static (string Top, string Right, string Bottom, string Left) StackedMargins(IElement block)
    {
        var own = Sides(Style(block), "margin");
        double? top = Pixels(own[0]), bottom = Pixels(own[2]);
        double right = Pixels(own[1]) ?? 0, left = Pixels(own[3]) ?? 0;
        var first = true;
        var last = true;
        for (var element = block; element.ParentElement is { LocalName: "div" } div; element = div)
        {
            first &= element.PreviousElementSibling is null;
            last &= element.NextElementSibling is null;
            var outer = Sides(Style(div), "margin");
            if (first && Pixels(outer[0]) is { } outerTop)
            {
                top = Math.Max(top ?? 0, outerTop);
            }

            if (last && Pixels(outer[2]) is { } outerBottom)
            {
                bottom = Math.Max(bottom ?? 0, outerBottom);
            }

            right += Pixels(outer[1]) ?? 0;
            left += Pixels(outer[3]) ?? 0;
        }

        return (Number(top), Number(right), Number(bottom), Number(left));

        static string Number(double? pixels) => pixels is { } value ? Math.Round(value, 2).ToString(CultureInfo.InvariantCulture) : "auto";
    }

    /// <summary>The text of a block in stretches of the same look: <c>[bold,#RRGGBB,font,underline]text</c>; line
    /// breaks as <c>⏎</c>, white space collapsed.</summary>
    private static string Stretches(IElement block)
    {
        var stretches = new List<(string Look, StringBuilder Text)>();
        foreach (var node in block.Descendants())
        {
            if (node is IText text && text.Data.Length > 0 && !IsInsideNestedBlock(text, block))
            {
                Add(Look(text), text.Data);
            }
            else if (node is IElement { LocalName: "br" } br && !IsInsideNestedBlock(br, block))
            {
                Add(stretches.Count > 0 ? stretches[^1].Look : "[]", "⏎");
            }
        }

        var result = new StringBuilder();
        foreach (var (look, text) in stretches)
        {
            result.Append(look).Append(Regex.Replace(text.ToString(), "[ \t\n\r\f]+", " "));
        }

        return result.ToString().Trim();

        void Add(string look, string value)
        {
            if (stretches.Count > 0 && stretches[^1].Look == look)
            {
                stretches[^1].Text.Append(value);
            }
            else
            {
                stretches.Add((look, new StringBuilder(value)));
            }
        }
    }

    private static bool IsInsideNestedBlock(INode node, IElement block)
    {
        for (var parent = node.ParentElement; parent is not null && parent != block; parent = parent.ParentElement)
        {
            if (parent.LocalName is "p" or "li" or "ol" or "ul" or "table" or "div")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The look of a text: bold, colour, font family and underline from the nearest element that sets them
    /// (a link's own colour and underline are the link's fact).</summary>
    private static string Look(IText text)
    {
        bool? bold = null;
        string? color = null, font = null;
        var underline = false;
        for (var element = text.ParentElement; element is not null && element.LocalName != "body"; element = element.ParentElement)
        {
            var style = Style(element);
            if (bold is null && (element.LocalName is "b" or "strong" || style.ContainsKey("font-weight")))
            {
                bold = element.LocalName is "b" or "strong" || style["font-weight"] is "bold" or "bolder" or "600" or "700" or "800" or "900";
            }

            if (color is null && element.LocalName != "a" && style.GetValueOrDefault("color") is { } c)
            {
                color = Color(c);
            }

            if (color is null && element.LocalName == "a")
            {
                color = "link";
            }

            font ??= style.GetValueOrDefault("font-family")?.Replace("'", string.Empty, StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace(", ", ",", StringComparison.Ordinal).ToLowerInvariant();
            underline |= element.LocalName is "u" or "ins" || (element.LocalName != "a" && style.GetValueOrDefault("text-decoration")?.Contains("underline", StringComparison.Ordinal) == true);
        }

        return "[" + (bold == true ? "bold," : string.Empty) + (color ?? "default") + "," + (font ?? "default") + (underline ? ",underline" : string.Empty) + "]";
    }

    /// <summary>Per side (top, right, bottom, left) the border as drawn: <c>none</c> or width, style and colour.</summary>
    private static IEnumerable<string> Borders(Dictionary<string, string> style, IElement? table)
    {
        // border="N" on the table: cells get a 1px inset border.
        var sides = Enumerable.Range(0, 4).Select(_ => table?.GetAttribute("border") is { } b && b != "0" ? (Width: "1", Style: "inset", Color: "#808080") : (Width: "3", Style: "none", Color: "default")).ToArray();
        foreach (var (property, value) in style)
        {
            var parts = property.Split('-');
            if (property == "border")
            {
                for (var i = 0; i < 4; i++)
                {
                    sides[i] = Shorthand(value);
                }
            }
            else if (parts is ["border", var side] && Index(side) is { } index)
            {
                sides[index] = Shorthand(value);
            }
        }

        return sides.Select(s => s.Style is "none" or "hidden" ? "none" : Length(s.Width) + " " + s.Style + " " + s.Color);

        static (string Width, string Style, string Color) Shorthand(string value)
        {
            var result = (Width: "3px", Style: "none", Color: "default");
            foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset")
                {
                    result.Style = token;
                }
                else if (Pixels(token) is not null || token is "thin" or "medium" or "thick")
                {
                    result.Width = token switch { "thin" => "1px", "medium" => "3px", "thick" => "5px", _ => token };
                }
                else
                {
                    result.Color = Color(token);
                }
            }

            return result;
        }

        static int? Index(string side) => side switch { "top" => 0, "right" => 1, "bottom" => 2, "left" => 3, _ => null };
    }

    /// <summary>The style attribute in declaration order, the last value of a property winning.</summary>
    private static Dictionary<string, string> Style(IElement element)
    {
        var style = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in CssParser.ParseDeclarations(element.GetAttribute("style")))
        {
            style.Remove(declaration.Property);
            style[declaration.Property] = declaration.Value.Trim();
        }

        return style;
    }

    /// <summary>The sides (top, right, bottom, left) of a margin or padding, null where not set.</summary>
    private static string?[] Sides(Dictionary<string, string> style, string property)
    {
        var sides = new string?[4];
        foreach (var (name, value) in style)
        {
            if (name == property)
            {
                var values = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                sides[0] = values[0];
                sides[1] = values.Length > 1 ? values[1] : values[0];
                sides[2] = values.Length > 2 ? values[2] : values[0];
                sides[3] = values.Length > 3 ? values[3] : sides[1];
            }
            else if (name.StartsWith(property + "-", StringComparison.Ordinal))
            {
                var index = name[(property.Length + 1)..] switch { "top" => 0, "right" => 1, "bottom" => 2, "left" => 3, _ => -1 };
                if (index >= 0)
                {
                    sides[index] = value;
                }
            }
        }

        return sides;
    }

    private static string Attribute(IElement element, string name) => element.GetAttribute(name) ?? "-";

    private static string Length(string? value) => value is null ? "-" : Pixels(value) is { } pixels ? Math.Round(pixels, 2).ToString(CultureInfo.InvariantCulture) : value;

    private static double? Pixels(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var v = value.Trim().ToLowerInvariant();
        (string Unit, double Factor)[] units = [("px", 1), ("pt", 96.0 / 72), ("em", 16), ("cm", 96 / 2.54), ("mm", 96 / 25.4), ("in", 96)];
        foreach (var (unit, factor) in units)
        {
            if (v.EndsWith(unit, StringComparison.Ordinal) && double.TryParse(v[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return number * factor;
            }
        }

        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : null;
    }

    /// <summary>A CSS colour as <c>#RRGGBB</c>; black (the message's colour) is <c>default</c>.</summary>
    private static string Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var v = value.Trim();
        if (v is "default" or "link")
        {
            return v;
        }

        string hex;
        if (v.StartsWith('#'))
        {
            hex = v.Length == 4 ? "#" + string.Concat(v[1..].Select(c => new string(c, 2))) : v;
        }
        else if (v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = v[4..^1].Split(',').Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
            hex = "#" + string.Concat(parts.Select(p => p.ToString("X2", CultureInfo.InvariantCulture)));
        }
        else
        {
            var named = System.Drawing.Color.FromName(v);
            hex = named.IsKnownColor ? "#" + named.R.ToString("X2", CultureInfo.InvariantCulture) + named.G.ToString("X2", CultureInfo.InvariantCulture) + named.B.ToString("X2", CultureInfo.InvariantCulture) : v;
        }

        hex = hex.ToUpperInvariant();
        return hex == "#000000" ? "default" : hex;
    }
}
