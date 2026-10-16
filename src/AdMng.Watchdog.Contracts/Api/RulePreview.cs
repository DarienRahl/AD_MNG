using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary><c>POST /api/v1/rules/preview</c>: what an (unsaved) rule would have matched.</summary>
public sealed record RulePreviewRequest
{
    public required RuleSettings Rule { get; init; }
    public int Days { get; init; } = 7;           // 1..90
}

public sealed record DailyCount(DateOnly Day, int Events, int Messages);

public sealed record RulePreviewResult
{
    public int MatchedEvents { get; init; }
    public int EstimatedImmediateMessages { get; init; }   // after aggregation + throttle simulation
    public IReadOnlyList<DailyCount> PerDay { get; init; } = [];
    public IReadOnlyList<WatchEvent> Samples { get; init; } = [];    // ≤ 20, newest first
    public bool ScanLimitReached { get; init; }                      // > 100 000 events scanned
}
