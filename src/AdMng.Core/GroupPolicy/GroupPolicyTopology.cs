using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.GroupPolicy;

/// <summary>LDAP names of the group policy attributes.</summary>
public static class GroupPolicyAttributes
{
    public const string GpOptions = "gPOptions";
    public const string FileSysPath = "gPCFileSysPath";
    public const string Flags = "flags";
    public const string MachineExtensionNames = "gPCMachineExtensionNames";
    public const string WqlFilter = "gPCWQLFilter";
    public const string ServerReference = "serverReference";
    public const string VersionNumber = "versionNumber";
    public const string UserExtensionNames = "gPCUserExtensionNames";
    public const string FunctionalityVersion = "gPCFunctionalityVersion";

    /// <summary>objectClass of a group policy container.</summary>
    public const string ContainerClass = "groupPolicyContainer";

    /// <summary>objectClass of a WMI filter (<c>CN=SOM,CN=WMIPolicy,CN=System</c>).</summary>
    public const string WmiFilterClass = "msWMI-Som";
}

/// <summary>A group policy container (GPC) of <c>CN=Policies,CN=System</c>.</summary>
public sealed record GroupPolicyObjectInfo
{
    /// <summary><c>flags</c> bit: the computer configuration of the GPO is disabled.</summary>
    public const int ComputerSettingsDisabledFlag = 2;

    /// <summary>The GPO's GUID (the cn of the container).</summary>
    public required Guid Id { get; init; }

    public required string Dn { get; init; }

    /// <summary>displayName, else the GUID.</summary>
    public required string DisplayName { get; init; }

    /// <summary>gPCFileSysPath as stored (never used as a path: files are read from the session DC by GUID).</summary>
    public string? FileSysPath { get; init; }

    public int Flags { get; init; }

    /// <summary>
    /// The client-side extensions of the computer configuration (<c>gPCMachineExtensionNames</c>), null when the attribute
    /// is not set (then every file is read).
    /// </summary>
    public IReadOnlySet<Guid>? MachineExtensions { get; init; }

    /// <summary>A WMI filter is linked (<c>gPCWQLFilter</c>): the GPO applies only where the query is true.</summary>
    public bool HasWmiFilter { get; init; }

    /// <summary>
    /// The security filtering (who may apply the GPO, from the container's DACL); null when the descriptor could not be
    /// read - the GPO is then taken to apply to every computer it is linked to.
    /// </summary>
    public GroupPolicySecurityFilter? SecurityFilter { get; init; }

    public bool ComputerSettingsDisabled => (Flags & ComputerSettingsDisabledFlag) != 0;

    /// <summary>The client runs the extension for this GPO (always when the GPO does not list its extensions).</summary>
    public bool UsesExtension(Guid extension) => MachineExtensions is null || MachineExtensions.Contains(extension);

    /// <summary>
    /// gPCFileSysPath is not <c>\\&lt;domain&gt;\SysVol\&lt;domain&gt;\Policies\{GUID}</c> (a redirected GPO path; the review
    /// still reads the session DC's SYSVOL by GUID and says so).
    /// </summary>
    public bool IsPathRedirected(string domainDnsName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDnsName);
        if (string.IsNullOrEmpty(FileSysPath))
        {
            return false;
        }

        var domain = domainDnsName.Trim().TrimEnd('.');
        var expected = @"\\" + domain + @"\SysVol\" + domain + @"\Policies\" + SysvolPaths.FolderName(Id);
        return !string.Equals(FileSysPath, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The CSE GUIDs of <c>gPCMachineExtensionNames</c>: <c>[{CSE}{tool}…][…]</c> → the first GUID of each bracket.</summary>
    public static IReadOnlySet<Guid>? ParseExtensionNames(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var result = new HashSet<Guid>();
        var position = 0;
        while (position < value.Length)
        {
            var open = value.IndexOf('[', position);
            if (open < 0)
            {
                break;
            }

            var close = value.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }

            var body = value[(open + 1)..close];
            position = close + 1;
            var end = body.IndexOf('}', StringComparison.Ordinal);
            if (body.StartsWith('{') && end > 0 && Guid.TryParse(body.AsSpan(1, end - 1), out var guid))
            {
                result.Add(guid);
            }
        }

        return result;
    }
}

/// <summary>What a container that GPOs can be linked to is ([MS-GPOL] scope of management).</summary>
public enum GroupPolicySomKind
{
    Site,
    Domain,
    OrganizationalUnit,
}

/// <summary>A site, the domain head or an OU with its links and block inheritance (<c>gPOptions</c> 1).</summary>
public sealed record GroupPolicySom(string Dn, GroupPolicySomKind Kind, IReadOnlyList<GroupPolicyLink> Links, bool BlocksInheritance);

/// <summary>What a computer is for the levels of local rights.</summary>
public enum ComputerRole
{
    Workstation,
    Server,
    DomainController,
}

/// <summary>A computer account the GPOs apply to: its parent container decides the OU links, its site the site links.</summary>
/// <param name="Dn">The computer's DN.</param>
/// <param name="Role">Workstation, server (operatingSystem names a server) or DC (server trust / RODC).</param>
/// <param name="SiteDn">The site of a DC (its server object in CN=Sites); null for other computers (the site follows the subnet).</param>
public sealed record GroupPolicyComputer(string Dn, ComputerRole Role, string? SiteDn)
{
    /// <summary>objectSid (the security filtering of a GPO may name the computer itself).</summary>
    public string? Sid { get; init; }

    /// <summary>primaryGroupID (515 Domain Computers, 516 Domain Controllers, 521 RODCs).</summary>
    public int? PrimaryGroupId { get; init; }

    /// <summary>memberOf: the groups the security filtering of a GPO may name (nested through the group graph).</summary>
    public IReadOnlyList<string> MemberOf { get; init; } = [];
}

/// <summary>
/// The group policy objects, their links and the computers of a domain, read from the session's DC (CLAUDE.md rule 4) for
/// one privileged access run and kept only in memory: the GPCs of <c>CN=Policies,CN=System</c> (one search), the domain
/// head, the OUs with <c>gPLink</c> or <c>gPOptions</c> (one search), the sites with links (one search of the
/// configuration NC; the DCs' server objects only when a site has links) and the computer accounts (one paged search).
/// The DACL of every GPC (security filtering) and the SID, primary group and memberOf of every computer come with those
/// searches.
/// </summary>
public sealed class GroupPolicyTopology
{
    /// <summary>Attributes of the GPC search (nTSecurityDescriptor with the SD flags control: DACL, for the security filtering).</summary>
    public static IReadOnlyList<string> PolicyAttributes { get; } =
    [
        Attr.Cn, Attr.DisplayName, GroupPolicyAttributes.FileSysPath, GroupPolicyAttributes.Flags,
        GroupPolicyAttributes.MachineExtensionNames, GroupPolicyAttributes.WqlFilter, Attr.NtSecurityDescriptor,
    ];

    /// <summary>Attributes of the computer search (role, and the token SIDs for the security filtering of the GPOs).</summary>
    public static IReadOnlyList<string> ComputerAttributes { get; } =
    [
        Attr.ObjectClass, Attr.UserAccountControl, Attr.OperatingSystem, Attr.ObjectSid, Attr.PrimaryGroupId, Attr.MemberOf,
    ];

    /// <summary>OUs that link GPOs or block inheritance.</summary>
    public static string OrganizationalUnitFilter { get; } = LdapFilter.And(
        ObjectKind.OrganizationalUnit.ToLdapFilter(),
        LdapFilter.Or(LdapFilter.Present(Attr.GpLink), LdapFilter.Present(GroupPolicyAttributes.GpOptions)));

    public GroupPolicyTopology(
        string domainDn,
        IEnumerable<GroupPolicyObjectInfo> policies,
        IEnumerable<GroupPolicySom> containers,
        IEnumerable<GroupPolicyComputer> computers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDn);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(computers);
        DomainDn = domainDn;
        Policies = policies.GroupBy(p => p.Id).Select(g => g.First()).ToDictionary(p => p.Id);
        Containers = [.. containers];
        Computers = [.. computers];
    }

    public string DomainDn { get; }

    public IReadOnlyDictionary<Guid, GroupPolicyObjectInfo> Policies { get; }

    /// <summary>The domain head, the OUs and the sites.</summary>
    public IReadOnlyList<GroupPolicySom> Containers { get; }

    public IReadOnlyList<GroupPolicyComputer> Computers { get; }

    /// <summary>Reads the topology (see the class remarks for the queries).</summary>
    public static async Task<GroupPolicyTopology> ReadAsync(IDirectorySession session, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var domainDn = session.Info.DefaultNamingContext;
        var domain = DistinguishedName.Parse(domainDn);

        var policies = new List<GroupPolicyObjectInfo>();
        try
        {
            await foreach (var gpc in session.SearchAsync(new DirectorySearch
            {
                BaseDn = domain.Child("CN", "System").Child("CN", "Policies").ToString(),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, "groupPolicyContainer"),
                Attributes = PolicyAttributes,
                ReadSecurityDescriptor = true,
                PageSize = pageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (ToPolicy(gpc) is { } policy)
                {
                    policies.Add(policy);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No CN=Policies container: no GPOs.
        }

        var containers = new List<GroupPolicySom>();
        if (await session.GetAsync(domainDn, [Attr.GpLink, GroupPolicyAttributes.GpOptions], cancellationToken).ConfigureAwait(false) is { } head)
        {
            containers.Add(ToSom(head, GroupPolicySomKind.Domain));
        }

        await foreach (var ou in session.SearchAsync(new DirectorySearch
        {
            BaseDn = domainDn,
            Scope = SearchScope.Subtree,
            Filter = OrganizationalUnitFilter,
            Attributes = [Attr.GpLink, GroupPolicyAttributes.GpOptions],
            PageSize = pageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            containers.Add(ToSom(ou, GroupPolicySomKind.OrganizationalUnit));
        }

        var sitesDn = DistinguishedName.Parse(session.Info.ConfigurationNamingContext).Child("CN", "Sites").ToString();
        try
        {
            await foreach (var site in session.SearchAsync(new DirectorySearch
            {
                BaseDn = sitesDn,
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "site"), LdapFilter.Present(Attr.GpLink)),
                Attributes = [Attr.GpLink, GroupPolicyAttributes.GpOptions],
                PageSize = pageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                containers.Add(ToSom(site, GroupPolicySomKind.Site));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No CN=Sites (not a real AD): no site links.
        }

        // The sites of the DCs, only when a site links a GPO: server objects name their computer account.
        var dcSites = new Dictionary<DistinguishedName, string>();
        if (containers.Any(c => c.Kind == GroupPolicySomKind.Site && c.Links.Count > 0))
        {
            await foreach (var server in session.SearchAsync(new DirectorySearch
            {
                BaseDn = sitesDn,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "server"), LdapFilter.Present(GroupPolicyAttributes.ServerReference)),
                Attributes = [GroupPolicyAttributes.ServerReference],
                PageSize = pageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (DistinguishedName.TryParse(server.GetString(GroupPolicyAttributes.ServerReference), out var computer)
                    && server.Dn.Parent?.Parent is { IsRoot: false } site)
                {
                    dcSites[computer] = site.ToString();
                }
            }
        }

        var computers = new List<GroupPolicyComputer>();
        await foreach (var computer in session.SearchAsync(new DirectorySearch
        {
            BaseDn = domainDn,
            Scope = SearchScope.Subtree,
            Filter = ObjectKind.Computer.ToLdapFilter(),
            Attributes = ComputerAttributes,
            PageSize = pageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            // Managed service accounts are machine accounts but no computers; disabled computers get no policy.
            if (computer.Kind != ObjectKind.Computer || computer.IsDisabled)
            {
                continue;
            }

            var role = RoleOf(computer);
            computers.Add(new GroupPolicyComputer(
                computer.DistinguishedName,
                role,
                role == ComputerRole.DomainController && dcSites.TryGetValue(computer.Dn, out var siteDn) ? siteDn : null)
            {
                Sid = computer.ObjectSid,
                PrimaryGroupId = computer.GetInt32(Attr.PrimaryGroupId),
                MemberOf = computer.GetStrings(Attr.MemberOf),
            });
        }

        return new GroupPolicyTopology(domainDn, policies, containers, computers);
    }

    /// <summary>DC (server trust account or RODC), server (operatingSystem contains "Server") or workstation.</summary>
    public static ComputerRole RoleOf(DirectoryObject computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var uac = (UserAccountControl)(computer.GetInt32(Attr.UserAccountControl) ?? 0);
        if (uac.HasFlag(UserAccountControl.ServerTrustAccount) || uac.HasFlag(UserAccountControl.PartialSecretsAccount))
        {
            return ComputerRole.DomainController;
        }

        return computer.GetString(Attr.OperatingSystem) is { } os && os.Contains("Server", StringComparison.OrdinalIgnoreCase)
            ? ComputerRole.Server
            : ComputerRole.Workstation;
    }

    private static GroupPolicyObjectInfo? ToPolicy(DirectoryObject gpc)
    {
        if (!GroupPolicyLinks.TryParseGpoId(gpc.Dn.IsRoot ? null : gpc.Dn.Name, out var id))
        {
            return null;
        }

        return new GroupPolicyObjectInfo
        {
            Id = id,
            Dn = gpc.DistinguishedName,
            DisplayName = gpc.GetString(Attr.DisplayName) is { Length: > 0 } name ? name : SysvolPaths.FolderName(id),
            FileSysPath = gpc.GetString(GroupPolicyAttributes.FileSysPath),
            Flags = gpc.GetInt32(GroupPolicyAttributes.Flags) ?? 0,
            MachineExtensions = GroupPolicyObjectInfo.ParseExtensionNames(gpc.GetString(GroupPolicyAttributes.MachineExtensionNames)),
            HasWmiFilter = gpc.GetString(GroupPolicyAttributes.WqlFilter) is { Length: > 0 } wql && wql.Trim().Length > 0,
            SecurityFilter = GroupPolicySecurityFilter.FromDescriptor(gpc.GetBytes(Attr.NtSecurityDescriptor)),
        };
    }

    private static GroupPolicySom ToSom(DirectoryObject obj, GroupPolicySomKind kind)
    {
        var options = obj.GetInt64(GroupPolicyAttributes.GpOptions) ?? 0;
        return new GroupPolicySom(
            obj.DistinguishedName,
            kind,
            GroupPolicyLinks.Parse(obj.GetString(Attr.GpLink)),
            (options & GroupPolicyLinks.BlockInheritanceOption) != 0);
    }

    /// <summary>For diagnostics: never directory values.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{Policies.Count} GPOs, {Containers.Count} containers, {Computers.Count} computers");
}
