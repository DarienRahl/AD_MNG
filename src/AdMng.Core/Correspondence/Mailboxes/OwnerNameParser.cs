using AdMng.Core.Model;

namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>The name of the person a mailbox is assigned to, as found on the mailbox account.</summary>
/// <param name="GivenName">The given name(s).</param>
/// <param name="Surname">The surname.</param>
/// <param name="Text">"Given Surname" as found (titles removed).</param>
public sealed record OwnerName(string GivenName, string Surname, string Text)
{
    /// <summary>Neither a given name nor a surname.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(GivenName) && string.IsNullOrWhiteSpace(Surname);

    public override string ToString() => nameof(OwnerName);
}

/// <summary>Reads the owner's name from a mailbox account (design note 3.7).</summary>
public static class OwnerNameParser
{
    /// <summary>
    /// GivenNameAndSurname: givenName + sn of the mailbox (one of them may hold both words – then split);
    /// DisplayName/Description/Attribute: the text without titles (TitleParser.WithoutTitles with TitleTable.Default),
    /// without "(…)" remarks and after a ':' or ' - ' prefix ("Skrzynka: Jan Kowalski"); 2+ words → given = all but the
    /// last, surname = last; a single word → surname only.
    /// </summary>
    public static OwnerName? Parse(DirectoryObject mailbox, OwnerNameSource source, string? attribute) =>
        throw new NotImplementedException("U5 T2 mailboxes");
}
