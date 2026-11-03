using System.Globalization;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// The principals the diagnostics rules recognise (design note docs/dev/k-computers-and-gpo.md 20.2): the broad groups
/// (Everyone, Authenticated Users, This Organization, BUILTIN\Users, Domain Users, Domain Computers), the administrators and
/// the domain SIDs that need a lookup. A principal of a GPO is a SID or a name as written (English and Polish names of the
/// well-known groups are recognised; local groups through <see cref="LocalRightsCatalog.LocalGroupSid"/>).
/// </summary>
internal static class GpoPrincipals
{
    public const string Everyone = GroupPolicySecurityFilter.EveryoneSid;
    public const string AuthenticatedUsers = GroupPolicySecurityFilter.AuthenticatedUsersSid;
    public const string ThisOrganization = GroupPolicySecurityFilter.ThisOrganizationSid;
    public const string Administrators = LocalRightsCatalog.AdministratorsSid;
    public const string BuiltinUsers = LocalRightsCatalog.UsersSid;

    public const int AdministratorRid = 500;
    public const int DomainAdminsRid = 512;
    public const int DomainUsersRid = 513;
    public const int DomainComputersRid = 515;

    /// <summary>Domain RIDs below this are the built-in accounts and groups of every domain: never looked up, never orphaned.</summary>
    public const int FirstAccountRid = 1000;

    private static readonly Dictionary<string, string> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Everyone"] = Everyone,
        ["Wszyscy"] = Everyone,
        ["Authenticated Users"] = AuthenticatedUsers,
        ["Użytkownicy uwierzytelnieni"] = AuthenticatedUsers,
        ["This Organization"] = ThisOrganization,
        ["Ta organizacja"] = ThisOrganization,
    };

    private static readonly Dictionary<string, int> DomainNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Administrator"] = AdministratorRid,
        ["Domain Admins"] = DomainAdminsRid,
        ["Administratorzy domeny"] = DomainAdminsRid,
        ["Domain Users"] = DomainUsersRid,
        ["Użytkownicy domeny"] = DomainUsersRid,
        ["Domain Computers"] = DomainComputersRid,
        ["Komputery domeny"] = DomainComputersRid,
    };

    /// <summary>
    /// The SID of a principal as a GPO writes it: a SID as is (upper case); a well-known name, a local group name or a
    /// domain group name (with or without <c>DOMAIN\</c>); null for any other name.
    /// </summary>
    public static string? SidOf(string? principal, string? domainSid)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return null;
        }

        var text = SecurityTemplate.Principal(principal);
        if (SecurityTemplate.IsSid(text))
        {
            return text.ToUpperInvariant();
        }

        var name = text;
        var backslash = name.LastIndexOf('\\');
        if (backslash >= 0)
        {
            name = name[(backslash + 1)..];
        }

        name = name.Trim();
        if (WellKnownNames.TryGetValue(name, out var wellKnown))
        {
            return wellKnown;
        }

        if (LocalRightsCatalog.LocalGroupSid(text) is { } local)
        {
            return local;
        }

        return domainSid is not null && DomainNames.TryGetValue(name, out var rid) ? DomainSid(domainSid, rid) : null;
    }

    /// <summary><c>&lt;domain SID&gt;-&lt;rid&gt;</c>.</summary>
    public static string DomainSid(string domainSid, int rid) => string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{rid}");

    /// <summary>The broad groups of the design note: Everyone, Authenticated Users, This Organization, BUILTIN\Users, Domain Users, Domain Computers.</summary>
    public static IReadOnlySet<string> BroadSids(string? domainSid)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Everyone, AuthenticatedUsers, ThisOrganization, BuiltinUsers };
        if (domainSid is not null)
        {
            result.Add(DomainSid(domainSid, DomainUsersRid));
            result.Add(DomainSid(domainSid, DomainComputersRid));
        }

        return result;
    }

    /// <summary>The principals a deny-logon right must not name: Everyone, Authenticated Users, Administrators, Domain Users.</summary>
    public static IReadOnlySet<string> DenyLogonSids(string? domainSid)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Everyone, AuthenticatedUsers, Administrators };
        if (domainSid is not null)
        {
            result.Add(DomainSid(domainSid, DomainUsersRid));
        }

        return result;
    }

    /// <summary>A SID of the domain itself (<c>&lt;domain SID&gt;-&lt;rid&gt;</c>), with its RID.</summary>
    public static bool TryGetDomainRid(string? sid, string? domainSid, out long rid)
    {
        rid = 0;
        if (sid is null || domainSid is null || sid.Length <= domainSid.Length + 1
            || !sid.StartsWith(domainSid + "-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(sid.AsSpan(domainSid.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out rid);
    }

    /// <summary>A domain SID of an account or group (RID 1000 or above) that only a lookup can confirm.</summary>
    public static bool NeedsLookup(string? sid, string? domainSid) => TryGetDomainRid(sid, domainSid, out var rid) && rid >= FirstAccountRid;

    /// <summary>A domain SID that needs a lookup and is not among <paramref name="existing"/>.</summary>
    public static bool IsUnresolved(string? sid, string? domainSid, IReadOnlySet<string> existing) =>
        NeedsLookup(sid, domainSid) && !existing.Contains(sid!);
}
