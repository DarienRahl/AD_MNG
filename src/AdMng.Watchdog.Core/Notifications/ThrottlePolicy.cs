using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Hourly cap per rule × channel (section 13.3): when the messages of a rule sent over a channel in the last hour reach
/// <c>rule.Delivery.MaxMessagesPerHour ?? Notifications.MaxMessagesPerRulePerHour</c>, the bucket is rescheduled by
/// <see cref="RescheduleDelay"/> and keeps accumulating. Critical notifications are never throttled.
/// </summary>
public sealed class ThrottlePolicy
{
    /// <summary>How far a throttled bucket is moved.</summary>
    public static readonly TimeSpan RescheduleDelay = TimeSpan.FromMinutes(15);

    /// <summary>The counting window of the cap.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IOutboxStore _outbox;

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

        var sent = await _outbox.CountSentAsync(target.ChannelId, ruleId, now - Window, cancellationToken).ConfigureAwait(false);
        return sent >= Math.Max(1, cap);
    }
}
