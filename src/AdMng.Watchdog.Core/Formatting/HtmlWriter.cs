using System.Collections.Frozen;
using System.Text;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// A tiny HTML builder for notification bodies that encodes everything (section 14.3): text and attribute values are
/// always HTML-encoded, element and attribute names come from a fixed allow-list (no scripts, no images, no event
/// handlers), and <c>href</c> accepts only absolute http(s) URLs. Elements must be closed in order.
/// </summary>
public sealed class HtmlWriter
{
    private static readonly FrozenSet<string> Elements = new[]
    {
        "html", "head", "body", "meta", "title", "style", "div", "span", "p", "table", "thead", "tbody", "tr", "td", "th",
        "a", "b", "strong", "em", "br", "hr", "h1", "h2", "h3", "ul", "ol", "li", "small", "code",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> VoidElements = new[] { "meta", "br", "hr" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Attributes = new[]
    {
        "style", "href", "width", "cellpadding", "cellspacing", "border", "align", "valign", "role", "lang", "colspan",
        "dir", "charset", "name", "content", "bgcolor",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly StringBuilder _html = new();
    private readonly Stack<string> _open = new();

    /// <summary>Encodes text for an HTML text node or a quoted attribute value: <c>&amp; &lt; &gt; " '</c> become
    /// entities, everything else (Polish letters, '+') stays readable.</summary>
    public static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                '\'' => builder.Append("&#39;"),
                _ => builder.Append(c),
            };
        }

        return builder.ToString();
    }

    /// <summary>Writes <c>&lt;!DOCTYPE html&gt;</c>.</summary>
    public HtmlWriter Doctype()
    {
        _html.Append("<!DOCTYPE html>\n");
        return this;
    }

    /// <summary>Opens an element.</summary>
    public HtmlWriter Open(string element, params (string Name, string? Value)[] attributes)
    {
        WriteStartTag(element, attributes);
        if (!VoidElements.Contains(element))
        {
            _open.Push(element);
        }

        return this;
    }

    /// <summary>Closes the innermost open element.</summary>
    public HtmlWriter Close()
    {
        if (_open.Count == 0)
        {
            throw new InvalidOperationException("No open element.");
        }

        _html.Append("</").Append(_open.Pop()).Append('>');
        return this;
    }

    /// <summary>Writes an element with an encoded text content.</summary>
    public HtmlWriter Element(string element, string? text, params (string Name, string? Value)[] attributes)
    {
        Open(element, attributes);
        Text(text);
        return VoidElements.Contains(element) ? this : Close();
    }

    /// <summary>Writes encoded text.</summary>
    public HtmlWriter Text(string? text)
    {
        _html.Append(Encode(text));
        return this;
    }

    /// <summary>Writes encoded text with its line breaks as <c>&lt;br&gt;</c>.</summary>
    public HtmlWriter MultiLineText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                _html.Append("<br>");
            }

            Text(lines[i]);
        }

        return this;
    }

    /// <summary>A link with an encoded text; <paramref name="url"/> must be an absolute http(s) URL.</summary>
    public HtmlWriter Link(string url, string? text, string? style = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Only absolute http(s) URLs can be linked.", nameof(url));
        }

        return Element("a", text, ("href", uri.AbsoluteUri), ("style", style));
    }

    /// <summary>The document; every element must be closed.</summary>
    public override string ToString() =>
        _open.Count == 0 ? _html.ToString() : throw new InvalidOperationException("Unclosed element: " + _open.Peek());

    private void WriteStartTag(string element, (string Name, string? Value)[] attributes)
    {
        if (!Elements.Contains(element))
        {
            throw new ArgumentException("Element not allowed: " + element, nameof(element));
        }

        _html.Append('<').Append(element);
        foreach (var (name, value) in attributes)
        {
            if (!Attributes.Contains(name))
            {
                throw new ArgumentException("Attribute not allowed: " + name, nameof(attributes));
            }

            if (value is null)
            {
                continue;
            }

            if (name == "href" && !(value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Only absolute http(s) URLs can be linked.", nameof(attributes));
            }

            _html.Append(' ').Append(name).Append("=\"").Append(Encode(value)).Append('"');
        }

        _html.Append('>');
    }
}
