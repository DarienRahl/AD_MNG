using System.Text;

namespace AdMng.Core.Correspondence.Import;

/// <summary>One record of delimited text: where it starts in the text and its cells.</summary>
internal sealed record DelimitedRecord(int Start, IReadOnlyList<string> Cells);

/// <summary>
/// Splits text into records of cells (CSV as Excel writes it): a field that starts with <c>"</c> is quoted (<c>""</c> is a
/// quote, delimiters and line breaks inside are text) when a closing quote follows somewhere; any other quote is a
/// character. Records end at CR, LF or CRLF outside quotes.
/// </summary>
internal static class DelimitedText
{
    public static IReadOnlyList<DelimitedRecord> Parse(string text, char delimiter)
    {
        var records = new List<DelimitedRecord>();
        var cells = new List<string>();
        var field = new StringBuilder();
        var recordStart = 0;
        var atFieldStart = true;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (atFieldStart && c == '"' && text.IndexOf('"', i + 1) > i)
            {
                // Quoted field: up to the quote that is not doubled.
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    field.Append(text[i]);
                    i++;
                }

                atFieldStart = false;
                continue;
            }

            if (c == delimiter)
            {
                cells.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                i++;
                continue;
            }

            if (c is '\r' or '\n')
            {
                cells.Add(field.ToString());
                field.Clear();
                records.Add(new DelimitedRecord(recordStart, cells));
                cells = [];
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                recordStart = i;
                atFieldStart = true;
                continue;
            }

            field.Append(c);
            atFieldStart = false;
            i++;
        }

        if (field.Length > 0 || cells.Count > 0)
        {
            cells.Add(field.ToString());
            records.Add(new DelimitedRecord(recordStart, cells));
        }

        return records;
    }
}
