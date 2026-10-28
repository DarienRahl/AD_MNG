using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using Dn = AdMng.Core.Ldap.DistinguishedName;

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
    /// <summary>One candidate at the best level, which is <see cref="OwnerMatchLevel.Exact"/> or <see cref="OwnerMatchLevel.Folded"/>.</summary>
    Matched,

    /// <summary>Exactly one candidate is a delegate of the mailbox (<c>msExchDelegateListLink</c> / <c>publicDelegates</c>).</summary>
    MatchedByDelegate,

    /// <summary>One candidate at the best level, which is swapped, by display name or partial – to be reviewed.</summary>
    Probable,

    /// <summary>Several candidates at the best level (after preferring enabled accounts); no best.</summary>
    Ambiguous,

    /// <summary>No candidate.</summary>
    NotFound,

    /// <summary>The mailbox holds no owner name.</summary>
    NoName,

    /// <summary>The chosen candidate is disabled.</summary>
    OwnerDisabled,

    /// <summary>The chosen candidate has no valid address.</summary>
    OwnerWithoutAddress,
}

/// <summary>One candidate of a mailbox: level, whether it is a delegate of the mailbox, and its score.</summary>
public sealed record OwnerCandidateMatch(PersonCandidate Person, OwnerMatchLevel Level, bool IsDelegate, int Score)
{
    public override string ToString() => $"{nameof(OwnerCandidateMatch)} {Level} {Score}";
}

/// <summary>The owner of one mailbox: status, the best candidate (null when ambiguous or not found) and all candidates.</summary>
/// <param name="Status">The result.</param>
/// <param name="Best">
/// The chosen candidate – also for <see cref="OwnerMatchStatus.OwnerDisabled"/> and
/// <see cref="OwnerMatchStatus.OwnerWithoutAddress"/>, so the review shows whom the rules chose; null when ambiguous, not
/// found or without a name.
/// </param>
/// <param name="Candidates">Every candidate by score (highest first), at most <see cref="MailboxOwnerMatcher.MaxCandidates"/>.</param>
public sealed record OwnerMatch(OwnerMatchStatus Status, OwnerCandidateMatch? Best, IReadOnlyList<OwnerCandidateMatch> Candidates)
{
    public override string ToString() => $"{Status} ({Candidates.Count})";
}

/// <summary>A mailbox account: DN, name, address, type, the DNs of its delegates and whether it is disabled.</summary>
/// <param name="DistinguishedName">The DN of the mailbox account.</param>
/// <param name="Name">displayName, else the RDN value.</param>
/// <param name="Address">mail, else the primary SMTP proxy address; null without a valid one.</param>
/// <param name="Type">The mailbox type.</param>
/// <param name="DelegateDns">The DNs of <c>msExchDelegateListLink</c> and <c>publicDelegates</c> (distinct).</param>
/// <param name="IsDisabled">The mailbox account is disabled (normal for shared mailboxes).</param>
public sealed record MailboxEntry(string DistinguishedName, string Name, string? Address, MailboxType Type,
    IReadOnlyList<string> DelegateDns, bool IsDisabled)
{
    private static readonly string[] BaseAttributes = [Attr.DisplayName, Attr.Mail, Attr.ProxyAddresses, Attr.UserAccountControl];

    /// <summary>
    /// The attributes <see cref="From"/> reads: display name, addresses and account state, plus the Exchange type and the
    /// delegate links when <paramref name="schema"/> has them (or no schema is given). The owner's name needs
    /// <see cref="OwnerNameParser.AttributesFor"/> on top.
    /// </summary>
    public static IReadOnlyList<string> AttributesFor(ISchemaCatalog? schema) =>
    [
        .. BaseAttributes,
        .. PersonCandidate.Optional(schema, MailboxTypes.RecipientTypeDetails, MailboxTypes.DelegateListLink, MailboxTypes.PublicDelegates),
    ];

    /// <summary>A mailbox from an account read with <see cref="AttributesFor"/>.</summary>
    public static MailboxEntry From(DirectoryObject mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        var delegates = new List<string>();
        foreach (var dn in mailbox.GetStrings(MailboxTypes.DelegateListLink).Concat(mailbox.GetStrings(MailboxTypes.PublicDelegates)))
        {
            if (!string.IsNullOrWhiteSpace(dn) && !delegates.Any(existing => Dn.AreEqual(existing, dn)))
            {
                delegates.Add(dn);
            }
        }

        var display = mailbox.GetString(Attr.DisplayName);
        return new MailboxEntry(
            mailbox.DistinguishedName,
            string.IsNullOrWhiteSpace(display) ? mailbox.Dn.Name : display.Trim(),
            PersonCandidate.AddressOf(mailbox),
            MailboxTypes.Of(mailbox),
            delegates,
            mailbox.IsDisabled);
    }

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
/// DisplayName 30, Partial 15) + delegate 40 + enabled 10 + has address 5, then the status rules of the note. Pure.
/// </summary>
public static class MailboxOwnerMatcher
{
    /// <summary>The most candidates returned.</summary>
    public const int MaxCandidates = 20;

    /// <summary>Score of a candidate that is a delegate of the mailbox.</summary>
    public const int DelegateScore = 40;

    /// <summary>Score of an enabled candidate.</summary>
    public const int EnabledScore = 10;

    /// <summary>Score of a candidate with an address.</summary>
    public const int AddressScore = 5;

    /// <summary>
    /// The owner of <paramref name="mailbox"/>:
    /// <list type="number">
    /// <item>no name → <see cref="OwnerMatchStatus.NoName"/>;</item>
    /// <item>candidates = <see cref="PersonIndex.Lookup"/> minus the mailbox itself and, with
    /// <see cref="OwnerMatchOptions.ExcludeMailboxAccounts"/>, functional mailbox accounts; none →
    /// <see cref="OwnerMatchStatus.NotFound"/>;</item>
    /// <item>exactly one delegate among them → <see cref="OwnerMatchStatus.MatchedByDelegate"/>;</item>
    /// <item>else the candidates of the best level present: one → <see cref="OwnerMatchStatus.Matched"/> (Exact, Folded)
    /// or <see cref="OwnerMatchStatus.Probable"/>; several → the only enabled one when
    /// <see cref="OwnerMatchOptions.PreferEnabled"/> leaves exactly one, else <see cref="OwnerMatchStatus.Ambiguous"/>;</item>
    /// <item>a chosen candidate that is disabled → <see cref="OwnerMatchStatus.OwnerDisabled"/>, without an address →
    /// <see cref="OwnerMatchStatus.OwnerWithoutAddress"/>.</item>
    /// </list>
    /// Candidates are sorted by score (highest first), then by the folded display name (ordinal), then by DN.
    /// </summary>
    public static OwnerMatch Match(MailboxEntry mailbox, OwnerName? name, PersonIndex persons, OwnerMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(persons);
        ArgumentNullException.ThrowIfNull(options);
        if (name is null || name.IsEmpty)
        {
            return new OwnerMatch(OwnerMatchStatus.NoName, null, []);
        }

        var candidates = persons.Find(name)
            .Where(f => !Dn.AreEqual(f.Person.DistinguishedName, mailbox.DistinguishedName))
            .Where(f => !(options.ExcludeMailboxAccounts && MailboxTypes.IsFunctional(f.Person.Type)))
            .Select(f => Candidate(f.Person, f.Level, mailbox.DelegateDns ?? []))
            .ToList();
        if (candidates.Count == 0)
        {
            return new OwnerMatch(OwnerMatchStatus.NotFound, null, []);
        }

        var (status, best) = Decide(candidates, options);
        return new OwnerMatch(status, best, Ranked(candidates, best));
    }

    /// <summary>The level at which <paramref name="person"/> matches <paramref name="name"/>, or null when it does not.</summary>
    public static OwnerMatchLevel? LevelOf(OwnerName name, PersonCandidate person)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(person);
        return name.IsEmpty ? null : OwnerNameLevels.Of(new OwnerKeys(name), new PersonIndex.Entry(person, 0));
    }

    /// <summary>The weight of a level: Exact 50, Folded 45, Swapped 35, DisplayName 30, Partial 15.</summary>
    public static int WeightOf(OwnerMatchLevel level) => level switch
    {
        OwnerMatchLevel.Exact => 50,
        OwnerMatchLevel.Folded => 45,
        OwnerMatchLevel.Swapped => 35,
        OwnerMatchLevel.DisplayName => 30,
        OwnerMatchLevel.Partial => 15,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static OwnerCandidateMatch Candidate(PersonCandidate person, OwnerMatchLevel level, IReadOnlyList<string> delegateDns)
    {
        var isDelegate = delegateDns.Any(d => Dn.AreEqual(d, person.DistinguishedName));
        var score = WeightOf(level)
            + (isDelegate ? DelegateScore : 0)
            + (person.IsDisabled ? 0 : EnabledScore)
            + (string.IsNullOrWhiteSpace(person.Address) ? 0 : AddressScore);
        return new OwnerCandidateMatch(person, level, isDelegate, score);
    }

    private static (OwnerMatchStatus Status, OwnerCandidateMatch? Best) Decide(List<OwnerCandidateMatch> candidates, OwnerMatchOptions options)
    {
        var delegates = candidates.Where(c => c.IsDelegate).ToList();
        if (delegates.Count == 1)
        {
            return (Final(OwnerMatchStatus.MatchedByDelegate, delegates[0]), delegates[0]);
        }

        // OwnerMatchLevel is ordered best first.
        var level = candidates.Min(c => c.Level);
        var atLevel = candidates.Where(c => c.Level == level).ToList();
        if (atLevel.Count > 1 && options.PreferEnabled)
        {
            var enabled = atLevel.Where(c => !c.Person.IsDisabled).ToList();
            if (enabled.Count == 1)
            {
                atLevel = enabled;
            }
        }

        if (atLevel.Count != 1)
        {
            return (OwnerMatchStatus.Ambiguous, null);
        }

        var chosen = atLevel[0];
        var status = level is OwnerMatchLevel.Exact or OwnerMatchLevel.Folded ? OwnerMatchStatus.Matched : OwnerMatchStatus.Probable;
        return (Final(status, chosen), chosen);
    }

    private static OwnerMatchStatus Final(OwnerMatchStatus status, OwnerCandidateMatch chosen) =>
        chosen.Person.IsDisabled ? OwnerMatchStatus.OwnerDisabled
        : string.IsNullOrWhiteSpace(chosen.Person.Address) ? OwnerMatchStatus.OwnerWithoutAddress
        : status;

    /// <summary>By score, folded display name and DN; capped, the chosen one always kept.</summary>
    private static IReadOnlyList<OwnerCandidateMatch> Ranked(List<OwnerCandidateMatch> candidates, OwnerCandidateMatch? best)
    {
        var sorted = candidates
            .Select(c => (Candidate: c, Key: PersonNameKey.Fold(c.Person.DisplayName)))
            .OrderByDescending(c => c.Candidate.Score)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ThenBy(c => c.Candidate.Person.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Candidate)
            .ToList();
        if (sorted.Count <= MaxCandidates)
        {
            return sorted;
        }

        var capped = sorted.Take(MaxCandidates).ToList();
        if (best is not null && !capped.Contains(best))
        {
            capped[^1] = best;
        }

        return capped;
    }
}
