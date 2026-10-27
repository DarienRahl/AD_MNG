using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace AdMng.Core.Messaging.Html;

/// <summary>
/// Word's lists → real HTML lists (design note section 3 step 5). Word (and classic Outlook) writes a list as paragraphs
/// with <c>mso-list:lN levelM lfoK</c> and a typed marker inside <c>&lt;![if !supportLists]&gt;</c> (or a leading
/// <c>&lt;span style="mso-list:Ignore"&gt;</c>). Consecutive sibling paragraphs become <c>ul</c>/<c>ol</c> with nested levels;
/// the marker decides the type (<c>·</c> <c>•</c> <c>-</c> <c>–</c> <c>o</c> <c>§</c> <c>Ø</c> <c>ü</c> → bullets, <c>1.</c>
/// <c>1)</c> → decimal with <c>start</c>, <c>a.</c> → lower-alpha, <c>A.</c> → upper-alpha, <c>i.</c> → lower-roman,
/// <c>I.</c> → upper-roman). The item keeps the paragraph's text properties; margins, <c>text-indent</c> and
/// <c>mso-list</c> are dropped; the list gets <c>margin-top:0;margin-bottom:0</c> when the paragraphs had no vertical margins.
/// </summary>
public static partial class WordListConverter
{
    /// <summary>Temporary attribute with the marker text captured from the <c>[if !supportLists]</c> region.</summary>
    public const string MarkerAttribute = "data-admng-list-marker";

    private static readonly string[] DroppedItemProperties =
    [
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left", "text-indent", "mso-list", "padding", "padding-left",
        "mso-add-space", "mso-margin-top-alt", "mso-margin-bottom-alt",
    ];

    /// <summary>The kind of a list.</summary>
    public sealed record ListStyle(string Element, string? ListStyleType, int Start);

    /// <summary>Converts every run of Word list paragraphs under <paramref name="root"/>; returns the number of lists.</summary>
    public static int Convert(IElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var count = 0;
        foreach (var parent in new[] { root }.Concat(root.Descendants<IElement>()).ToList())
        {
            if (parent.Parent is null && parent != root)
            {
                continue;
            }

            var run = new List<(IElement Paragraph, int Level)>();
            foreach (var child in parent.ChildNodes.ToList())
            {
                if (child is IText text && string.IsNullOrWhiteSpace(text.Data) && run.Count > 0)
                {
                    continue;
                }

                if (child is IElement element && ListLevel(element) is { } level)
                {
                    run.Add((element, level));
                    continue;
                }

                count += Flush(parent, run);
            }

            count += Flush(parent, run);
        }

        return count;
    }

    /// <summary>The level of a Word list paragraph (<c>mso-list:l0 level2 lfo1</c> → 2; Word has nine, so any other
    /// number is clamped to 1–9), or null.</summary>
    public static int? ListLevel(IElement element)
    {
        if (element.LocalName is not ("p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "div"))
        {
            return null;
        }

        var match = MsoListPattern().Match(element.GetAttribute("style") ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["level"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? Math.Clamp(level, 1, 9) : 9;
    }

    /// <summary>The list kind of a marker ("1." → ol decimal start 1, "·" → ul disc).</summary>
    public static ListStyle Classify(string? marker, bool firstOfLevel)
    {
        var value = new string([.. (marker ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '\u00A0')]);
        var number = NumberPattern().Match(value);
        if (number.Success)
        {
            var start = int.TryParse(number.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
            return new ListStyle("ol", null, start);
        }

        var letter = LetterPattern().Match(value);
        if (letter.Success)
        {
            var text = letter.Groups[1].Value;
            var lower = text.ToLowerInvariant();
            var upper = text == text.ToUpperInvariant();
            if (RomanValue(lower) is { } roman && (lower.Length > 1 || (firstOfLevel && lower == "i")))
            {
                return new ListStyle("ol", upper ? "upper-roman" : "lower-roman", roman);
            }

            if (text.Length == 1)
            {
                return new ListStyle("ol", upper ? "upper-alpha" : "lower-alpha", lower[0] - 'a' + 1);
            }
        }

        return value switch
        {
            "o" => new ListStyle("ul", "circle", 1),
            "§" or "▪" or "■" => new ListStyle("ul", "square", 1),
            _ => new ListStyle("ul", null, 1),
        };
    }

    private static int Flush(IElement parent, List<(IElement Paragraph, int Level)> run)
    {
        if (run.Count == 0)
        {
            return 0;
        }

        var document = parent.Owner!;
        var stack = new List<(int Level, IElement List, ListStyle Style)>();
        var created = 0;
        var anchor = run[0].Paragraph;
        IElement? topList = null;
        foreach (var (paragraph, level) in run)
        {
            while (stack.Count > 0 && stack[^1].Level > level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var marker = TakeMarker(paragraph);
            var style = Classify(marker, stack.Count == 0 || stack[^1].Level < level);
            if (stack.Count > 0 && stack[^1].Level == level && stack[^1].Style.Element != style.Element)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (stack.Count > 0 && stack[^1].Level == level)
            {
                style = stack[^1].Style;
            }
            else
            {
                var list = document.CreateElement(style.Element);
                if (style.Element == "ol" && style.Start != 1)
                {
                    list.SetAttribute("start", style.Start.ToString(CultureInfo.InvariantCulture));
                }

                var listStyle = new List<string>();
                if (HasNoVerticalMargins(paragraph))
                {
                    listStyle.Add("margin-top:0");
                    listStyle.Add("margin-bottom:0");
                }

                if (style.ListStyleType is { } type)
                {
                    listStyle.Add("list-style-type:" + type);
                }

                if (listStyle.Count > 0)
                {
                    list.SetAttribute("style", string.Join(";", listStyle));
                }

                if (stack.Count > 0 && stack[^1].List.LastElementChild is { } lastItem)
                {
                    lastItem.AppendChild(list);
                }
                else if (stack.Count > 0)
                {
                    stack[^1].List.AppendChild(list);
                }
                else
                {
                    // The first list takes the place of the first paragraph; a following top-level list goes after it.
                    parent.InsertBefore(list, topList is null ? anchor : topList.NextSibling);
                    topList = list;
                    created++;
                }

                stack.Add((level, list, style));
            }

            var item = document.CreateElement("li");
            if (ItemStyle(paragraph) is { Length: > 0 } itemStyle)
            {
                item.SetAttribute("style", itemStyle);
            }

            if (paragraph.GetAttribute("align") is { } align)
            {
                item.SetAttribute("align", align);
            }

            foreach (var node in paragraph.ChildNodes.ToList())
            {
                item.AppendChild(node);
            }

            stack[^1].List.AppendChild(item);
            paragraph.Parent?.RemoveChild(paragraph);
        }

        // White space between the former paragraphs.
        run.Clear();
        return created;
    }

    /// <summary>Removes and returns the paragraph's marker: the captured <c>[if !supportLists]</c> text, else a leading
    /// <c>&lt;span style="mso-list:Ignore"&gt;</c>.</summary>
    private static string? TakeMarker(IElement paragraph)
    {
        if (paragraph.GetAttribute(MarkerAttribute) is { } captured)
        {
            paragraph.RemoveAttribute(MarkerAttribute);
            return captured;
        }

        var ignore = paragraph.Descendants<IElement>().FirstOrDefault(e =>
            (e.GetAttribute("style") ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Contains("mso-list:ignore", StringComparison.OrdinalIgnoreCase));
        if (ignore is null)
        {
            return null;
        }

        // The marker span sits inside the first runs of the paragraph; its parents (font spans) go with it when empty.
        var marker = ignore.TextContent;
        var parent = ignore.Parent;
        parent?.RemoveChild(ignore);
        while (parent is IElement element && element != paragraph && element.ChildNodes.Length == 0)
        {
            var next = element.Parent;
            next?.RemoveChild(element);
            parent = next;
        }

        return marker;
    }

    private static string ItemStyle(IElement paragraph)
    {
        var declarations = CssParser.ParseDeclarations(paragraph.GetAttribute("style"))
            .Where(d => !DroppedItemProperties.Contains(d.Property, StringComparer.Ordinal));
        return string.Join(";", declarations.Select(d => d.Property + ":" + d.Value));
    }

    private static bool HasNoVerticalMargins(IElement paragraph)
    {
        var declarations = CssInliner.ToDictionary(CssParser.ParseDeclarations(paragraph.GetAttribute("style")));
        string? top = null, bottom = null;
        if (declarations.TryGetValue("margin", out var margin))
        {
            var parts = margin.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            top = parts.ElementAtOrDefault(0);
            bottom = parts.Length switch { 1 or 2 => parts[0], > 2 => parts[2], _ => null };
        }

        top = declarations.GetValueOrDefault("margin-top", top ?? string.Empty);
        bottom = declarations.GetValueOrDefault("margin-bottom", bottom ?? string.Empty);
        return IsZero(top) && IsZero(bottom);
    }

    private static bool IsZero(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CssValues.Length(value, allowNegative: true) is { } length
        && CssValues.Number(new string([.. length.Where(c => char.IsAsciiDigit(c) || c is '.' or '-')])) is "0" or "-0";

    private static int? RomanValue(string value)
    {
        if (value.Length is 0 or > 6 || value.Any(c => "ivxlcdm".IndexOf(c, StringComparison.Ordinal) < 0))
        {
            return null;
        }

        var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100, ['d'] = 500, ['m'] = 1000 };
        var total = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var current = values[value[i]];
            total += i + 1 < value.Length && values[value[i + 1]] > current ? -current : current;
        }

        return total;
    }

    [GeneratedRegex(@"mso-list\s*:\s*l\d+\s+level(?<level>\d+)\s+lfo\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsoListPattern();

    [GeneratedRegex(@"^(\d{1,4})[.)]$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^([A-Za-z]{1,6})[.)]$", RegexOptions.CultureInvariant)]
    private static partial Regex LetterPattern();
}
