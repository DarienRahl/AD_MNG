using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Formatting.AdaptiveCards;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// The outbox messages of one digest (section 13.4 "Recipients"): the full e-mail per recipient group of
/// <c>Digest.RecipientGroupIds</c> (its detail level), the summary card per Teams channel and the
/// <c>watchdog.digest</c> payload per webhook channel with <c>IncludeDigest</c> among <c>Digest.ChannelIds</c> (enabled
/// channels only). Every message links the events shown in the digest.
/// </summary>
public static class DigestMessages
{
    /// <summary>The messages to enqueue for <paramref name="model"/>.</summary>
    public static IReadOnlyList<OutboxMessage> Create(DigestModel model, WatchdogConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(configuration);
        var digest = configuration.Digest ?? new DigestSettings();
        var messages = new List<OutboxMessage>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var groupId in digest.RecipientGroupIds ?? [])
        {
            if (NotificationTargetResolver.EmailTarget(configuration, groupId) is { } target && targets.Add(target.Key))
            {
                messages.Add(Message(target, model, now) with { Email = EmailDigestFormatter.Format(model, NotificationContext.Create(configuration, target.DetailLevel)) });
            }
        }

        var channelIds = new HashSet<string>(digest.ChannelIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var channel in (configuration.Channels ?? []).Where(c => c is not null && c.Enabled && channelIds.Contains(c.Id)))
        {
            var target = NotificationTargetResolver.ChannelTarget(channel);
            var context = NotificationContext.Create(configuration, target.DetailLevel);
            if (channel.Type == ChannelType.Teams)
            {
                messages.Add(Message(target, model, now) with { Http = TeamsDigestCardFormatter.Format(model, context, channel.Teams?.CardVersion) });
            }
            else if (channel.Webhook?.IncludeDigest != false)
            {
                messages.Add(Message(target, model, now) with
                {
                    Http = WebhookPayloadFormatter.Digest(model.PeriodStart, model.PeriodEnd, model.Domain, model.Counts.ToDictionary(), model.EventIds, context),
                });
            }
        }

        return messages;
    }

    private static OutboxMessage Message(NotificationTarget target, DigestModel model, DateTimeOffset now) => new()
    {
        MessageId = Guid.CreateVersion7(now).ToString("N"),
        ChannelType = target.ChannelType,
        ChannelId = target.ChannelId,
        TargetKey = target.Key,
        Kind = DeliveryKind.Digest,
        Recipients = target.Recipients,
        EventIds = model.ShownEventIds,
        Severity = model.Attention.Count == 0 ? Severity.Info : model.Attention.Max(e => e.Severity),
        CreatedAt = now,
        NextAttemptAt = now,
    };
}
