using System.Collections.Frozen;
using System.Globalization;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>Where a right applies (shown with every finding).</summary>
public enum PrivilegeScopeKind
{
    /// <summary>The whole forest (Enterprise Admins, Schema Admins, Enterprise Key Admins).</summary>
    Forest,

    /// <summary>The whole domain (Domain Admins, Administrators, DnsAdmins, delegation, SID history).</summary>
    Domain,

    /// <summary>The domain controllers (the built-in domain-local groups of CN=Builtin act on the DCs).</summary>
    DomainControllers,

    /// <summary>The account itself (a setting of the account: SPN, pre-authentication, adminCount).</summary>
    Account,

    /// <summary>A container or object of the directory (U4b: ACL delegations); the finding names it.</summary>
    Container,

    /// <summary>Computers (U4c: local groups and user rights from GPOs, the linked OU is named).</summary>
    Computers,

    /// <summary>The peers of the account (same OU, department or title).</summary>
    Peers,
}

/// <summary>
/// A built-in or well-known privileged group. Groups are recognised by SID or RID (never by name: the names are
/// localized in Polish, German … domains); only groups without a well-known RID (DnsAdmins, Exchange) by their
/// sAMAccountName, which AD does not localize.
/// </summary>
public sealed record PrivilegedGroupDefinition
{
    /// <summary>Stable id (resource keys <c>PrivilegedAccess.Group.{Id}</c>, level overrides in the settings).</summary>
    public required string Id { get; init; }

    /// <summary>The well-known SID of a group of CN=Builtin (S-1-5-32-…).</summary>
    public string? BuiltinSid { get; init; }

    /// <summary>The RID of a group of the domain (SID = domain SID + RID).</summary>
    public uint? Rid { get; init; }

    /// <summary>The group exists only in the forest root domain (Enterprise Admins, Schema Admins …).</summary>
    public bool IsForestRoot { get; init; }

    /// <summary>The sAMAccountName of a group without a well-known RID (case-insensitive).</summary>
    public string? SamAccountName { get; init; }

    public required PrivilegeLevel DefaultLevel { get; init; }

    public required PrivilegeScopeKind Scope { get; init; }

    /// <summary>Protected by AdminSDHolder (SDProp sets adminCount = 1 on its members).</summary>
    public bool IsProtected { get; init; }

    /// <summary>The SID of the group in a domain; null for name-matched groups or when the needed domain SID is unknown.</summary>
    public string? SidIn(string? domainSid, string? rootDomainSid)
    {
        if (BuiltinSid is not null)
        {
            return BuiltinSid;
        }

        var baseSid = IsForestRoot ? rootDomainSid : domainSid;
        return Rid is { } rid && PrivilegedGroupCatalog.IsDomainSid(baseSid)
            ? string.Create(CultureInfo.InvariantCulture, $"{baseSid}-{rid}")
            : null;
    }
}

/// <summary>
/// The privileged groups of Active Directory, from Tier 0 down to the minor built-in groups (README U4, source 1). Shared
/// by the account review (U2 "privileged with a problem", U4 "Uprawnienia") and AdMng Watchdog (forest-root RIDs; the
/// Watchdog's configurable default tiers are kept consistent with it by a test).
/// </summary>
public static class PrivilegedGroupCatalog
{
    /// <summary>Everyone (S-1-1-0): every account is a member; a group it is nested in is held by everyone.</summary>
    public const string EveryoneSid = "S-1-1-0";

    /// <summary>Authenticated Users (S-1-5-11): every account is a member once it logged on.</summary>
    public const string AuthenticatedUsersSid = "S-1-5-11";

    /// <summary>All groups of the catalogue, highest level first.</summary>
    public static IReadOnlyList<PrivilegedGroupDefinition> All { get; } =
    [
        // Tier 0: control of the domain or the forest.
        Builtin("Administrators", "S-1-5-32-544", PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain, isProtected: true),
        Domain("DomainAdmins", WellKnownRids.DomainAdmins, PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain, isProtected: true),
        Root("EnterpriseAdmins", WellKnownRids.EnterpriseAdmins, PrivilegeLevel.Tier0, isProtected: true),
        Root("SchemaAdmins", WellKnownRids.SchemaAdmins, PrivilegeLevel.Tier0, isProtected: true),
        Domain("DomainControllers", WellKnownRids.DomainControllers, PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain, isProtected: true),
        Builtin("AccountOperators", "S-1-5-32-548", PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain, isProtected: true),
        Builtin("ServerOperators", "S-1-5-32-549", PrivilegeLevel.Tier0, PrivilegeScopeKind.DomainControllers, isProtected: true),
        Builtin("PrintOperators", "S-1-5-32-550", PrivilegeLevel.Tier0, PrivilegeScopeKind.DomainControllers, isProtected: true),
        Builtin("BackupOperators", "S-1-5-32-551", PrivilegeLevel.Tier0, PrivilegeScopeKind.DomainControllers, isProtected: true),
        Domain("KeyAdmins", WellKnownRids.KeyAdmins, PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain, isProtected: true),
        Root("EnterpriseKeyAdmins", WellKnownRids.EnterpriseKeyAdmins, PrivilegeLevel.Tier0, isProtected: true),
        Named("DnsAdmins", "DnsAdmins", PrivilegeLevel.Tier0, PrivilegeScopeKind.DomainControllers),
        Named("ExchangeWindowsPermissions", "Exchange Windows Permissions", PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain),
        Named("ExchangeTrustedSubsystem", "Exchange Trusted Subsystem", PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain),

        // High: many objects or a path to Tier 0.
        Domain("GroupPolicyCreatorOwners", WellKnownRids.GroupPolicyCreatorOwners, PrivilegeLevel.High, PrivilegeScopeKind.Domain),
        Domain("CertPublishers", WellKnownRids.CertPublishers, PrivilegeLevel.High, PrivilegeScopeKind.Domain),
        Domain("ReadOnlyDomainControllers", WellKnownRids.ReadOnlyDomainControllers, PrivilegeLevel.High, PrivilegeScopeKind.Domain, isProtected: true),
        Root("EnterpriseReadOnlyDomainControllers", WellKnownRids.EnterpriseReadOnlyDomainControllers, PrivilegeLevel.High, isProtected: false),
        Domain("CloneableDomainControllers", WellKnownRids.CloneableDomainControllers, PrivilegeLevel.High, PrivilegeScopeKind.Domain),
        Named("OrganizationManagement", "Organization Management", PrivilegeLevel.High, PrivilegeScopeKind.Domain),

        // Elevated: access to the domain controllers or legacy rights.
        Builtin("Replicator", "S-1-5-32-552", PrivilegeLevel.Elevated, PrivilegeScopeKind.DomainControllers, isProtected: true),
        Builtin("RemoteDesktopUsers", "S-1-5-32-555", PrivilegeLevel.Elevated, PrivilegeScopeKind.DomainControllers),
        Builtin("RemoteManagementUsers", "S-1-5-32-580", PrivilegeLevel.Elevated, PrivilegeScopeKind.DomainControllers),
        Builtin("HyperVAdministrators", "S-1-5-32-578", PrivilegeLevel.Elevated, PrivilegeScopeKind.DomainControllers),
        Builtin("StorageReplicaAdministrators", "S-1-5-32-582", PrivilegeLevel.Elevated, PrivilegeScopeKind.DomainControllers),
        Builtin("IncomingForestTrustBuilders", "S-1-5-32-557", PrivilegeLevel.Elevated, PrivilegeScopeKind.Forest),

        // Minor: small extra rights.
        Builtin("NetworkConfigurationOperators", "S-1-5-32-556", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("PerformanceMonitorUsers", "S-1-5-32-558", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("PerformanceLogUsers", "S-1-5-32-559", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("WindowsAuthorizationAccessGroup", "S-1-5-32-560", PrivilegeLevel.Minor, PrivilegeScopeKind.Domain),
        Builtin("TerminalServerLicenseServers", "S-1-5-32-561", PrivilegeLevel.Minor, PrivilegeScopeKind.Domain),
        Builtin("DistributedComUsers", "S-1-5-32-562", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("CryptographicOperators", "S-1-5-32-569", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("EventLogReaders", "S-1-5-32-573", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("CertificateServiceDcomAccess", "S-1-5-32-574", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("RdsRemoteAccessServers", "S-1-5-32-575", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("RdsEndpointServers", "S-1-5-32-576", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("RdsManagementServers", "S-1-5-32-577", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Builtin("AccessControlAssistanceOperators", "S-1-5-32-579", PrivilegeLevel.Minor, PrivilegeScopeKind.DomainControllers),
        Named("DnsUpdateProxy", "DnsUpdateProxy", PrivilegeLevel.Minor, PrivilegeScopeKind.Domain),
    ];

    private static readonly FrozenDictionary<string, PrivilegedGroupDefinition> ById =
        All.ToFrozenDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, PrivilegedGroupDefinition> ByBuiltinSid =
        All.Where(g => g.BuiltinSid is not null).ToFrozenDictionary(g => g.BuiltinSid!, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, PrivilegedGroupDefinition> ByName =
        All.Where(g => g.SamAccountName is not null).ToFrozenDictionary(g => g.SamAccountName!, StringComparer.OrdinalIgnoreCase);

    /// <summary>RIDs of the groups that exist only in the forest root domain (518, 519, 527, 498).</summary>
    public static IReadOnlyList<uint> ForestRootRids { get; } = [.. All.Where(g => g.IsForestRoot).Select(g => g.Rid!.Value)];

    /// <summary>RIDs of the Tier 0 groups of the forest root domain: Schema Admins, Enterprise Admins, Enterprise Key Admins.</summary>
    public static IReadOnlyList<uint> ForestRootTier0Rids { get; } =
        [.. All.Where(g => g.IsForestRoot && g.DefaultLevel == PrivilegeLevel.Tier0).Select(g => g.Rid!.Value)];

    /// <summary>
    /// The groups the account review (U2) treats as protected: AdminSDHolder groups and every SID/RID group of level
    /// High or Tier 0 (name-matched groups need a name search and are left to the U4 tab).
    /// </summary>
    public static IReadOnlyList<PrivilegedGroupDefinition> ProtectedOrHigh { get; } =
        [.. All.Where(g => g.SamAccountName is null && (g.IsProtected || g.DefaultLevel >= PrivilegeLevel.High))];

    /// <summary>The definition with this id, or null.</summary>
    public static PrivilegedGroupDefinition? Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var definition) ? definition : null;

    /// <summary>
    /// The catalogue entry of a group, by SID (built-in SIDs, domain SID + RID; forest-root RIDs in the forest root domain,
    /// or in any domain while the root domain SID is unknown), else by sAMAccountName for the name-matched groups.
    /// </summary>
    /// <param name="sid">objectSid of the group (SDDL form).</param>
    /// <param name="samAccountName">sAMAccountName of the group.</param>
    /// <param name="domainSid">SID of the domain the group was read from.</param>
    /// <param name="rootDomainSid">SID of the forest root domain, null when unknown.</param>
    public static PrivilegedGroupDefinition? Match(string? sid, string? samAccountName, string? domainSid, string? rootDomainSid)
    {
        if (sid is not null)
        {
            if (ByBuiltinSid.TryGetValue(sid, out var builtin))
            {
                return builtin;
            }

            if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && AdConvert.GetRid(sid) is { } rid)
            {
                var prefix = sid[..sid.LastIndexOf('-')];
                foreach (var group in All)
                {
                    if (group.Rid != rid)
                    {
                        continue;
                    }

                    var owner = group.IsForestRoot ? rootDomainSid ?? (IsDomainSid(prefix) ? prefix : null) : domainSid;
                    if (owner is not null && string.Equals(owner, prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return group;
                    }
                }
            }
        }

        return samAccountName is not null && ByName.TryGetValue(samAccountName, out var named) ? named : null;
    }

    /// <summary>A domain SID in SDDL form: S-1-5-21 and three numeric sub-authorities.</summary>
    public static bool IsDomainSid(string? sid)
    {
        var parts = sid?.Split('-');
        return parts is ["S", "1", "5", "21", _, _, _]
            && parts.Skip(4).All(p => p.Length > 0 && uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static PrivilegedGroupDefinition Builtin(string id, string sid, PrivilegeLevel level, PrivilegeScopeKind scope, bool isProtected = false) =>
        new() { Id = id, BuiltinSid = sid, DefaultLevel = level, Scope = scope, IsProtected = isProtected };

    private static PrivilegedGroupDefinition Domain(string id, uint rid, PrivilegeLevel level, PrivilegeScopeKind scope, bool isProtected = false) =>
        new() { Id = id, Rid = rid, DefaultLevel = level, Scope = scope, IsProtected = isProtected };

    private static PrivilegedGroupDefinition Root(string id, uint rid, PrivilegeLevel level, bool isProtected) =>
        new() { Id = id, Rid = rid, IsForestRoot = true, DefaultLevel = level, Scope = PrivilegeScopeKind.Forest, IsProtected = isProtected };

    private static PrivilegedGroupDefinition Named(string id, string samAccountName, PrivilegeLevel level, PrivilegeScopeKind scope) =>
        new() { Id = id, SamAccountName = samAccountName, DefaultLevel = level, Scope = scope };
}
