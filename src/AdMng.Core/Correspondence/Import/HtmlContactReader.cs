using System.Text;
using AdMng.Core.Messaging.Html;
using AngleSharp.Dom;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// Contacts of an HTML file (design note 3.1), parsed with AngleSharp: every <c>&lt;table&gt;</c> → rows (the text of its
/// own <c>th</c>/<c>td</c> cells; a nested table is a table of its own, not text of the outer cell), then the remaining
/// body text, in which (as in cells) a <c>mailto:</c> link reads "text &lt;address&gt;". Scripts, styles, templates and
/// the head are ignored.
/// </summary>
internal static class HtmlContactReader
{
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "head", "script", "style", "template", "noscript", "title", "svg", "math", "iframe", "object", "select", "textarea",
    };

    private static readonly HashSet<string> LineElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "caption", "center", "dd", "div", "dl", "dt", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre",
        "section", "ul", "tr", "table", "tbody", "thead", "tfoot",
    };

    public static ContactImportResult Read(string html)
    {
        var document = HtmlNesting.ParseDocument(html) ?? throw new InvalidDataException("The HTML is nested too deeply.");
        var root = (INode?)document.Body ?? document.DocumentElement;
        var accumulator = new ContactAccumulator();
        foreach (var table in root.Descendants<IElement>().Where(e => e.LocalName == "table" && !InIgnored(e)))
        {
            var rows = Rows(table);
            if (rows.Count > 0)
            {
                accumulator.Merge(ContactExtractor.FromRows(rows, ContactSourceFormat.Html));
            }
        }

        var text = new StringBuilder();
        AppendText(root, text, skipTables: true, cellSeparator: '\t');
        accumulator.Merge(ContactExtractor.FromText(text.ToString()));
        return accumulator.Result(ContactSourceFormat.Html);
    }

    /// <summary>The rows of a table: the <c>tr</c> elements whose nearest table is this one, each as the text of its own
    /// cells (<c>colspan</c> adds empty cells, capped at the column limit).</summary>
    private static List<IReadOnlyList<string>> Rows(IElement table)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var tr in table.Descendants<IElement>().Where(e => e.LocalName == "tr" && NearestTable(e) == table))
        {
            var cells = new List<string>();
            foreach (var cell in tr.Children.Where(c => c.LocalName is "td" or "th"))
            {
                if (cells.Count >= SpreadsheetLimits.MaxColumns)
                {
                    break;
                }

                var text = new StringBuilder();
                AppendText(cell, text, skipTables: true, cellSeparator: ' ');
                cells.Add(text.ToString().Trim());
                var span = int.TryParse(cell.GetAttribute("colspan"), out var s) ? Math.Clamp(s, 1, 64) : 1;
                for (var i = 1; i < span && cells.Count < SpreadsheetLimits.MaxColumns; i++)
                {
                    cells.Add(string.Empty);
                }
            }

            if (cells.Any(c => c.Length > 0))
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    private static IElement? NearestTable(IElement element)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.LocalName == "table")
            {
                return parent;
            }
        }

        return null;
    }

    private static bool InIgnored(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (Ignored.Contains(e.LocalName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text under <paramref name="node"/> without recursion: block elements and <c>br</c> → line breaks,
    /// cells → <paramref name="cellSeparator"/>, <c>mailto:</c> links → "text &lt;address&gt;".</summary>
    private static void AppendText(INode node, StringBuilder builder, bool skipTables, char cellSeparator)
    {
        var stack = new Stack<(INode Node, bool Closing)>();
        var links = new Stack<(int Start, string? Target)>();
        for (var child = node.LastChild; child is not null; child = child.PreviousSibling)
        {
            stack.Push((child, false));
        }

        while (stack.Count > 0)
        {
            var (current, closing) = stack.Pop();
            if (current is IText text)
            {
                // Source line breaks are spaces to a browser (a contact list is no <pre>).
                foreach (var c in text.Data)
                {
                    builder.Append(c is '\r' or '\n' or '\t' or '\f' ? ' ' : c);
                }

                continue;
            }

            if (current is not IElement element)
            {
                continue;
            }

            var name = element.LocalName;
            if (closing)
            {
                if (name == "a" && links.Count > 0)
                {
                    var (start, target) = links.Pop();
                    DocumentParts.AppendLinkAddress(builder, start, target);
                }
                else if (name is "td" or "th")
                {
                    builder.Append(cellSeparator);
                }
                else if (LineElements.Contains(name))
                {
                    builder.Append('\n');
                }

                continue;
            }

            if (Ignored.Contains(name) || (skipTables && name == "table"))
            {
                continue;
            }

            if (name == "br")
            {
                builder.Append('\n');
                continue;
            }

            if (name == "a")
            {
                links.Push((builder.Length, element.GetAttribute("href")));
            }
            else if (LineElements.Contains(name))
            {
                builder.Append('\n');
            }

            stack.Push((element, true));
            for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
            {
                stack.Push((child, false));
            }
        }
    }
}
