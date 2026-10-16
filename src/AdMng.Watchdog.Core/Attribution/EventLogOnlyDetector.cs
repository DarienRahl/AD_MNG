using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Detections that only exist in the DC logs (section 11.6): 1102 → SecurityLogCleared (Critical), 4719 →
/// AuditPolicyChanged (Critical; Changes: SubcategoryGuid and the AuditPolicyChanges tokens), 4794 → DsrmPasswordChanged
/// (High; workstation from the record). The object is the DC that logged the record; the attribution is resolved at
/// creation – the record itself is the evidence (actor from SubjectUserSid). 1104/1100 are gaps
/// (<see cref="EventLogGapDetector"/>).
/// </summary>
public sealed class EventLogOnlyDetector(ActorResolver actors, TimeProvider timeProvider)
{
    /// <summary>The watch event of <paramref name="record"/>, or null when the record is not an event-log-only detection.</summary>
    public async Task<WatchEvent?> DetectAsync(SecurityEvent record, string? domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        WatchEventKind? kind = record.EventCode switch
        {
            SecurityEventCatalog.SecurityLogCleared => WatchEventKind.SecurityLogCleared,
            SecurityEventCatalog.AuditPolicyChanged => WatchEventKind.AuditPolicyChanged,
            SecurityEventCatalog.DsrmPasswordSet => WatchEventKind.DsrmPasswordChanged,
            _ => null,
        };
        if (kind is not { } detected)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var host = HostNames.Normalize(record.Computer);
        var watchEvent = new WatchEvent
        {
            EventId = id,
            ChangeSetId = id,
            Kind = detected,
            Severity = EventKindCatalog.DefaultSeverity(detected, WatchObjectKind.DomainController),
            Object = new ObjectRef { Dn = string.Empty, Name = host, DisplayName = record.Computer, Kind = WatchObjectKind.DomainController },
            Changes = Changes(record),
            Domain = domain,
            DetectedAt = now,
            OriginatingTime = record.TimeCreated,
            OriginatingDc = host,
            Source = DetectionSource.SecurityLog,
        };

        var actor = await actors.ResolveAsync(record, watchEvent, cancellationToken).ConfigureAwait(false);
        return watchEvent with
        {
            Attribution = new EventAttribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.High,
                Reason = actor.Reason,
                Actor = actor.Actor,
                Workstation = record.EventCode == SecurityEventCatalog.DsrmPasswordSet ? record.Get("Workstation") : null,
                SourceMethod = record.EventCode == SecurityEventCatalog.DsrmPasswordSet && record.Get("Workstation") is not null ? "4794" : null,
                SourceConfidence = record.EventCode == SecurityEventCatalog.DsrmPasswordSet && record.Get("Workstation") is not null
                    ? AttributionConfidence.Low
                    : AttributionConfidence.None,
                Evidence = [new EvidenceRef { Dc = record.Computer, EventCode = record.EventCode, RecordId = record.RecordId, TimeCreated = record.TimeCreated }],
                ResolvedAt = now,
            },
            Tags = actor.AddedTags,
        };
    }

    private static IReadOnlyList<AttributeChange> Changes(SecurityEvent record)
    {
        if (record.EventCode != SecurityEventCatalog.AuditPolicyChanged)
        {
            return [];
        }

        var changes = new List<AttributeChange>();
        foreach (var name in new[] { "SubcategoryGuid", "AuditPolicyChanges" })
        {
            if (record.Get(name) is { } value)
            {
                var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                changes.Add(new AttributeChange { Attribute = name, ValueKind = AttributeValueKind.Text, NewValues = values, NewCount = values.Length });
            }
        }

        return changes;
    }
}
