using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Rule matches → pending notifications (section 13.2). Every match with <c>Delivery.Immediate</c> that may notify now –
/// (0) rollout mode lets only <c>tier0-*</c>, <c>bursts</c> and <c>health</c> through; (2) delivery hours at the rule's
/// notification severity (<c>Default</c> = Always for ≥ High, else business hours) – outside them only the digest reports
/// the event; (3) repeat suppression per object – contributes to each of its targets (1, <see cref="NotificationTargetResolver"/>).
/// One row per (event, target) carries the strongest treatment of its contributing rules: (4) no attribution hold when
/// any of them notifies at once (hold: severity &lt; High, attribution-dependent kinds and provisional actor matches while
/// the actor is pending) – the treatment then comes from those rules only –, the highest notification severity, (5) the
/// shortest aggregation window, and the rule earliest in configuration order as the displayed rule and bucket.
/// </summary>
/// <remarks>The rule evaluation job calls it as <see cref="IRuleMatchPlanner"/> (the <see cref="RuleMatch"/>es map 1:1 onto
/// <see cref="PlannedMatch"/>); the attribution engine's re-evaluation re-plans the event's waiting rows
/// (<see cref="IRuleMatchPlanner.PlanReevaluationAsync"/>) and the dispatch job re-plans a released target
/// (<see cref="PlanTargetAsync"/>) – always from every rule that matches now, so a target never stays with a rule that
/// no longer matches or with a weaker treatment. <c>WatchdogConfigurationChanged</c> is planned with the previous
/// configuration by passing it explicitly (17.2).</remarks>
public sealed class NotificationPlanner : IRuleMatchPlanner
{
    /// <summary>Kinds whose severity is re-resolved once the attribution is final (12.2 step 3): always held while the
    /// actor is pending. The one source is <see cref="SeverityResolver.AttributionDependentKinds"/>.</summary>
    public static IReadOnlySet<WatchEventKind> AttributionDependentKinds => SeverityResolver.AttributionDependentKinds;

    private static readonly IReadOnlySet<string> NoExemptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IOutboxStore _outbox;
    private readonly NotificationTargetResolver _targets;
    private readonly BusinessCalendarProvider _calendars;
    private readonly RolloutGate _rollout;

    public NotificationPlanner(IOutboxStore outbox, NotificationTargetResolver targets, BusinessCalendarProvider calendars, RolloutGate rollout)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(calendars);
        ArgumentNullException.ThrowIfNull(rollout);
        _outbox = outbox;
        _targets = targets;
        _calendars = calendars;
        _rollout = rollout;
    }

    /// <summary>True when an immediate notification of <paramref name="severity"/> under <paramref name="rule"/> may go out
    /// at <paramref name="now"/> (delivery hours, 13.2 #2).</summary>
    public bool IsWithinDeliveryHours(RuleSettings rule, Severity severity, WatchdogConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(configuration);
        var hours = rule.Delivery?.Hours ?? DeliveryHours.Default;
        var businessHoursOnly = hours == DeliveryHours.BusinessHours || (hours == DeliveryHours.Default && severity < Severity.High);
        return !businessHoursOnly || _calendars.IsBusinessTime(now, configuration);
    }

    /// <summary>True when the notification must wait for the attribution (13.2 #4).</summary>
    public static bool NeedsAttributionHold(WatchEvent watchEvent, PlannedMatch match)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(match);
        return watchEvent.Attribution?.Status == AttributionStatus.Pending
            && (match.IsProvisional || match.NotificationSeverity < Severity.High || AttributionDependentKinds.Contains(watchEvent.Kind));
    }

    /// <summary>The pending notifications of one evaluated event – one per target (empty when nothing notifies
    /// immediately).</summary>
    /// <param name="watchEvent">The stored event (Id assigned).</param>
    /// <param name="matches">Its rule matches in configuration order.</param>
    /// <param name="configuration">The configuration the matches were computed with.</param>
    /// <param name="now">Evaluation time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<IReadOnlyList<PendingNotification>> PlanAsync(WatchEvent watchEvent, IReadOnlyList<PlannedMatch> matches,
        WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(configuration);
        var contributions = await ContributionsAsync(watchEvent, matches, configuration, now, NoExemptions, cancellationToken).ConfigureAwait(false);
        return [.. contributions.Select(c => Row(watchEvent, c.Value, configuration, now))];
    }

    /// <summary>
    /// The treatment of one (event, target) from <paramref name="matches"/> – every rule that matches the event now (13.3:
    /// a released held row, or a row whose rule left its delivery hours or the configuration). Null when none of them
    /// notifies the target now. <paramref name="plannedRuleId"/> – the row's rule – passed its repeat suppression when the
    /// row was planned and is not checked again.
    /// </summary>
    public async ValueTask<PendingNotification?> PlanTargetAsync(WatchEvent watchEvent, IReadOnlyList<PlannedMatch> matches, string targetKey,
        string? plannedRuleId, WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(configuration);
        var exempt = plannedRuleId is null ? NoExemptions : new HashSet<string>([plannedRuleId], StringComparer.OrdinalIgnoreCase);
        var contributions = await ContributionsAsync(watchEvent, matches, configuration, now, exempt, cancellationToken).ConfigureAwait(false);
        return contributions.TryGetValue(targetKey, out var forTarget) ? Row(watchEvent, forTarget, configuration, now) : null;
    }

    /// <inheritdoc/>
    ValueTask<IReadOnlyList<PendingNotification>> IRuleMatchPlanner.PlanAsync(RuleEvaluationResult evaluation, WatchdogConfiguration configuration,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return PlanAsync(evaluation.Event, Planned(evaluation), configuration, now, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every target the rules that match now notify is planned with all of them (13.2 #1). A target with a waiting row takes
    /// the new treatment (earlier due time kept): merged – and inserted again if the row was flushed meanwhile, the dispatch
    /// job then drops it when the alert covers it – when an added rule notifies it, otherwise updated while it waits. A
    /// target without a waiting row is planned only when an added rule notifies it and the event has no alert for it that
    /// covers the plan (<see cref="AlertCoverage"/>, per event: not lost, the event shown with at least the planned severity,
    /// with its actor or followed by the attribution follow-up); a higher severity, or an alert sent with the actor pending
    /// and no follow-up to come, is new information. Waiting rows whose target no rule notifies any more are dropped. The
    /// rules of the waiting rows passed their repeat suppression when they were planned and are not checked again.
    /// </remarks>
    async ValueTask<ReevaluationPlan> IRuleMatchPlanner.PlanReevaluationAsync(RuleEvaluationResult evaluation, IReadOnlySet<string> addedRuleIds,
        IReadOnlyList<PendingNotification> waiting, WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(addedRuleIds);
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(configuration);
        var watchEvent = evaluation.Event;
        var waitingRows = waiting.Where(w => w.EventId == watchEvent.Id && !w.IsFollowUp).ToList();
        var exempt = waitingRows.Select(w => w.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contributions = await ContributionsAsync(watchEvent, Planned(evaluation), configuration, now, exempt, cancellationToken).ConfigureAwait(false);

        var insert = new List<PendingNotification>();
        var merge = new List<PendingNotification>();
        var update = new List<PendingNotification>();
        Func<string, Severity, bool>? covered = null;
        foreach (var (key, forTarget) in contributions)
        {
            var row = Row(watchEvent, forTarget, configuration, now);
            var notifiedByAddedRule = forTarget.Any(c => addedRuleIds.Contains(c.Match.Rule.Id));
            if (waitingRows.FirstOrDefault(w => string.Equals(w.Target.Key, key, StringComparison.Ordinal)) is { } current)
            {
                var replanned = row with { Id = current.Id, CreatedAt = current.CreatedAt, DueAt = Min(current.DueAt, row.DueAt) };
                (notifiedByAddedRule ? merge : update).Add(replanned);
                continue;
            }

            if (!notifiedByAddedRule)
            {
                continue;   // notified before (or not at all): a rule that matched already is never planned again
            }

            covered ??= await AlertCoverage.CoverageAsync(_outbox, watchEvent.Id, configuration, cancellationToken).ConfigureAwait(false);
            if (!covered(key, row.Severity))
            {
                insert.Add(row);
            }
        }

        return new ReevaluationPlan
        {
            Insert = insert,
            Merge = merge,
            Update = update,
            Drop = [.. waitingRows.Where(w => !contributions.ContainsKey(w.Target.Key)).Select(w => w.Id)],
        };
    }

    private static List<PlannedMatch> Planned(RuleEvaluationResult evaluation) =>
        [.. evaluation.Matches.Select(m => new PlannedMatch(m.Rule, m.NotificationSeverity, m.IsProvisional))];

    /// <summary>Per target (in first-seen order: configuration order of the rules, then of their targets) the immediate
    /// matches that may notify it now.</summary>
    private async Task<Dictionary<string, List<Contribution>>> ContributionsAsync(WatchEvent watchEvent, IReadOnlyList<PlannedMatch> matches,
        WatchdogConfiguration configuration, DateTimeOffset now, IReadOnlySet<string> repeatExempt, CancellationToken cancellationToken)
    {
        var byTarget = new Dictionary<string, List<Contribution>>(StringComparer.Ordinal);
        var immediate = matches.Where(m => m.Rule.Delivery?.Immediate == true).ToList();
        if (immediate.Count == 0)
        {
            return byTarget;
        }

        var rolloutActive = await _rollout.IsActiveAsync(configuration, watchEvent.Domain, now, cancellationToken).ConfigureAwait(false);
        var settings = configuration.Notifications ?? new NotificationSettings();
        foreach (var match in immediate)
        {
            var rule = match.Rule;
            if ((rolloutActive && !RolloutGate.AllowsImmediate(rule.Id))
                || !IsWithinDeliveryHours(rule, match.NotificationSeverity, configuration, now)
                || (!repeatExempt.Contains(rule.Id) && await IsRepeatAsync(watchEvent, rule, now, cancellationToken).ConfigureAwait(false)))
            {
                continue;
            }

            var contribution = new Contribution(match, NeedsAttributionHold(watchEvent, match), AggregationPolicy.Window(rule, settings, match.NotificationSeverity));
            foreach (var target in await _targets.ResolveAsync(watchEvent, rule, configuration, cancellationToken).ConfigureAwait(false))
            {
                if (!byTarget.TryGetValue(target.Key, out var forTarget))
                {
                    byTarget[target.Key] = forTarget = [];
                }

                forTarget.Add(contribution with { Target = target });
            }
        }

        return byTarget;
    }

    /// <summary>
    /// The row of one target: the contributions that notify at once (no attribution hold) decide when any exists – the
    /// target is notified now and the held ones would only repeat it –, otherwise all of them (held until the actor is
    /// known or the grace ends). Highest notification severity, shortest window, the earliest rule as rule and bucket.
    /// </summary>
    private static PendingNotification Row(WatchEvent watchEvent, List<Contribution> forTarget, WatchdogConfiguration configuration, DateTimeOffset now)
    {
        var immediate = forTarget.Where(c => !c.Hold).ToList();
        var deciding = immediate.Count > 0 ? immediate : forTarget;
        var lead = deciding[0];
        var settings = configuration.Notifications ?? new NotificationSettings();
        return new PendingNotification
        {
            EventId = watchEvent.Id,
            RuleId = lead.Match.Rule.Id,
            Target = lead.Target!,
            Severity = deciding.Max(c => c.Match.NotificationSeverity),
            BucketKey = AggregationPolicy.BucketKey(lead.Match.Rule.Id, lead.Target!, watchEvent.Id),
            DueAt = now + deciding.Min(c => c.Window),
            HoldForAttributionUntil = immediate.Count > 0 ? null : watchEvent.DetectedAt + settings.AttributionGrace,
            CreatedAt = now,
        };
    }

    private async Task<bool> IsRepeatAsync(WatchEvent watchEvent, RuleSettings rule, DateTimeOffset now, CancellationToken cancellationToken) =>
        rule.Delivery?.RepeatSuppression is { } period && period > TimeSpan.Zero && watchEvent.Object.ObjectGuid is { } guid
        && await _outbox.WasNotifiedAboutObjectAsync(rule.Id, guid, now - period, cancellationToken).ConfigureAwait(false);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>One rule that may notify one target now.</summary>
    private sealed record Contribution(PlannedMatch Match, bool Hold, TimeSpan Window)
    {
        public NotificationTarget? Target { get; init; }
    }
}
