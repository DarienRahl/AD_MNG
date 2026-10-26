using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Hourly cap per rule × channel (section 13.3): when the messages of a rule enqueued for a channel in the last hour reach
/// <c>rule.Delivery.MaxMessagesPerHour ?? Notifications.MaxMessagesPerRulePerHour</c>, the bucket is rescheduled by
/// <see cref="RescheduleDelay"/> and keeps accumulating. Critical notifications are never throttled.
/// </summary>
/// <remarks>The count is the larger of the messages <b>sent</b> in the last hour (<see cref="IOutboxStore.CountSentAsync"/>,
/// survives a restart) and the messages <b>enqueued</b> in the last hour (<see cref="RecordEnqueued"/>, kept in memory by
/// this singleton) – so a channel that cannot deliver (SMTP down, Teams flow broken, rate-limited) does not let a burst
/// pile up one message per flush that floods the recipients after the recovery.</remarks>
public sealed class ThrottlePolicy
{
    /// <summary>How far a throttled bucket is moved.</summary>
    public static readonly TimeSpan RescheduleDelay = TimeSpan.FromMinutes(15);

    /// <summary>The counting window of the cap.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IOutboxStore _outbox;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string RuleId, string ChannelId), List<DateTimeOffset>> _enqueued = [];

    public ThrottlePolicy(IOutboxStore outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        _outbox = outbox;
    }

    /// <summary>The effective hourly cap of <paramref name="rule"/> (at least 1).</summary>
    public static int Cap(RuleSettings? rule, NotificationSettings? settings) =>
        Math.Max(1, rule?.Delivery?.MaxMessagesPerHour ?? (settings ?? new NotificationSettings()).MaxMessagesPerRulePerHour);

    /// <summary>True when a message of <paramref name="severity"/> for <paramref name="ruleId"/> over the channel of
    /// <paramref name="target"/> must wait (cap reached in the last hour; Critical is exempt).</summary>
    public async Task<bool> IsExceededAsync(string ruleId, int cap, NotificationTarget target, Severity severity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentNullException.ThrowIfNull(target);
        if (severity >= Severity.Critical)
        {
            return false;
        }

        var since = now - Window;
        var sent = await _outbox.CountSentAsync(target.ChannelId, ruleId, since, cancellationToken).ConfigureAwait(false);
        var enqueued = CountEnqueued(ruleId, target.ChannelId, since);
        return Math.Max(sent, enqueued) >= Math.Max(1, cap);
    }

    /// <summary>Records a message of <paramref name="ruleId"/> enqueued for <paramref name="channelId"/> at
    /// <paramref name="at"/> (the dispatch job calls it after every completed bucket).</summary>
    public void RecordEnqueued(string ruleId, string channelId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        lock (_gate)
        {
            var key = Key(ruleId, channelId);
            if (!_enqueued.TryGetValue(key, out var times))
            {
                times = [];
                _enqueued[key] = times;
            }

            times.Add(at);
            Prune(at - Window);
        }
    }

    private int CountEnqueued(string ruleId, string channelId, DateTimeOffset since)
    {
        lock (_gate)
        {
            return _enqueued.TryGetValue(Key(ruleId, channelId), out var times) ? times.Count(t => t >= since) : 0;
        }
    }

    /// <summary>Drops entries outside the window (caller holds the lock).</summary>
    private void Prune(DateTimeOffset since)
    {
        foreach (var key in _enqueued.Keys.ToList())
        {
            var times = _enqueued[key];
            times.RemoveAll(t => t < since);
            if (times.Count == 0)
            {
                _enqueued.Remove(key);
            }
        }
    }

    private static (string, string) Key(string ruleId, string channelId) => (ruleId.ToLowerInvariant(), channelId.ToLowerInvariant());
}
