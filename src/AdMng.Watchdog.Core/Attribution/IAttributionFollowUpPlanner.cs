using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Follow-ups when an attribution becomes final (sections 11.4 #9 and 13.2 #6): the notification side
/// (<c>FollowUpPlanner</c>) returns pending follow-up rows for every message that was sent with the attribution pending;
/// the <see cref="AttributionEngine"/> adds them with <c>INotificationQueueStore.AddAsync</c>. Every registered planner
/// is called; none registered = no follow-ups.
/// </summary>
public interface IAttributionFollowUpPlanner
{
    /// <summary>The follow-up rows for <paramref name="attributedEvent"/> (its attribution is final).</summary>
    Task<IReadOnlyList<PendingNotification>> PlanAsync(WatchEvent attributedEvent, CancellationToken cancellationToken);
}
