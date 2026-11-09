using AdMng.Core.Directory;

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
/// DC; filters built with <c>LdapFilter</c> (rule 3).
/// </summary>
public static class GpoPrincipalResolver
{
    /// <summary>Names per OR search.</summary>
    public const int NamesPerSearch = 50;

    /// <summary>
    /// The SID of a principal without LDAP: SID text with or without <c>*</c>, the en/pl well-known names, <c>BUILTIN\…</c>,
    /// local groups and the domain RIDs 500/512/513/515 (wraps the internal <c>GpoPrincipals.SidOf</c>); null otherwise.
    /// </summary>
    public static string? KnownSid(string? principal, string? domainSid) =>
        throw new NotImplementedException("K7 W1: GpoPrincipalResolver.KnownSid");

    /// <summary>
    /// One result per distinct input: <see cref="KnownSid"/> first, then LDAP for <c>DOMAIN\name</c> (only this domain:
    /// <c>new GroupPolicyDomainNames(session.Info.DomainDnsName, session.Info.DomainNetBiosName).IsThisDomain</c>), UPN and
    /// bare names, <see cref="NamesPerSearch"/> per OR search below the domain NC.
    /// </summary>
    public static Task<IReadOnlyList<GpoPrincipal>> ResolveAsync(IDirectorySession session, IEnumerable<string> principals,
        string? domainSid, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W1: GpoPrincipalResolver.ResolveAsync");
}
