using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The attribution side's <see cref="IDomainControllerCatalog"/> over <see cref="DomainControllerDirectory"/> (section
/// 10.11): writable DCs of this domain as attribution sources, every DC of the forest for <see cref="EventSourceGuard"/>,
/// invocationId → host and the PDC emulator. The detection cycle keeps the directory fresh; when attribution runs first
/// the catalog refreshes it with the service's session, and an invocationId miss refreshes it at most every
/// <see cref="MissRefreshInterval"/>. While the DC list could never be read the catalog degrades to
/// <see cref="SessionDomainControllerCatalog"/> (configured DCs and the watched DC, health Warning
/// <c>attribution.dcDirectory</c>).
/// </summary>
public sealed class DomainControllerCatalog : IDomainControllerCatalog
{
    /// <summary>Minimum time between two refreshes caused by an unknown invocationId.</summary>
    public static readonly TimeSpan MissRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly DomainControllerDirectory _directory;
    private readonly IDirectorySessionProvider _sessions;
    private readonly SessionDomainControllerCatalog _fallback;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public DomainControllerCatalog(
        DomainControllerDirectory directory,
        IDirectorySessionProvider sessions,
        SessionDomainControllerCatalog fallback,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _directory = directory;
        _sessions = sessions;
        _fallback = fallback;
        _timeProvider = timeProvider;
    }

    /// <summary>True once the DC list of the forest was read (otherwise the degraded fallback answers).</summary>
    public bool IsAuthoritative => _directory.RefreshedAt is not null && _directory.All.Count > 0;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetAttributionSourcesAsync(CancellationToken cancellationToken)
    {
        await EnsureReadAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (!IsAuthoritative)
        {
            return await _fallback.GetAttributionSourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Hosts(_directory.AttributionSources);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetKnownDomainControllersAsync(CancellationToken cancellationToken)
    {
        await EnsureReadAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (!IsAuthoritative)
        {
            return await _fallback.GetKnownDomainControllersAsync(cancellationToken).ConfigureAwait(false);
        }

        return Hosts(_directory.All);
    }

    /// <inheritdoc/>
    public async Task<string?> ResolveHostAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        if (_directory.ResolveHost(invocationId) is { } host)
        {
            return HostNames.Normalize(host);
        }

        var refreshedAt = _directory.RefreshedAt;
        if (refreshedAt is null || _timeProvider.GetUtcNow() - refreshedAt >= MissRefreshInterval)
        {
            await EnsureReadAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        return _directory.ResolveHost(invocationId) is { } refreshed ? HostNames.Normalize(refreshed) : null;
    }

    /// <inheritdoc/>
    public async Task<string?> GetPdcHostAsync(CancellationToken cancellationToken)
    {
        await EnsureReadAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (_directory.PdcHostName is { } pdc)
        {
            return HostNames.Normalize(pdc);
        }

        return IsAuthoritative ? null : await _fallback.GetPdcHostAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<string> Hosts(IEnumerable<KnownDomainController> controllers) =>
        [.. controllers.Where(d => d.HostName is not null).Select(d => HostNames.Normalize(d.HostName!)).Distinct(StringComparer.Ordinal)];

    /// <summary>Reads the DC list when it was never read or is older than <see cref="DomainControllerDirectory.RefreshInterval"/>
    /// (<paramref name="force"/>: now). Directory errors keep the last known list (the detection cycle reports them).</summary>
    private async Task EnsureReadAsync(bool force, CancellationToken cancellationToken)
    {
        var refreshedAt = _directory.RefreshedAt;
        if (!force && refreshedAt is not null && _timeProvider.GetUtcNow() - refreshedAt < DomainControllerDirectory.RefreshInterval)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_directory.RefreshedAt != refreshedAt)
            {
                return; // refreshed meanwhile (detection cycle or another caller)
            }

            var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            await _directory.RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            // Unreachable or denied: keep the last known list; the detection cycle reports the directory health.
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
