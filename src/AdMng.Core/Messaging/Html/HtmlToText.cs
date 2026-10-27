using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Core.Messaging.Html;

/// <summary>
/// The plain-text part of an HTML message (design note section 3 step 10): blocks → lines (paragraphs separated by an
/// empty line), <c>&lt;br&gt;</c> → line break, list items → "- " / "1. ", table cells tab-separated, links → "text (url)"
/// unless the text is the URL, images → <c>[alt]</c> (nothing without alt). White space collapses as in a browser (not
/// inside <c>pre</c>); template fields stay fields.
/// </summary>
public static class HtmlToText
{
    private static readonly HashSet<string> ParagraphBlocks = new(StringComparer.Ordinal)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "table", "ul", "ol", "dl", "hr",
    };

    private static readonly HashSet<string> LineBlocks = new(StringComparer.Ordinal)
    {
        "div", "dt", "dd", "caption", "center", "body", "html",
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "head", "style", "script", "title", "template", "noscript", "svg", "math",
    };

    /// <summary>The text of an HTML fragment or document (empty when it is nested deeper than
    /// <see cref="MessageHtmlLimits.MaxNestingDepth"/> – such HTML is refused by every check).</summary>
    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html);
        if (HtmlNesting.IsTooDeep(document))
        {
            return string.Empty;
        }

        var writer = new TextWriterState();
        Walk(document.Body ?? document.DocumentElement, writer, new Context(null, 0, Preformatted: false, InCell: false));
        return writer.Result();
    }

    /// <summary>Where the walk is: the counter of the enclosing <c>ol</c> (null for <c>ul</c>), the list depth, inside
    /// <c>pre</c>, inside a table cell (blocks there are separated by a space only).</summary>
    private sealed record Context(int[]? ListCounter, int Depth, bool Preformatted, bool InCell);

    private static void Walk(INode node, TextWriterState writer, Context context)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    writer.Text(text.Data, context.Preformatted);
                    break;
                case IElement element:
                    Element(element, writer, context);
                    break;
            }
        }
    }

    private static void Element(IElement element, TextWriterState writer, Context context)
    {
        var preformatted = context.Preformatted;
        var name = element.LocalName;
        if (Skipped.Contains(name))
        {
            return;
        }

        switch (name)
        {
            case "br":
                writer.LineBreak();
                return;
            case "hr":
                writer.Paragraph();
                writer.Raw("----------");
                writer.Paragraph();
                return;
            case "img":
                if (element.GetAttribute("alt") is { Length: > 0 } alt && !string.IsNullOrWhiteSpace(alt))
                {
                    writer.Text("[" + alt.Trim() + "]", preformatted: false);
                }

                return;
            case "a":
            {
                var before = writer.Mark();
                Walk(element, writer, context);
                var text = writer.Since(before).Trim();
                var href = element.GetAttribute("href")?.Trim();
                if (!string.IsNullOrEmpty(href) && !SameAddress(text, href))
                {
                    writer.Text(text.Length == 0 ? href : " (" + href + ")", preformatted: false);
                }

                return;
            }

            case "ul" or "ol":
            {
                // A nested list continues the item's lines; a top-level one is a paragraph.
                Break(writer, context, context.Depth == 0 ? 2 : 1);
                var start = name == "ol" && int.TryParse(element.GetAttribute("start"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 1;
                Walk(element, writer, context with { ListCounter = name == "ol" ? [start] : null, Depth = context.Depth + 1 });
                Break(writer, context, context.Depth == 0 ? 2 : 1);
                return;
            }

            case "li":
                Break(writer, context, 1);
                var indent = new string(' ', Math.Max(0, context.Depth - 1) * 2);
                if (context.ListCounter is { Length: 1 } counter)
                {
                    writer.Marker(indent + counter[0].ToString(CultureInfo.InvariantCulture) + ". ");
                    counter[0]++;
                }
                else
                {
                    writer.Marker(indent + "- ");
                }

                Walk(element, writer, context with { ListCounter = null });
                Break(writer, context, 1);
                return;
            case "tr":
                Break(writer, context, 1);
                Walk(element, writer, context);
                Break(writer, context, 1);
                return;
            case "td" or "th":
                if (element.PreviousElementSibling is not null)
                {
                    writer.Raw("\t");
                }

                Walk(element, writer, context with { InCell = true });
                return;
        }

        var breaks = ParagraphBlocks.Contains(name) ? 2 : LineBlocks.Contains(name) ? 1 : 0;
        Break(writer, context, breaks);
        Walk(element, writer, context with { Preformatted = preformatted || name == "pre" });
        Break(writer, context, breaks);
    }

    /// <summary>Line breaks around a block; inside a table cell a block boundary is a space.</summary>
    private static void Break(TextWriterState writer, Context context, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (context.InCell)
        {
            writer.Text(" ", preformatted: false);
        }
        else if (count == 1)
        {
            writer.Line();
        }
        else
        {
            writer.Paragraph();
        }
    }

    private static bool SameAddress(string text, string href)
    {
        static string Strip(string value)
        {
            var v = value.Trim().TrimEnd('/');
            foreach (var prefix in new[] { "mailto:", "https://", "http://", "tel:" })
            {
                if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    v = v[prefix.Length..];
                }
            }

            return v.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? v[4..] : v;
        }

        return string.Equals(Strip(text), Strip(href), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the text with collapsed white space and pending line breaks.</summary>
    private sealed class TextWriterState
    {
        private readonly StringBuilder _text = new();
        private int _pendingBreaks;
        private bool _pendingSpace;
        private bool _afterMarker;

        public int Mark() => _text.Length;

        public string Since(int mark) => _text.ToString(mark, _text.Length - mark);

        public void Text(string value, bool preformatted)
        {
            if (preformatted)
            {
                Flush();
                _text.Append(value.Replace('\u00A0', ' ').ReplaceLineEndings("\n"));
                return;
            }

            foreach (var c in value)
            {
                if (c is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    _pendingSpace = _text.Length > 0 && _pendingBreaks == 0 && !char.IsWhiteSpace(_text[^1]);
                    continue;
                }

                Flush();
                if (_pendingSpace)
                {
                    _text.Append(' ');
                    _pendingSpace = false;
                }

                _afterMarker = false;
                _text.Append(c == '\u00A0' ? ' ' : c);
            }
        }

        public void Raw(string value)
        {
            Flush();
            _pendingSpace = false;
            _afterMarker = false;
            _text.Append(value);
        }

        /// <summary>A list marker: a block that starts the item's content does not break the line after it.</summary>
        public void Marker(string value)
        {
            Raw(value);
            _afterMarker = true;
        }

        public void LineBreak()
        {
            Flush();
            _afterMarker = false;
            _text.Append('\n');
            _pendingSpace = false;
        }

        public void Line() => Break(1);

        public void Paragraph() => Break(2);

        private void Break(int count)
        {
            if (_text.Length == 0 || _afterMarker)
            {
                return;
            }

            _pendingBreaks = Math.Max(_pendingBreaks, count);
            _pendingSpace = false;
        }

        private void Flush()
        {
            if (_pendingBreaks == 0)
            {
                return;
            }

            var existing = 0;
            for (var i = _text.Length - 1; i >= 0 && _text[i] == '\n'; i--)
            {
                existing++;
            }

            for (var i = existing; i < _pendingBreaks; i++)
            {
                _text.Append('\n');
            }

            _pendingBreaks = 0;
        }

        public string Result()
        {
            var lines = _text.ToString().Split('\n').Select(l => l.TrimEnd());
            var result = new StringBuilder();
            var blank = 0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    blank++;
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append(blank > 0 ? "\n\n" : "\n");
                }

                result.Append(line);
                blank = 0;
            }

            return result.ToString();
        }
    }
}
