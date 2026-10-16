using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Formatting.AdaptiveCards;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The test buttons (spec 15.8, 17.2): the same transport code path as real deliveries, reported stage by stage
/// (<see cref="DeliveryStageRecorder"/>), never queued in the outbox and never counted against the channel's rate limit
/// or health. Unsaved settings use their own <see cref="SecretField.NewValue"/>s; a stored secret is used only when its
/// binding matches (the API rejects a mismatch with 400 <c>secretRebindRequired</c> before calling the tester; the
/// <see cref="DestinationGuard"/> refuses it here as well). The payloads are the Core test formatters' (e-mail
/// "Wiadomość testowa", the Teams test card, the <c>watchdog.test</c> webhook), in the configured language. Teams results
/// always carry hint <see cref="DeliveryHints.AcceptedNotDelivered"/>.
/// </summary>
public sealed class DeliveryTester(
    IWatchdogConfigProvider configuration,
    SmtpEmailTransport smtp,
    TeamsWorkflowTransport teams,
    WebhookTransport webhook,
    TimeProvider timeProvider) : IDeliveryTester
{
    public async Task<DeliveryTestResult> TestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = configuration.Current.Configuration;
        var settings = request.Settings ?? current.Smtp;
        var started = timeProvider.GetTimestamp();
        var recorder = new DeliveryStageRecorder(timeProvider);
        var now = timeProvider.GetUtcNow();
        var message = new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(now).ToString("N"),
            ChannelType = DeliveryChannelType.Email,
            ChannelId = "email",
            Kind = DeliveryKind.Test,
            Recipients = request.Recipients,
            Email = EmailAlertFormatter.Test(NotificationContext.Create(current, DetailLevel.Full), now),
            CreatedAt = now,
            NextAttemptAt = now,
        };
        var result = string.IsNullOrWhiteSpace(settings.Host)
            ? ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "smtp.hostMissing")
            : await smtp.SendAsync(message, settings, recorder, cancellationToken).ConfigureAwait(false);
        return Result(result, recorder, started, []);
    }

    public async Task<DeliveryTestResult> TestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(request);
        var current = configuration.Current.Configuration;
        var channel = request.Settings ?? current.Channels.FirstOrDefault(c => c is not null && string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The channel does not exist.");
        var started = timeProvider.GetTimestamp();
        var recorder = new DeliveryStageRecorder(timeProvider);
        if (channel.Type == ChannelType.Teams)
        {
            var card = TeamsAlertCardFormatter.Test(NotificationContext.Create(current, channel.DetailLevel), timeProvider.GetUtcNow(), channel.Teams?.CardVersion);
            var result = await teams.SendAsync(channel, card.Body, card.ContentType, recorder, cancellationToken).ConfigureAwait(false);
            return Result(result, recorder, started, [DeliveryHints.AcceptedNotDelivered]);
        }

        var messageId = Guid.CreateVersion7(timeProvider.GetUtcNow()).ToString("N");
        var payload = WebhookPayloadFormatter.Test(NotificationContext.Create(current, channel.DetailLevel), timeProvider.GetUtcNow());
        var sent = await webhook.SendAsync(channel, messageId, payload.Body, payload.ContentType, recorder, cancellationToken).ConfigureAwait(false);
        return Result(sent, recorder, started, []);
    }

    private DeliveryTestResult Result(ClassifiedDelivery result, DeliveryStageRecorder recorder, long started, IReadOnlyList<string> extraHints)
    {
        recorder.AddHints(result.Hints);
        recorder.AddHints(extraHints);
        return new DeliveryTestResult
        {
            Success = result.Result.Outcome == DeliveryOutcome.Delivered,
            Stages = recorder.Stages,
            Hints = recorder.Hints,
            Duration = timeProvider.GetElapsedTime(started),
        };
    }
}
