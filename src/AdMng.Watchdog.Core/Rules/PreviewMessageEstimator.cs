using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Simulates the immediate messages one rule would have sent for a sequence of matched events (rule preview, 12.5 /
/// 17.2): targets (recipient groups with addresses when SMTP is enabled, the rule's enabled channels, every enabled
/// channel with <c>AllEnabledChannels</c>), delivery hours, repeat suppression per object, aggregation (13.2 #5: one
/// bucket per e-mail/Teams target flushed at the earliest item due time; one message per event for webhooks) and the
/// hourly throttle (13.3: a bucket below Critical waits 15 minutes while the cap of the last hour is reached).
/// Rollout mode is ignored on purpose – the estimate is the "would have sent N" figure.
/// </summary>
internal static class PreviewMessageEstimator
{
    /// <summary>The send times of the estimated messages (all targets), ascending per target.</summary>
    /// <param name="rule">The rule (normalized).</param>
    /// <param name="configuration">The effective configuration (targets, notification defaults).</param>
    /// <param name="matches">Matched events with the rule's notification severity, ascending by detection time.</param>
    /// <param name="isBusinessTime">Business-time check.</param>
    public static IReadOnlyList<DateTimeOffset> Estimate(RuleSettings rule, WatchdogConfiguration configuration,
        IReadOnlyList<(WatchEvent Event, Severity Severity)> matches, Func<DateTimeOffset, bool> isBusinessTime)
    {
        var delivery = rule.Delivery ?? new RuleDelivery();
        if (!delivery.Immediate)
        {
            return [];
        }

        var notifications = configuration.Notifications ?? new NotificationSettings();
        var cap = delivery.MaxMessagesPerHour ?? notifications.MaxMessagesPerRulePerHour;
        var targets = Targets(delivery, configuration).Select(isWebhook => new PreviewTargetQueue(isWebhook, cap)).ToList();
        if (targets.Count == 0)
        {
            return [];
        }

        var lastImmediate = new Dictionary<Guid, DateTimeOffset>();
        foreach (var (watchEvent, severity) in matches)
        {
            var at = watchEvent.DetectedAt;
            targets.ForEach(t => t.FlushBefore(at));
            var always = delivery.Hours == DeliveryHours.Always || (delivery.Hours == DeliveryHours.Default && severity >= Severity.High);
            if (!always && !isBusinessTime(at))
            {
                continue;
            }

            if (delivery.RepeatSuppression is { } suppression && suppression > TimeSpan.Zero && watchEvent.Object?.ObjectGuid is { } guid)
            {
                if (lastImmediate.TryGetValue(guid, out var last) && at - last < suppression)
                {
                    continue;
                }

                lastImmediate[guid] = at;
            }

            var window = severity >= Severity.High ? notifications.HighSeverityFlushDelay : delivery.AggregationWindow ?? notifications.AggregationWindow;
            targets.ForEach(t => t.Add(at + window, severity));
        }

        targets.ForEach(t => t.FlushBefore(DateTimeOffset.MaxValue));
        return [.. targets.SelectMany(t => t.Sent)];
    }

    /// <summary>One entry per target: true for a webhook (one POST per event).</summary>
    private static IEnumerable<bool> Targets(RuleDelivery delivery, WatchdogConfiguration configuration)
    {
        var smtpEnabled = configuration.Smtp?.Enabled == true;
        var groups = (configuration.RecipientGroups ?? []).Where(g => g is not null).ToList();
        foreach (var id in (delivery.RecipientGroupIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (smtpEnabled && groups.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase) && (g.Addresses?.Count ?? 0) > 0))
            {
                yield return false;
            }
        }

        var channels = (configuration.Channels ?? []).Where(c => c is { Enabled: true }).ToList();
        var selected = channels
            .Where(c => delivery.AllEnabledChannels || (delivery.ChannelIds ?? []).Contains(c.Id, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in selected)
        {
            yield return channel.Type == ChannelType.Webhook;
        }
    }
}
