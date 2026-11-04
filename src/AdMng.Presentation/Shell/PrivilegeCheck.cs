using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Best effort detection of a highly privileged bound identity (Domain Admins, Enterprise Admins, Schema Admins,
/// BUILTIN\Administrators): tokenGroups of the bound account (base read, transitive, includes universal groups of the
/// forest root), falling back to an IN_CHAIN group query when tokenGroups is not readable.
/// </summary>
public static class PrivilegeCheck
{
    /// <summary>BUILTIN\Administrators.</summary>
    public const string BuiltinAdministratorsSid = "S-1-5-32-544";

    private static readonly uint[] PrivilegedDomainRids =
        [WellKnownRids.DomainAdmins, WellKnownRids.SchemaAdmins, WellKnownRids.EnterpriseAdmins];

    /// <summary>The domain groups KB5020276 trusts as the owner of a reused computer account (not Schema Admins).</summary>
    private static readonly uint[] JoinOwnerDomainRids = [WellKnownRids.DomainAdmins, WellKnownRids.EnterpriseAdmins];

    public static bool IsPrivilegedSid(string? sid) => IsAdministratorSid(sid, PrivilegedDomainRids);

    /// <summary>
    /// Domain Admins, Enterprise Admins or BUILTIN\Administrators: the owners the domain join hardening of KB5020276 trusts
    /// when a join reuses an existing computer account.
    /// </summary>
    public static bool IsJoinOwnerSid(string? sid) => IsAdministratorSid(sid, JoinOwnerDomainRids);

    public static Task<bool> IsPrivilegedAsync(IDirectorySession session, string userDn, CancellationToken cancellationToken) =>
        HasGroupAsync(session, userDn, IsPrivilegedSid, cancellationToken);

    /// <summary>Whether the account is a member of a group <see cref="IsJoinOwnerSid"/> accepts (prestaged accounts).</summary>
    public static Task<bool> IsJoinOwnerAsync(IDirectorySession session, string userDn, CancellationToken cancellationToken) =>
        HasGroupAsync(session, userDn, IsJoinOwnerSid, cancellationToken);

    private static bool IsAdministratorSid(string? sid, uint[] domainRids)
    {
        if (string.IsNullOrEmpty(sid))
        {
            return false;
        }

        if (string.Equals(sid, BuiltinAdministratorsSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
            && AdConvert.GetRid(sid) is { } rid
            && domainRids.Contains(rid);
    }

    private static async Task<bool> HasGroupAsync(IDirectorySession session, string userDn, Func<string?, bool> matches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDn);
        var user = await session.GetAsync(userDn, [Attr.TokenGroups], cancellationToken).ConfigureAwait(false);
        if (user is not null && user.TryGet(Attr.TokenGroups, out var tokenGroups) && tokenGroups.Count > 0)
        {
            return tokenGroups.OfType<byte[]>().Any(b => b.Length >= 8 && matches(AdConvert.SidToString(b)));
        }

        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectCategory, "group"), LdapFilter.InChain(Attr.Member, userDn)),
            Attributes = [Attr.ObjectSid],
        };
        await foreach (var group in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            if (matches(group.ObjectSid))
            {
                return true;
            }
        }

        return false;
    }
}
