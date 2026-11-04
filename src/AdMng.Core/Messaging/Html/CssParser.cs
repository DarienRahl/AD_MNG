using System.Text;

namespace AdMng.Core.Messaging.Html;

/// <summary>One CSS declaration (<c>property: value [!important]</c>); the property in lower case, the value trimmed.</summary>
public sealed record CssDeclaration(string Property, string Value, bool Important = false)
{
    public override string ToString() => Property + ":" + Value + (Important ? " !important" : string.Empty);
}

/// <summary>A simple selector of the supported subset: a type, a class, both, or an id (<c>p</c>, <c>.MsoNormal</c>,
/// <c>p.MsoNormal</c>, <c>#x</c>, <c>a:link</c> as <c>a</c>).</summary>
public sealed record CssSelector(string? Type, string? Class, string? Id, int Specificity)
{
    /// <summary>True when the element (lower-case name, its class list and id) matches. Class and id compare
    /// case-insensitively (pasted Office HTML renders in quirks mode).</summary>
    public bool Matches(string elementName, IReadOnlyList<string> classes, string? id) =>
        (Type is null || string.Equals(Type, elementName, StringComparison.OrdinalIgnoreCase))
        && (Class is null || classes.Contains(Class, StringComparer.OrdinalIgnoreCase))
        && (Id is null || string.Equals(Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A style rule with supported selectors only; <see cref="Order"/> is its position in the style sheets.</summary>
public sealed record CssRule(IReadOnlyList<CssSelector> Selectors, IReadOnlyList<CssDeclaration> Declarations, int Order);

/// <summary>
/// A minimal CSS parser for the message sanitizer (design note section 4.3): declarations of <c>style</c> attributes and
/// the style rules of <c>&lt;style&gt;</c> elements with the selector subset <c>type</c>, <c>.class</c>,
/// <c>type.class</c>, <c>#id</c>, selector lists and <c>a:link</c>. Rules with any other selector (combinators, attribute
/// selectors, other pseudo-classes) and every at-rule (<c>@media</c>, <c>@font-face</c>, <c>@page</c>, <c>@list</c>,
/// <c>@import</c>) are skipped; the CDO/CDC tokens Outlook wraps its style sheet in are ignored, comments removed.
/// </summary>
public static class CssParser
{
    /// <summary>The declarations of a <c>style</c> attribute (or a rule body), in source order. Invalid ones are skipped.</summary>
    public static IReadOnlyList<CssDeclaration> ParseDeclarations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<CssDeclaration>();
        foreach (var part in SplitTopLevel(text, ';'))
        {
            var colon = IndexOfTopLevel(part, ':');
            if (colon <= 0)
            {
                continue;
            }

            var property = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();
            if (property.Length == 0 || !property.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                continue;
            }

            var important = false;
            var bang = value.LastIndexOf('!');
            if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..bang].TrimEnd();
            }

            result.Add(new CssDeclaration(property, value, important));
        }

        return result;
    }

    /// <summary>The supported style rules of a style sheet (in order; <paramref name="orderOffset"/> continues the
    /// numbering across several <c>&lt;style&gt;</c> elements).</summary>
    public static IReadOnlyList<CssRule> ParseStyleSheet(string? css, int orderOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return [];
        }

        var text = RemoveComments(css);
        var rules = new List<CssRule>();
        var i = 0;
        var order = orderOffset;
        while (i < text.Length)
        {
            // CDO / CDC and white space between rules.
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                i += 4;
                continue;
            }

            if (string.CompareOrdinal(text, i, "-->", 0, 3) == 0)
            {
                i += 3;
                continue;
            }

            if (text[i] == '@')
            {
                i = SkipAtRule(text, i);
                continue;
            }

            var open = IndexOfTopLevel(text, '{', i);
            if (open < 0)
            {
                break;
            }

            var close = MatchingBrace(text, open);
            var selectorText = text[i..open];
            var body = text[(open + 1)..Math.Max(open + 1, close)];
            i = close < 0 ? text.Length : close + 1;
            var selectors = ParseSelectors(selectorText);
            if (selectors is null)
            {
                continue;
            }

            rules.Add(new CssRule(selectors, ParseDeclarations(body), order++));
        }

        return rules;
    }

    /// <summary>The selectors of a selector list, or null when any of them is outside the supported subset.</summary>
    public static IReadOnlyList<CssSelector>? ParseSelectors(string selectorText)
    {
        var result = new List<CssSelector>();
        foreach (var raw in selectorText.Split(','))
        {
            var selector = ParseSelector(raw.Trim());
            if (selector is null)
            {
                return null;
            }

            result.Add(selector);
        }

        return result.Count == 0 ? null : result;
    }

    private static CssSelector? ParseSelector(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var specificity = 0;
        if (text.EndsWith(":link", StringComparison.OrdinalIgnoreCase) && text.Length > 5)
        {
            text = text[..^5];
            if (!text.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            specificity += 10;
        }

        if (text.StartsWith('#'))
        {
            var id = text[1..];
            return IsIdentifier(id) ? new CssSelector(null, null, id, 100) : null;
        }

        string? type = null;
        string? cls = null;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            type = text;
        }
        else
        {
            type = dot == 0 ? null : text[..dot];
            cls = text[(dot + 1)..];
            if (!IsIdentifier(cls))
            {
                return null;
            }

            specificity += 10;
        }

        if (type is not null)
        {
            if (!type.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetter(type[0]))
            {
                return null;
            }

            specificity += 1;
        }

        return new CssSelector(type?.ToLowerInvariant(), cls, null, specificity);
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') && !char.IsAsciiDigit(value[0]);

    /// <summary>Skips an at-rule: up to its <c>;</c> or its block (nested blocks included).</summary>
    private static int SkipAtRule(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case ';':
                    return i + 1;
                case '{':
                    var close = MatchingBrace(text, i);
                    return close < 0 ? text.Length : close + 1;
                case '"' or '\'':
                    i = SkipString(text, i);
                    break;
            }
        }

        return text.Length;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
                case '"' or '\'':
                    i = SkipString(text, i);
                    break;
            }
        }

        return -1;
    }

    /// <summary>The index of the closing quote of the string starting at <paramref name="start"/> (or the end).</summary>
    private static int SkipString(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote || text[i] is '\n' or '\r')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static string RemoveComments(string css)
    {
        var builder = new StringBuilder(css.Length);
        for (var i = 0; i < css.Length; i++)
        {
            if (css[i] is '"' or '\'')
            {
                var end = SkipString(css, i);
                builder.Append(css, i, end - i + 1);
                i = end;
                continue;
            }

            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 1;
                builder.Append(' ');
                continue;
            }

            builder.Append(css[i]);
        }

        return builder.ToString();
    }

    /// <summary>Splits on <paramref name="separator"/> outside quotes and parentheses.</summary>
    internal static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipString(text, i);
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
            else if (c == separator && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static int IndexOfTopLevel(string text, char value, int from = 0)
    {
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipString(text, i);
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
            else if (c == value && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
