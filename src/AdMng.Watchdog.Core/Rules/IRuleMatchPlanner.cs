using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Turns the rule matches of one event into pending notifications (section 13.2: targets, delivery hours, rollout mode,
/// repeat suppression, attribution holds, aggregation buckets – one row per target with the strongest treatment of the
/// rules that notify it). Implemented by the notification side (<c>NotificationPlanner</c>); <see cref="RuleEvaluationJob"/>
/// calls it for every evaluated event and commits the rows together with the evaluations.
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
    /// The re-plan of a re-evaluation after attribution (11.4 #9). <paramref name="evaluation"/> holds the event with its
    /// final attribution and every rule that matches it now; <paramref name="addedRuleIds"/> are the ones that were not
    /// among its stored matched rules; <paramref name="waiting"/> are the event's waiting (non-follow-up) rows. Rules that
    /// matched before are never planned again for a target without a waiting row (it may already be notified), the targets
    /// that already got the final state are left out, and every waiting row takes the treatment of the rules that notify
    /// its target now. Default: the added matches planned as new rows.
    /// </summary>
    async ValueTask<ReevaluationPlan> PlanReevaluationAsync(RuleEvaluationResult evaluation, IReadOnlySet<string> addedRuleIds,
        IReadOnlyList<PendingNotification> waiting, WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(addedRuleIds);
        var added = evaluation with { Matches = [.. evaluation.Matches.Where(m => addedRuleIds.Contains(m.Rule.Id))] };
        return added.Matches.Count == 0
            ? ReevaluationPlan.Empty
            : new ReevaluationPlan { Insert = await PlanAsync(added, configuration, now, cancellationToken).ConfigureAwait(false) };
    }
}
