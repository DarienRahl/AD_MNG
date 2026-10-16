using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Follow-ups once an attribution became final (sections 11.4 #9, 11.5 and 13.2 #6): for every alert message of the
/// event that was sent with the actor pending and has severity ≥ High, a pending follow-up for the same target, due now
/// and replying to that message (same e-mail thread, Teams "Uzupełnienie", webhook <c>ad.attribution.updated</c>) – only
/// with <c>Notifications.FollowUpWhenAttributed</c>. The AttributionEngine adds the rows (<c>INotificationQueueStore.AddAsync</c>;
/// duplicates are ignored) through <see cref="IAttributionFollowUpPlanner"/>.
/// </summary>
public sealed class FollowUpPlanner : IAttributionFollowUpPlanner
{
    private readonly IOutboxStore _outbox;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly TimeProvider _timeProvider;

    public FollowUpPlanner(IOutboxStore outbox, IWatchdogConfigProvider configProvider, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _outbox = outbox;
        _configProvider = configProvider;
        _timeProvider = timeProvider;
    }

    /// <summary>The follow-up rows for <paramref name="attributedEvent"/> (its attribution is final).</summary>
    public async Task<IReadOnlyList<PendingNotification>> PlanAsync(WatchEvent attributedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributedEvent);
        var configuration = _configProvider.Current.Configuration;
        if (configuration.Notifications?.FollowUpWhenAttributed == false || attributedEvent.Attribution?.Status == AttributionStatus.Pending)
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow();
        var messages = await _outbox.GetMessagesForEventAsync(attributedEvent.Id, cancellationToken).ConfigureAwait(false);
        var rows = new List<PendingNotification>();
        foreach (var message in messages)
        {
            if (message.Kind != DeliveryKind.Alert || !message.SentWithPendingAttribution || message.Severity < Severity.High)
            {
                continue;
            }

            var target = Target(message, configuration);
            rows.Add(new PendingNotification
            {
                EventId = attributedEvent.Id,
                RuleId = message.RuleId ?? string.Empty,
                Target = target,
                Severity = message.Severity,
                BucketKey = AggregationPolicy.FollowUpBucketKey(message.MessageId, target),
                DueAt = now,
                IsFollowUp = true,
                ReplyToMessageId = message.MessageId,
                CreatedAt = now,
            });
        }

        return rows;
    }

    /// <summary>The target an outbox message was rendered for (detail level from the current configuration).</summary>
    public static NotificationTarget Target(OutboxMessage message, WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(configuration);
        if (message.ChannelType != DeliveryChannelType.Email)
        {
            var channel = (configuration.Channels ?? []).FirstOrDefault(c => c is not null && string.Equals(c.Id, message.ChannelId, StringComparison.OrdinalIgnoreCase));
            return new NotificationTarget
            {
                ChannelType = message.ChannelType,
                ChannelId = message.ChannelId,
                DetailLevel = channel?.DetailLevel ?? (message.ChannelType == DeliveryChannelType.Webhook ? DetailLevel.Minimal : DetailLevel.Standard),
            };
        }

        var target = new NotificationTarget { ChannelType = DeliveryChannelType.Email, ChannelId = message.ChannelId, Recipients = message.Recipients, DetailLevel = DetailLevel.Minimal };
        var group = (configuration.RecipientGroups ?? [])
            .Select(g => NotificationTargetResolver.EmailTarget(configuration, g.Id))
            .FirstOrDefault(t => t is not null && t.Key == target.Key);
        return group ?? target;
    }
}
