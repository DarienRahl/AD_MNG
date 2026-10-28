namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// Contacts from text and tables (design note 3.1): the e-mail column, the name column (or "Imię" + "Nazwisko"), every
/// column as a field under its header; free text as "Name &lt;addr&gt;" / "Name (addr)" pairs, then bare addresses.
/// </summary>
public static class ContactExtractor
{
    private const string Track = "U5 T1 core";

    /// <summary>A table first (delimiters ';', tab, ',' in this order, header within the first 8 rows), then "Name &lt;addr&gt;", then bare addresses.</summary>
    public static ContactImportResult FromText(string? text) => throw new NotImplementedException(Track);

    /// <summary>One sheet or table (header within the first 8 rows).</summary>
    public static ContactImportResult FromRows(IReadOnlyList<IReadOnlyList<string>> rows, ContactSourceFormat format) =>
        throw new NotImplementedException(Track);
}
