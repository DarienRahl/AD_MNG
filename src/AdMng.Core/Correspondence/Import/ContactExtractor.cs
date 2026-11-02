using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// Contacts from text and tables (design note 3.1): the e-mail column, the name column (or "Imię" + "Nazwisko"), every
/// column as a field under its header; free text as "Name &lt;addr&gt;" / "Name (addr)" pairs, then bare addresses.
/// </summary>
/// <remarks>
/// <para>Header detection: among the first <see cref="HeaderRows"/> non-blank rows, a row with an e-mail column whose
/// table gives addresses – one with two or more cells before a one-cell title (see <see cref="FindTable"/>). A header
/// cell (folded with <see cref="FieldKey"/>) equal to <c>email</c>, <c>e-mail</c>, <c>mail</c>, <c>adres e-mail</c>,
/// <c>adres email</c> or <c>e-mail address</c> is an e-mail column; then <c>poczta</c>; then a header containing
/// <c>e-mail</c>/<c>email</c>/<c>mail</c> ("Inne e-maile" therefore loses to "E-mail"). Of these candidates, in this
/// order, the first one with at least one valid address in the data rows is taken (else the first one).</para>
/// <para>Name column: the first header equal to or containing <c>nazwa</c>, <c>name</c>, <c>instytucj</c>,
/// <c>urząd</c>, <c>firma</c>, <c>odbiorca</c>, <c>adresat</c>, <c>imię i nazwisko</c> or <c>pełna nazwa</c> (not a
/// given name, surname or login header); else, when an <c>imię</c>/<c>first name</c> and a
/// <c>nazwisko</c>/<c>surname</c>/<c>last name</c> column exist, "Imię Nazwisko".</para>
/// <para>Text: a delimited table (<c>;</c>, tab, <c>,</c> in this order) whose header has at least two cells, is no lead-in
/// sentence and whose e-mail column gives more addresses than the lines that end before it; the lines before its header
/// are free text. Rows or tables without such a header are free text: every "Name &lt;addr&gt;" / "Name (addr)" pair and
/// every other address, in the order they appear.</para>
/// </remarks>
public static class ContactExtractor
{
    /// <summary>The header is looked for in this many first non-blank rows.</summary>
    public const int HeaderRows = 8;

    /// <summary>The name of a column without a header: "Kolumna {n}" (1-based).</summary>
    public const string UnnamedColumnFormat = "Kolumna {0}";

    private static readonly char[] Delimiters = [';', '\t', ','];

    private static readonly string[] EmailHeaders = ["email", "mail", "adresemail", "emailaddress"];
    private static readonly string[] PostHeaders = ["poczta"];
    private static readonly string[] NameParts = ["nazwa", "name", "instytucj", "urzad", "firma", "odbiorca", "adresat", "imieinazwisko", "pelnanazwa"];
    private static readonly HashSet<string> GivenNameHeaders = new(StringComparer.Ordinal) { "imie", "imiona", "firstname", "givenname" };
    private static readonly HashSet<string> SurnameHeaders = new(StringComparer.Ordinal) { "nazwisko", "surname", "lastname", "familyname" };
    private static readonly HashSet<string> NotNameHeaders = new(StringComparer.Ordinal)
    {
        "username", "nazwauzytkownika", "login", "nick", "nickname", "filename", "nazwapliku",
        "middlename", "secondname", "additionalname", "drugieimie", "drugieimiona",   // Outlook / Gmail contact exports
    };

    /// <summary>A table first (delimiters ';', tab, ',' in this order, header within the first 8 rows), then "Name &lt;addr&gt;", then bare addresses.</summary>
    public static ContactImportResult FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ContactImportResult.Empty;
        }

        ContactImportResult? emptyTable = null;
        foreach (var delimiter in Delimiters)
        {
            var records = DelimitedText.Parse(text, delimiter);
            if (FindTable([.. records.Select(r => r.Cells)], ContactSourceFormat.Delimited, minimumCells: 2) is not { } match)
            {
                continue;
            }

            if (!match.Accepted)
            {
                if (match.Table.Contacts.Count == 0)
                {
                    emptyTable ??= match.Table;
                }

                continue;
            }

            var accumulator = new ContactAccumulator();
            var preamble = text[..records[match.HeaderRow].Start];
            if (preamble.Contains('@', StringComparison.Ordinal))
            {
                accumulator.Merge(FreeText(preamble, ContactSourceFormat.Text));
            }

            accumulator.Merge(match.Table);
            return accumulator.Result(ContactSourceFormat.Delimited);
        }

        var free = FreeText(text, ContactSourceFormat.Text);
        return emptyTable is null ? free : free with { InvalidAddresses = free.InvalidAddresses + emptyTable.InvalidAddresses };
    }

    /// <summary>One sheet or table (header within the first 8 rows; the rows above it are free text); without an e-mail
    /// column – or when its e-mail column holds no address – its cells are free text.</summary>
    public static ContactImportResult FromRows(IReadOnlyList<IReadOnlyList<string>> rows, ContactSourceFormat format)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return FindTable(rows, format, minimumCells: 1) is { } match ? Contacts(rows, match, format) : FreeText(RowsText(rows), format);
    }

    /// <summary>The contacts of rows with a header: the table (after the free text above its header) when it was accepted,
    /// else the rows as free text (with the invalid rows of a table whose e-mail column held no address).</summary>
    internal static ContactImportResult Contacts(IReadOnlyList<IReadOnlyList<string>> rows, TableMatch match, ContactSourceFormat format)
    {
        if (match.Accepted)
        {
            return WithPreamble(rows, match.HeaderRow, match.Table, format);
        }

        var free = FreeText(RowsText(rows), format);
        return match.Table.Contacts.Count == 0 ? free with { InvalidAddresses = free.InvalidAddresses + match.Table.InvalidAddresses } : free;
    }

    /// <summary>The table's contacts after those of the free text in the rows above its header.</summary>
    internal static ContactImportResult WithPreamble(
        IReadOnlyList<IReadOnlyList<string>> rows, int headerRow, ContactImportResult table, ContactSourceFormat format)
    {
        var above = rows.Take(headerRow).ToList();
        if (!above.Any(r => r.Any(c => c?.Contains('@', StringComparison.Ordinal) == true)))
        {
            return table;
        }

        var accumulator = new ContactAccumulator();
        accumulator.Merge(FreeText(RowsText(above), format));
        accumulator.Merge(table);
        return accumulator.Result(format);
    }

    /// <summary>The rows as text: cells joined by tab, rows by new lines.</summary>
    internal static string RowsText(IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendJoin('\t', row).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The table of <paramref name="rows"/>, or null when none of the first <see cref="HeaderRows"/> non-blank rows is a
    /// header candidate: a row with at least <paramref name="minimumCells"/> cells, an e-mail column and no address.
    /// </summary>
    /// <remarks>
    /// <para>Every candidate is tried, not only the first: a title or caption above the real header ("Wykaz adresów e-mail
    /// jednostek" merged over the columns) also names "e-mail", but its column holds names. A candidate is accepted when its
    /// e-mail column gives at least one address; a candidate with two or more non-empty cells wins over a one-cell row (a
    /// title), then the earlier row.</para>
    /// <para>Delimited text (<see cref="ContactSourceFormat.Delimited"/>) splits prose at its commas too, so a sentence
    /// such as "Dzień dobry, adresy e-mail poniżej" looks like a header. There a candidate is also refused when its last
    /// cell is a lead-in ending with a colon ("…, pod które wysyłamy:"), and when no more of its rows give an address in the
    /// e-mail column than hold an address but do not reach that column (lines without the delimiter: the text is a list,
    /// not a table). In an accepted delimited table such a short line is read as free text, never lost.</para>
    /// <para>Without an accepted candidate the result is the first candidate's table, not <see cref="TableMatch.Accepted"/>
    /// (its invalid rows count when its e-mail column held no address at all).</para>
    /// </remarks>
    internal static TableMatch? FindTable(IReadOnlyList<IReadOnlyList<string>> rows, ContactSourceFormat format, int minimumCells)
    {
        var delimited = format == ContactSourceFormat.Delimited;
        var candidates = new List<(int Row, List<int> MailColumns)>();
        var seen = 0;
        for (var i = 0; i < rows.Count && seen < HeaderRows; i++)
        {
            if (IsBlank(rows[i]))
            {
                continue;
            }

            seen++;
            if (rows[i].Count < minimumCells || rows[i].Any(c => EmailAddressText.FirstValid(c) is not null))
            {
                continue;   // too few cells, or data: a header never holds an address
            }

            var mailColumns = MailColumns(rows[i]);
            if (mailColumns.Count > 0 && !(delimited && IsLeadIn(rows[i])))
            {
                candidates.Add((i, mailColumns));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        (int Row, int MailColumn)? chosen = null;
        var chosenCells = 0;
        foreach (var (row, mailColumns) in candidates)
        {
            var data = DataRows(rows, row);
            var mailColumn = MailColumn(rows, data, mailColumns);
            var found = data.Count(r => EmailAddressText.FirstValid(Cell(rows[r], mailColumn)) is not null);
            if (found == 0)
            {
                continue;
            }

            if (delimited && found <= data.Count(r => IsShortRowWithAddress(rows[r], mailColumn)))
            {
                continue;   // most addresses are on lines without the e-mail column: a list in prose, not a table
            }

            var cells = rows[row].Count(c => !string.IsNullOrWhiteSpace(c));
            if (chosen is null || (chosenCells < 2 && cells >= 2))
            {
                chosen = (row, mailColumn);
                chosenCells = cells;
            }

            if (cells >= 2)
            {
                break;
            }
        }

        if (chosen is { } accepted)
        {
            return new TableMatch(Table(rows, accepted.Row, accepted.MailColumn, format), accepted.Row, Accepted: true);
        }

        var first = candidates[0];
        return new TableMatch(Table(rows, first.Row, MailColumn(rows, DataRows(rows, first.Row), first.MailColumns), format), first.Row, Accepted: false);
    }

    /// <summary>The indexes of the non-blank rows below <paramref name="headerRow"/>.</summary>
    private static List<int> DataRows(IReadOnlyList<IReadOnlyList<string>> rows, int headerRow)
    {
        var data = new List<int>();
        for (var r = headerRow + 1; r < rows.Count; r++)
        {
            if (!IsBlank(rows[r]))
            {
                data.Add(r);
            }
        }

        return data;
    }

    /// <summary>The first e-mail column (in tier order) with at least one valid address in the data rows, else the first.</summary>
    private static int MailColumn(IReadOnlyList<IReadOnlyList<string>> rows, List<int> dataRows, List<int> mailColumns) =>
        mailColumns.FirstOrDefault(c => dataRows.Any(r => EmailAddressText.FirstValid(Cell(rows[r], c)) is not null), mailColumns[0]);

    /// <summary>A row that holds an address but ends before the e-mail column (a line of delimited text without the
    /// delimiter, such as a lone "anna@y.pl" below a header).</summary>
    private static bool IsShortRowWithAddress(IReadOnlyList<string> row, int mailColumn) =>
        row.Count <= mailColumn && row.Any(c => EmailAddressText.FirstValid(c) is not null);

    /// <summary>The table of the header <paramref name="headerRow"/> with its e-mail column <paramref name="mailColumn"/>:
    /// one contact per row with a valid address there, the other rows counted as invalid – except, in delimited text, a
    /// line that ends before the e-mail column: its addresses are free text.</summary>
    private static ContactImportResult Table(IReadOnlyList<IReadOnlyList<string>> rows, int headerRow, int mailColumn, ContactSourceFormat format)
    {
        var header = rows[headerRow];
        var data = rows.Skip(headerRow + 1).Where(r => !IsBlank(r)).ToList();
        var (nameColumn, givenColumn, surnameColumn) = NameColumns(header, mailColumn);
        var columns = Columns(header, data);

        var accumulator = new ContactAccumulator();
        accumulator.AddColumns(columns.Select(c => c.Name));
        foreach (var row in data)
        {
            var address = EmailAddressText.FirstValid(Cell(row, mailColumn));
            if (address is null)
            {
                if (format == ContactSourceFormat.Delimited && IsShortRowWithAddress(row, mailColumn))
                {
                    accumulator.Merge(FreeText(string.Join('\t', row), format));
                    continue;
                }

                accumulator.InvalidAddresses++;
                continue;
            }

            var name = nameColumn >= 0 ? EmailAddressText.CleanName(Cell(row, nameColumn)) : string.Empty;
            if (name.Length == 0 && givenColumn >= 0 && surnameColumn >= 0)
            {
                // No name column, or its cell is empty in this row (or holds only an address): "Imię Nazwisko".
                name = EmailAddressText.CleanName(Cell(row, givenColumn).Trim() + " " + Cell(row, surnameColumn).Trim());
            }

            var fields = new Dictionary<string, string>(columns.Count, StringComparer.Ordinal);
            foreach (var (index, columnName) in columns)
            {
                fields[columnName] = Cell(row, index).Trim();
            }

            accumulator.Add(address, name, fields.AsReadOnly());
        }

        return accumulator.Result(format);
    }

    /// <summary>A line of prose that introduces a list: its last non-empty cell has two or more words and ends with a colon
    /// ("Poniżej adresy e-mail, pod które wysyłamy:").</summary>
    private static bool IsLeadIn(IReadOnlyList<string> row)
    {
        var last = row.LastOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
        return last is not null && last.EndsWith(':') && last.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>Free text: "Name &lt;addr&gt;" / "Name (addr)" pairs and bare addresses, in the order they appear.</summary>
    internal static ContactImportResult FreeText(string? text, ContactSourceFormat format)
    {
        var accumulator = new ContactAccumulator();
        if (string.IsNullOrEmpty(text) || !text.Contains('@', StringComparison.Ordinal))
        {
            return accumulator.Result(format);
        }

        var candidates = new List<(int Position, string Address, string Name)>();
        var pairAddresses = new List<(int Start, int End)>();
        foreach (Match match in EmailAddressText.NamedMatches(text))
        {
            var address = match.Groups[2];
            pairAddresses.Add((address.Index, address.Index + address.Length));
            candidates.Add((address.Index, address.Value, EmailAddressText.CleanName(match.Groups[1].Value)));
        }

        var n = 0;
        foreach (Match match in EmailAddressText.Matches(text))
        {
            while (n < pairAddresses.Count && pairAddresses[n].End <= match.Index)
            {
                n++;
            }

            if (n < pairAddresses.Count && pairAddresses[n].Start < match.Index + match.Length)
            {
                continue;   // the address of a "Name <addr>" pair (other addresses inside its name part stay)
            }

            candidates.Add((match.Index, match.Value, string.Empty));
        }

        foreach (var (_, raw, name) in candidates.OrderBy(c => c.Position))
        {
            var address = EmailAddressText.Normalize(raw);
            if (EmailAddressText.IsValid(address))
            {
                accumulator.Add(address, name, null);
            }
        }

        return accumulator.Result(format);
    }

    private static List<int> MailColumns(IReadOnlyList<string> header)
    {
        var keys = header.Select(FieldKey.Of).ToList();
        var exact = new List<int>();
        var post = new List<int>();
        var contains = new List<int>();
        for (var c = 0; c < keys.Count; c++)
        {
            var key = keys[c];
            if (key.Length == 0)
            {
                continue;
            }

            if (EmailHeaders.Contains(key))
            {
                exact.Add(c);
            }
            else if (PostHeaders.Contains(key))
            {
                post.Add(c);
            }
            else if (key.Contains("mail", StringComparison.Ordinal))
            {
                contains.Add(c);
            }
        }

        return [.. exact, .. post, .. contains];
    }

    private static (int Name, int Given, int Surname) NameColumns(IReadOnlyList<string> header, int mailColumn)
    {
        int name = -1, given = -1, surname = -1;
        for (var c = 0; c < header.Count; c++)
        {
            var key = FieldKey.Of(header[c]);
            if (key.Length == 0 || c == mailColumn)
            {
                continue;
            }

            if (GivenNameHeaders.Contains(key))
            {
                given = given < 0 ? c : given;
                continue;
            }

            if (SurnameHeaders.Contains(key))
            {
                surname = surname < 0 ? c : surname;
                continue;
            }

            if (name < 0 && !NotNameHeaders.Contains(key) && NameParts.Any(p => key.Contains(p, StringComparison.Ordinal)))
            {
                name = c;
            }
        }

        return (name, given, surname);
    }

    /// <summary>The columns of a table: every column (of the first 256) with a header or a value, named by its header on one
    /// line (<see cref="ColumnName"/>; "Kolumna N" when empty), a repeated name made unique as "Name (2)".</summary>
    private static List<(int Index, string Name)> Columns(IReadOnlyList<string> header, List<IReadOnlyList<string>> data)
    {
        var width = header.Count;
        foreach (var row in data)
        {
            width = Math.Max(width, row.Count);
        }

        width = Math.Min(width, SpreadsheetLimits.MaxColumns);   // a crafted row of a million cells is no contact list

        var columns = new List<(int Index, string Name)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < width; c++)
        {
            var title = ColumnName(Cell(header, c));
            if (title.Length == 0 && !data.Any(r => Cell(r, c).Trim().Length > 0))
            {
                continue;
            }

            var name = title.Length > 0 ? title : string.Format(CultureInfo.InvariantCulture, UnnamedColumnFormat, c + 1);
            var unique = name;
            for (var n = 2; !keys.Add(FieldKey.Of(unique)); n++)
            {
                unique = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            }

            columns.Add((c, unique));
        }

        return columns;
    }

    /// <summary>
    /// A header as a field name: every run of white space and control characters (the line break of an Excel header typed
    /// with Alt+Enter, a multi-line quoted CSV header, a tab) becomes one space, the ends trimmed – a <c>{{field}}</c> holds
    /// no line break, so "Kod⏎pocztowy" could never be inserted and merged.
    /// </summary>
    private static string ColumnName(string header)
    {
        var builder = new StringBuilder(header.Length);
        var space = false;
        foreach (var c in header)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))   // white space includes U+2028 and U+2029
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] ?? string.Empty : string.Empty;

    private static bool IsBlank(IReadOnlyList<string> row) => row.All(string.IsNullOrWhiteSpace);
}

/// <summary>A table found in rows (<see cref="ContactExtractor.FindTable"/>).</summary>
/// <param name="Table">The contacts of its rows, its columns and invalid rows.</param>
/// <param name="HeaderRow">The index of its header row.</param>
/// <param name="Accepted">The rows are this table; else they are free text (the table only tells the invalid rows).</param>
internal readonly record struct TableMatch(ContactImportResult Table, int HeaderRow, bool Accepted);
