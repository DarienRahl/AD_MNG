using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IWatchdogJob"/> "dispatch" (section 13.3): flushes due buckets of pending notifications into the outbox.
/// Held items are released when the attribution is final or the grace period ended; their target is re-planned from
/// every rule that matches the event's final state now (<see cref="IHeldNotificationMatcher.MatchAll"/>,
/// <see cref="NotificationPlanner.PlanTargetAsync"/>; delivery hours and rollout mode recomputed): the strongest treatment
/// of those rules, and a target whose rule no longer matches passes to the next rule that does – items no rule notifies
/// any more are dropped. Every other (non-follow-up) item whose rule's delivery hours ended while it waited (aggregation
/// window, throttling) or that left the configuration is re-planned the same way – dropped when no rule delivers now (the
/// digest reports it). A re-planned item led by another rule moves to that rule's bucket and is flushed in a second pass
/// of the same run. A row whose event already has an alert for the target
/// that covers it (<see cref="AlertCoverage"/>, per event) is dropped right before the bucket is completed – this job is
/// the only writer of alerts, so a row planned while that alert was being completed never becomes a second one; every
/// alert records how it showed each event (<see cref="OutboxMessage.EventStates"/>). A bucket whose rule reached its hourly cap on the
/// channel waits 15 minutes (Critical is never throttled). At most <c>MaxItemsPerMessage</c> items are rendered, the rest
/// is announced as "+N kolejnych"; all ready items are completed with the message in one store call. A follow-up waits
/// (<see cref="FollowUpWait"/>) until its original message was sent and is dropped when the original went Dead or
/// Cancelled or the event already has a follow-up for the target. After an alert sent with a pending actor, events whose
/// attribution became final meanwhile get their follow-ups planned here (the AttributionEngine planned them before the
/// alert was in the outbox). Wakes on NotificationsQueued and AttributionUpdated.
/// </summary>
public sealed partial class NotificationDispatchJob : IWatchdogJob
{
    /// <summary>How long a follow-up waits before it checks again whether its original message was sent.</summary>
    public static readonly TimeSpan FollowUpWait = TimeSpan.FromMinutes(1);

    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.NotificationsQueued, WatchdogSignal.AttributionUpdated];

    private readonly INotificationQueueStore _queue;
    private readonly IEventStore _events;
    private readonly IOutboxStore _outbox;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly IHeldNotificationMatcher _matcher;
    private readonly NotificationPlanner _planner;
    private readonly ThrottlePolicy _throttle;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationDispatchJob> _logger;
    private readonly FollowUpPlanner _followUps;

    public NotificationDispatchJob(INotificationQueueStore queue, IEventStore events, IOutboxStore outbox, IWatchdogConfigProvider configProvider,
        IHeldNotificationMatcher matcher, NotificationPlanner planner, RolloutGate rollout, ThrottlePolicy throttle, IWatchdogSignals signals,
        TimeProvider timeProvider, ILogger<NotificationDispatchJob> logger)
    {
        // Rollout mode is applied by the planner's re-plan (PlanTargetAsync); the gate stays a dependency of the job.
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _events = events;
        _outbox = outbox;
        _configProvider = configProvider;
        _matcher = matcher;
        _planner = planner;
        _throttle = throttle;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
        _followUps = new FollowUpPlanner(outbox, configProvider, timeProvider);
    }

    /// <inheritdoc/>
    public string Name => "dispatch";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var configuration = _configProvider.Current.Configuration;
            var nextWake = now + DefaultInterval;
            var enqueued = 0;

            // A re-planned row that another rule leads now moves to that rule's bucket (due at once): a second pass over
            // the stored rows flushes it with that bucket.
            var moved = new List<long>();
            for (var pass = 0; pass < 2 && (pass == 0 || moved.Count > 0); pass++)
            {
                moved.Clear();
                var pending = await _queue.GetAllAsync(cancellationToken).ConfigureAwait(false);
                foreach (var bucket in pending.GroupBy(p => p.BucketKey, StringComparer.Ordinal))
                {
                    var dueAt = bucket.Min(p => p.DueAt);
                    if (dueAt > now)
                    {
                        nextWake = Min(nextWake, dueAt);
                        continue;
                    }

                    var outcome = await FlushAsync([.. bucket.OrderBy(p => p.Id)], configuration, now, moved, cancellationToken).ConfigureAwait(false);
                    enqueued += outcome.Enqueued ? 1 : 0;
                    if (outcome.WakeAt is { } wake)
                    {
                        nextWake = Min(nextWake, wake);
                    }
                }
            }

            if (enqueued > 0)
            {
                _signals.Raise(WatchdogSignal.OutboxEnqueued);
                LogEnqueued(enqueued);
            }

            var delay = nextWake - now;
            return new JobResult(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "notification dispatch");
            return new JobResult(DefaultInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    private async Task<(bool Enqueued, DateTimeOffset? WakeAt)> FlushAsync(IReadOnlyList<PendingNotification> items, WatchdogConfiguration configuration,
        DateTimeOffset now, List<long> moved, CancellationToken cancellationToken)
    {
        var ready = new List<(PendingNotification Item, WatchEvent Event)>();
        var drop = new List<long>();
        var settings = configuration.Notifications ?? new NotificationSettings();
        DateTimeOffset? wakeAt = null;
        foreach (var item in items)
        {
            var watchEvent = await _events.GetAsync(item.EventId, cancellationToken).ConfigureAwait(false);
            if (watchEvent is null)
            {
                drop.Add(item.Id);
                continue;
            }

            if (item.IsFollowUp)
            {
                ready.Add((item, watchEvent));
                continue;
            }

            var held = item.HoldForAttributionUntil is not null;
            var graceExpired = (item.HoldForAttributionUntil ?? watchEvent.DetectedAt + settings.AttributionGrace) <= now;
            if (held && watchEvent.Attribution?.Status == AttributionStatus.Pending && !graceExpired)
            {
                wakeAt = Min(wakeAt, item.HoldForAttributionUntil!.Value);
                continue;
            }

            // Delivery hours were checked at planning; the aggregation window or throttling may have run past them.
            if (!held && FindRule(configuration, item.RuleId) is { } itemRule && _planner.IsWithinDeliveryHours(itemRule, item.Severity, configuration, now))
            {
                ready.Add((item, watchEvent));
                continue;
            }

            // A released held row, or one whose rule left its delivery hours or the configuration: re-planned from every rule
            // that matches the event now (13.3) – the strongest treatment, and a target never stays with a rule that no longer
            // matches while another one still does.
            switch (await ReplanAsync(item, watchEvent, configuration, now, graceExpired, cancellationToken).ConfigureAwait(false))
            {
                case null:
                    drop.Add(item.Id);
                    break;
                case { HoldForAttributionUntil: { } stillHeld } waiting:
                    // Only rules that wait for the actor are left (a row released by its delivery hours): it waits again.
                    await _queue.ReplanAsync([waiting], insertMissing: false, cancellationToken).ConfigureAwait(false);
                    wakeAt = Min(wakeAt, stillHeld);
                    break;
                case var released when !string.Equals(released.BucketKey, item.BucketKey, StringComparison.Ordinal):
                    // Another rule leads the target now: the row moves to that rule's bucket (still due) and goes out with it
                    // in the second pass of this run.
                    await _queue.ReplanAsync([released], insertMissing: false, cancellationToken).ConfigureAwait(false);
                    moved.Add(released.Id);
                    break;
                case var released:
                    ready.Add((released, watchEvent));
                    break;
            }
        }

        // One alert per event and target (13.2 #1): a row planned while an earlier alert of its event was being completed
        // (the re-evaluation after attribution, the second planning of a configuration change) is a repeat when that alert
        // covers it. This job is the only writer of alerts and completes one bucket at a time, so the check cannot race.
        foreach (var (item, _) in ready.Where(r => !r.Item.IsFollowUp).ToList())
        {
            if (await AlertCoverage.IsCoveredAsync(_outbox, item.EventId, item.Target.Key, item.Severity, configuration, cancellationToken).ConfigureAwait(false))
            {
                drop.Add(item.Id);
                ready.RemoveAll(r => r.Item.Id == item.Id);
            }
        }

        if (drop.Count > 0)
        {
            await _queue.DropAsync(drop, cancellationToken).ConfigureAwait(false);
            LogDropped(drop.Count);
        }

        if (ready.Count == 0)
        {
            return (false, wakeAt);
        }

        var first = ready[0].Item;
        var rule = FindRule(configuration, first.RuleId);
        if (!first.IsFollowUp && rule is null)
        {
            await _queue.DropAsync([.. ready.Select(r => r.Item.Id)], cancellationToken).ConfigureAwait(false);
            return (false, wakeAt);
        }

        OutboxMessage? original = null;
        if (first.IsFollowUp)
        {
            var gate = await FollowUpGateAsync(ready, cancellationToken).ConfigureAwait(false);
            if (gate.Duplicates.Count > 0)
            {
                await _queue.DropAsync(gate.Duplicates, cancellationToken).ConfigureAwait(false);
                LogDropped(gate.Duplicates.Count);
                ready.RemoveAll(r => gate.Duplicates.Contains(r.Item.Id));
            }

            if (ready.Count == 0)
            {
                return (false, wakeAt);
            }

            switch (gate.OriginalStatus)
            {
                case OutboxStatus.Dead or OutboxStatus.Cancelled:
                    // The recipients never got the alert: a reply to it would be the first thing they see.
                    await _queue.DropAsync([.. ready.Select(r => r.Item.Id)], cancellationToken).ConfigureAwait(false);
                    LogDropped(ready.Count);
                    return (false, wakeAt);
                case OutboxStatus.Pending or OutboxStatus.Sending or OutboxStatus.Retrying:
                    // The original is still waiting (e.g. 421, retry in 2 h): the follow-up must not overtake it.
                    var retryAt = now + FollowUpWait;
                    await _queue.RescheduleAsync([.. ready.Select(r => r.Item.Id)], retryAt, cancellationToken).ConfigureAwait(false);
                    return (false, Min(wakeAt, retryAt));
            }

            original = gate.Original;
        }

        var ids = ready.Select(r => r.Item.Id).ToList();
        var severity = ready.Max(r => r.Item.Severity);
        if (!first.IsFollowUp && rule is not null
            && await _throttle.IsExceededAsync(rule.Id, ThrottlePolicy.Cap(rule, settings), first.Target, severity, now, cancellationToken).ConfigureAwait(false))
        {
            await _queue.RescheduleAsync(ids, now + ThrottlePolicy.RescheduleDelay, cancellationToken).ConfigureAwait(false);
            LogThrottled(rule.Id);
            return (false, wakeAt);
        }

        // Rendered with the notification severity of the row (rule override or re-resolved event severity).
        var ordered = ready.Select(r => r.Event with { Severity = r.Item.Severity }).DistinctBy(e => e.Id).OrderBy(e => e.DetectedAt).ThenBy(e => e.Id).ToList();
        var cap = AggregationPolicy.MaxItems(settings);
        var shown = ordered.Take(cap).ToList();
        var message = first.IsFollowUp
            ? await FollowUpAsync(first, original, rule, shown, configuration, now, cancellationToken).ConfigureAwait(false)
            : NotificationRenderer.Alert(first.Target, rule, shown, ordered.Count - shown.Count, [.. ordered.Select(e => e.Id)], configuration, now) with
            {
                // What the recipients know about each event (an aggregated alert's events differ): the coverage of later plans.
                EventStates = [.. ordered.Select(e => new OutboxEventState(e.Id, e.Severity, e.Attribution?.Status == AttributionStatus.Pending))],
            };
        await _queue.CompleteAsync(ids, message, cancellationToken).ConfigureAwait(false);
        if (message.RuleId is { } ruleId)
        {
            _throttle.RecordEnqueued(ruleId, message.ChannelId, now);
        }

        if (!first.IsFollowUp && message.SentWithPendingAttribution)
        {
            await PlanMissedFollowUpsAsync(message, ordered, cancellationToken).ConfigureAwait(false);
        }

        return (true, wakeAt);
    }

    /// <summary>
    /// The AttributionEngine plans follow-ups right after it committed an attribution – from the messages already in the
    /// outbox. An attribution committed after this job read the event (pending) but before the alert was completed found no
    /// message, so the follow-ups are planned here once the alert is stored; duplicates of rows still queued are ignored
    /// by the store and a follow-up already sent is dropped at flush.
    /// </summary>
    private async Task PlanMissedFollowUpsAsync(OutboxMessage message, IReadOnlyList<WatchEvent> rendered, CancellationToken cancellationToken)
    {
        var rows = new List<PendingNotification>();
        foreach (var pending in rendered.Where(e => e.Attribution?.Status == AttributionStatus.Pending))
        {
            if (await _events.GetAsync(pending.Id, cancellationToken).ConfigureAwait(false) is not { } current
                || current.Attribution?.Status == AttributionStatus.Pending)
            {
                continue;
            }

            rows.AddRange((await _followUps.PlanAsync(current, cancellationToken).ConfigureAwait(false))
                .Where(r => string.Equals(r.ReplyToMessageId, message.MessageId, StringComparison.Ordinal)));
        }

        if (rows.Count > 0)
        {
            await _queue.AddAsync(rows, cancellationToken).ConfigureAwait(false);
            _signals.Raise(WatchdogSignal.NotificationsQueued);
        }
    }

    /// <summary>The original message of a follow-up bucket with its delivery status (null status: unknown, e.g. purged –
    /// the follow-up goes out), and the items whose event already has a follow-up for their target.</summary>
    private async Task<(OutboxMessage? Original, OutboxStatus? OriginalStatus, List<long> Duplicates)> FollowUpGateAsync(
        IReadOnlyList<(PendingNotification Item, WatchEvent Event)> ready, CancellationToken cancellationToken)
    {
        var replyTo = ready[0].Item.ReplyToMessageId;
        var duplicates = new List<long>();
        OutboxMessage? original = null;
        var originalEventId = 0L;
        foreach (var (item, _) in ready)
        {
            var messages = await _outbox.GetMessagesForEventAsync(item.EventId, cancellationToken).ConfigureAwait(false);
            if (original is null && messages.FirstOrDefault(m => string.Equals(m.MessageId, replyTo, StringComparison.Ordinal)) is { } found)
            {
                original = found;
                originalEventId = item.EventId;
            }

            if (messages.Any(m => m.Kind == DeliveryKind.FollowUp && string.Equals(m.TargetKey, item.Target.Key, StringComparison.Ordinal)))
            {
                duplicates.Add(item.Id);
            }
        }

        if (original is null)
        {
            return (null, null, duplicates);
        }

        var status = (await _outbox.GetDeliveriesForEventAsync(originalEventId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(d => d.OutboxId == original.Id)?.Status;
        return (original, status, duplicates);
    }

    /// <summary>
    /// The item's target re-planned from every rule that matches the event now (<see cref="IHeldNotificationMatcher.MatchAll"/>
    /// with the final own severity, tags and actor; delivery hours and rollout mode recomputed), or null when no rule notifies
    /// the target any more. A row that may go out has no hold, keeps its id, creation and due time; one that has to wait for
    /// the actor again keeps the new hold.
    /// </summary>
    private async Task<PendingNotification?> ReplanAsync(PendingNotification item, WatchEvent watchEvent, WatchdogConfiguration configuration,
        DateTimeOffset now, bool graceExpired, CancellationToken cancellationToken)
    {
        var matches = _matcher.MatchAll(watchEvent, configuration, now, graceExpired);
        var replanned = await _planner.PlanTargetAsync(watchEvent, matches, item.Target.Key, item.RuleId, configuration, now, cancellationToken)
            .ConfigureAwait(false);
        if (replanned is null)
        {
            return null;
        }

        var waits = replanned.HoldForAttributionUntil is { } hold && hold > now && watchEvent.Attribution?.Status == AttributionStatus.Pending;
        return replanned with
        {
            Id = item.Id,
            CreatedAt = item.CreatedAt,
            DueAt = item.DueAt,
            HoldForAttributionUntil = waits ? replanned.HoldForAttributionUntil : null,
        };
    }

    private async Task<OutboxMessage> FollowUpAsync(PendingNotification first, OutboxMessage? original, RuleSettings? rule, IReadOnlyList<WatchEvent> events,
        WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var replyTo = first.ReplyToMessageId ?? string.Empty;
        var originalRule = FindRule(configuration, original?.RuleId) ?? rule;
        var originalEvents = new List<WatchEvent>();
        foreach (var id in original?.EventIds ?? [])
        {
            if (await _events.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } e)
            {
                originalEvents.Add(e with { Severity = original!.Severity });     // the severity the alert was sent with
            }
        }

        var subject = NotificationRenderer.OriginalSubject(originalEvents.Count > 0 ? originalEvents : events, originalRule, configuration, first.Target.DetailLevel);
        return NotificationRenderer.FollowUp(first.Target, originalRule, events, replyTo.Length > 0 ? replyTo : Guid.Empty.ToString("N"), subject, configuration, now);
    }

    private static RuleSettings? FindRule(WatchdogConfiguration configuration, string? ruleId) =>
        string.IsNullOrEmpty(ruleId)
            ? null
            : (configuration.Rules ?? []).FirstOrDefault(r => r is not null && string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value < b ? value : b;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Enqueued {Count} notification messages.")]
    private partial void LogEnqueued(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped {Count} pending notifications that no longer qualify.")]
    private partial void LogDropped(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rule {RuleId} reached its hourly message cap; the bucket waits 15 minutes.")]
    private partial void LogThrottled(string ruleId);
}
