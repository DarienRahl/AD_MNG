using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Presentation.Connection;

/// <summary>A domain controller of the session's domain as the configuration partition records it.</summary>
/// <param name="HostName">dNSHostName of the server object.</param>
/// <param name="Site">Name of the site the server object is in.</param>
/// <param name="IsGlobalCatalog">NTDS Settings <c>options</c> has NTDSDSA_OPT_IS_GC.</param>
/// <param name="IsReadOnly">RODC (NTDS Settings of category NTDS-DSA-RO).</param>
/// <param name="IsPdcEmulator">The domain head's fSMORoleOwner is this DC's NTDS Settings object.</param>
public sealed record DirectoryDomainController(string HostName, string? Site, bool IsGlobalCatalog, bool IsReadOnly, bool IsPdcEmulator);

/// <summary>One domain controller offered by "Change domain controller" (directory facts merged with the DC locator).</summary>
/// <param name="HostName">DNS host name.</param>
/// <param name="Site">Site name, null when unknown.</param>
/// <param name="IsInClientSite">The DC is in the site of this computer (DC locator).</param>
/// <param name="IsGlobalCatalog">Global catalog server.</param>
/// <param name="IsReadOnly">Read-only DC (writes are refused).</param>
/// <param name="IsPdcEmulator">Holds the PDC emulator role.</param>
/// <param name="IsCurrent">The DC the current session is pinned to.</param>
public sealed record DomainControllerEntry(
    string HostName,
    string? Site,
    bool IsInClientSite,
    bool IsGlobalCatalog,
    bool IsReadOnly,
    bool IsPdcEmulator,
    bool IsCurrent);

/// <summary>The domain controllers of the domain, ordered for the dialog, and the site of this computer.</summary>
/// <param name="Entries">Client site first, writable before read-only, then in DC locator order and by name.</param>
/// <param name="ClientSite">Site of this computer (from the DC locator), null when unknown.</param>
public sealed record DomainControllerList(IReadOnlyList<DomainControllerEntry> Entries, string? ClientSite)
{
    /// <summary>The entry of the current DC (always listed).</summary>
    public DomainControllerEntry? Current => Entries.FirstOrDefault(e => e.IsCurrent);

    /// <summary>
    /// "Any writable domain controller in my site": the current DC when it is one (nothing to change), else the first
    /// writable DC of the client's site; null when the client's site is unknown or has no writable DC.
    /// </summary>
    public DomainControllerEntry? AnyWritableInClientSite =>
        ClientSite is null
            ? null
            : Entries.FirstOrDefault(e => e is { IsCurrent: true, IsInClientSite: true, IsReadOnly: false })
                ?? Entries.FirstOrDefault(e => e is { IsInClientSite: true, IsReadOnly: false });

    /// <summary>The entry for a typed name (case-insensitive, trailing dot ignored, a single label matches the host's first label).</summary>
    public DomainControllerEntry? Find(string? hostName) =>
        DomainControllerCatalog.TryNormalizeHostName(hostName, out var host)
            ? Entries.FirstOrDefault(e => DomainControllerCatalog.IsSameHost(e.HostName, host))
            : null;
}

/// <summary>
/// Reads the domain controllers of the current session's domain from the configuration partition and merges them with
/// the DC locator's list (<see cref="IDirectoryConnector.FindDomainControllersAsync"/>). Metadata only, in memory.
/// </summary>
public static class DomainControllerCatalog
{
    private const string ServerClass = "server";
    private const string NtdsDsaClass = "nTDSDSA";
    private const string OptionsAttribute = "options";
    private const string HasDomainNcsAttribute = "msDS-HasDomainNCs";
    private const string HasMasterNcsAttribute = "hasMasterNCs";
    private const string FsmoRoleOwnerAttribute = "fSMORoleOwner";
    private const string ReadOnlyNtdsDsaCategory = "NTDS-DSA-RO";
    private const int IsGlobalCatalogOption = 1;

    /// <summary>
    /// The DCs of the session's domain: two searches below <c>CN=Sites</c> of the configuration partition (server objects
    /// with dNSHostName; their NTDS Settings with options, objectCategory and the hosted domain) and one read of the
    /// domain head's fSMORoleOwner (PDC emulator). Empty when the configuration has no sites container.
    /// </summary>
    /// <exception cref="DirectoryException">The configuration partition could not be read.</exception>
    public static async Task<IReadOnlyList<DirectoryDomainController>> ReadAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var info = session.Info;
        var sites = DistinguishedName.Parse(info.ConfigurationNamingContext).Child("CN", "Sites").ToString();
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await foreach (var server in session.SearchAsync(new DirectorySearch
            {
                BaseDn = sites,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.Equal(Attr.ObjectClass, ServerClass),
                Attributes = [Attr.DnsHostName],
                PageSize = 1000,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (server.GetString(Attr.DnsHostName) is { Length: > 0 } host)
                {
                    hosts[Key(server.DistinguishedName)] = host;
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            return [];
        }

        var settings = new List<(DistinguishedName Dn, string Host, bool Gc, bool Rodc)>();
        await foreach (var ntds in session.SearchAsync(new DirectorySearch
        {
            BaseDn = sites,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.Equal(Attr.ObjectClass, NtdsDsaClass),
            Attributes = [OptionsAttribute, Attr.ObjectCategory, HasDomainNcsAttribute, HasMasterNcsAttribute],
            PageSize = 1000,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (!HostsDomain(ntds, info.DefaultNamingContext)
                || !DistinguishedName.TryParse(ntds.DistinguishedName, out var dn)
                || dn.Parent is not { } server
                || !hosts.TryGetValue(Key(server.ToString()), out var host))
            {
                continue;
            }

            var options = ntds.GetInt32(OptionsAttribute) ?? 0;
            settings.Add((dn, host, (options & IsGlobalCatalogOption) != 0, IsReadOnlyCategory(ntds.GetString(Attr.ObjectCategory))));
        }

        var head = await session.GetAsync(info.DefaultNamingContext, [FsmoRoleOwnerAttribute], cancellationToken).ConfigureAwait(false);
        var pdc = head?.GetString(FsmoRoleOwnerAttribute);
        return
        [
            .. settings.Select(s => new DirectoryDomainController(
                s.Host,
                SiteOf(s.Dn),
                s.Gc,
                s.Rodc,
                pdc is not null && DistinguishedName.AreEqual(pdc, s.Dn.ToString()))),
        ];
    }

    /// <summary>
    /// Merges the directory facts (authoritative for site, GC, RODC, PDC) with the DC locator's list (client site; DCs
    /// that only DNS knows). The locator's fallback entry – the domain name itself – is not a DC and is skipped. The
    /// current DC is always listed.
    /// </summary>
    public static DomainControllerList Merge(
        IReadOnlyList<DirectoryDomainController> directory,
        IReadOnlyList<DomainControllerInfo> located,
        SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(located);
        ArgumentNullException.ThrowIfNull(session);
        var clientSite = located.FirstOrDefault(d => d.IsInClientSite && !string.IsNullOrWhiteSpace(d.Site))?.Site;
        var rows = new List<Row>();
        foreach (var dc in directory)
        {
            if (FindRow(rows, dc.HostName) is null)
            {
                rows.Add(new Row(dc.HostName, dc.Site, false, dc.IsGlobalCatalog, dc.IsReadOnly, dc.IsPdcEmulator, int.MaxValue));
            }
        }

        for (var rank = 0; rank < located.Count; rank++)
        {
            var dc = located[rank];
            if (!TryNormalizeHostName(dc.HostName, out var host) || IsSameHost(host, session.DomainDnsName))
            {
                continue;
            }

            if (FindRow(rows, host) is { } row)
            {
                row.InClientSite |= dc.IsInClientSite;
                row.Rank = Math.Min(row.Rank, rank);
                row.Site ??= dc.Site;
            }
            else
            {
                rows.Add(new Row(host, dc.Site, dc.IsInClientSite, dc.IsGlobalCatalog, dc.IsReadOnly, false, rank));
            }
        }

        if (FindRow(rows, session.Server) is null)
        {
            rows.Add(new Row(session.Server, null, false, false, false, false, int.MaxValue));
        }

        var entries = rows
            .Select(r => new DomainControllerEntry(
                r.Host,
                r.Site,
                r.InClientSite || (clientSite is not null && string.Equals(r.Site, clientSite, StringComparison.OrdinalIgnoreCase)),
                r.Gc,
                r.Rodc,
                r.Pdc,
                IsSameHost(r.Host, session.Server)))
            .Select((e, index) => (Entry: e, Rank: rows[index].Rank))
            .OrderByDescending(x => x.Entry.IsInClientSite)
            .ThenBy(x => x.Entry.IsReadOnly)
            .ThenBy(x => x.Rank)
            .ThenBy(x => x.Entry.HostName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .ToList();
        return new DomainControllerList(entries, clientSite);
    }

    /// <summary>
    /// Validates a typed DC name: a DNS host name, NetBIOS-style single label or IP address – no port, URL scheme, path,
    /// spaces or account syntax. Trims white space and a trailing dot.
    /// </summary>
    public static bool TryNormalizeHostName(string? text, out string hostName)
    {
        hostName = string.Empty;
        var trimmed = text?.Trim().TrimEnd('.') ?? string.Empty;
        if (trimmed.Length == 0 || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
        {
            return false;
        }

        hostName = trimmed;
        return true;
    }

    /// <summary>
    /// True when both names denote the same host: equal ignoring case and a trailing dot, or – when one of them is a
    /// single label (NetBIOS style, e.g. <c>dc01</c>) – equal first labels. IP addresses compare as a whole.
    /// </summary>
    public static bool IsSameHost(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        var a = first.Trim().TrimEnd('.');
        var b = second.Trim().TrimEnd('.');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.CheckHostName(a) != UriHostNameType.Dns || Uri.CheckHostName(b) != UriHostNameType.Dns
            || (a.Contains('.', StringComparison.Ordinal) && b.Contains('.', StringComparison.Ordinal)))
        {
            return false;
        }

        return string.Equals(a.Split('.')[0], b.Split('.')[0], StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostsDomain(DirectoryObject ntds, string domainNc)
    {
        var domains = ntds.GetStrings(HasDomainNcsAttribute);
        if (domains.Count == 0)
        {
            // DCs that predate msDS-HasDomainNCs: the domain is one of the writable naming contexts.
            domains = ntds.GetStrings(HasMasterNcsAttribute);
        }

        return domains.Any(nc => DistinguishedName.AreEqual(nc, domainNc));
    }

    private static bool IsReadOnlyCategory(string? objectCategory) =>
        objectCategory is not null && DistinguishedName.TryParse(objectCategory, out var dn) && !dn.IsRoot
        && string.Equals(dn.Name, ReadOnlyNtdsDsaCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>CN=NTDS Settings,CN=&lt;server&gt;,CN=Servers,CN=&lt;site&gt;,CN=Sites,… → the site name.</summary>
    private static string? SiteOf(DistinguishedName ntdsSettings) =>
        ntdsSettings.Parent?.Parent?.Parent is { IsRoot: false } site ? site.Name : null;

    private static string Key(string dn) =>
        (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpper(CultureInfo.InvariantCulture);

    private static Row? FindRow(List<Row> rows, string host) => rows.FirstOrDefault(r => IsSameHost(r.Host, host));

    private sealed class Row(string host, string? site, bool inClientSite, bool gc, bool rodc, bool pdc, int rank)
    {
        public string Host { get; } = host;

        public string? Site { get; set; } = site;

        public bool InClientSite { get; set; } = inClientSite;

        public bool Gc { get; } = gc;

        public bool Rodc { get; } = rodc;

        public bool Pdc { get; } = pdc;

        public int Rank { get; set; } = rank;
    }
}
