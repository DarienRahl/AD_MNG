using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Turns the rule matches of one event into pending notifications (section 13.2: targets, delivery hours, rollout mode,
/// repeat suppression, attribution holds, aggregation buckets). Implemented by the notification side
/// (<c>NotificationPlanner</c>); <see cref="RuleEvaluationJob"/> calls it for every evaluated event and commits the rows
/// together with the evaluations.
/// </summary>
public interface IRuleMatchPlanner
{
    /// <summary>The pending notifications of <paramref name="evaluation"/> (empty when nothing notifies immediately).</summary>
    /// <param name="evaluation">The evaluated event and its matches.</param>
    /// <param name="configuration">The configuration the matches were computed with (the current one, or the previous
    /// one for <c>WatchdogConfigurationChanged</c>, 17.2).</param>
    /// <param name="now">Evaluation time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<PendingNotification>> PlanAsync(RuleEvaluationResult evaluation, WatchdogConfiguration configuration,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The pending notifications of the rules a re-evaluation after attribution added (11.4 #9): <paramref name="evaluation"/>
    /// holds the event with its final attribution and only the added matches. The event may already have been notified
    /// under the rules that matched before – the notification side leaves out the targets that already got (or will get,
    /// through the follow-up) the final state. Default: <see cref="PlanAsync"/>.
    /// </summary>
    ValueTask<IReadOnlyList<PendingNotification>> PlanReevaluationAsync(RuleEvaluationResult evaluation, WatchdogConfiguration configuration,
        DateTimeOffset now, CancellationToken cancellationToken) => PlanAsync(evaluation, configuration, now, cancellationToken);
}
