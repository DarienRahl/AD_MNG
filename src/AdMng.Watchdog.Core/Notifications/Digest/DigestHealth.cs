using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The health line of the digest header: DC, last poll, attribution coverage, event-log gaps of the period,
/// broken channels and rollout mode.</summary>
public sealed record DigestHealth
{
    public HealthState DirectoryState { get; init; } = HealthState.Unknown;

    public string? DcHostName { get; init; }

    public DateTimeOffset? LastPollAt { get; init; }

    /// <summary>Lowest 24-hour attribution coverage of the read DCs (0..1), null when attribution is off or unknown.</summary>
    public double? AttributionCoverage { get; init; }

    /// <summary>HealthEventLogGap events in the period.</summary>
    public int EventLogGaps { get; init; }

    /// <summary>Names of the channels currently in Error.</summary>
    public IReadOnlyList<string> BrokenChannels { get; init; } = [];

    /// <summary>End of rollout mode while it is active.</summary>
    public DateTimeOffset? RolloutUntil { get; init; }
}
