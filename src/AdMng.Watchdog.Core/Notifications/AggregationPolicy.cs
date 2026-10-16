using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Aggregation of immediate notifications (section 13.2 #5): bucket keys, flush delays and the item cap of one message.
/// E-mail and Teams aggregate per rule and target (<c>ruleId|target.Key</c>); webhooks send one POST per event
/// (<c>eventId|target.Key</c>); follow-ups aggregate per original message (<c>followup|messageId|target.Key</c>).
/// </summary>
public static class AggregationPolicy
{
    /// <summary>Prefix of follow-up bucket keys.</summary>
    public const string FollowUpPrefix = "followup|";

    /// <summary>The bucket key of a new (non-follow-up) pending notification.</summary>
    public static string BucketKey(string ruleId, NotificationTarget target, long eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentNullException.ThrowIfNull(target);
        return target.ChannelType == DeliveryChannelType.Webhook
            ? eventId.ToString(CultureInfo.InvariantCulture) + "|" + target.Key
            : ruleId + "|" + target.Key;
    }

    /// <summary>The bucket key of a follow-up to the message <paramref name="replyToMessageId"/>.</summary>
    public static string FollowUpBucketKey(string replyToMessageId, NotificationTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyToMessageId);
        ArgumentNullException.ThrowIfNull(target);
        return FollowUpPrefix + replyToMessageId + "|" + target.Key;
    }

    /// <summary>How long a bucket collects items: <see cref="NotificationSettings.HighSeverityFlushDelay"/> for
    /// severity ≥ High, else the rule's aggregation window or the global one.</summary>
    public static TimeSpan Window(RuleSettings? rule, NotificationSettings? settings, Severity severity)
    {
        var notifications = settings ?? new NotificationSettings();
        var window = severity >= Severity.High
            ? notifications.HighSeverityFlushDelay
            : rule?.Delivery?.AggregationWindow ?? notifications.AggregationWindow;
        return window < TimeSpan.Zero ? TimeSpan.Zero : window;
    }

    /// <summary>The most items rendered into one message (at least 1).</summary>
    public static int MaxItems(NotificationSettings? settings) => Math.Max(1, (settings ?? new NotificationSettings()).MaxItemsPerMessage);
}
