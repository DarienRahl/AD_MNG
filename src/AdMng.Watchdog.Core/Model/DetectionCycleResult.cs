namespace AdMng.Watchdog.Core.Model;

public enum DetectionMode { NoChange, Baseline, Incremental, Failback, Resync, Reconciliation }

/// <summary>Outcome of one detection cycle (logs, health, tests).</summary>
public sealed record DetectionCycleResult
{
    public required DetectionMode Mode { get; init; }
    public int ObjectsExamined { get; init; }
    public int EventsCreated { get; init; }
    public long HighestUsn { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];    // codes, no directory values
}
