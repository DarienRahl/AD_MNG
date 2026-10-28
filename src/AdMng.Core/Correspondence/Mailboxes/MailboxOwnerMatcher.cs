namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>How well a candidate's name matches the owner's name (best first).</summary>
public enum OwnerMatchLevel
{
    /// <summary>Given and surname equal ignoring case.</summary>
    Exact,

    /// <summary>Equal after <see cref="PersonNameKey.Fold"/>.</summary>
    Folded,

    /// <summary>Given and surname swapped (folded).</summary>
    Swapped,

    /// <summary>The folded display name without titles equals "given surname" or "surname given".</summary>
    DisplayName,

    /// <summary>One part of a compound surname, or the first given name with an equal surname.</summary>
    Partial,
}

/// <summary>The result of matching one mailbox to its owner.</summary>
public enum OwnerMatchStatus
{
    Matched,
    MatchedByDelegate,
    Probable,
    Ambiguous,
    NotFound,
    NoName,
    OwnerDisabled,
    OwnerWithoutAddress,
}

/// <summary>One candidate of a mailbox: level, whether it is a delegate of the mailbox, and its score.</summary>
public sealed record OwnerCandidateMatch(PersonCandidate Person, OwnerMatchLevel Level, bool IsDelegate, int Score);

/// <summary>The owner of one mailbox: status, the best candidate (null when ambiguous or not found) and all candidates.</summary>
public sealed record OwnerMatch(OwnerMatchStatus Status, OwnerCandidateMatch? Best, IReadOnlyList<OwnerCandidateMatch> Candidates)
{
    public override string ToString() => $"{Status} ({Candidates.Count})";
}

/// <summary>A mailbox account: DN, name, address, type, the DNs of its delegates and whether it is disabled.</summary>
public sealed record MailboxEntry(string DistinguishedName, string Name, string? Address, MailboxType Type,
    IReadOnlyList<string> DelegateDns, bool IsDisabled)
{
    public override string ToString() => nameof(MailboxEntry);
}

/// <summary>Options of <see cref="MailboxOwnerMatcher"/>.</summary>
public sealed record OwnerMatchOptions
{
    /// <summary>Persons whose own account is a functional mailbox (shared/room/equipment) are no candidates.</summary>
    public bool ExcludeMailboxAccounts { get; init; } = true;

    /// <summary>Among several candidates of the same level, enabled accounts win.</summary>
    public bool PreferEnabled { get; init; } = true;
}

/// <summary>
/// Matches a mailbox to the person it is assigned to (design note 3.7): candidates from the index minus the mailbox itself
/// (and functional mailbox accounts), level per candidate, score = level weight (Exact 50, Folded 45, Swapped 35,
/// DisplayName 30, Partial 15) + delegate 40 + enabled 10 + has address 5, then the status rules of the note.
/// </summary>
public static class MailboxOwnerMatcher
{
    /// <summary>The most candidates returned.</summary>
    public const int MaxCandidates = 20;

    public static OwnerMatch Match(MailboxEntry mailbox, OwnerName? name, PersonIndex persons, OwnerMatchOptions options) =>
        throw new NotImplementedException("U5 T2 mailboxes");
}
