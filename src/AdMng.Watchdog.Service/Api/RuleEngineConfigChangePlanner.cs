using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The <see cref="IConfigChangeNotificationPlanner"/> of the service (spec 17.2 step 5, 13.2): evaluates the stored
/// <see cref="WatchEventKind.WatchdogConfigurationChanged"/> event against the effective form of the PREVIOUS
/// configuration (<see cref="RuleEngine"/>), lets the notification planner (<see cref="IRuleMatchPlanner"/>) turn the
/// matches into pending notifications for the previous recipients and adds them to the queue. The event stays
/// unevaluated, so the rule evaluation job plans it with the new configuration as well; the queue's (event, target,
/// follow-up) uniqueness removes the duplicates.
/// </summary>
public sealed class RuleEngineConfigChangePlanner(
    RuleEngine engine,
    IEnumerable<IRuleMatchPlanner> planners,
    INotificationQueueStore queue,
    IWatchdogSignals signals,
    TimeProvider timeProvider) : IConfigChangeNotificationPlanner
{
    private readonly IReadOnlyList<IRuleMatchPlanner> _planners = [.. planners];

    public async Task PlanWithPreviousConfigurationAsync(WatchEvent stored, WatchdogConfiguration previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(previous);
        var effective = WatchdogConfigProvider.Effective(previous);
        var now = timeProvider.GetUtcNow();
        var evaluation = RuleEngine.Evaluate(stored, engine.CreateContext(effective), now);
        var rows = new List<PendingNotification>();
        foreach (var planner in _planners)
        {
            rows.AddRange(await planner.PlanAsync(evaluation, effective, now, cancellationToken).ConfigureAwait(false));
        }

        if (rows.Count > 0)
        {
            await queue.AddAsync(rows, cancellationToken).ConfigureAwait(false);
            signals.Raise(WatchdogSignal.NotificationsQueued);
        }
    }
}
