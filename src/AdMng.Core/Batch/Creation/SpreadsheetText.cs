using System.Text;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The tab-separated text Excel, LibreOffice and Google Sheets put on the clipboard (docs/dev/phase3-bulk-create.md 2.1,
/// owner B1). Not <c>DelimitedText</c>: Excel quotes a cell only when it holds a tab or a line break, so a cell is quoted
/// only when it starts with <c>"</c> <b>and</b> its closing quote (the first one that is not doubled) is followed by a tab,
/// CR, LF or the end of the text; otherwise every quote is a literal character – <c>"Społem" PSS</c> keeps its quotes and an
/// unbalanced opening quote does not swallow the following rows. CRLF, LF-only and CR line endings and a missing final line
/// break are accepted. Pure.
/// </summary>
internal static class SpreadsheetText
{
    /// <summary>A quoted cell longer than this is not taken as quoted (a stray quote would otherwise scan the whole text).</summary>
    private const int MaxQuotedLength = TableTextReader.MaxCellLength + 1;

    /// <summary>The lines of <paramref name="text"/>, each as its cells (raw: not normalized). A final line break adds no empty line.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> SplitTsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<IReadOnlyList<string>>();
        if (text.Length == 0)
        {
            return lines;
        }

        var line = new List<string>();
        var i = 0;
        while (true)
        {
            string cell;
            if (i < text.Length && text[i] == '"' && TryQuoted(text, i, out var quoted, out var next))
            {
                cell = quoted;
                i = next;
            }
            else
            {
                var end = text.AsSpan(i).IndexOfAny('\t', '\r', '\n');
                end = end < 0 ? text.Length : i + end;
                cell = text[i..end];
                i = end;
            }

            line.Add(cell);
            if (i >= text.Length)
            {
                lines.Add(line);
                return lines;
            }

            if (text[i] == '\t')
            {
                i++;
                continue;
            }

            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add(line);
            if (i >= text.Length)
            {
                return lines;
            }

            line = [];
        }
    }

    /// <summary>True when a tab occurs in the first <paramref name="lines"/> lines (the text is a table, not a single column).</summary>
    public static bool HasTabInFirstLines(string text, int lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\t':
                    return true;
                case '\n':
                    seen++;
                    break;
                case '\r':
                    seen++;
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
            }

            if (seen >= lines)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>True when any cell (split on tab and line breaks, trimmed) is in scientific notation – a fast scan without allocation.</summary>
    public static bool ContainsScientificCell(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan();
        while (span.Length > 0)
        {
            var end = span.IndexOfAny('\t', '\r', '\n');
            var cell = end < 0 ? span : span[..end];
            var trimmed = cell.Trim();

            // The shortest form is "1E5"; 'E' or 'e' must be in it.
            if (trimmed.Length >= 3 && trimmed.ContainsAny('E', 'e') && CellText.IsScientific(trimmed))
            {
                return true;
            }

            if (end < 0)
            {
                break;
            }

            span = span[(end + 1)..];
        }

        return false;
    }

    /// <summary>
    /// The quoted cell starting at <paramref name="start"/>: up to the first quote that is not doubled, which must be followed
    /// by a tab, a line break or the end of the text (else the cell is not quoted and its quotes are literal).
    /// </summary>
    private static bool TryQuoted(string text, int start, out string cell, out int next)
    {
        cell = string.Empty;
        next = start;
        var builder = new StringBuilder();
        var i = start + 1;
        while (i < text.Length)
        {
            var quote = text.IndexOf('"', i);
            if (quote < 0 || builder.Length + (quote - i) > MaxQuotedLength)
            {
                return false;
            }

            builder.Append(text, i, quote - i);
            if (quote + 1 < text.Length && text[quote + 1] == '"')
            {
                builder.Append('"');
                i = quote + 2;
                continue;
            }

            if (quote + 1 == text.Length || text[quote + 1] is '\t' or '\r' or '\n')
            {
                cell = builder.ToString();
                next = quote + 1;
                return true;
            }

            return false;
        }

        return false;
    }
}
