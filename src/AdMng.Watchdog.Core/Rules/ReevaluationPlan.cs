using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// What a re-evaluation after attribution (11.4 #9) changes in the pending notifications of one event (13.2 #1: one row
/// per event and target, carrying the strongest treatment of every rule that notifies the target now; 13.2 #7: a target
/// that already got an alert hears about a different final severity only in a follow-up of that alert).
/// </summary>
public sealed record ReevaluationPlan
{
    /// <summary>No change.</summary>
    public static ReevaluationPlan Empty { get; } = new();

    /// <summary>New rows: targets an added rule notifies that have no waiting row and no alert that covers them – stored
    /// with the evaluation (duplicates ignored).</summary>
    public IReadOnlyList<PendingNotification> Insert { get; init; } = [];

    /// <summary>Targets an added rule notifies that have a waiting row: the re-planned row replaces its treatment, and is
    /// inserted when the waiting row was flushed meanwhile (the dispatch job drops it then if the alert covers it)
    /// (<c>INotificationQueueStore.ReplanAsync</c>, insert missing).</summary>
    public IReadOnlyList<PendingNotification> Merge { get; init; } = [];

    /// <summary>The other waiting rows, re-planned from the rules that match now – applied only while they wait.</summary>
    public IReadOnlyList<PendingNotification> Update { get; init; } = [];

    /// <summary>Waiting rows (ids) whose target no rule notifies any more.</summary>
    public IReadOnlyList<long> Drop { get; init; } = [];

    /// <summary>Follow-up rows (13.2 #7) of targets that already got an alert of the event and that a stored or added rule
    /// notifies now with a different severity: an escalation to High or above, or the final severity of the attribution
    /// follow-up still to come. One row per (event, target) replying to the target's latest alert – the attribution
    /// follow-up of the same target merges into it (<c>INotificationQueueStore.ReplanAsync</c>, insert missing: a waiting
    /// follow-up takes its severity and rule; the follow-up planner's later row is a duplicate and ignored).</summary>
    public IReadOnlyList<PendingNotification> FollowUps { get; init; } = [];

    /// <summary>True when nothing changes.</summary>
    public bool IsEmpty => Insert.Count == 0 && Merge.Count == 0 && Update.Count == 0 && Drop.Count == 0 && FollowUps.Count == 0;

    /// <summary>Both plans together (several planners).</summary>
    public ReevaluationPlan Combine(ReevaluationPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new ReevaluationPlan
        {
            Insert = [.. Insert, .. other.Insert],
            Merge = [.. Merge, .. other.Merge],
            Update = [.. Update, .. other.Update],
            Drop = [.. Drop.Concat(other.Drop).Distinct()],
            FollowUps = [.. FollowUps, .. other.FollowUps],
        };
    }
}
