using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>One row of the <c>events</c> table of <see cref="InMemoryWatchdogStore"/>: the payload plus the columns
/// that are not part of <see cref="WatchEvent"/>.</summary>
internal sealed class StoredEventRow(WatchEvent watchEvent)
{
    /// <summary>The stored payload (a copy; its <see cref="WatchEvent.Id"/> is the row id).</summary>
    public WatchEvent Event { get; set; } = watchEvent;

    /// <summary><c>rules_state</c>: false until <c>CommitEvaluationAsync</c>.</summary>
    public bool Evaluated { get; set; }

    /// <summary><c>next_attribution_at</c>: detection time on insert for pending events, then the AttributionEngine's
    /// next attempt; null when final.</summary>
    public DateTimeOffset? NextAttributionAt { get; set; } =
        watchEvent.Attribution.Status == AttributionStatus.Pending ? watchEvent.DetectedAt : null;

    public long Id => Event.Id;
}
