using AdMng.Core.GroupPolicy;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>How a GPO grants a local right (resource keys <c>PrivilegedAccess.Mechanism.{value}</c>).</summary>
public enum LocalRightMechanism
{
    /// <summary>Restricted Groups, Members form: the local group's members are set by the GPO.</summary>
    RestrictedGroupMembers,

    /// <summary>Restricted Groups, MemberOf form: a group or account is added to the local group.</summary>
    RestrictedGroupMemberOf,

    /// <summary>Group Policy Preferences, Local Users and Groups (Groups.xml).</summary>
    PreferenceGroups,

    /// <summary>User Rights Assignment ([Privilege Rights] of GptTmpl.inf).</summary>
    UserRightsAssignment,
}

/// <summary>
/// Where a <see cref="PrivilegeFindingKind.LocalRight"/> comes from (U4c): the GPO, the link it applies through, the
/// mechanism, the right (a local group's SID or a user right's constant) and the computers it currently reaches.
/// Directory data in memory only (CLAUDE.md rule 1).
/// </summary>
public sealed record GroupPolicyGrantDetails
{
    public required Guid GpoId { get; init; }

    public required string GpoName { get; init; }

    public required string GpoDn { get; init; }

    public required LocalRightMechanism Mechanism { get; init; }

    /// <summary>
    /// The local group's SID (memberships), the user right's constant (<c>SeDebugPrivilege</c>) or the SID of a domain group
    /// whose membership the GPO enforces on the DCs.
    /// </summary>
    public required string Right { get; init; }

    /// <summary>The catalogue entry of the right (<see cref="LocalRightsCatalog"/>).</summary>
    public required LocalRightDefinition Definition { get; init; }

    /// <summary>The site, domain head or OU of the link (also the finding's scope).</summary>
    public required string LinkDn { get; init; }

    public bool LinkEnforced { get; init; }

    /// <summary>Link order within <see cref="LinkDn"/> (1 = highest precedence).</summary>
    public int LinkOrder { get; init; }

    /// <summary>A GPP item with item-level targeting: only the computers its filter matches get it.</summary>
    public bool ItemLevelTargeting { get; init; }

    /// <summary>The GPO has a WMI filter: only the computers where the query is true get it.</summary>
    public bool WmiFilter { get; init; }

    public bool IsConditional => ItemLevelTargeting || WmiFilter;

    /// <summary>The computers the right currently reaches through this link, by role.</summary>
    public int DomainControllers { get; init; }

    public int Servers { get; init; }

    public int Workstations { get; init; }

    public int Computers => DomainControllers + Servers + Workstations;
}

/// <summary>What a local right is: a local group (by SID), a user right (by constant) or a group of the domain.</summary>
public enum LocalRightKind
{
    LocalGroup,
    UserRight,

    /// <summary>
    /// A privileged group of the domain (catalogue, not built-in) whose membership Restricted Groups enforce on the domain
    /// controllers (their "local" groups are the domain's groups); <see cref="LocalRightDefinition.Id"/> is the catalogue id.
    /// </summary>
    DomainGroup,
}

/// <summary>
/// A local group or user right the review reports (resource keys <c>PrivilegedAccess.LocalRight.{Id}</c> for what it
/// gives; local groups are named with <c>PrivilegedAccess.GroupName.{Id}</c>, user rights with
/// <c>PrivilegedAccess.UserRightName.{Id}</c>).
/// </summary>
/// <param name="Id">Stable id: the catalogue id of the built-in group, or the user right constant.</param>
/// <param name="Kind">Local group or user right.</param>
/// <param name="Key">The SID of the local group or the user right constant.</param>
/// <param name="WorkstationLevel">Level on workstations (None: not reported there).</param>
/// <param name="ServerLevel">Level on member servers (None: not reported there).</param>
/// <param name="DomainControllerLevel">
/// Level on domain controllers, where the local groups are the built-in groups of the domain (None: not reported; for a
/// catalogue group the operator's level of that group is used instead).
/// </param>
public sealed record LocalRightDefinition(
    string Id,
    LocalRightKind Kind,
    string Key,
    PrivilegeLevel WorkstationLevel,
    PrivilegeLevel ServerLevel,
    PrivilegeLevel DomainControllerLevel);

/// <summary>
/// The local groups and user rights a GPO can grant that are more than a plain domain user has (design
/// docs/dev/u4-privileged-access.md section 6, U4c). Local groups are recognised by SID only (their names are localized);
/// on a domain controller the "local" groups are the built-in groups of the domain, so the level there is the catalogue's
/// (with the operator's overrides): Administrators and Backup Operators are Tier 0 through the Default Domain Controllers
/// Policy or any GPO that reaches the DCs. The sensitive user rights are High on members and Tier 0 on DCs (Access Credential
/// Manager as a trusted caller: Elevated on members, High on DCs); interactive and Remote Desktop logon are reported only
/// on DCs. Rights whose default holders include the domain's own groups (e.g. <c>SeSecurityPrivilege</c>, held by Exchange
/// Servers on the DCs) are left out.
/// </summary>
public static class LocalRightsCatalog
{
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";
    public const string PowerUsersSid = "S-1-5-32-547";
    public const string BackupOperatorsSid = "S-1-5-32-551";
    public const string RemoteDesktopUsersSid = "S-1-5-32-555";
    public const string RemoteManagementUsersSid = "S-1-5-32-580";

    /// <summary>Every local group and user right of the catalogue.</summary>
    public static IReadOnlyList<LocalRightDefinition> All { get; } =
    [
        Group("Administrators", AdministratorsSid, PrivilegeLevel.Elevated, PrivilegeLevel.High),
        Group("BackupOperators", BackupOperatorsSid, PrivilegeLevel.High, PrivilegeLevel.High),
        Group("PowerUsers", PowerUsersSid, PrivilegeLevel.Elevated, PrivilegeLevel.Elevated),
        Group("RemoteDesktopUsers", RemoteDesktopUsersSid, PrivilegeLevel.Elevated, PrivilegeLevel.Elevated),
        Group("RemoteManagementUsers", RemoteManagementUsersSid, PrivilegeLevel.Elevated, PrivilegeLevel.Elevated),
        Group("HyperVAdministrators", "S-1-5-32-578", PrivilegeLevel.Elevated, PrivilegeLevel.Elevated),
        Group("NetworkConfigurationOperators", "S-1-5-32-556", PrivilegeLevel.Minor, PrivilegeLevel.Minor),
        Group("PerformanceMonitorUsers", "S-1-5-32-558", PrivilegeLevel.Minor, PrivilegeLevel.Minor),
        Group("PerformanceLogUsers", "S-1-5-32-559", PrivilegeLevel.Minor, PrivilegeLevel.Minor),
        Group("DistributedComUsers", "S-1-5-32-562", PrivilegeLevel.Minor, PrivilegeLevel.Minor),
        Group("CryptographicOperators", "S-1-5-32-569", PrivilegeLevel.Minor, PrivilegeLevel.Minor),
        Group("EventLogReaders", "S-1-5-32-573", PrivilegeLevel.Minor, PrivilegeLevel.Minor),
        Group("AccessControlAssistanceOperators", "S-1-5-32-579", PrivilegeLevel.Minor, PrivilegeLevel.Minor),

        // Only on domain controllers (the built-in groups of the domain): the catalogue's level.
        Group("AccountOperators", "S-1-5-32-548", PrivilegeLevel.None, PrivilegeLevel.None),
        Group("ServerOperators", "S-1-5-32-549", PrivilegeLevel.None, PrivilegeLevel.None),
        Group("PrintOperators", "S-1-5-32-550", PrivilegeLevel.None, PrivilegeLevel.None),

        Right("SeDebugPrivilege"),
        Right("SeBackupPrivilege"),
        Right("SeRestorePrivilege"),
        Right("SeTakeOwnershipPrivilege"),
        Right("SeLoadDriverPrivilege"),
        Right("SeImpersonatePrivilege"),
        Right("SeTcbPrivilege"),
        Right("SeAssignPrimaryTokenPrivilege"),
        Right("SeEnableDelegationPrivilege"),
        Right("SeCreateTokenPrivilege"),
        Right("SeManageVolumePrivilege"),
        Right("SeRelabelPrivilege"),
        new LocalRightDefinition("SeTrustedCredManAccessPrivilege", LocalRightKind.UserRight, "SeTrustedCredManAccessPrivilege",
            PrivilegeLevel.Elevated, PrivilegeLevel.Elevated, PrivilegeLevel.High),
        new LocalRightDefinition("SeRemoteInteractiveLogonRight", LocalRightKind.UserRight, "SeRemoteInteractiveLogonRight",
            PrivilegeLevel.None, PrivilegeLevel.None, PrivilegeLevel.Elevated),
        new LocalRightDefinition("SeInteractiveLogonRight", LocalRightKind.UserRight, "SeInteractiveLogonRight",
            PrivilegeLevel.None, PrivilegeLevel.None, PrivilegeLevel.Elevated),
    ];

    private static readonly Dictionary<string, LocalRightDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// English and Polish names of the main local groups (Restricted Groups and GPP may name a group instead of its SID;
    /// GPP adds " (built-in)" / " (wbudowane)"). The names of the domain's own built-in groups are matched too (the
    /// review takes them from the group graph).
    /// </summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Administrators"] = AdministratorsSid,
        ["Administratorzy"] = AdministratorsSid,
        ["Backup Operators"] = BackupOperatorsSid,
        ["Operatorzy kopii zapasowych"] = BackupOperatorsSid,
        ["Power Users"] = PowerUsersSid,
        ["Użytkownicy zaawansowani"] = PowerUsersSid,
        ["Remote Desktop Users"] = RemoteDesktopUsersSid,
        ["Użytkownicy pulpitu zdalnego"] = RemoteDesktopUsersSid,
        ["Remote Management Users"] = RemoteManagementUsersSid,
        ["Użytkownicy zarządzania zdalnego"] = RemoteManagementUsersSid,
        ["Users"] = UsersSid,
        ["Użytkownicy"] = UsersSid,
    };

    /// <summary>The catalogue entry of a local group SID or user right constant, null when it is not reported.</summary>
    public static LocalRightDefinition? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>
    /// The SID of a local group named in a GPO: a SID as is, a known English/Polish name (with or without
    /// " (built-in)"/" (wbudowane)"), else <paramref name="lookup"/> (the domain's built-in group names).
    /// </summary>
    public static string? LocalGroupSid(string? nameOrSid, Func<string, string?>? lookup = null)
    {
        if (string.IsNullOrWhiteSpace(nameOrSid))
        {
            return null;
        }

        var text = SecurityTemplate.Principal(nameOrSid);
        if (SecurityTemplate.IsSid(text))
        {
            return text;
        }

        var name = text;
        var backslash = name.LastIndexOf('\\');
        if (backslash >= 0)
        {
            // BUILTIN\Administrators, .\Administrators
            name = name[(backslash + 1)..];
        }

        foreach (var suffix in new[] { " (built-in)", " (wbudowane)", " (wbudowana)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
            }
        }

        name = name.Trim();
        return KnownNames.TryGetValue(name, out var sid) ? sid : lookup?.Invoke(name);
    }

    /// <summary>
    /// A privileged group of the domain as the target of Restricted Groups: it has an effect only on the domain controllers,
    /// where its level is the catalogue's (with the operator's override).
    /// </summary>
    /// <param name="group">The catalogue entry (a domain group, not a built-in one).</param>
    /// <param name="sid">The group's SID in this domain.</param>
    public static LocalRightDefinition DomainGroup(PrivilegedGroupDefinition group, string sid)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        return new LocalRightDefinition(group.Id, LocalRightKind.DomainGroup, sid, PrivilegeLevel.None, PrivilegeLevel.None, PrivilegeLevel.None);
    }

    /// <summary>
    /// The level of a right on a computer of <paramref name="role"/>: on a DC the catalogue level of the built-in group
    /// (with the operator's override) when the group is in <see cref="PrivilegedGroupCatalog"/>, else the definition's; a
    /// domain group has its catalogue level on the DCs and none elsewhere.
    /// </summary>
    public static PrivilegeLevel LevelOn(LocalRightDefinition definition, ComputerRole role, PrivilegedAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        return role switch
        {
            ComputerRole.DomainController when definition.Kind == LocalRightKind.DomainGroup
                && PrivilegedGroupCatalog.Find(definition.Id) is { } domainGroup => options.LevelOf(domainGroup),
            ComputerRole.DomainController when definition.Kind == LocalRightKind.LocalGroup
                && PrivilegedGroupCatalog.Match(definition.Key, null, null, null) is { } group => options.LevelOf(group),
            ComputerRole.DomainController => definition.DomainControllerLevel,
            ComputerRole.Server => definition.ServerLevel,
            _ => definition.WorkstationLevel,
        };
    }

    private static LocalRightDefinition Group(string id, string sid, PrivilegeLevel workstation, PrivilegeLevel server) =>
        new(id, LocalRightKind.LocalGroup, sid, workstation, server, server);

    private static LocalRightDefinition Right(string constant) =>
        new(constant, LocalRightKind.UserRight, constant, PrivilegeLevel.High, PrivilegeLevel.High, PrivilegeLevel.Tier0);
}
