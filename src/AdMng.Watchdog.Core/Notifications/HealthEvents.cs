using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>Health events raised by the notification and storage side (HealthDeliveryFailing, HealthStorage): object =
/// the watchdog instance, details as text changes (codes, ids and numbers only – never directory values), attribution
/// not applicable, default severity of the kind.</summary>
public static class HealthEvents
{
    /// <summary>A health event of <paramref name="kind"/> detected at <paramref name="now"/>.</summary>
    public static WatchEvent Create(WatchEventKind kind, DateTimeOffset now, params IEnumerable<(string Attribute, string Value)> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var id = Guid.CreateVersion7(now);
        return new WatchEvent
        {
            EventId = id,
            ChangeSetId = id,
            Kind = kind,
            Severity = EventKindCatalog.DefaultSeverity(kind, WatchObjectKind.Watchdog),
            Object = new ObjectRef { Dn = string.Empty, Name = string.Empty, Kind = WatchObjectKind.Watchdog },
            Changes = [.. details.Select(d => new AttributeChange { Attribute = d.Attribute, ValueKind = AttributeValueKind.Text, NewValues = [d.Value], NewCount = 1 })],
            DetectedAt = now,
            Source = DetectionSource.Health,
            Attribution = AdMng.Watchdog.Contracts.Events.Attribution.NotApplicable,
        };
    }
}
