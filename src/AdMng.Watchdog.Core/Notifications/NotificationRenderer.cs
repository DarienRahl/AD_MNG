using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Formatting.AdaptiveCards;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Renders a flushed bucket into an outbox message (section 13.3 "Rendering choice"): e-mail – one item → alert, more →
/// aggregated alert; Teams – one item → alert card, more → burst card; webhook – one payload per event; follow-ups → the
/// follow-up template of the channel. The message id is a Guid v7 ("N"), the webhook-id and the Message-ID local part.
/// </summary>
public static class NotificationRenderer
{
    /// <summary>An alert message for <paramref name="events"/> (already capped; <paramref name="moreCount"/> left out).</summary>
    public static OutboxMessage Alert(NotificationTarget target, RuleSettings? rule, IReadOnlyList<WatchEvent> events, int moreCount,
        IReadOnlyList<long> linkedEventIds, WatchdogConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(configuration);
        if (events.Count == 0)
        {
            throw new ArgumentException("A message needs at least one event.", nameof(events));
        }

        var context = NotificationContext.Create(configuration, target.DetailLevel);
        var severity = events.Max(e => e.Severity);
        var pending = events.Any(e => e.Attribution?.Status == AttributionStatus.Pending);
        var followUpExpected = pending && severity >= Severity.High && configuration.Notifications?.FollowUpWhenAttributed != false;
        var message = Base(target, rule, events, linkedEventIds, now, DeliveryKind.Alert) with { SentWithPendingAttribution = pending };
        return target.ChannelType switch
        {
            DeliveryChannelType.Email => message with
            {
                Email = events.Count == 1 && moreCount == 0
                    ? EmailAlertFormatter.Alert(events[0], rule, context, followUpExpected)
                    : EmailAlertFormatter.Aggregated(events, moreCount, rule, context),
            },
            DeliveryChannelType.Teams => message with
            {
                Http = events.Count == 1 && moreCount == 0
                    ? TeamsAlertCardFormatter.Alert(events[0], rule, context, CardVersion(configuration, target), followUpExpected)
                    : TeamsAlertCardFormatter.Burst(events, moreCount, rule, context, CardVersion(configuration, target)),
            },
            _ => message with { Http = WebhookPayloadFormatter.Event(events[0], MatchedRules(events[0], rule, configuration), context) },
        };
    }

    /// <summary>A follow-up of the message <paramref name="originalMessageId"/> for <paramref name="events"/> (final
    /// attribution); <paramref name="originalSubject"/> is the subject the e-mail thread started with.</summary>
    public static OutboxMessage FollowUp(NotificationTarget target, RuleSettings? rule, IReadOnlyList<WatchEvent> events, string originalMessageId,
        string originalSubject, WatchdogConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(configuration);
        if (events.Count == 0)
        {
            throw new ArgumentException("A follow-up needs at least one event.", nameof(events));
        }

        var context = NotificationContext.Create(configuration, target.DetailLevel);
        var message = Base(target, rule, events, [.. events.Select(e => e.Id)], now, DeliveryKind.FollowUp);
        return target.ChannelType switch
        {
            DeliveryChannelType.Email => message with { Email = EmailAlertFormatter.FollowUp(events, originalSubject, originalMessageId, rule, context) },
            DeliveryChannelType.Teams => message with { Http = TeamsAlertCardFormatter.FollowUp(events, rule, context, CardVersion(configuration, target)) },
            _ => message with { Http = WebhookPayloadFormatter.Event(events[0], MatchedRules(events[0], rule, configuration), context, followUp: true) },
        };
    }

    /// <summary>The subject an alert of <paramref name="events"/> had while their actors were pending (follow-up threads).</summary>
    public static string OriginalSubject(IReadOnlyList<WatchEvent> events, RuleSettings? rule, WatchdogConfiguration configuration, DetailLevel detailLevel)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(configuration);
        var context = NotificationContext.Create(configuration, detailLevel);
        var pending = events.Select(e => e with { Attribution = AdMng.Watchdog.Contracts.Events.Attribution.Pending }).ToList();
        if (pending.Count == 1)
        {
            return EmailSubjectBuilder.Single(AlertContentBuilder.Build(pending[0], rule, context), context);
        }

        var safe = pending.Select(e => DetailLevelFilter.ForNotification(e, detailLevel, context.MaxValueLength)).ToList();
        return EmailSubjectBuilder.Aggregated(safe, safe.Count, pending.Max(e => e.Severity), rule, context);
    }

    private static OutboxMessage Base(NotificationTarget target, RuleSettings? rule, IReadOnlyList<WatchEvent> events, IReadOnlyList<long> linkedEventIds,
        DateTimeOffset now, DeliveryKind kind) => new()
        {
            MessageId = Guid.CreateVersion7(now).ToString("N"),
            ChannelType = target.ChannelType,
            ChannelId = target.ChannelId,
            TargetKey = target.Key,
            Kind = kind,
            Recipients = target.Recipients,
            RuleId = rule?.Id,
            EventIds = linkedEventIds,
            Severity = events.Max(e => e.Severity),
            CreatedAt = now,
            NextAttemptAt = now,
        };

    private static string? CardVersion(WatchdogConfiguration configuration, NotificationTarget target) =>
        (configuration.Channels ?? []).FirstOrDefault(c => c is not null && string.Equals(c.Id, target.ChannelId, StringComparison.OrdinalIgnoreCase))?.Teams?.CardVersion;

    private static IReadOnlyList<RuleSettings> MatchedRules(WatchEvent watchEvent, RuleSettings? rule, WatchdogConfiguration configuration)
    {
        var matched = (configuration.Rules ?? [])
            .Where(r => r is not null && watchEvent.MatchedRules.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (rule is not null && !matched.Any(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase)))
        {
            matched.Insert(0, rule);
        }

        return matched;
    }
}
