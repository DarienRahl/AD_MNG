using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Replication metadata → DC and time window of the attribution search (section 11.4 #1): with originating time and
/// originating DC → that DC, <c>[t − MatchWindow, t + MatchWindow]</c>; with originating time only → every attribution
/// source, same window; otherwise (LVR membership changes, missing metadata) → every source,
/// <c>[LocalChangeTime (or DetectedAt) − ReplicationLatencyBound, (LocalChangeTime or DetectedAt) + FallbackWindow]</c>.
/// An originating DC that is not an attribution source (another domain, an RODC, a configured subset) widens the search
/// to every source. Also resolves invocation ids to hosts for the detection side (10.10).
/// </summary>
public sealed class OriginatingDcResolver(IDomainControllerCatalog domainControllers)
{
    /// <summary>The DC host of a replication-metadata invocation id (null when unknown).</summary>
    public Task<string?> ResolveHostAsync(Guid invocationId, CancellationToken cancellationToken) =>
        domainControllers.ResolveHostAsync(invocationId, cancellationToken);

    /// <summary>The search plan of <paramref name="watchEvent"/>.</summary>
    /// <param name="watchEvent">The pending event.</param>
    /// <param name="sources">The current attribution sources (normalized host names).</param>
    /// <param name="settings">Attribution settings (windows).</param>
    public static AttributionSearchPlan Plan(WatchEvent watchEvent, IReadOnlyList<string> sources, AttributionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        var originatingDc = string.IsNullOrWhiteSpace(watchEvent.OriginatingDc) ? null : HostNames.Normalize(watchEvent.OriginatingDc);
        if (watchEvent.OriginatingTime is { } originating)
        {
            var single = originatingDc is not null && sources.Contains(originatingDc, StringComparer.OrdinalIgnoreCase);
            return new AttributionSearchPlan
            {
                DomainControllers = single ? [originatingDc!] : sources,
                From = originating - settings.MatchWindow,
                To = originating + settings.MatchWindow,
                ReferenceTime = originating,
                OriginatingDc = originatingDc,
                HasOriginatingTime = true,
            };
        }

        var reference = watchEvent.LocalChangeTime ?? watchEvent.DetectedAt;
        return new AttributionSearchPlan
        {
            DomainControllers = sources,
            From = reference - settings.ReplicationLatencyBound,
            To = reference + settings.FallbackWindow,
            ReferenceTime = reference,
            OriginatingDc = originatingDc,
        };
    }
}
