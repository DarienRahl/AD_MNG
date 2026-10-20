using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>Result of the rule evaluation of one stored event.</summary>
public sealed record EventEvaluation
{
    public required long EventId { get; init; }
    /// <summary>The evaluated severity (<see cref="WatchEvent.Severity"/>); the event's own severity is not changed.</summary>
    public required Severity Severity { get; init; }
    public IReadOnlyList<string> MatchedRules { get; init; } = [];
}

/// <summary>What the AttributionEngine writes back into a stored event.</summary>
public sealed record AttributionUpdate
{
    // Fully qualified: the simple name would bind to the namespace AdMng.Watchdog.Core.Attribution (Attribution/ folder).
    public required AdMng.Watchdog.Contracts.Events.Attribution Attribution { get; init; }
    public IReadOnlyList<string> AddedTags { get; init; } = [];     // selfChange, passwordReset, syncAccount
    /// <summary>Re-resolved own severity of attribution-dependent kinds (12.2, <see cref="WatchEvent.OwnSeverity"/>); null =
    /// unchanged.</summary>
    public Severity? Severity { get; init; }
    /// <summary>The evaluated severity of the re-evaluation after the attribution (11.4 #9): the final own severity raised by
    /// the overrides of the rules that match (<see cref="WatchEvent.Severity"/>). Null = unchanged, or the new own severity
    /// when <see cref="Severity"/> is set.</summary>
    public Severity? EvaluatedSeverity { get; init; }
    /// <summary>Lockout sources filled by LockoutSourceResolver; null = unchanged.</summary>
    public LockoutInfo? Lockout { get; init; }
    /// <summary>Null when the attribution is final.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }
}
