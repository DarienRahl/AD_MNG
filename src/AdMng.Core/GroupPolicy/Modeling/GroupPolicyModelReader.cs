using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.GroupPolicy.Modeling;

/// <summary>What the modeller reads from the directory once per model: the domain, its GPOs and its linked containers.</summary>
/// <param name="DomainDn">The domain head.</param>
/// <param name="DomainSid">The domain's SID (null when unreadable).</param>
/// <param name="Containers">The domain head, the OUs and the sites with links.</param>
/// <param name="Gpos">The GPOs by GUID.</param>
public sealed record GroupPolicyModelDirectory(string DomainDn, string? DomainSid, IReadOnlyList<GroupPolicySom> Containers, IReadOnlyDictionary<Guid, ModelingGpo> Gpos)
{
    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Gpos.Count} GPOs, {Containers.Count} containers");
}

/// <summary>
/// The reads of a Group Policy model (K5), all from the session's DC (CLAUDE.md rule 4) and kept in memory only: the
/// GPCs of <c>CN=Policies,CN=System</c> with flags, version, WMI filter link and DACL (one search), the WMI filters'
/// names (one search), the linked containers (<see cref="GroupPolicyTopology.ReadContainersAsync"/>), the accounts
/// modelled with <c>tokenGroups</c> (one base read each) and the folders of the GPOs that may apply (SYSVOL of the same
/// DC, at most four at a time, within K2's limits).
/// </summary>
public static class GroupPolicyModelReader
{
    /// <summary>Attributes of a GPC for the model (nTSecurityDescriptor read with the SD flags control: DACL).</summary>
    public static IReadOnlyList<string> GpoAttributes { get; } =
    [
        Attr.Cn, Attr.DisplayName, GroupPolicyAttributes.Flags, GroupPolicyAttributes.VersionNumber, GroupPolicyAttributes.WqlFilter,
        Attr.NtSecurityDescriptor,
    ];

    /// <summary>Attributes of a modelled account.</summary>
    public static IReadOnlyList<string> AccountAttributes { get; } =
        [Attr.ObjectClass, Attr.ObjectSid, Attr.PrimaryGroupId, Attr.UserAccountControl, Attr.TokenGroups];

    private const int PageSize = 500;
    private const int SysvolParallelism = 4;

    /// <summary>The domain, its GPOs and its linked containers.</summary>
    public static async Task<GroupPolicyModelDirectory> ReadDirectoryAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var domainDn = session.Info.DefaultNamingContext;
        var domain = await session.GetAsync(domainDn, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        var wmiNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in await new GroupPolicyReader().ReadWmiFiltersAsync(session, cancellationToken).ConfigureAwait(false))
        {
            wmiNames.TryAdd(filter.Id, filter.Name);
        }

        var gpos = new Dictionary<Guid, ModelingGpo>();
        try
        {
            await foreach (var gpc in session.SearchAsync(new DirectorySearch
            {
                BaseDn = GroupPolicyReader.PoliciesDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, GroupPolicyAttributes.ContainerClass),
                Attributes = GpoAttributes,
                ReadSecurityDescriptor = true,
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (ToGpo(gpc, wmiNames) is { } gpo)
                {
                    gpos.TryAdd(gpo.Id, gpo);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No CN=Policies container: no GPOs.
        }

        var containers = await GroupPolicyTopology.ReadContainersAsync(session, PageSize, cancellationToken).ConfigureAwait(false);
        return new GroupPolicyModelDirectory(domainDn, domain?.ObjectSid, containers, gpos);
    }

    /// <summary>A computer or user account: its parent container and its token (<c>tokenGroups</c>, else its primary group).</summary>
    /// <exception cref="DirectoryException">The account does not exist (<see cref="DirectoryErrorKind.NoSuchObject"/>).</exception>
    public static async Task<ModelingTarget> ReadAccountAsync(IDirectorySession session, string accountDn, string? domainSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDn);
        var account = await session.GetAsync(accountDn, AccountAttributes, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The account does not exist.");
        var uac = (UserAccountControl)(account.GetInt32(Attr.UserAccountControl) ?? 0);
        var writableDc = uac.HasFlag(UserAccountControl.ServerTrustAccount);
        var isDc = writableDc || uac.HasFlag(UserAccountControl.PartialSecretsAccount);
        var token = WellKnown(writableDc);
        if (account.ObjectSid is { } sid)
        {
            token.Add(sid);
            domainSid ??= sid[..sid.LastIndexOf('-')];
        }

        // The primary group is in tokenGroups too; added here also when tokenGroups cannot be read.
        if (account.GetInt32(Attr.PrimaryGroupId) is { } rid && domainSid is not null)
        {
            token.Add(string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{rid}"));
        }

        var incomplete = true;
        if (account.TryGet(Attr.TokenGroups, out var groups) && groups.Count > 0)
        {
            incomplete = false;
            foreach (var group in groups.OfType<byte[]>().Where(b => b.Length >= 8))
            {
                token.Add(AdConvert.SidToString(group));
            }
        }

        return new ModelingTarget
        {
            ContainerDn = account.Dn.Parent?.ToString() ?? session.Info.DefaultNamingContext,
            AccountDn = account.DistinguishedName,
            Token = token,
            TokenIncomplete = incomplete,
            IsDomainController = isDc,
        };
    }

    /// <summary>A new computer or user in <paramref name="containerDn"/>: Domain Computers or Domain Users and the well-known SIDs.</summary>
    public static ModelingTarget ContainerTarget(string containerDn, ModelingPart part, string? domainSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDn);
        var token = WellKnown(writableDc: false);
        if (domainSid is not null)
        {
            var rid = part == ModelingPart.Computer ? WellKnownRids.DomainComputers : WellKnownRids.DomainUsers;
            token.Add(string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{(int)rid}"));
        }

        return new ModelingTarget { ContainerDn = containerDn, Token = token };
    }

    /// <summary>
    /// The folders of <paramref name="gpoIds"/> from SYSVOL of the session's DC; a folder that cannot be read (missing,
    /// access denied, too large) is <see cref="ModelingGpoContent.Unreadable"/>.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ModelingGpoContent>> ReadContentsAsync(
        ISysvolWriteConnection sysvol, IEnumerable<Guid> gpoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(gpoIds);
        var result = new System.Collections.Concurrent.ConcurrentDictionary<Guid, ModelingGpoContent>();
        await Parallel.ForEachAsync(gpoIds.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = SysvolParallelism, CancellationToken = cancellationToken }, async (id, token) =>
        {
            ModelingGpoContent content;
            try
            {
                content = FromFolder(await GroupPolicyReader.ReadFolderAsync(sysvol, id, token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SysvolWriteException or GpoTooLargeException)
            {
                content = new ModelingGpoContent { Unreadable = true };
            }

            result[id] = content;
        }).ConfigureAwait(false);
        return new Dictionary<Guid, ModelingGpoContent>(result);
    }

    /// <summary>What the model takes from a GPO's folder: the normalized settings and both <c>Registry.pol</c> documents.</summary>
    public static ModelingGpoContent FromFolder(GpoFolderContent folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new ModelingGpoContent
        {
            Settings = GroupPolicyReader.Settings(folder),
            MachineRegistry = Registry(folder, "Machine/Registry.pol"),
            UserRegistry = Registry(folder, "User/Registry.pol"),
        };
    }

    private static RegistryPolicyDocument? Registry(GpoFolderContent folder, string path)
    {
        var bytes = folder.Files.FirstOrDefault(f => f.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).Value;
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return RegistryPolicyDocument.Parse(bytes);
        }
        catch (GpoParseException)
        {
            return null; // the settings carry the file's unreadable marker
        }
    }

    private static HashSet<string> WellKnown(bool writableDc)
    {
        var token = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GroupPolicySecurityFilter.EveryoneSid, GroupPolicySecurityFilter.AuthenticatedUsersSid, GroupPolicySecurityFilter.ThisOrganizationSid,
        };
        if (writableDc)
        {
            token.Add(GroupPolicySecurityFilter.EnterpriseDomainControllersSid);
        }

        return token;
    }

    private static ModelingGpo? ToGpo(DirectoryObject gpc, IReadOnlyDictionary<string, string> wmiNames)
    {
        if (!GroupPolicyLinks.TryParseGpoId(gpc.Dn.IsRoot ? null : gpc.Dn.Name, out var id))
        {
            return null;
        }

        var wql = gpc.GetString(GroupPolicyAttributes.WqlFilter);
        var hasWmi = wql is { Length: > 0 } && wql.Trim().Length > 0;
        var wmiId = hasWmi ? WmiFilterLinks.ParseId(wql) : null;
        return new ModelingGpo
        {
            Id = id,
            DisplayName = gpc.GetString(Attr.DisplayName) is { Length: > 0 } name ? name : SysvolPaths.FolderName(id),
            Flags = gpc.GetInt32(GroupPolicyAttributes.Flags) ?? 0,
            Version = gpc.GetInt32(GroupPolicyAttributes.VersionNumber) is { } version ? GpoVersion.FromInt32(version) : null,
            HasWmiFilter = hasWmi,
            WmiFilterName = wmiId is not null && wmiNames.TryGetValue(wmiId, out var wmiName) ? wmiName : null,
            SecurityFilter = GroupPolicySecurityFilter.FromDescriptor(gpc.GetBytes(Attr.NtSecurityDescriptor)),
        };
    }
}
