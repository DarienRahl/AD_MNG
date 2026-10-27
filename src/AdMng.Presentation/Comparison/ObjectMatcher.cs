using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Presentation.Comparison;

/// <summary>How the object of the other connection is found ("Dopasuj według").</summary>
public enum ObjectMatchRule
{
    /// <summary>The rules of the object kind in order; the first with exactly one result wins.</summary>
    Automatic,

    /// <summary>The same sAMAccountName.</summary>
    SamAccountName,

    /// <summary>A userPrincipalName with the same part before <c>@</c>.</summary>
    UserPrincipalNamePrefix,

    /// <summary>The same mail address.</summary>
    Mail,

    /// <summary>The same name (the RDN value, cn).</summary>
    Name,

    /// <summary>A DN typed by the operator (under the target's naming context), or an ANR search text.</summary>
    Explicit,
}

/// <summary>The outcome of matching.</summary>
public enum MatchStatus
{
    /// <summary>Exactly one object: it is compared.</summary>
    Found,

    /// <summary>No object ("Nie znaleziono").</summary>
    NotFound,

    /// <summary>2–50 objects: the operator picks one ("Znaleziono N obiektów – wybierz jeden").</summary>
    Ambiguous,

    /// <summary>More than 50 objects ("więcej niż 50 – zawęź wyszukiwanie").</summary>
    TooMany,

    /// <summary>The explicit DN is outside the target's naming context.</summary>
    OutsideNamingContext,

    /// <summary>The source object has no value for the chosen rule, or the explicit text is empty.</summary>
    NoValue,
}

/// <summary>A candidate of the other connection (name, path, sAMAccountName, mail) - in memory, for the candidate list.</summary>
public sealed record MatchCandidate(string DistinguishedName, string Name, ObjectKind Kind)
{
    public string? SamAccountName { get; init; }

    public string? UserPrincipalName { get; init; }

    public string? Mail { get; init; }

    public string? DisplayName { get; init; }

    internal static MatchCandidate From(DirectoryObject obj) => new(obj.DistinguishedName, obj.Name, obj.Kind)
    {
        SamAccountName = obj.GetString(Attr.SamAccountName),
        UserPrincipalName = obj.GetString(Attr.UserPrincipalName),
        Mail = obj.GetString(Attr.Mail),
        DisplayName = obj.GetString(Attr.DisplayName),
    };

    /// <summary>The kind only (never a name or DN).</summary>
    public override string ToString() => $"MatchCandidate({Kind})";
}

/// <summary>The result of <see cref="ObjectMatcher.MatchAsync"/>.</summary>
/// <param name="Status">Found, not found, ambiguous …</param>
/// <param name="Rule">The rule that produced the result (for Automatic: the rule that found the object, or the first ambiguous one).</param>
/// <param name="Candidates">The found objects (one for <see cref="MatchStatus.Found"/>, up to 50 for <see cref="MatchStatus.Ambiguous"/>).</param>
/// <param name="RulesTried">The rules that were searched (Automatic skips rules without a source value).</param>
public sealed record MatchResult(MatchStatus Status, ObjectMatchRule Rule, IReadOnlyList<MatchCandidate> Candidates, IReadOnlyList<ObjectMatchRule> RulesTried)
{
    /// <summary>The single match, or null.</summary>
    public MatchCandidate? Match => Status == MatchStatus.Found && Candidates.Count == 1 ? Candidates[0] : null;

    /// <summary>The status only (never names).</summary>
    public override string ToString() => $"MatchResult({Status}, {Rule}, {Candidates.Count})";
}

/// <summary>
/// Finds the object of another connection that matches an object of this one (design note docs/dev/multi-window.md
/// 8.3): a subtree search under the TARGET's default naming context, AND the kind of the source, size limit 51. Every
/// filter is built with <see cref="LdapFilter"/> (rule 3).
/// </summary>
public static class ObjectMatcher
{
    /// <summary>The most candidates listed; one more means "zawęź wyszukiwanie".</summary>
    public const int MaxCandidates = 50;

    /// <summary>What a match search reads.</summary>
    public static IReadOnlyList<string> Attributes { get; } =
        [Attr.Name, Attr.DistinguishedName, Attr.ObjectClass, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.DisplayName];

    /// <summary>The object kinds that can be compared (user, group, computer, contact).</summary>
    public static bool CanCompare(ObjectKind kind) =>
        kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Group or ObjectKind.Computer or ObjectKind.Contact;

    /// <summary>The rules "Automatycznie" tries for <paramref name="kind"/>, in order.</summary>
    public static IReadOnlyList<ObjectMatchRule> AutomaticRules(ObjectKind kind) => kind switch
    {
        ObjectKind.User or ObjectKind.InetOrgPerson =>
            [ObjectMatchRule.SamAccountName, ObjectMatchRule.UserPrincipalNamePrefix, ObjectMatchRule.Mail, ObjectMatchRule.Name],
        ObjectKind.Group => [ObjectMatchRule.SamAccountName, ObjectMatchRule.Mail, ObjectMatchRule.Name],
        ObjectKind.Computer => [ObjectMatchRule.SamAccountName, ObjectMatchRule.Name],
        ObjectKind.Contact => [ObjectMatchRule.Mail, ObjectMatchRule.Name],
        _ => [ObjectMatchRule.Name],
    };

    /// <summary>The choices of "Dopasuj według" for <paramref name="kind"/>: Automatic, the kind's rules, Explicit.</summary>
    public static IReadOnlyList<ObjectMatchRule> Choices(ObjectKind kind) =>
        [ObjectMatchRule.Automatic, .. AutomaticRules(kind), ObjectMatchRule.Explicit];

    /// <summary>
    /// The filter of a rule for the source object, or null when the source has no value for it (Automatic and Explicit
    /// have no filter of their own).
    /// </summary>
    public static string? RuleFilter(ObjectMatchRule rule, DirectoryObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (rule)
        {
            case ObjectMatchRule.SamAccountName:
                return Value(source.GetString(Attr.SamAccountName)) is { } sam ? LdapFilter.Equal(Attr.SamAccountName, sam) : null;
            case ObjectMatchRule.UserPrincipalNamePrefix:
                var upn = Value(source.GetString(Attr.UserPrincipalName));
                var at = upn?.LastIndexOf('@') ?? -1;
                return upn is not null && at > 0 ? LdapFilter.StartsWith(Attr.UserPrincipalName, upn[..(at + 1)]) : null;
            case ObjectMatchRule.Mail:
                return Value(source.GetString(Attr.Mail)) is { } mail ? LdapFilter.Equal(Attr.Mail, mail) : null;
            case ObjectMatchRule.Name:
                return Value(source.Name) is { } name ? LdapFilter.Equal(Attr.Name, name) : null;
            default:
                return null;
        }
    }

    /// <summary>The filter of the source's kind (users and inetOrgPersons both as user accounts).</summary>
    public static string KindFilter(ObjectKind kind) =>
        (kind == ObjectKind.InetOrgPerson ? ObjectKind.User : kind).ToLdapFilter();

    /// <summary>Finds the object of <paramref name="target"/> matching <paramref name="source"/> by <paramref name="rule"/>.</summary>
    /// <param name="target">The other connection's read-only reader.</param>
    /// <param name="source">The source object (read with at least <see cref="Attributes"/>).</param>
    /// <param name="rule">The rule; <see cref="ObjectMatchRule.Automatic"/> tries the kind's rules in order.</param>
    /// <param name="explicitText">For <see cref="ObjectMatchRule.Explicit"/>: a DN or a search text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<MatchResult> MatchAsync(
        IDirectoryReader target,
        DirectoryObject source,
        ObjectMatchRule rule,
        string? explicitText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        var kind = source.Kind;
        switch (rule)
        {
            case ObjectMatchRule.Explicit:
                return await MatchExplicitAsync(target, kind, explicitText, cancellationToken).ConfigureAwait(false);
            case ObjectMatchRule.Automatic:
                var tried = new List<ObjectMatchRule>();
                MatchResult? firstAmbiguous = null;
                foreach (var candidate in AutomaticRules(kind))
                {
                    if (RuleFilter(candidate, source) is not { } filter)
                    {
                        continue;
                    }

                    tried.Add(candidate);
                    var found = await SearchAsync(target, kind, filter, cancellationToken).ConfigureAwait(false);
                    if (found.Count == 1)
                    {
                        return new MatchResult(MatchStatus.Found, candidate, found, tried);
                    }

                    if (found.Count > 1)
                    {
                        firstAmbiguous ??= Many(candidate, found, []);
                    }
                }

                return firstAmbiguous is { } ambiguous
                    ? ambiguous with { RulesTried = tried }
                    : new MatchResult(tried.Count == 0 ? MatchStatus.NoValue : MatchStatus.NotFound, ObjectMatchRule.Automatic, [], tried);
            default:
                if (RuleFilter(rule, source) is not { } ruleFilter)
                {
                    return new MatchResult(MatchStatus.NoValue, rule, [], []);
                }

                var results = await SearchAsync(target, kind, ruleFilter, cancellationToken).ConfigureAwait(false);
                return results.Count switch
                {
                    0 => new MatchResult(MatchStatus.NotFound, rule, [], [rule]),
                    1 => new MatchResult(MatchStatus.Found, rule, results, [rule]),
                    _ => Many(rule, results, [rule]),
                };
        }
    }

    private static async Task<MatchResult> MatchExplicitAsync(IDirectoryReader target, ObjectKind kind, string? text, CancellationToken cancellationToken)
    {
        var value = Value(text);
        if (value is null)
        {
            return new MatchResult(MatchStatus.NoValue, ObjectMatchRule.Explicit, [], []);
        }

        if (value.Contains('=', StringComparison.Ordinal) && DistinguishedName.TryParse(value, out var dn) && !dn.IsRoot)
        {
            // A DN must belong to the target's domain partition: the comparison never reads elsewhere.
            if (!DistinguishedName.TryParse(target.Info.DefaultNamingContext, out var namingContext)
                || !(dn.Equals(namingContext) || dn.IsDescendantOf(namingContext)))
            {
                return new MatchResult(MatchStatus.OutsideNamingContext, ObjectMatchRule.Explicit, [], [ObjectMatchRule.Explicit]);
            }

            var obj = await target.GetAsync(dn.ToString(), Attributes, cancellationToken).ConfigureAwait(false);
            return obj is null
                ? new MatchResult(MatchStatus.NotFound, ObjectMatchRule.Explicit, [], [ObjectMatchRule.Explicit])
                : new MatchResult(MatchStatus.Found, ObjectMatchRule.Explicit, [MatchCandidate.From(obj)], [ObjectMatchRule.Explicit]);
        }

        var found = await SearchAsync(target, kind, LdapFilter.Anr(value), cancellationToken).ConfigureAwait(false);
        return found.Count switch
        {
            0 => new MatchResult(MatchStatus.NotFound, ObjectMatchRule.Explicit, [], [ObjectMatchRule.Explicit]),
            1 => new MatchResult(MatchStatus.Found, ObjectMatchRule.Explicit, found, [ObjectMatchRule.Explicit]),
            _ => Many(ObjectMatchRule.Explicit, found, [ObjectMatchRule.Explicit]),
        };
    }

    private static MatchResult Many(ObjectMatchRule rule, IReadOnlyList<MatchCandidate> found, IReadOnlyList<ObjectMatchRule> tried) =>
        found.Count > MaxCandidates
            ? new MatchResult(MatchStatus.TooMany, rule, [], tried)
            : new MatchResult(MatchStatus.Ambiguous, rule, found, tried);

    private static async Task<IReadOnlyList<MatchCandidate>> SearchAsync(IDirectoryReader target, ObjectKind kind, string filter, CancellationToken cancellationToken)
    {
        var search = new DirectorySearch
        {
            BaseDn = target.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And(KindFilter(kind), filter),
            Attributes = Attributes,
            SizeLimit = MaxCandidates + 1,
            PageSize = MaxCandidates + 1,
        };
        var found = new List<MatchCandidate>();
        await foreach (var obj in target.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            found.Add(MatchCandidate.From(obj));
        }

        return found;
    }

    private static string? Value(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
