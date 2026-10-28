namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// One recipient found in a file or text (U5, design note <c>docs/dev/u5-outlook-correspondence.md</c> 3.1): address
/// (normalized, lower case), name (may be empty) and the cells of its table row by column name (empty for free text).
/// </summary>
public sealed record ImportedContact(string Email, string Name, IReadOnlyDictionary<string, string> Fields)
{
    public override string ToString() => nameof(ImportedContact);
}

/// <summary>The kind of source contacts were read from.</summary>
public enum ContactSourceFormat
{
    Text,
    Delimited,
    Excel,
    Word,
    OpenDocument,
    Rtf,
    Html,
}

/// <summary>The contacts of one file or text.</summary>
/// <param name="Contacts">De-duplicated by <see cref="ImportedContact.Email"/>, in file order.</param>
/// <param name="Columns">Column names in table order (empty for free text).</param>
/// <param name="InvalidAddresses">Table rows whose e-mail cell held no valid address.</param>
/// <param name="Duplicates">Repeated addresses dropped.</param>
/// <param name="Format">The format the contacts were read from.</param>
public sealed record ContactImportResult(
    IReadOnlyList<ImportedContact> Contacts,
    IReadOnlyList<string> Columns,
    int InvalidAddresses,
    int Duplicates,
    ContactSourceFormat Format)
{
    /// <summary>No contacts (free text).</summary>
    public static ContactImportResult Empty { get; } = new([], [], 0, 0, ContactSourceFormat.Text);

    public override string ToString() => $"{Contacts.Count} contacts";
}
