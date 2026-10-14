using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using ChannelKind = AdMng.Watchdog.Contracts.Configuration.ChannelType;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Microsoft Teams through a Power Automate Workflows trigger (spec 15.7): POST of the rendered card envelope to the URL
/// secret. SAS triggers get no Authorization header (one would break them); "Specific users in my tenant" triggers
/// (<see cref="TeamsAuthMode.EntraClientCredentials"/>) get <c>Authorization: Bearer</c> – only for a Power Platform
/// workflow URL and a client secret whose binding matches (<see cref="TeamsTokenProvider"/>), otherwise the channel is
/// broken (hint <c>secret.rebindRequired</c> for a secret bound to another destination); a failing token endpoint is an
/// authentication failure (transient for network errors, throttling and 5xx). Rate limit per channel (1/s, 20/min,
/// 100/h by default).
/// </summary>
public sealed class TeamsWorkflowTransport(
    IWatchdogConfigProvider configuration,
    DestinationGuard guard,
    TeamsTokenProvider tokens,
    HttpChannelSender sender,
    ChannelRateLimiters limiters,
    ChannelHealthTracker health,
    IHostEnvironment environment) : INotificationTransport
{
    /// <summary>Attempt timeout (Teams channels have no timeout setting).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public DeliveryChannelType ChannelType => DeliveryChannelType.Teams;

    public async Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channel = configuration.Current.Configuration.Channels
            .FirstOrDefault(c => c is not null && c.Type == ChannelKind.Teams && string.Equals(c.Id, message.ChannelId, StringComparison.OrdinalIgnoreCase));
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

            result = await SendAsync(channel, message.Http.Body, message.Http.ContentType, recorder: null, cancellationToken).ConfigureAwait(false);
        }

        health.Record(DeliveryChannelType.Teams, message.ChannelId, result.Result);
        return result.Result;
    }

    /// <summary>The delivery code path shared with the test send.</summary>
    internal async Task<ClassifiedDelivery> SendAsync(ChannelSettings channel, byte[] body, string contentType, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
    {
        var url = await guard.ChannelUrlAsync(channel, cancellationToken).ConfigureAwait(false);
        if (!url.IsAvailable)
        {
            return Refuse(recorder, DeliveryStage.HttpRequest, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, url.FailureCode ?? "secret.missing", url.Hint));
        }

        var urlText = url.Value!.Reveal();
        var (uri, refusal) = ChannelUrlPolicy.Check(channel, urlText, environment.IsDevelopment());
        if (refusal is not null)
        {
            return Refuse(recorder, DeliveryStage.HttpRequest, refusal);
        }

        string? bearer = null;
        if (channel.Teams?.AuthMode == TeamsAuthMode.EntraClientCredentials)
        {
            recorder?.Begin();
            var token = await tokens.AcquireAsync(channel, urlText, cancellationToken).ConfigureAwait(false);
            if (!token.IsAvailable)
            {
                var outcome = token.IsTransient ? DeliveryOutcome.Transient
                    : token.FailureCode?.StartsWith("auth.token", StringComparison.Ordinal) == true ? DeliveryOutcome.AuthenticationFailed
                    : DeliveryOutcome.ChannelBroken;
                return Refuse(recorder, DeliveryStage.Token, ClassifiedDelivery.Failure(outcome, token.FailureCode ?? "auth.token", token.Hint));
            }

            recorder?.Succeeded(DeliveryStage.Token);
            bearer = token.Token;
        }

        return await sender.SendAsync(
            new HttpChannelRequest
            {
                ChannelType = DeliveryChannelType.Teams,
                ChannelId = channel.Id,
                Url = uri!,
                Body = body,
                ContentType = contentType,
                Timeout = Timeout,
                Headers = bearer is null ? null : headers => headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer),
            },
            recorder,
            cancellationToken).ConfigureAwait(false);
    }

    private static ClassifiedDelivery Refuse(DeliveryStageRecorder? recorder, DeliveryStage stage, ClassifiedDelivery refusal)
    {
        recorder?.Failed(stage, refusal.Result.ErrorCode);
        recorder?.AddHints(refusal.Hints);
        return refusal;
    }
}
