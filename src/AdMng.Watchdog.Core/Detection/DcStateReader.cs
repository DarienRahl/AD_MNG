using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Reads the state of the watched DC (section 10.2): RootDSE (highestCommittedUSN H first – Microsoft's documented
/// algorithm reads H before any search), the invocationId of the NTDS Settings object and, once per session, the
/// domain head (fSMORoleOwner → IsPdc, wellKnownObjects, objectSid) and the root domain SID (best effort). Two base
/// reads per idle cycle (budget 10.18).
/// </summary>
public sealed class DcStateReader
{
    private static readonly string[] RootDseAttributes =
    [
        WatchdogAttributes.HighestCommittedUsn, WatchdogAttributes.DsServiceName, Attr.DnsHostNameRootDse,
        WatchdogAttributes.IsSynchronized, Attr.CurrentTime, Attr.ServerName,
    ];

    private readonly Lock _gate = new();
    private (IDirectorySession Session, DomainHeadFacts Facts)? _cache;

    /// <summary>Reads the DC state.</summary>
    /// <exception cref="DirectoryException">The DC is unreachable or RootDSE/NTDS Settings are unreadable.</exception>
    public async Task<DcSnapshot> ReadAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rootDse = await session.GetAsync(string.Empty, RootDseAttributes, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "RootDSE is not readable.");
        var highestUsn = rootDse.GetInt64(WatchdogAttributes.HighestCommittedUsn)
            ?? throw new DirectoryException(DirectoryErrorKind.Unknown, "RootDSE has no highestCommittedUSN.");
        var dsServiceName = rootDse.GetString(WatchdogAttributes.DsServiceName)
            ?? throw new DirectoryException(DirectoryErrorKind.Unknown, "RootDSE has no dsServiceName.");
        var ntds = await session.GetAsync(dsServiceName, [WatchdogAttributes.InvocationId, Attr.ObjectGuid], cancellationToken).ConfigureAwait(false);
        var invocation = ntds?.GetBytes(WatchdogAttributes.InvocationId) is { Length: 16 } bytes
            ? new Guid(bytes)
            : throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The NTDS Settings object has no readable invocationId.");
        var head = await GetDomainHeadAsync(session, cancellationToken).ConfigureAwait(false);
        return new DcSnapshot
        {
            HighestUsn = highestUsn,
            DsServiceName = dsServiceName,
            DnsHostName = rootDse.GetString(Attr.DnsHostNameRootDse) ?? session.Info.Server,
            InvocationId = invocation,
            IsSynchronized = rootDse.GetBoolean(WatchdogAttributes.IsSynchronized) ?? true,
            CurrentTime = rootDse.GetGeneralizedTime(Attr.CurrentTime),
            IsPdc = head.FsmoRoleOwner is not null && DistinguishedName.AreEqual(head.FsmoRoleOwner, dsServiceName),
            Facts = head.Facts,
            Containers = head.Containers,
        };
    }

    /// <summary>Forgets the per-session domain head facts (e.g. after the domain head changed).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    private async Task<DomainHeadFacts> GetDomainHeadAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cache is { } cached && ReferenceEquals(cached.Session, session))
            {
                return cached.Facts;
            }
        }

        var info = session.Info;
        var head = await session.GetAsync(info.DefaultNamingContext,
            [WatchdogAttributes.FsmoRoleOwner, WatchdogAttributes.WellKnownObjects, Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        var domainSid = head?.ObjectSid;
        string? rootSid = domainSid;
        if (info.RootDomainNamingContext is { } root && !DistinguishedName.AreEqual(root, info.DefaultNamingContext))
        {
            try
            {
                rootSid = (await session.GetAsync(root, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false))?.ObjectSid;
            }
            catch (DirectoryException ex) when (!ex.IsTransient)
            {
                rootSid = null;
            }
        }

        var facts = new DomainHeadFacts(
            head?.GetString(WatchdogAttributes.FsmoRoleOwner),
            new DirectoryFacts
            {
                DomainDn = info.DefaultNamingContext,
                DomainDnsName = info.DomainDnsName,
                ConfigurationDn = info.ConfigurationNamingContext,
                SchemaDn = info.SchemaNamingContext,
                DomainSid = domainSid,
                RootDomainSid = rootSid,
            },
            WellKnownContainers.Parse(head?.GetStrings(WatchdogAttributes.WellKnownObjects) ?? [], info.DefaultNamingContext));
        lock (_gate)
        {
            _cache = (session, facts);
        }

        return facts;
    }

    private sealed record DomainHeadFacts(string? FsmoRoleOwner, DirectoryFacts Facts, WellKnownContainers Containers);
}
