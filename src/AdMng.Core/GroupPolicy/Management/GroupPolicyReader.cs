using System.Collections.Concurrent;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// Reads the GPOs, their links, the WMI filters, the sites and the GPO folders - everything on the session's DC (CLAUDE.md
/// rule 4; design note docs/dev/k-computers-and-gpo.md 5.1). Stateless; the results live in the memory of the window that
/// asked (rule 1). Every filter goes through <see cref="LdapFilter"/> and every DN through <see cref="DistinguishedName"/>
/// (rule 3).
/// </summary>
public sealed class GroupPolicyReader
{
    /// <summary>Attributes of a group policy container (nTSecurityDescriptor with the SD flags control: owner + DACL).</summary>
    public static IReadOnlyList<string> PolicyAttributes { get; } =
    [
        Attr.Cn, Attr.DisplayName, GroupPolicyAttributes.VersionNumber, GroupPolicyAttributes.Flags, GroupPolicyAttributes.FileSysPath,
        GroupPolicyAttributes.FunctionalityVersion, GroupPolicyAttributes.MachineExtensionNames, GroupPolicyAttributes.UserExtensionNames,
        GroupPolicyAttributes.WqlFilter, Attr.WhenCreated, Attr.WhenChanged, Attr.NtSecurityDescriptor,
    ];

    /// <summary>Attributes of a WMI filter.</summary>
    public static IReadOnlyList<string> WmiFilterAttributes { get; } =
        ["msWMI-ID", "msWMI-Name", "msWMI-Parm1", "msWMI-Parm2", "msWMI-Author", "msWMI-CreationDate", "msWMI-ChangeDate"];

    private const int PageSize = 500;
    private const int SysvolParallelism = 4;

    /// <summary><c>CN=Policies,CN=System,&lt;domain&gt;</c>.</summary>
    public static string PoliciesDn(IDirectorySession session) =>
        DistinguishedName.Parse(session.Info.DefaultNamingContext).Child("CN", "System").Child("CN", "Policies").ToString();

    /// <summary><c>CN=SOM,CN=WMIPolicy,CN=System,&lt;domain&gt;</c>.</summary>
    public static string WmiFiltersDn(IDirectorySession session) =>
        DistinguishedName.Parse(session.Info.DefaultNamingContext).Child("CN", "System").Child("CN", "WMIPolicy").Child("CN", "SOM").ToString();

    /// <summary><c>CN=Sites,CN=Configuration,…</c>.</summary>
    public static string SitesDn(IDirectorySession session) =>
        DistinguishedName.Parse(session.Info.ConfigurationNamingContext).Child("CN", "Sites").ToString();

    /// <summary>Every GPO of the domain, sorted by name (SYSVOL versions not read).</summary>
    public async Task<IReadOnlyList<GpoInfo>> ReadPoliciesAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new List<GpoInfo>();
        try
        {
            await foreach (var gpc in session.SearchAsync(new DirectorySearch
            {
                BaseDn = PoliciesDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, GroupPolicyAttributes.ContainerClass),
                Attributes = PolicyAttributes,
                ReadSecurityDescriptor = true,
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (ToPolicy(gpc) is { } policy)
                {
                    result.Add(policy);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No CN=Policies container: no GPOs.
        }

        return [.. result.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>One GPO by its GUID, null when it does not exist.</summary>
    public async Task<GpoInfo?> ReadPolicyAsync(IDirectorySession session, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            await foreach (var gpc in session.SearchAsync(new DirectorySearch
            {
                BaseDn = GpLinkValue.GpoDn(id, session.Info.DefaultNamingContext),
                Scope = SearchScope.Base,
                Attributes = PolicyAttributes,
                ReadSecurityDescriptor = true,
            }, cancellationToken).ConfigureAwait(false))
            {
                return ToPolicy(gpc);
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            return null;
        }

        return null;
    }

    /// <summary>The <c>GPT.INI</c> versions of the GPOs (4 reads in parallel) on the SYSVOL connection of the session's DC.</summary>
    public async Task<IReadOnlyList<GpoInfo>> WithSysvolVersionsAsync(ISysvolConnection sysvol, IReadOnlyList<GpoInfo> policies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(policies);
        var results = new GpoInfo[policies.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, policies.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = SysvolParallelism,
            CancellationToken = cancellationToken,
        }, async (index, token) =>
        {
            results[index] = await WithSysvolVersionAsync(sysvol, policies[index], token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>The GPO with the <c>GPT.INI</c> version of its folder.</summary>
    public async Task<GpoInfo> WithSysvolVersionAsync(ISysvolConnection sysvol, GpoInfo policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(policy);
        var file = await sysvol.ReadGpoFileAsync(policy.Id, GptIni.RelativePath, 64 * 1024, cancellationToken).ConfigureAwait(false);
        if (file.Content is null)
        {
            return policy with { SysvolVersion = null, SysvolStatus = file.Status };
        }

        try
        {
            return GptIni.ReadVersion(file.Content) is { } version
                ? policy with { SysvolVersion = GpoVersion.FromInt32(version), SysvolStatus = null }
                : policy with { SysvolVersion = GpoVersion.Zero, SysvolStatus = null };
        }
        catch (GpoParseException)
        {
            return policy with { SysvolVersion = null, SysvolStatus = SysvolFileStatus.Failed };
        }
    }

    /// <summary>
    /// The domain head, every OU with <c>gPLink</c> or <c>gPOptions</c> and every site with <c>gPLink</c> - the containers
    /// that link GPOs or block inheritance.
    /// </summary>
    public async Task<IReadOnlyList<GroupPolicyContainerInfo>> ReadLinkingContainersAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var domainDn = session.Info.DefaultNamingContext;
        var result = new List<GroupPolicyContainerInfo>();
        if (await ReadContainerAsync(session, domainDn, cancellationToken).ConfigureAwait(false) is { } head)
        {
            result.Add(head);
        }

        await foreach (var ou in session.SearchAsync(new DirectorySearch
        {
            BaseDn = domainDn,
            Scope = SearchScope.Subtree,
            Filter = GroupPolicyTopology.OrganizationalUnitFilter,
            Attributes = [Attr.Name, Attr.ObjectClass, Attr.GpLink, GroupPolicyAttributes.GpOptions],
            PageSize = PageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            result.Add(ToContainer(ou, GroupPolicySomKind.OrganizationalUnit, domainDn, session.Info.DomainDnsName));
        }

        try
        {
            await foreach (var site in session.SearchAsync(new DirectorySearch
            {
                BaseDn = SitesDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "site"), LdapFilter.Present(Attr.GpLink)),
                Attributes = [Attr.Name, Attr.ObjectClass, Attr.GpLink, GroupPolicyAttributes.GpOptions],
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                result.Add(ToContainer(site, GroupPolicySomKind.Site, domainDn, session.Info.DomainDnsName));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No CN=Sites: no site links.
        }

        return result;
    }

    /// <summary>The links of every GPO of the domain, from <paramref name="containers"/>.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<GpoLinkInfo>> LinksByGpo(IEnumerable<GroupPolicyContainerInfo> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var result = new Dictionary<Guid, List<GpoLinkInfo>>();
        foreach (var container in containers)
        {
            foreach (var link in container.Links.Links)
            {
                if (!result.TryGetValue(link.GpoId, out var list))
                {
                    result[link.GpoId] = list = [];
                }

                list.Add(new GpoLinkInfo(container.Dn, container.Kind, container.Name, link.GpoId, link.Order, link.Enforced, link.Enabled));
            }
        }

        return result.ToDictionary(r => r.Key, r => (IReadOnlyList<GpoLinkInfo>)[.. r.Value.OrderBy(l => l.Kind).ThenBy(l => l.ContainerDn, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// The links of one GPO: a search of the domain NC for <c>gPLink=*{GUID}*</c> on the domain head and OUs, and of the
    /// sites (the GUID through <see cref="LdapFilter.Contains"/>).
    /// </summary>
    public async Task<IReadOnlyList<GpoLinkInfo>> ReadLinksOfAsync(IDirectorySession session, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var guid = SysvolPaths.FolderName(gpoId);
        var domainDn = session.Info.DefaultNamingContext;
        var containers = new List<GroupPolicyContainerInfo>();
        await foreach (var obj in session.SearchAsync(new DirectorySearch
        {
            BaseDn = domainDn,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And(
                LdapFilter.Or(LdapFilter.Equal(Attr.ObjectClass, "domainDNS"), LdapFilter.Equal(Attr.ObjectClass, "organizationalUnit")),
                LdapFilter.Contains(Attr.GpLink, guid)),
            Attributes = [Attr.Name, Attr.ObjectClass, Attr.GpLink, GroupPolicyAttributes.GpOptions],
            PageSize = PageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            var kind = DistinguishedName.AreEqual(obj.DistinguishedName, domainDn) ? GroupPolicySomKind.Domain : GroupPolicySomKind.OrganizationalUnit;
            containers.Add(ToContainer(obj, kind, domainDn, session.Info.DomainDnsName));
        }

        try
        {
            await foreach (var site in session.SearchAsync(new DirectorySearch
            {
                BaseDn = SitesDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "site"), LdapFilter.Contains(Attr.GpLink, guid)),
                Attributes = [Attr.Name, Attr.ObjectClass, Attr.GpLink, GroupPolicyAttributes.GpOptions],
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                containers.Add(ToContainer(site, GroupPolicySomKind.Site, domainDn, session.Info.DomainDnsName));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No sites.
        }

        return LinksByGpo(containers).TryGetValue(gpoId, out var links) ? links : [];
    }

    /// <summary>A domain head, OU or site with its <c>gPLink</c> and <c>gPOptions</c>; null when it does not exist.</summary>
    public async Task<GroupPolicyContainerInfo?> ReadContainerAsync(IDirectorySession session, string distinguishedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        var obj = await session.GetAsync(distinguishedName, [Attr.Name, Attr.ObjectClass, Attr.GpLink, GroupPolicyAttributes.GpOptions], cancellationToken)
            .ConfigureAwait(false);
        if (obj is null)
        {
            return null;
        }

        var classes = obj.GetStrings(Attr.ObjectClass);
        var kind = DistinguishedName.AreEqual(obj.DistinguishedName, session.Info.DefaultNamingContext) || classes.Contains("domainDNS", StringComparer.OrdinalIgnoreCase)
            ? GroupPolicySomKind.Domain
            : classes.Contains("site", StringComparer.OrdinalIgnoreCase) ? GroupPolicySomKind.Site : GroupPolicySomKind.OrganizationalUnit;
        return ToContainer(obj, kind, session.Info.DefaultNamingContext, session.Info.DomainDnsName);
    }

    /// <summary>
    /// The GPOs that apply to objects in a domain head or OU, in application order (U4c's
    /// <see cref="GroupPolicyInheritance"/>, as GPMC's Group Policy Inheritance tab; site links are not included for a
    /// domain or OU). For a site: its own enabled links.
    /// </summary>
    public static IReadOnlyList<AppliedGroupPolicy> Inheritance(string domainDn, IReadOnlyList<GroupPolicyContainerInfo> containers, GroupPolicyContainerInfo target)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == GroupPolicySomKind.Site)
        {
            var som = target.ToSom();
            return [.. som.Links.Where(l => !l.Disabled && l.GpoId is not null).Select(l => new AppliedGroupPolicy(l.GpoId!.Value, l, som))];
        }

        var all = containers.Where(c => c.Kind != GroupPolicySomKind.Site && !DistinguishedName.AreEqual(c.Dn, target.Dn)).Select(c => c.ToSom()).Append(target.ToSom());
        return new GroupPolicyInheritance(domainDn, all).Resolve(target.Dn, siteDn: null);
    }

    /// <summary>The WMI filters of the domain, sorted by name.</summary>
    public async Task<IReadOnlyList<WmiFilterInfo>> ReadWmiFiltersAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new List<WmiFilterInfo>();
        try
        {
            await foreach (var som in session.SearchAsync(new DirectorySearch
            {
                BaseDn = WmiFiltersDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, GroupPolicyAttributes.WmiFilterClass),
                Attributes = WmiFilterAttributes,
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                result.Add(ToWmiFilter(som));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No WMI filter container yet.
        }

        return [.. result.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The sites of the forest (DN and name), sorted by name.</summary>
    public async Task<IReadOnlyList<GroupPolicyContainerInfo>> ReadSitesAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new List<GroupPolicyContainerInfo>();
        try
        {
            await foreach (var site in session.SearchAsync(new DirectorySearch
            {
                BaseDn = SitesDn(session),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, "site"),
                Attributes = [Attr.Name, Attr.ObjectClass, Attr.GpLink, GroupPolicyAttributes.GpOptions],
                PageSize = PageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                result.Add(ToContainer(site, GroupPolicySomKind.Site, session.Info.DefaultNamingContext, session.Info.DomainDnsName));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // Not a real forest.
        }

        return [.. result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// The DNS name of the PDC emulator: <c>fSMORoleOwner</c> of the domain head is its NTDS Settings object, whose parent
    /// server object carries <c>dNSHostName</c>. Null when it cannot be read.
    /// </summary>
    public async Task<string?> ReadPdcEmulatorAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            var head = await session.GetAsync(session.Info.DefaultNamingContext, ["fSMORoleOwner"], cancellationToken).ConfigureAwait(false);
            if (!DistinguishedName.TryParse(head?.GetString("fSMORoleOwner"), out var ntds) || ntds.Parent is not { IsRoot: false } server)
            {
                return null;
            }

            var host = await session.GetAsync(server.ToString(), [Attr.DnsHostName], cancellationToken).ConfigureAwait(false);
            return host?.GetString(Attr.DnsHostName);
        }
        catch (DirectoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every file of a GPO folder copied into memory (copy, backup, compare), within the limits of
    /// <see cref="GpoTransaction"/>; leftovers of interrupted writes are listed but not read.
    /// </summary>
    /// <exception cref="GpoTooLargeException">The folder exceeds a limit.</exception>
    /// <exception cref="SysvolWriteException">The folder cannot be listed or a file cannot be read.</exception>
    public static async Task<GpoFolderContent> ReadFolderAsync(ISysvolWriteConnection sysvol, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        var entries = await sysvol.ListGpoFolderAsync(gpoId, SysvolListOptions.Default, cancellationToken).ConfigureAwait(false);
        if (entries.Count > GpoTransaction.MaxEntriesPerGpo)
        {
            throw new GpoTooLargeException();
        }

        var leftovers = entries.Where(e => !e.IsFolder && GpoTransaction.IsTempName(e.RelativePath)).Select(e => e.RelativePath).ToList();
        var files = entries.Where(e => !e.IsFolder && !GpoTransaction.IsTempName(e.RelativePath)).ToList();
        if (files.Any(f => f.Length > GpoTransaction.MaxFileBytes) || files.Sum(f => f.Length) > GpoTransaction.MaxPlanBytes)
        {
            throw new GpoTooLargeException();
        }

        var contents = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = SysvolParallelism, CancellationToken = cancellationToken }, async (file, token) =>
        {
            var read = await sysvol.ReadGpoFileAsync(gpoId, file.RelativePath, GpoTransaction.MaxFileBytes, token).ConfigureAwait(false);
            contents[file.RelativePath] = read.Status switch
            {
                SysvolFileStatus.Read => read.Content!,
                SysvolFileStatus.TooLarge => throw new GpoTooLargeException(),
                SysvolFileStatus.AccessDenied => throw new SysvolWriteException(SysvolWriteError.AccessDenied, "A file of the GPO cannot be read."),
                SysvolFileStatus.NotFound => throw new SysvolWriteException(SysvolWriteError.NotFound, "A file of the GPO disappeared while it was read."),
                _ => throw new SysvolWriteException(SysvolWriteError.Failed, "A file of the GPO cannot be read."),
            };
        }).ConfigureAwait(false);
        if (contents.Values.Sum(c => (long)c.Length) > GpoTransaction.MaxPlanBytes)
        {
            throw new GpoTooLargeException();
        }

        return new GpoFolderContent(entries, new Dictionary<string, byte[]>(contents, StringComparer.OrdinalIgnoreCase)) { Leftovers = leftovers };
    }

    /// <summary>
    /// The normalized settings of a GPO's files (the Watchdog's keys: "Computer/Security/…", "Computer/Registry/…"), sorted
    /// by key; a file that cannot be parsed contributes a <c>File/&lt;path&gt;</c> marker (<see cref="GpoSettingKind.Unreadable"/>).
    /// </summary>
    public static IReadOnlyList<GpoSetting> Settings(GpoFolderContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var settings = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in content.Files.OrderBy(f => GpoFileClassifier.Classify(f.Key).Priority).ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var file = GpoFileClassifier.Classify(path);
            if (!file.IsContent || file.Role == GpoFileRole.GptIni)
            {
                continue;
            }

            var sink = new GpoSettingSink(path);
            try
            {
                GpoContentParser.Parse(file, bytes, sink);
            }
            catch (GpoParseException)
            {
                settings["File/" + path] = new GpoSetting { Key = "File/" + path, Kind = GpoSettingKind.Unreadable, Category = file.Category, Source = path };
                continue;
            }

            foreach (var setting in sink.Settings)
            {
                settings[setting.Key] = setting;
            }
        }

        return [.. settings.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A WMI filter object as the model.</summary>
    public static WmiFilterInfo ToWmiFilter(DirectoryObject som)
    {
        ArgumentNullException.ThrowIfNull(som);
        var raw = som.GetString("msWMI-Parm2");
        return new WmiFilterInfo
        {
            Dn = som.DistinguishedName,
            Id = som.GetString("msWMI-ID") ?? som.Dn.Name,
            Name = som.GetString("msWMI-Name") ?? som.Dn.Name,
            Description = som.GetString("msWMI-Parm1"),
            RawQueries = raw,
            Queries = WmiFilterQueries.Parse(raw) ?? [],
            Author = som.GetString("msWMI-Author"),
            Created = WmiFilterQueries.ParseDate(som.GetString("msWMI-CreationDate")),
            Changed = WmiFilterQueries.ParseDate(som.GetString("msWMI-ChangeDate")),
        };
    }

    /// <summary>A group policy container as the model; null when its cn is not a GUID.</summary>
    public static GpoInfo? ToPolicy(DirectoryObject gpc)
    {
        ArgumentNullException.ThrowIfNull(gpc);
        if (!GroupPolicyLinks.TryParseGpoId(gpc.Dn.IsRoot ? null : gpc.Dn.Name, out var id))
        {
            return null;
        }

        return new GpoInfo
        {
            Id = id,
            Dn = gpc.DistinguishedName,
            DisplayName = gpc.GetString(Attr.DisplayName) is { Length: > 0 } name ? name : SysvolPaths.FolderName(id),
            DirectoryVersion = gpc.GetInt32(GroupPolicyAttributes.VersionNumber) is { } version ? GpoVersion.FromInt32(version) : null,
            Flags = gpc.GetInt32(GroupPolicyAttributes.Flags) ?? 0,
            FileSysPath = gpc.GetString(GroupPolicyAttributes.FileSysPath),
            FunctionalityVersion = gpc.GetInt32(GroupPolicyAttributes.FunctionalityVersion),
            MachineExtensionNames = gpc.GetString(GroupPolicyAttributes.MachineExtensionNames),
            UserExtensionNames = gpc.GetString(GroupPolicyAttributes.UserExtensionNames),
            WmiFilterLink = gpc.GetString(GroupPolicyAttributes.WqlFilter),
            SecurityDescriptor = gpc.GetBytes(Attr.NtSecurityDescriptor),
            Created = gpc.GetGeneralizedTime(Attr.WhenCreated),
            Changed = gpc.GetGeneralizedTime(Attr.WhenChanged),
        };
    }

    private static GroupPolicyContainerInfo ToContainer(DirectoryObject obj, GroupPolicySomKind kind, string domainDn, string domainDnsName)
    {
        var raw = obj.GetString(Attr.GpLink);
        var name = kind == GroupPolicySomKind.Domain ? domainDnsName : obj.Name;
        return new GroupPolicyContainerInfo(obj.DistinguishedName, kind, name, GpLinkValue.Parse(raw, domainDn), raw, obj.GetString(GroupPolicyAttributes.GpOptions));
    }
}
