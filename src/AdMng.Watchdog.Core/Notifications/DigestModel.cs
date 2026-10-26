using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// The content of one digest (section 13.4): header (period, domain, health line), counters, "Wymaga uwagi" (events ≥
/// High with their acknowledgement state) and the non-empty sections in display order. The model holds stored data as
/// is; every formatter masks and sanitizes it for its recipient.
/// </summary>
public sealed record DigestModel
{
    /// <summary>Start of the reported period (the end of the last business-day digest when non-business days are covered).</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>The reported period goes back over non-business days (Monday after a weekend, the day after a holiday).</summary>
    public bool CoversNonBusinessDays { get; init; }

    /// <summary>The weekly sections (inactive accounts, hygiene) are included.</summary>
    public bool IsWeekly { get; init; }

    public string? Domain { get; init; }

    public DigestHealth Health { get; init; } = new();

    public DigestCounts Counts { get; init; } = new();

    /// <summary>The first <c>MaxRowsPerSection</c> events ≥ High (most severe first, then oldest first).</summary>
    public IReadOnlyList<WatchEvent> Attention { get; init; } = [];

    /// <summary>Events of <see cref="Attention"/> whose severity changed after their alert went out (13.2 #7: an escalation
    /// or a follow-up told the recipients another severity), with the severity the alert showed – rendered as "priorytet:
    /// Średni → Wysoki" next to the event (<see cref="EventNotices.ChangedFrom"/>).</summary>
    public IReadOnlyDictionary<long, Severity> SeverityChanges { get; init; } = new Dictionary<long, Severity>();

    /// <summary>The severity the alert of <paramref name="eventId"/> showed before an escalation changed it, or null.</summary>
    public Severity? PreviousSeverity(long eventId) => SeverityChanges.TryGetValue(eventId, out var previous) ? previous : null;

    public IReadOnlyList<DigestSection> Sections { get; init; } = [];

    /// <summary>Events reported by the sections and "Wymaga uwagi" (webhook payload, capped).</summary>
    public IReadOnlyList<Guid> EventIds { get; init; } = [];

    /// <summary>Stored ids of the events shown in rows (outbox links of the digest messages).</summary>
    public IReadOnlyList<long> ShownEventIds { get; init; } = [];

    /// <summary>No events in the period ("Brak zmian w okresie" heartbeat digest).</summary>
    public bool IsEmpty => Counts.Total == 0;

    /// <summary>The section of <paramref name="kind"/>, or null when it is empty.</summary>
    public DigestSection? Section(DigestSectionKind kind) => Sections.FirstOrDefault(s => s.Kind == kind);
}
