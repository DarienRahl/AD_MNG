using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>Result of the rule evaluation of one stored event.</summary>
public sealed record EventEvaluation
{
    public required long EventId { get; init; }
    public required Severity Severity { get; init; }
    public IReadOnlyList<string> MatchedRules { get; init; } = [];
}

/// <summary>What the AttributionEngine writes back into a stored event.</summary>
public sealed record AttributionUpdate
{
    // Fully qualified: the simple name would bind to the namespace AdMng.Watchdog.Core.Attribution (Attribution/ folder).
    public required AdMng.Watchdog.Contracts.Events.Attribution Attribution { get; init; }
    public IReadOnlyList<string> AddedTags { get; init; } = [];     // selfChange, passwordReset, syncAccount
    /// <summary>Re-resolved severity of attribution-dependent kinds (12.2); null = unchanged.</summary>
    public Severity? Severity { get; init; }
    /// <summary>Lockout sources filled by LockoutSourceResolver; null = unchanged.</summary>
    public LockoutInfo? Lockout { get; init; }
    /// <summary>Null when the attribution is final.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }
}
