using System.Text;
using AngleSharp.Dom;

namespace AdMng.Core.Messaging.Html;

/// <summary>
/// Deterministic serialization of sanitized HTML (design note section 4.3): lower-case names, attributes in the policy's
/// order (element-specific ones, then <c>title lang dir</c>, then <c>style</c>) and double-quoted, text escaped minimally
/// (<c>&amp;amp; &amp;lt; &amp;gt; &amp;nbsp;</c> – Polish letters stay literal UTF-8), a line break after the end tag of a block
/// element and after <c>&lt;br&gt;</c> when an element (or nothing) follows. The output parses back to the same tree.
/// </summary>
internal static class HtmlSerializer
{
    private static readonly HashSet<string> BooleanAttributes = new(StringComparer.Ordinal) { "nowrap", "reversed" };

    /// <summary>The children of <paramref name="container"/> (a fragment).</summary>
    public static string Children(IElement container)
    {
        var builder = new StringBuilder();
        foreach (var child in container.ChildNodes)
        {
            Write(builder, child, preformatted: false);
        }

        return builder.ToString();
    }

    /// <summary>A whole document: <c>&lt;!DOCTYPE html&gt;</c> and the <c>html</c> element.</summary>
    public static string Document(IElement html)
    {
        var builder = new StringBuilder("<!DOCTYPE html>\n");
        Write(builder, html, preformatted: false);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, INode node, bool preformatted)
    {
        switch (node)
        {
            case IText text:
                EscapeText(builder, text.Data);
                break;
            case IElement element:
                WriteElement(builder, element, preformatted);
                break;
        }
    }

    private static void WriteElement(StringBuilder builder, IElement element, bool preformatted)
    {
        var name = element.LocalName.ToLowerInvariant();
        builder.Append('<').Append(name);
        foreach (var attribute in OrderedAttributes(element, name))
        {
            builder.Append(' ').Append(attribute.Name.ToLowerInvariant());
            if (BooleanAttributes.Contains(attribute.Name))
            {
                continue;
            }

            builder.Append("=\"");
            EscapeAttribute(builder, attribute.Value);
            builder.Append('"');
        }

        builder.Append('>');
        if (HtmlPolicy.VoidElements.Contains(name))
        {
            LineBreakAfter(builder, element, name, preformatted);
            return;
        }

        var inPre = preformatted || name == "pre";
        if (name is "html" or "body" && element.FirstChild is null or IElement)
        {
            builder.Append('\n');
        }

        if (name == "pre" && element.FirstChild is IText { Data: ['\n', ..] })
        {
            // The parser drops one line break right after <pre>: keep the content's own one.
            builder.Append('\n');
        }

        foreach (var child in element.ChildNodes)
        {
            Write(builder, child, inPre);
        }

        builder.Append("</").Append(name).Append('>');
        LineBreakAfter(builder, element, name, preformatted);
    }

    private static void LineBreakAfter(StringBuilder builder, IElement element, string name, bool preformatted)
    {
        if (!preformatted && (HtmlPolicy.BlockElements.Contains(name) || name == "br") && element.NextSibling is null or IElement)
        {
            builder.Append('\n');
        }
    }

    private static IEnumerable<IAttr> OrderedAttributes(IElement element, string name)
    {
        var order = new List<string>();
        if (HtmlPolicy.ElementAttributes.TryGetValue(name, out var specific))
        {
            order.AddRange(specific);
        }

        order.AddRange(HtmlPolicy.GlobalAttributes);
        order.Add("style");
        var attributes = element.Attributes.ToList();
        return attributes
            .OrderBy(a => order.IndexOf(a.Name.ToLowerInvariant()) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(a => a.Name, StringComparer.Ordinal);
    }

    private static void EscapeText(StringBuilder builder, string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '\u00A0':
                    builder.Append("&nbsp;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }

    private static void EscapeAttribute(StringBuilder builder, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '\u00A0':
                    builder.Append("&nbsp;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }
}
