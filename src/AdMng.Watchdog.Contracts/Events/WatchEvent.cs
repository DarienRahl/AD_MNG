namespace AdMng.Watchdog.Contracts.Events;

/// <summary>One detected change or condition.</summary>
public sealed record WatchEvent
{
    /// <summary>Store cursor (autoincrement); 0 until stored.</summary>
    public long Id { get; init; }
    /// <summary>Stable id (Guid v7): webhook-id, e-mail Message-ID, HTML link.</summary>
    public required Guid EventId { get; init; }
    /// <summary>Events produced from one object diff (create + enable + password set) share this id.</summary>
    public Guid ChangeSetId { get; init; }
    public required WatchEventKind Kind { get; init; }
    /// <summary>The evaluated (displayed) severity: <see cref="OwnSeverity"/> raised by the <c>Severity</c> overrides of the
    /// rules the event matched (12.1) – feed, digest, counts.</summary>
    public Severity Severity { get; init; }
    /// <summary>The event's own severity without rule overrides (12.2: detection, tag bumps, attribution) – the input of
    /// every rule match (<c>MinSeverity</c>, <c>rule.Severity ?? OwnSeverity</c>). The store fills it on insert; null =
    /// <see cref="Severity"/> (an event not stored yet).</summary>
    public Severity? OwnSeverity { get; init; }
    public required ObjectRef Object { get; init; }
    public string? PreviousDn { get; init; }                 // moves/renames, deletions (last live DN)
    public IReadOnlyList<AttributeChange> Changes { get; init; } = [];
    public ObjectRef? Member { get; init; }                  // GroupMember*: the member; Privilege*: the privileged group
    public IReadOnlyList<ObjectRef> Via { get; init; } = []; // Privilege*: nesting path principal → … → privileged group
    public ExpiryInfo? Expiry { get; init; }
    public LockoutInfo? Lockout { get; init; }
    public BurstInfo? Burst { get; init; }
    public string? Domain { get; init; }                     // DNS name
    public required DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? OriginatingTime { get; init; }    // replication metadata (or Security event time)
    public string? OriginatingDc { get; init; }              // DNS host name
    /// <summary>whenChanged on the watched DC (arrival time there) – correlation fallback when metadata is missing (LVR member changes).</summary>
    public DateTimeOffset? LocalChangeTime { get; init; }
    public required DetectionSource Source { get; init; }
    public Attribution Attribution { get; init; } = Attribution.Pending;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> MatchedRules { get; init; } = [];
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public string? AcknowledgedBy { get; init; }
    public string? AcknowledgeComment { get; init; }
}
