using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using ChannelKind = AdMng.Watchdog.Contracts.Configuration.ChannelType;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Generic webhook with Standard Webhooks v1 signatures (spec 14.5): per attempt <c>webhook-id</c> (the outbox
/// MessageId – stable across retries), <c>webhook-timestamp</c> (this attempt, from the <see cref="TimeProvider"/>),
/// <c>webhook-signature</c> (current and, during rotation, previous key) and optionally <c>X-Hub-Signature-256</c>. The
/// signing secrets are released by the <see cref="DestinationGuard"/> only when bound to the URL host; https only
/// (Development: loopback http); <see cref="WebhookChannelOptions.AllowedHosts"/> enforced at send time.
/// </summary>
public sealed class WebhookTransport(
    IWatchdogConfigProvider configuration,
    DestinationGuard guard,
    HttpChannelSender sender,
    ChannelRateLimiters limiters,
    ChannelHealthTracker health,
    IHostEnvironment environment,
    TimeProvider timeProvider) : INotificationTransport
{
    public DeliveryChannelType ChannelType => DeliveryChannelType.Webhook;

    public async Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channel = configuration.Current.Configuration.Channels
            .FirstOrDefault(c => c is not null && c.Type == ChannelKind.Webhook && string.Equals(c.Id, message.ChannelId, StringComparison.OrdinalIgnoreCase));
        ClassifiedDelivery result;
        if (channel is null || !channel.Enabled)
        {
            result = ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, channel is null ? "channel.unknown" : "channel.disabled");
        }
        else if (message.Http is null)
        {
            result = ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, "http.noPayload");
        }
        else
        {
            var decision = limiters.TryAcquireChannel(channel.Id, channel.RateLimit ?? new RateLimitSettings());
            if (!decision.Acquired)
            {
                return new DeliveryResult { Outcome = DeliveryOutcome.Deferred, ErrorCode = "rateLimit", RetryAfter = decision.RetryAfter };
            }

            result = await SendAsync(channel, message.MessageId, message.Http.Body, message.Http.ContentType, recorder: null, cancellationToken)
                .ConfigureAwait(false);
        }

        health.Record(DeliveryChannelType.Webhook, message.ChannelId, result.Result);
        return result.Result;
    }

    /// <summary>The delivery code path shared with the test send.</summary>
    internal async Task<ClassifiedDelivery> SendAsync(
        ChannelSettings channel,
        string messageId,
        byte[] body,
        string contentType,
        DeliveryStageRecorder? recorder,
        CancellationToken cancellationToken)
    {
        var url = await guard.ChannelUrlAsync(channel, cancellationToken).ConfigureAwait(false);
        if (!url.IsAvailable)
        {
            return Refuse(recorder, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, url.FailureCode ?? "secret.missing", url.Hint));
        }

        var urlText = url.Value!.Reveal();
        var (uri, refusal) = ChannelUrlPolicy.Check(channel, urlText, environment.IsDevelopment());
        if (refusal is not null)
        {
            return Refuse(recorder, refusal);
        }

        var signing = await guard.WebhookSigningSecretAsync(channel, urlText, previous: false, cancellationToken).ConfigureAwait(false);
        if (!signing.IsAvailable)
        {
            return Refuse(recorder, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, signing.FailureCode ?? "secret.missing", signing.Hint));
        }

        var signingKey = signing.Value!;
        if (!StandardWebhookSigner.IsValidSecret(signingKey.Reveal()))
        {
            return Refuse(recorder, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "webhook.invalidSecret"));
        }

        // The previous key signs too while it is set (rotation); a previous key bound elsewhere or malformed is not used.
        var previous = await guard.WebhookSigningSecretAsync(channel, urlText, previous: true, cancellationToken).ConfigureAwait(false);
        var previousKey = previous.Value is { } key && StandardWebhookSigner.IsValidSecret(key.Reveal()) ? key : null;
        var webhook = channel.Webhook ?? new WebhookChannelOptions();
        return await sender.SendAsync(
            new HttpChannelRequest
            {
                ChannelType = DeliveryChannelType.Webhook,
                ChannelId = channel.Id,
                Url = uri!,
                Body = body,
                ContentType = contentType,
                Timeout = webhook.Timeout > TimeSpan.Zero ? webhook.Timeout : TimeSpan.FromSeconds(30),
                Headers = headers =>
                {
                    var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    headers.Add(StandardWebhookSigner.IdHeader, messageId);
                    headers.Add(StandardWebhookSigner.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
                    headers.Add(
                        StandardWebhookSigner.SignatureHeader,
                        StandardWebhookSigner.CreateSignatureHeader(signingKey, previousKey, messageId, timestamp, body));
                    if (webhook.SendGitHubSignature)
                    {
                        headers.Add(StandardWebhookSigner.GitHubSignatureHeader, StandardWebhookSigner.SignGitHub(signingKey, body));
                    }
                },
            },
            recorder,
            cancellationToken).ConfigureAwait(false);
    }

    private static ClassifiedDelivery Refuse(DeliveryStageRecorder? recorder, ClassifiedDelivery refusal)
    {
        recorder?.Failed(DeliveryStage.HttpRequest, refusal.Result.ErrorCode);
        recorder?.AddHints(refusal.Hints);
        return refusal;
    }
}
