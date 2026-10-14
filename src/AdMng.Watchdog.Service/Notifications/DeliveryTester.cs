using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The test buttons (spec 15.8, 17.2): the same transport code path as real deliveries, reported stage by stage
/// (<see cref="DeliveryStageRecorder"/>), never queued in the outbox and never counted against the channel's rate limit
/// or health. Unsaved settings use their own <see cref="SecretField.NewValue"/>s; a stored secret is used only when its
/// binding matches (the API rejects a mismatch with 400 <c>secretRebindRequired</c> before calling the tester; the
/// <see cref="DestinationGuard"/> refuses it here as well). Teams results always carry hint
/// <see cref="DeliveryHints.AcceptedNotDelivered"/>.
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
            Email = TestMessages.Email(current),
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
            var body = TestMessages.TeamsCard(current, channel.Teams?.CardVersion ?? "1.4");
            var result = await teams.SendAsync(channel, body, "application/json; charset=utf-8", recorder, cancellationToken).ConfigureAwait(false);
            return Result(result, recorder, started, [DeliveryHints.AcceptedNotDelivered]);
        }

        var messageId = Guid.CreateVersion7(timeProvider.GetUtcNow()).ToString("N");
        var payload = TestMessages.Webhook(current, messageId, timeProvider.GetUtcNow());
        var sent = await webhook.SendAsync(channel, messageId, payload, "application/json; charset=utf-8", recorder, cancellationToken).ConfigureAwait(false);
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
