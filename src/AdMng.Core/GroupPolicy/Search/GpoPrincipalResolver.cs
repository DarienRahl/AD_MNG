using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.Ldap;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.GroupPolicy.Search;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.3, 21.11) – implementation: W1.

/// <summary>How a principal was resolved.</summary>
public enum GpoPrincipalKind
{
    /// <summary>Written as a SID (with or without <c>*</c>).</summary>
    Sid,

    /// <summary>A well-known name (en/pl), <c>BUILTIN\…</c>, a local group or a domain RID 500/512/513/515.</summary>
    WellKnown,

    /// <summary>Found in the domain by LDAP (<c>DOMAIN\name</c>, UPN or bare name).</summary>
    Account,

    /// <summary>Not found, or another domain (never guessed).</summary>
    Unresolved,
}

/// <summary>A principal as written and its SID (null when unresolved).</summary>
public sealed record GpoPrincipal(string Written, string? Sid, GpoPrincipalKind Kind)
{
    /// <summary>For diagnostics: the kind only (the name and SID are directory values).</summary>
    public override string ToString() => Kind.ToString();
}

/// <summary>
/// Name ↔ SID for GPO principals (user rights, restricted groups, preference groups, item-level targeting) on the session's
/// DC; filters built with <c>LdapFilter</c> (rule 3). The private resolver of <see cref="GroupPolicyGrantSource"/> (U4c) is a
/// separate, older copy for <c>sAMAccountName</c> only.
/// </summary>
public static class GpoPrincipalResolver
{
    /// <summary>Names per OR search.</summary>
    public const int NamesPerSearch = 50;

    private const int PageSize = 500;

    /// <summary>
    /// The SID of a principal without LDAP: SID text with or without <c>*</c>, the en/pl well-known names, <c>BUILTIN\…</c>,
    /// local groups and the domain RIDs 500/512/513/515 (wraps the internal <c>GpoPrincipals.SidOf</c>); null otherwise.
    /// </summary>
    public static string? KnownSid(string? principal, string? domainSid) => GpoPrincipals.SidOf(principal, domainSid);

    /// <summary>
    /// One result per distinct input (case-insensitive; the first spelling is kept, in input order): <see cref="KnownSid"/>
    /// first, then LDAP for <c>DOMAIN\name</c> (only this domain:
    /// <c>new GroupPolicyDomainNames(session.Info.DomainDnsName, session.Info.DomainNetBiosName).IsThisDomain</c>), UPN and
    /// bare names, <see cref="NamesPerSearch"/> per OR search below the domain NC. A name of another domain, an empty one and
    /// a name the domain does not know come back <see cref="GpoPrincipalKind.Unresolved"/>.
    /// </summary>
    public static async Task<IReadOnlyList<GpoPrincipal>> ResolveAsync(IDirectorySession session, IEnumerable<string> principals,
        string? domainSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(principals);
        var domain = new GroupPolicyDomainNames(session.Info.DomainDnsName, session.Info.DomainNetBiosName);
        var inputs = principals.Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new GpoPrincipal?[inputs.Count];
        var bySam = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var byUpn = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < inputs.Count; i++)
        {
            var written = inputs[i];
            var text = GpoSettingUnits.Written(written);
            if (text.Length == 0)
            {
                results[i] = new GpoPrincipal(written, null, GpoPrincipalKind.Unresolved);
                continue;
            }

            var backslash = text.IndexOf('\\', StringComparison.Ordinal);
            if (KnownSid(text, domainSid) is { } known)
            {
                // "KONTOSO\Domain Admins" is another domain's group: a domain RID is only this domain's when the prefix says so.
                var otherDomain = backslash > 0 && domainSid is not null && known.StartsWith(domainSid + "-", StringComparison.OrdinalIgnoreCase)
                    && !domain.IsThisDomain(text[..backslash].Trim());
                results[i] = otherDomain
                    ? new GpoPrincipal(written, null, GpoPrincipalKind.Unresolved)
                    : new GpoPrincipal(written, known, SecurityTemplate.IsSid(text) ? GpoPrincipalKind.Sid : GpoPrincipalKind.WellKnown);
                continue;
            }

            if (backslash >= 0)
            {
                var name = text[(backslash + 1)..].Trim();
                if (backslash == 0 || name.Length == 0 || !domain.IsThisDomain(text[..backslash].Trim()))
                {
                    results[i] = new GpoPrincipal(written, null, GpoPrincipalKind.Unresolved);
                    continue;
                }

                Add(bySam, name, i);
            }
            else if (text.Contains('@', StringComparison.Ordinal))
            {
                Add(byUpn, text, i);
            }
            else
            {
                Add(bySam, text, i);
            }
        }

        await LookupAsync(session, Attr.SamAccountName, bySam, inputs, results, cancellationToken).ConfigureAwait(false);
        await LookupAsync(session, Attr.UserPrincipalName, byUpn, inputs, results, cancellationToken).ConfigureAwait(false);
        return [.. results.Select((r, i) => r ?? new GpoPrincipal(inputs[i], null, GpoPrincipalKind.Unresolved))];
    }

    private static void Add(Dictionary<string, List<int>> map, string name, int index)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = [];
            map[name] = list;
        }

        list.Add(index);
    }

    /// <summary>One OR search per <see cref="NamesPerSearch"/> values of <paramref name="attribute"/> (accounts with a SID only).</summary>
    private static async Task LookupAsync(IDirectorySession session, string attribute, Dictionary<string, List<int>> names,
        IReadOnlyList<string> inputs, GpoPrincipal?[] results, CancellationToken cancellationToken)
    {
        foreach (var chunk in names.Keys.Chunk(NamesPerSearch))
        {
            await foreach (var account in session.SearchAsync(new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.And(LdapFilter.Present(Attr.ObjectSid), LdapFilter.Or(chunk.Select(n => LdapFilter.Equal(attribute, n)))),
                Attributes = [attribute, Attr.ObjectSid],
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (account.GetString(attribute) is { } value && account.ObjectSid is { } sid && names.TryGetValue(value, out var indexes))
                {
                    foreach (var index in indexes)
                    {
                        results[index] ??= new GpoPrincipal(inputs[index], sid.ToUpperInvariant(), GpoPrincipalKind.Account);
                    }
                }
            }
        }
    }
}
