using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IWatchdogJob"/> "dispatch" (section 13.3): flushes due buckets of pending notifications into the outbox.
/// Held items are released when the attribution is final or the grace period ended and are re-matched with the event's
/// final state (<see cref="IHeldNotificationMatcher"/>; delivery hours and rollout mode recomputed) – items that no
/// longer qualify are dropped. A bucket whose rule reached its hourly cap on the channel waits 15 minutes (Critical is
/// never throttled). At most <c>MaxItemsPerMessage</c> items are rendered, the rest is announced as "+N kolejnych"; all
/// ready items are completed with the message in one store call. Wakes on NotificationsQueued and AttributionUpdated.
/// </summary>
public sealed partial class NotificationDispatchJob : IWatchdogJob
{
    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.NotificationsQueued, WatchdogSignal.AttributionUpdated];

    private readonly INotificationQueueStore _queue;
    private readonly IEventStore _events;
    private readonly IOutboxStore _outbox;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly IHeldNotificationMatcher _matcher;
    private readonly NotificationPlanner _planner;
    private readonly RolloutGate _rollout;
    private readonly ThrottlePolicy _throttle;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationDispatchJob> _logger;

    public NotificationDispatchJob(INotificationQueueStore queue, IEventStore events, IOutboxStore outbox, IWatchdogConfigProvider configProvider,
        IHeldNotificationMatcher matcher, NotificationPlanner planner, RolloutGate rollout, ThrottlePolicy throttle, IWatchdogSignals signals,
        TimeProvider timeProvider, ILogger<NotificationDispatchJob> logger)
    {
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
        _rollout = rollout;
        _throttle = throttle;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
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
            var pending = await _queue.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var nextWake = now + DefaultInterval;
            var enqueued = 0;
            foreach (var bucket in pending.GroupBy(p => p.BucketKey, StringComparer.Ordinal))
            {
                var dueAt = bucket.Min(p => p.DueAt);
                if (dueAt > now)
                {
                    nextWake = Min(nextWake, dueAt);
                    continue;
                }

                var outcome = await FlushAsync([.. bucket.OrderBy(p => p.Id)], configuration, now, cancellationToken).ConfigureAwait(false);
                enqueued += outcome.Enqueued ? 1 : 0;
                if (outcome.WakeAt is { } wake)
                {
                    nextWake = Min(nextWake, wake);
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
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ready = new List<(PendingNotification Item, WatchEvent Event)>();
        var drop = new List<long>();
        DateTimeOffset? wakeAt = null;
        foreach (var item in items)
        {
            var watchEvent = await _events.GetAsync(item.EventId, cancellationToken).ConfigureAwait(false);
            if (watchEvent is null)
            {
                drop.Add(item.Id);
                continue;
            }

            if (item.IsFollowUp || item.HoldForAttributionUntil is null)
            {
                ready.Add((item, watchEvent));
                continue;
            }

            var graceExpired = item.HoldForAttributionUntil <= now;
            if (watchEvent.Attribution?.Status == AttributionStatus.Pending && !graceExpired)
            {
                wakeAt = Min(wakeAt, item.HoldForAttributionUntil.Value);
                continue;
            }

            if (await ReleaseAsync(item, watchEvent, configuration, now, graceExpired, cancellationToken).ConfigureAwait(false) is { } released)
            {
                ready.Add((released, watchEvent));
            }
            else
            {
                drop.Add(item.Id);
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
        var ids = ready.Select(r => r.Item.Id).ToList();
        if (!first.IsFollowUp && rule is null)
        {
            await _queue.DropAsync(ids, cancellationToken).ConfigureAwait(false);
            return (false, wakeAt);
        }

        var settings = configuration.Notifications ?? new NotificationSettings();
        var severity = ready.Max(r => r.Item.Severity);
        if (!first.IsFollowUp && rule is not null
            && await _throttle.IsExceededAsync(rule.Id, ThrottlePolicy.Cap(rule, settings), first.Target, severity, now, cancellationToken).ConfigureAwait(false))
        {
            await _queue.RescheduleAsync(ids, now + ThrottlePolicy.RescheduleDelay, cancellationToken).ConfigureAwait(false);
            LogThrottled(rule.Id);
            return (false, wakeAt);
        }

        var ordered = ready.Select(r => r.Event).DistinctBy(e => e.Id).OrderBy(e => e.DetectedAt).ThenBy(e => e.Id).ToList();
        var cap = AggregationPolicy.MaxItems(settings);
        var shown = ordered.Take(cap).ToList();
        var message = first.IsFollowUp
            ? await FollowUpAsync(first, rule, shown, configuration, now, cancellationToken).ConfigureAwait(false)
            : NotificationRenderer.Alert(first.Target, rule, shown, ordered.Count - shown.Count, [.. ordered.Select(e => e.Id)], configuration, now);
        await _queue.CompleteAsync(ids, message, cancellationToken).ConfigureAwait(false);
        return (true, wakeAt);
    }

    /// <summary>A released held item with its re-matched severity, or null when it no longer qualifies.</summary>
    private async Task<PendingNotification?> ReleaseAsync(PendingNotification item, WatchEvent watchEvent, WatchdogConfiguration configuration,
        DateTimeOffset now, bool graceExpired, CancellationToken cancellationToken)
    {
        if (FindRule(configuration, item.RuleId) is not { } rule || _matcher.Rematch(watchEvent, rule, configuration, now, graceExpired) is not { } match)
        {
            return null;
        }

        if (!_planner.IsWithinDeliveryHours(rule, match.NotificationSeverity, configuration, now)
            || (!RolloutGate.AllowsImmediate(rule.Id)
                && await _rollout.IsActiveAsync(configuration, watchEvent.Domain, now, cancellationToken).ConfigureAwait(false)))
        {
            return null;
        }

        return item with { Severity = match.NotificationSeverity };
    }

    private async Task<OutboxMessage> FollowUpAsync(PendingNotification first, RuleSettings? rule, IReadOnlyList<WatchEvent> events,
        WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var replyTo = first.ReplyToMessageId ?? string.Empty;
        var original = (await _outbox.GetMessagesForEventAsync(events[0].Id, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(m => string.Equals(m.MessageId, replyTo, StringComparison.Ordinal));
        var originalRule = FindRule(configuration, original?.RuleId) ?? rule;
        var originalEvents = new List<WatchEvent>();
        foreach (var id in original?.EventIds ?? [])
        {
            if (await _events.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } e)
            {
                originalEvents.Add(e);
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
