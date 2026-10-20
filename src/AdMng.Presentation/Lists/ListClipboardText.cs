using System.Text;
using System.Text.RegularExpressions;

namespace AdMng.Presentation.Lists;

/// <summary>
/// Ctrl+C on an object list: rows as tab-separated text (Excel, Teams, helpdesk tickets), one row per line, optionally
/// with the column headers first.
/// </summary>
public static partial class ListClipboardText
{
    /// <summary>
    /// The rows (already formatted, one cell per column) as tab-separated lines. Cells are passed through
    /// <see cref="Cell"/>.
    /// </summary>
    public static string Format(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, bool includeHeaders)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        if (includeHeaders)
        {
            AppendLine(text, headers);
        }

        foreach (var row in rows)
        {
            AppendLine(text, row);
        }

        // No line break after the last row: pasting into one Excel cell or a chat must not add an empty line.
        if (text.Length >= Environment.NewLine.Length)
        {
            text.Length -= Environment.NewLine.Length;
        }

        return text.ToString();
    }

    /// <summary>
    /// One cell: tabs and line breaks become spaces (they would split the cell), and a value starting with <c>=</c>,
    /// <c>+</c>, <c>-</c> or <c>@</c> gets an apostrophe so Excel does not run it as a formula (like the CSV export) -
    /// except plain numbers and phone numbers ("+48 22 123 45 67"), which cannot run anything and would otherwise carry a
    /// stray apostrophe into a chat or a ticket.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return text[0] is '=' or '+' or '-' or '@' && !NumberLike().IsMatch(text) ? "'" + text : text;
    }

    private static void AppendLine(StringBuilder text, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\t');
            }

            text.Append(Cell(cells[i]));
        }

        text.Append(Environment.NewLine);
    }

    [GeneratedRegex(@"^[+\-]?[0-9][0-9 ()./\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();
}
