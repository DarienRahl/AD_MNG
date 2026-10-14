using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Rule matches → pending notifications (section 13.2). For every match with <c>Delivery.Immediate</c>, in
/// configuration order: (0) rollout mode lets only <c>tier0-*</c>, <c>bursts</c> and <c>health</c> through; (1) targets
/// (<see cref="NotificationTargetResolver"/>), de-duplicated across rules by (target, event) – the first rule owns the
/// row; (2) delivery hours (<c>Default</c> = Always for ≥ High, else business hours) – outside them only the digest
/// reports the event; (3) repeat suppression per object; (4) attribution hold for severity &lt; High, attribution-dependent
/// kinds and provisional actor matches while the actor is pending; (5) aggregation bucket and due time.
/// </summary>
/// <remarks>The rules area's <c>IRuleMatchPlanner.PlanAsync(RuleEvaluationResult, configuration, now, ct)</c> maps 1:1
/// onto <see cref="PlanAsync"/> (event, matches as <see cref="PlannedMatch"/>). <c>WatchdogConfigurationChanged</c> is
/// planned with the previous configuration by passing it explicitly (17.2).</remarks>
public sealed class NotificationPlanner
{
    /// <summary>Kinds whose severity is re-resolved once the attribution is final (12.2 step 3): always held while the
    /// actor is pending.</summary>
    public static IReadOnlySet<WatchEventKind> AttributionDependentKinds { get; } =
        new HashSet<WatchEventKind> { WatchEventKind.PasswordChanged, WatchEventKind.AdminCountSet, WatchEventKind.KeyCredentialChanged };

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

    /// <summary>The pending notifications of one evaluated event (empty when nothing notifies immediately).</summary>
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
        var immediate = matches.Where(m => m.Rule.Delivery?.Immediate == true).ToList();
        if (immediate.Count == 0)
        {
            return [];
        }

        var rolloutActive = await _rollout.IsActiveAsync(configuration, watchEvent.Domain, now, cancellationToken).ConfigureAwait(false);
        var settings = configuration.Notifications ?? new NotificationSettings();
        var planned = new List<PendingNotification>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in immediate)
        {
            var rule = match.Rule;
            if ((rolloutActive && !RolloutGate.AllowsImmediate(rule.Id))
                || !IsWithinDeliveryHours(rule, match.NotificationSeverity, configuration, now)
                || await IsRepeatAsync(watchEvent, rule, now, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var hold = NeedsAttributionHold(watchEvent, match) ? watchEvent.DetectedAt + settings.AttributionGrace : (DateTimeOffset?)null;
            var dueAt = now + AggregationPolicy.Window(rule, settings, match.NotificationSeverity);
            foreach (var target in await _targets.ResolveAsync(watchEvent, rule, configuration, cancellationToken).ConfigureAwait(false))
            {
                if (!seenTargets.Add(target.Key))
                {
                    continue;
                }

                planned.Add(new PendingNotification
                {
                    EventId = watchEvent.Id,
                    RuleId = rule.Id,
                    Target = target,
                    Severity = match.NotificationSeverity,
                    BucketKey = AggregationPolicy.BucketKey(rule.Id, target, watchEvent.Id),
                    DueAt = dueAt,
                    HoldForAttributionUntil = hold,
                    CreatedAt = now,
                });
            }
        }

        return planned;
    }

    private async Task<bool> IsRepeatAsync(WatchEvent watchEvent, RuleSettings rule, DateTimeOffset now, CancellationToken cancellationToken) =>
        rule.Delivery?.RepeatSuppression is { } period && period > TimeSpan.Zero && watchEvent.Object.ObjectGuid is { } guid
        && await _outbox.WasNotifiedAboutObjectAsync(rule.Id, guid, now - period, cancellationToken).ConfigureAwait(false);
}
