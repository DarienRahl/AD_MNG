using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// HealthAttributionDegraded (section 7): a DC log became unreadable, or its attribution coverage fell below 50 % over
/// 24 hours (NotApplicable kinds and lockout resets excluded). One event per DC on the transition from good to
/// degraded; repeated only after the DC recovered and relapsed.
/// </summary>
public sealed class AttributionDegradedMonitor(DcLogHealthTracker tracker, TimeProvider timeProvider)
{
    public const string Unreadable = "unreadable";
    public const string LowCoverage = "lowCoverage";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _degraded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The events of the DCs that turned degraded since the previous call.</summary>
    public IReadOnlyList<WatchEvent> Check(IEnumerable<string> hosts, string? domain)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var events = new List<WatchEvent>();
        foreach (var row in tracker.Snapshot(hosts))
        {
            string? reason = row.State == HealthState.Error ? Unreadable : tracker.IsCoverageLow(row.Host) ? LowCoverage : null;
            lock (_gate)
            {
                if (reason is null)
                {
                    _degraded.Remove(row.Host);
                    continue;
                }

                if (!_degraded.Add(row.Host))
                {
                    continue;
                }
            }

            var now = timeProvider.GetUtcNow();
            var id = Guid.CreateVersion7(now);
            events.Add(new WatchEvent
            {
                EventId = id,
                ChangeSetId = id,
                Kind = WatchEventKind.HealthAttributionDegraded,
                Severity = EventKindCatalog.DefaultSeverity(WatchEventKind.HealthAttributionDegraded, WatchObjectKind.Watchdog),
                Object = new ObjectRef { Dn = string.Empty, Name = row.Host, Kind = WatchObjectKind.Watchdog },
                Changes = [new AttributeChange { Attribute = "attribution", ValueKind = AttributeValueKind.Text, NewValues = [reason], NewCount = 1 }],
                Domain = domain,
                DetectedAt = now,
                OriginatingDc = row.Host,
                Source = DetectionSource.Health,
                Attribution = EventAttribution.NotApplicable,
            });
        }

        return events;
    }
}
