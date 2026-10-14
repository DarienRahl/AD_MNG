using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The domain controllers of the forest (section 10.11), refreshed hourly, on a resolution miss and by the
/// configuration partition watcher. Two views: <see cref="All"/> (every nTDSDSA under <c>CN=Sites,{config}</c> –
/// invocationId → host resolution of replication metadata) and <see cref="AttributionSources"/> (writable DCs of the
/// watched domain: msDS-HasDomainNCs contains the domain NC, RODCs excluded). Thread-safe; used by the attribution
/// engine (sources, EventSourceGuard) and the originating-DC resolution.
/// </summary>
public sealed class DomainControllerDirectory
{
    /// <summary>Age after which <see cref="EnsureFreshAsync"/> refreshes.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private IReadOnlyList<KnownDomainController> _all = [];
    private string? _pdcHostName;
    private DateTimeOffset? _refreshedAt;

    public DomainControllerDirectory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Every DC of the forest from the last refresh.</summary>
    public IReadOnlyList<KnownDomainController> All
    {
        get
        {
            lock (_gate)
            {
                return _all;
            }
        }
    }

    /// <summary>Writable DCs of the watched domain (attribution sources).</summary>
    public IReadOnlyList<KnownDomainController> AttributionSources => All.Where(d => d.IsThisDomain && !d.IsReadOnly && d.HostName is not null).ToList();

    /// <summary>Host name of the PDC emulator (fSMORoleOwner of the domain head), when known.</summary>
    public string? PdcHostName
    {
        get
        {
            lock (_gate)
            {
                return _pdcHostName;
            }
        }
    }

    /// <summary>Time of the last successful refresh.</summary>
    public DateTimeOffset? RefreshedAt
    {
        get
        {
            lock (_gate)
            {
                return _refreshedAt;
            }
        }
    }

    /// <summary>Upper-case NetBIOS names of all DCs (ComputerNameSpoofing).</summary>
    public IReadOnlySet<string> NetBiosNames => All.Select(d => d.NetBiosName).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-reads the DC list (two searches in the configuration NC and one base read of the domain head).</summary>
    public async Task<IReadOnlyList<KnownDomainController>> RefreshAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sites = DistinguishedName.Parse(session.Info.ConfigurationNamingContext).Child("CN", "Sites").ToString();
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var server in session.SearchAsync(new DirectorySearch
        {
            BaseDn = sites,
            Filter = LdapFilter.Equal(Attr.ObjectClass, "server"),
            Attributes = [Attr.DnsHostName],
            PageSize = 1000,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (server.GetString(Attr.DnsHostName) is { } host)
            {
                hosts[ValueCanonicalizer.NormalizeDn(server.DistinguishedName)] = host;
            }
        }

        var domainNc = session.Info.DefaultNamingContext;
        var result = new List<KnownDomainController>();
        await foreach (var ntds in session.SearchAsync(new DirectorySearch
        {
            BaseDn = sites,
            Filter = LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.NtdsDsa),
            Attributes = [Attr.ObjectGuid, WatchdogAttributes.InvocationId, Attr.ObjectCategory, WatchdogAttributes.HasDomainNcs],
            PageSize = 1000,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (ntds.ObjectGuid is not { } guid)
            {
                continue;
            }

            var parent = ntds.Dn.Parent?.ToString();
            result.Add(new KnownDomainController(
                guid,
                ntds.DistinguishedName,
                parent is not null && hosts.TryGetValue(ValueCanonicalizer.NormalizeDn(parent), out var host) ? host : null,
                ntds.GetBytes(WatchdogAttributes.InvocationId) is { Length: 16 } invocation ? new Guid(invocation) : null,
                IsReadOnlyCategory(ntds.GetString(Attr.ObjectCategory)),
                ntds.GetStrings(WatchdogAttributes.HasDomainNcs).Any(nc => DistinguishedName.AreEqual(nc, domainNc))));
        }

        var head = await session.GetAsync(domainNc, [WatchdogAttributes.FsmoRoleOwner], cancellationToken).ConfigureAwait(false);
        var pdcDsa = head?.GetString(WatchdogAttributes.FsmoRoleOwner);
        var pdc = pdcDsa is null ? null : result.FirstOrDefault(d => DistinguishedName.AreEqual(d.NtdsSettingsDn, pdcDsa))?.HostName;
        lock (_gate)
        {
            _all = result;
            _pdcHostName = pdc;
            _refreshedAt = _timeProvider.GetUtcNow();
        }

        return result;
    }

    /// <summary>Refreshes when the list is older than <see cref="RefreshInterval"/> (or was never read).</summary>
    public async Task EnsureFreshAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var refreshedAt = RefreshedAt;
        if (refreshedAt is null || _timeProvider.GetUtcNow() - refreshedAt >= RefreshInterval)
        {
            await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Host of the DC with this invocationId, or null (a miss: the caller may refresh).</summary>
    public string? ResolveHost(Guid invocationId) => All.FirstOrDefault(d => d.InvocationId == invocationId)?.HostName;

    /// <summary>Host of the DC whose NTDS Settings DN is given, or null.</summary>
    public string? ResolveHostByDsaDn(string? ntdsSettingsDn) =>
        ntdsSettingsDn is null ? null : All.FirstOrDefault(d => DistinguishedName.AreEqual(d.NtdsSettingsDn, ntdsSettingsDn))?.HostName;

    /// <summary>True when <paramref name="host"/> (FQDN or NetBIOS name) is a known DC.</summary>
    public bool IsKnownDomainController(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var label = host.Split('.')[0];
        return All.Any(d => string.Equals(d.HostName, host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.NetBiosName, label.TrimEnd('$'), StringComparison.OrdinalIgnoreCase) && !host.Contains('.', StringComparison.Ordinal));
    }

    /// <summary>objectCategory of RODC NTDS Settings objects is CN=NTDS-DSA-RO,…</summary>
    public static bool IsReadOnlyCategory(string? objectCategory) =>
        objectCategory is not null && DistinguishedName.TryParse(objectCategory, out var dn) && !dn.IsRoot
        && string.Equals(dn.Name, WatchdogObjectClasses.ReadOnlyNtdsDsaCategory, StringComparison.OrdinalIgnoreCase);
}
