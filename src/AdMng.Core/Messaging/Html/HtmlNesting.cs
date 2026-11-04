using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Core.Messaging.Html;

/// <summary>The nesting guard of every parse and tree walk over message HTML (<see cref="MessageHtmlLimits.MaxNestingDepth"/>).</summary>
internal static class HtmlNesting
{
    /// <summary>The nesting the estimate of <see cref="IsTextTooDeep"/> refuses: generous (the tree's own check is exact),
    /// but it bounds the parse – the HTML5 tree builder checks scopes along the stack of open elements, so its cost grows
    /// with the square of the depth (80 000 nested elements take about a minute).</summary>
    public const int MaxEstimatedDepth = MessageHtmlLimits.MaxNestingDepth * 4;

    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "img", "input", "keygen", "link", "meta",
        "param", "source", "track", "wbr",
    };

    /// <summary>Elements a later tag closes without their end tag: kept on the estimate's stack (their end tag closes what
    /// is still open inside), but not counted.</summary>
    private static readonly HashSet<string> ImpliedEnd = new(StringComparer.Ordinal)
    {
        "html", "head", "body", "p", "li", "dt", "dd", "option", "optgroup", "tr", "td", "th", "thead", "tbody", "tfoot", "colgroup",
        "caption", "rb", "rt", "rtc", "rp",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes",
    };

    /// <summary>Parses <paramref name="html"/> as a document, or null when it is nested too deeply: estimated from the text
    /// first (<see cref="IsTextTooDeep"/>), then measured on the tree (<see cref="IsTooDeep"/>).</summary>
    public static IDocument? ParseDocument(string html)
    {
        if (IsTextTooDeep(html))
        {
            return null;
        }

        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html);
        return IsTooDeep(document) ? null : document;
    }

    /// <summary>True when an element lies more than <see cref="MessageHtmlLimits.MaxNestingDepth"/> levels below
    /// <paramref name="root"/>. Iterative (an explicit stack), so it is safe on any parsed tree.</summary>
    public static bool IsTooDeep(INode root)
    {
        var stack = new Stack<(INode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MessageHtmlLimits.MaxNestingDepth)
            {
                return true;
            }

            foreach (var child in node.ChildNodes)
            {
                if (child is IElement)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A cheap estimate from the text, before parsing: true when more than <see cref="MaxEstimatedDepth"/> start tags are
    /// open at once. Void elements, comments and the content of raw-text elements do not count; an end tag closes its
    /// element and everything opened after it; elements with an implied end (<c>p</c>, <c>li</c>, <c>td</c> …) are not
    /// counted. Real messages stay far below it; a page of unclosed <c>div</c> or <c>&lt;div/&gt;</c> does not.
    /// </summary>
    public static bool IsTextTooDeep(string html)
    {
        var stack = new List<(string Name, bool Counted)>();
        var open = new Dictionary<string, int>(StringComparer.Ordinal);
        var depth = 0;
        var i = 0;
        while ((i = html.IndexOf('<', i)) >= 0 && i + 1 < html.Length)
        {
            if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            var closing = html[i + 1] == '/';
            var nameStart = closing ? i + 2 : i + 1;
            if (nameStart >= html.Length || !char.IsAsciiLetter(html[nameStart]))
            {
                i++;
                continue;
            }

            var nameEnd = nameStart;
            while (nameEnd < html.Length && !char.IsWhiteSpace(html[nameEnd]) && html[nameEnd] is not ('/' or '>'))
            {
                nameEnd++;
            }

            var name = html[nameStart..nameEnd].ToLowerInvariant();
            i = UserMessageRules.TagEnd(html, nameEnd);
            if (closing)
            {
                if (open.GetValueOrDefault(name) > 0)
                {
                    while (stack.Count > 0)
                    {
                        var (top, counted) = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        open[top]--;
                        depth -= counted ? 1 : 0;
                        if (top == name)
                        {
                            break;
                        }
                    }
                }

                continue;
            }

            if (name == "plaintext")
            {
                break;
            }

            if (RawText.Contains(name))
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
                continue;
            }

            if (VoidElements.Contains(name))
            {
                continue;
            }

            var countedHere = !ImpliedEnd.Contains(name);
            stack.Add((name, countedHere));
            open[name] = open.GetValueOrDefault(name) + 1;
            if (countedHere && ++depth > MaxEstimatedDepth)
            {
                return true;
            }
        }

        return false;
    }
}
