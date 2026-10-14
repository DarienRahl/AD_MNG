namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Where and when to look for the Security events of one change (section 11.4 #1).</summary>
public sealed record AttributionSearchPlan
{
    /// <summary>DCs whose records are candidates (normalized host names); one DC when the originating DC is known and is
    /// an attribution source.</summary>
    public required IReadOnlyList<string> DomainControllers { get; init; }

    /// <summary>Start of the time window (inclusive).</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>End of the time window (inclusive).</summary>
    public required DateTimeOffset To { get; init; }

    /// <summary>The time the change is believed to have happened (originating time, else local change time, else
    /// detection time) – the reference of the Δt scores.</summary>
    public required DateTimeOffset ReferenceTime { get; init; }

    /// <summary>The originating DC from replication metadata (normalized), when known.</summary>
    public string? OriginatingDc { get; init; }

    /// <summary>True when the window is the narrow ±MatchWindow around a replication-metadata originating time.</summary>
    public bool HasOriginatingTime { get; init; }
}
