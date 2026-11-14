using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The table of the HTML clipboard format or of a pasted <c>.html</c> file (docs/dev/phase3-bulk-create.md 2.1, owner B1):
/// the <c>&lt;table&gt;</c> with the most own rows; own <c>td</c>/<c>th</c> cells only (a nested table is skipped, also as
/// text of its cell); <c>&lt;br&gt;</c> and block elements → CRLF; text only (a <c>mailto:</c> link is its text); the
/// <c>colspan</c> and <c>rowspan</c> grid (the cells a merged cell covers are empty, the row is marked); Excel's
/// <c>x:num</c> integer of each cell. <c>HtmlContactReader</c> of U5 is neither reused nor edited (it appends link addresses
/// to the text). Pure; cells are raw (normalized by the caller).
/// </summary>
internal static class HtmlTableReader
{
    /// <summary>A merged cell spans at most this many rows (the rest of a larger <c>rowspan</c> is ignored).</summary>
    private const int MaxRowSpan = TableTextReader.MaxRows + 1;

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "head", "script", "style", "template", "noscript", "title", "svg", "math", "iframe", "object", "select", "textarea",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "dd", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "ul",
    };

    /// <summary>The table with the most own rows below <paramref name="root"/> (the first on a tie), or null when there is none.</summary>
    public static HtmlTable? Read(INode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        IElement? best = null;
        var bestRows = 0;
        foreach (var table in root.Descendants<IElement>().Where(e => e.LocalName == "table" && !InIgnored(e)))
        {
            var count = OwnRows(table).Count();
            if (count > bestRows)
            {
                best = table;
                bestRows = count;
            }
        }

        return best is null ? null : Grid(best);
    }

    /// <summary>The rows of a table in tree order: its own <c>tr</c> children and those of its <c>thead</c>, <c>tbody</c>, <c>tfoot</c>.</summary>
    private static IEnumerable<IElement> OwnRows(IElement table)
    {
        foreach (var child in table.Children)
        {
            if (child.LocalName == "tr")
            {
                yield return child;
            }
            else if (child.LocalName is "thead" or "tbody" or "tfoot")
            {
                foreach (var row in child.Children.Where(c => c.LocalName == "tr"))
                {
                    yield return row;
                }
            }
        }
    }

    private static HtmlTable Grid(IElement table)
    {
        var rows = new List<IReadOnlyList<string>>();
        var numbers = new List<IReadOnlyList<string?>>();
        var merged = new HashSet<int>();
        var covered = new int[TableTextReader.MaxColumns];
        var droppedColumns = 0;
        foreach (var tr in OwnRows(table))
        {
            var cells = new List<string>();
            var cellNumbers = new List<string?>();
            var column = 0;

            void Covered()
            {
                // A cell of a rowspan from a row above: empty here.
                while (column < covered.Length && covered[column] > 0)
                {
                    covered[column]--;
                    cells.Add(string.Empty);
                    cellNumbers.Add(null);
                    column++;
                }
            }

            var rowCells = tr.Children.Where(c => c.LocalName is "td" or "th").ToList();
            for (var index = 0; index < rowCells.Count; index++)
            {
                var cell = rowCells[index];
                Covered();
                if (column >= covered.Length)
                {
                    droppedColumns = Math.Max(droppedColumns, rowCells.Count - index);
                    break;
                }

                var colspan = Span(cell, "colspan", covered.Length - column);
                var rowspan = Span(cell, "rowspan", MaxRowSpan);
                if (colspan > 1 || rowspan > 1)
                {
                    merged.Add(rows.Count);
                }

                cells.Add(Text(cell));
                cellNumbers.Add(Number(cell));
                for (var k = 0; k < colspan; k++)
                {
                    if (k > 0)
                    {
                        cells.Add(string.Empty);
                        cellNumbers.Add(null);
                    }

                    if (rowspan > 1)
                    {
                        covered[column + k] = Math.Max(covered[column + k], rowspan - 1);
                    }
                }

                column += colspan;
            }

            // Columns of a rowspan to the right of the row's last cell.
            var last = Array.FindLastIndex(covered, c => c > 0);
            while (column <= last)
            {
                if (covered[column] > 0)
                {
                    covered[column]--;
                }

                cells.Add(string.Empty);
                cellNumbers.Add(null);
                column++;
            }

            rows.Add(cells);
            numbers.Add(cellNumbers);
        }

        return new HtmlTable(rows, numbers, merged, droppedColumns);
    }

    /// <summary>The <c>colspan</c> / <c>rowspan</c> of a cell: 1 when absent, invalid or 0, else at most <paramref name="max"/>.</summary>
    private static int Span(IElement cell, string attribute, int max) =>
        int.TryParse(cell.GetAttribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var span) && span > 1
            ? Math.Min(span, Math.Max(1, max))
            : 1;

    /// <summary>Excel's full numeric value of the cell (<c>x:num="486001002001"</c>) when it is an integer, else null.</summary>
    private static string? Number(IElement cell)
    {
        var value = cell.GetAttribute("x:num")?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length <= 28 && value.All(char.IsAsciiDigit))
        {
            return value;
        }

        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number >= 0 && number == decimal.Truncate(number)
            ? number.ToString("0", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The text of a cell without recursion: nested tables and ignored elements skipped, <c>br</c> and blocks → CRLF, source white space → one space.</summary>
    private static string Text(IElement cell)
    {
        var builder = new StringBuilder();
        var stack = new Stack<(INode Node, bool Closing)>();
        for (var child = cell.LastChild; child is not null; child = child.PreviousSibling)
        {
            stack.Push((child, false));
        }

        while (stack.Count > 0)
        {
            var (node, closing) = stack.Pop();
            if (node is IText text)
            {
                foreach (var c in text.Data)
                {
                    builder.Append(c is '\r' or '\n' or '\t' or '\f' ? ' ' : c);
                }

                continue;
            }

            if (node is not IElement element)
            {
                continue;
            }

            var name = element.LocalName;
            if (name == "br")
            {
                builder.Append("\r\n");
                continue;
            }

            if (name == "table" || Ignored.Contains(name))
            {
                continue;
            }

            var block = BlockElements.Contains(name);
            if (closing)
            {
                if (block)
                {
                    LineBreak(builder);
                }

                continue;
            }

            if (block)
            {
                LineBreak(builder);
                stack.Push((element, true));
            }

            for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
            {
                stack.Push((child, false));
            }
        }

        return builder.ToString();
    }

    private static void LineBreak(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append("\r\n");
        }
    }

    private static bool InIgnored(IElement element)
    {
        for (var e = element.ParentElement; e is not null; e = e.ParentElement)
        {
            if (Ignored.Contains(e.LocalName))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The cells of an HTML table (<see cref="HtmlTableReader.Read"/>): raw text, Excel's <c>x:num</c> integers, merged rows.</summary>
internal sealed class HtmlTable(
    IReadOnlyList<IReadOnlyList<string>> rows,
    IReadOnlyList<IReadOnlyList<string?>> numbers,
    IReadOnlySet<int> mergedRows,
    int droppedColumns)
{
    /// <summary>The rows (0-based, every row of the table) with their cells; the cells a merged cell covers are empty.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; } = rows;

    /// <summary>Per row and cell, Excel's full integer (<c>x:num</c>) or null.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Numbers { get; } = numbers;

    /// <summary>The 0-based rows with a <c>colspan</c> or <c>rowspan</c> cell.</summary>
    public IReadOnlySet<int> MergedRows { get; } = mergedRows;

    /// <summary>The most cells of a row beyond <see cref="TableTextReader.MaxColumns"/> that were dropped (0: none).</summary>
    public int DroppedColumns { get; } = droppedColumns;

    /// <summary>Counts only – cells are pasted data.</summary>
    public override string ToString() => $"HTML table: {Rows.Count} rows";
}
