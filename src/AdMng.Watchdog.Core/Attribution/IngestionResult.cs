using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Outcome of ingesting one source (<see cref="SecurityLogIngestor.IngestAsync"/>).</summary>
public sealed record IngestionResult
{
    /// <summary>Records read (before the source guard and the reduction).</summary>
    public int Read { get; init; }

    /// <summary>Records stored after the guard and the reduction.</summary>
    public int Stored { get; init; }

    /// <summary>Records rejected by <see cref="EventSourceGuard"/>.</summary>
    public int Foreign { get; init; }

    /// <summary>Records that could not be parsed.</summary>
    public int Malformed { get; init; }

    /// <summary>The log could not be read (health Error <c>eventlog.&lt;host&gt;</c>).</summary>
    public bool Failed { get; init; }

    /// <summary>Event-log-only detections and HealthEventLogGap events to append.</summary>
    public IReadOnlyList<WatchEvent> Events { get; init; } = [];
}
