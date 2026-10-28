using System.Text;

namespace AdMng.Core.Correspondence.Import;

/// <summary>A document read for contacts: text outside tables and tables (rows of cells), in document order.</summary>
internal sealed class DocumentParts
{
    private readonly List<(string? Text, IReadOnlyList<IReadOnlyList<string>>? Table)> _parts = [];

    public IReadOnlyList<(string? Text, IReadOnlyList<IReadOnlyList<string>>? Table)> Parts => _parts;

    public IEnumerable<IReadOnlyList<IReadOnlyList<string>>> Tables => _parts.Where(p => p.Table is not null).Select(p => p.Table!);

    public void AddText(StringBuilder text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var value = text.ToString();
        text.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (_parts.Count > 0 && _parts[^1].Text is { } previous)
        {
            _parts[^1] = (previous + value, null);
        }
        else
        {
            _parts.Add((value, null));
        }
    }

    public void AddTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count > 0)
        {
            _parts.Add((null, rows));
        }
    }

    /// <summary>Everything as text (tables: cells joined by tab, rows by new lines).</summary>
    public string AllText()
    {
        var builder = new StringBuilder();
        foreach (var (text, table) in _parts)
        {
            if (text is not null)
            {
                builder.Append(text).Append('\n');
            }
            else
            {
                builder.Append(ContactExtractor.RowsText(table!));
            }
        }

        return builder.ToString();
    }

    /// <summary>Document semantics: text → <see cref="ContactExtractor.FromText"/>, each table →
    /// <see cref="ContactExtractor.FromRows"/>, merged in document order.</summary>
    public ContactImportResult Contacts(ContactSourceFormat format)
    {
        var accumulator = new ContactAccumulator();
        foreach (var (text, table) in _parts)
        {
            accumulator.Merge(text is not null ? ContactExtractor.FromText(text) : ContactExtractor.FromRows(table!, format));
        }

        return accumulator.Result(format);
    }

    /// <summary>Spreadsheet semantics: the tables that have an e-mail column (each with the free text above its header),
    /// concatenated; when none has one, everything as free text.</summary>
    public ContactImportResult SheetContacts(ContactSourceFormat format) => Sheets(Tables.ToList(), AllText, format);

    internal static ContactImportResult Sheets(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> sheets, Func<string> allText, ContactSourceFormat format)
    {
        var accumulator = new ContactAccumulator();
        var any = false;
        foreach (var sheet in sheets)
        {
            if (ContactExtractor.TryTable(sheet, format, minimumCells: 1, out var table, out var headerRow))
            {
                any = true;
                accumulator.Merge(ContactExtractor.WithPreamble(sheet, headerRow, table, format));
            }
        }

        if (!any)
        {
            accumulator.Merge(ContactExtractor.FromText(allText()));
        }

        return accumulator.Result(format);
    }

    /// <summary>The hyperlink rule of documents: a <c>mailto:</c> link whose text does not show its address gets
    /// " &lt;address&gt;" appended (so "napisz" linking to an address becomes "napisz &lt;jan@…&gt;").</summary>
    internal static void AppendLinkAddress(StringBuilder builder, int textStart, string? target)
    {
        if (target is null || !target.TrimStart().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var address = EmailAddressText.Normalize(target);
        if (!EmailAddressText.IsValid(address))
        {
            return;
        }

        var start = Math.Clamp(textStart, 0, builder.Length);
        var text = builder.ToString(start, builder.Length - start);
        if (!text.Contains(address, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" <").Append(address).Append('>');
        }
    }
}
