namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>A person account that may own a mailbox (read once into memory, decision D9).</summary>
public sealed record PersonCandidate(string DistinguishedName, string GivenName, string Surname, string DisplayName,
    string? Address, bool IsDisabled, MailboxType Type, string? Department, string? Title, string? Login)
{
    public override string ToString() => nameof(PersonCandidate);
}

/// <summary>The matching key of person names: diacritics removed, lower case, hyphens and spaces collapsed.</summary>
public static class PersonNameKey
{
    public static string Fold(string? name) => throw new NotImplementedException("U5 T2 mailboxes");
}

/// <summary>The persons of the chosen base, indexed for local matching with diacritic folding.</summary>
public sealed class PersonIndex
{
    /// <summary>Indexes the folded given name, surname, both orders, display name and surname parts.</summary>
    public PersonIndex(IEnumerable<PersonCandidate> persons) => throw new NotImplementedException("U5 T2 mailboxes");

    /// <summary>The number of persons indexed.</summary>
    public int Count => throw new NotImplementedException("U5 T2 mailboxes");

    /// <summary>The union of all levels, distinct.</summary>
    public IReadOnlyList<PersonCandidate> Lookup(OwnerName name) => throw new NotImplementedException("U5 T2 mailboxes");

    public override string ToString() => nameof(PersonIndex);
}
