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
/// <para>Header detection: the first of the first <see cref="HeaderRows"/> non-blank rows with an e-mail column. A header
/// cell (folded with <see cref="FieldKey"/>) equal to <c>email</c>, <c>e-mail</c>, <c>mail</c>, <c>adres e-mail</c>,
/// <c>adres email</c> or <c>e-mail address</c> is an e-mail column; then <c>poczta</c>; then a header containing
/// <c>e-mail</c>/<c>email</c>/<c>mail</c> ("Inne e-maile" therefore loses to "E-mail"). Of these candidates, in this
/// order, the first one with at least one valid address in the data rows is taken (else the first one).</para>
/// <para>Name column: the first header equal to or containing <c>nazwa</c>, <c>name</c>, <c>instytucj</c>,
/// <c>urząd</c>, <c>firma</c>, <c>odbiorca</c>, <c>adresat</c>, <c>imię i nazwisko</c> or <c>pełna nazwa</c> (not a
/// given name, surname or login header); else, when an <c>imię</c>/<c>first name</c> and a
/// <c>nazwisko</c>/<c>surname</c>/<c>last name</c> column exist, "Imię Nazwisko".</para>
/// <para>Text: a delimited table (<c>;</c>, tab, <c>,</c> in this order) whose header has at least two cells and which
/// yields a contact; the lines before its header are free text. Free text: every "Name &lt;addr&gt;" / "Name (addr)" pair
/// and every other address, in the order they appear.</para>
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
            if (!TryTable([.. records.Select(r => r.Cells)], ContactSourceFormat.Delimited, minimumCells: 2, out var table, out var headerRow))
            {
                continue;
            }

            if (table.Contacts.Count == 0)
            {
                emptyTable ??= table;
                continue;
            }

            var accumulator = new ContactAccumulator();
            var preamble = text[..records[headerRow].Start];
            if (preamble.Contains('@', StringComparison.Ordinal))
            {
                accumulator.Merge(FreeText(preamble, ContactSourceFormat.Text));
            }

            accumulator.Merge(table);
            return accumulator.Result(ContactSourceFormat.Delimited);
        }

        var free = FreeText(text, ContactSourceFormat.Text);
        return emptyTable is null ? free : free with { InvalidAddresses = free.InvalidAddresses + emptyTable.InvalidAddresses };
    }

    /// <summary>One sheet or table (header within the first 8 rows; the rows above it are free text); without an e-mail
    /// column its cells are free text.</summary>
    public static ContactImportResult FromRows(IReadOnlyList<IReadOnlyList<string>> rows, ContactSourceFormat format)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (TryTable(rows, format, minimumCells: 1, out var table, out var headerRow))
        {
            return WithPreamble(rows, headerRow, table, format);
        }

        return FreeText(RowsText(rows), format);
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
    /// The table of <paramref name="rows"/> when a header with an e-mail column is among the first
    /// <see cref="HeaderRows"/> non-blank rows (with at least <paramref name="minimumCells"/> cells).
    /// </summary>
    internal static bool TryTable(
        IReadOnlyList<IReadOnlyList<string>> rows, ContactSourceFormat format, int minimumCells, out ContactImportResult result, out int headerRow)
    {
        result = ContactImportResult.Empty;
        headerRow = -1;
        List<int>? mailCandidates = null;
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

            var candidates = MailColumns(rows[i]);
            if (candidates.Count > 0)
            {
                headerRow = i;
                mailCandidates = candidates;
                break;
            }
        }

        if (mailCandidates is null)
        {
            return false;
        }

        var header = rows[headerRow];
        var data = rows.Skip(headerRow + 1).Where(r => !IsBlank(r)).ToList();
        var mailColumn = mailCandidates.FirstOrDefault(c => data.Any(r => EmailAddressText.FirstValid(Cell(r, c)) is not null), mailCandidates[0]);
        var (nameColumn, givenColumn, surnameColumn) = NameColumns(header, mailColumn);
        var columns = Columns(header, data);

        var accumulator = new ContactAccumulator();
        accumulator.AddColumns(columns.Select(c => c.Name));
        foreach (var row in data)
        {
            var address = EmailAddressText.FirstValid(Cell(row, mailColumn));
            if (address is null)
            {
                accumulator.InvalidAddresses++;
                continue;
            }

            var name = nameColumn >= 0
                ? EmailAddressText.CleanName(Cell(row, nameColumn))
                : givenColumn >= 0 && surnameColumn >= 0
                    ? EmailAddressText.CleanName(Cell(row, givenColumn).Trim() + " " + Cell(row, surnameColumn).Trim())
                    : string.Empty;
            var fields = new Dictionary<string, string>(columns.Count, StringComparer.Ordinal);
            foreach (var (index, columnName) in columns)
            {
                fields[columnName] = Cell(row, index).Trim();
            }

            accumulator.Add(address, name, fields.AsReadOnly());
        }

        result = accumulator.Result(format);
        return true;
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

    /// <summary>The columns of a table: every column (of the first 256) with a header or a value, named by its trimmed
    /// header ("Kolumna N" when empty), a repeated name made unique as "Name (2)".</summary>
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
            var title = Cell(header, c).Trim();
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

    private static string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] ?? string.Empty : string.Empty;

    private static bool IsBlank(IReadOnlyList<string> row) => row.All(string.IsNullOrWhiteSpace);
}
