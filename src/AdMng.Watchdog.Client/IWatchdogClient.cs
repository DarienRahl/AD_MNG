using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Client;

/// <summary>The watchdog API as the WPF app sees it. Failures throw <see cref="WatchdogClientException"/>.</summary>
public interface IWatchdogClient : IDisposable
{
    bool IsConfigured { get; }
    Uri? BaseAddress { get; }
    /// <summary>Replaces the connection (disposes the previous handler). Null = not configured.</summary>
    void Configure(WatchdogClientOptions? options);
    Task<MeDto> GetMeAsync(CancellationToken cancellationToken);
    Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<EventKindInfo>> GetCatalogAsync(CancellationToken cancellationToken);
    Task<EventPage> GetEventsAsync(EventQuery query, CancellationToken cancellationToken);
    Task<EventDetails> GetEventAsync(long id, CancellationToken cancellationToken);
    Task AcknowledgeAsync(long id, AcknowledgeRequest request, CancellationToken cancellationToken);
    Task AcknowledgeManyAsync(BulkAcknowledgeRequest request, CancellationToken cancellationToken);
    Task<ConfigEnvelope> GetConfigAsync(CancellationToken cancellationToken);
    Task<WatchdogConfiguration> GetDefaultConfigAsync(CancellationToken cancellationToken);
    /// <summary>PUT with If-Match: "Version". Returns the saved envelope (new version, secrets as IsSet/Hint).</summary>
    Task<ConfigEnvelope> SaveConfigAsync(ConfigEnvelope envelope, CancellationToken cancellationToken);
    Task<ConfigValidationResult> ValidateConfigAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken);
    Task<ConfigAuditPage> GetConfigAuditAsync(long? before, int limit, CancellationToken cancellationToken);
    Task<DeliveryTestResult> SendTestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken);
    Task<DeliveryTestResult> SendTestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken);
    Task<RulePreviewResult> PreviewRuleAsync(RulePreviewRequest request, CancellationToken cancellationToken);
    Task<DigestPreviewDto> PreviewDigestAsync(CancellationToken cancellationToken);
    Task SendDigestNowAsync(CancellationToken cancellationToken);
    Task<OutboxPage> GetOutboxAsync(OutboxQuery query, CancellationToken cancellationToken);
    Task RetryOutboxAsync(long id, CancellationToken cancellationToken);
    /// <summary><c>GET /user-messages/status</c> (Viewer): whether messages to users can be sent through the service.</summary>
    Task<UserMessagingStatusDto> GetUserMessagingStatusAsync(CancellationToken cancellationToken);
    /// <summary><c>POST /user-messages</c> (Viewer): one message to a user; idempotent by <see cref="UserMessageRequest.Id"/>
    /// (repeat it after a timeout). 409 (disabled, idempotency conflict), 400 (invalid) and 429 (limit, with
    /// <see cref="WatchdogClientException.RetryAfter"/>) throw with <see cref="WatchdogClientException.ProblemType"/>.</summary>
    Task<UserMessageResult> SendUserMessageAsync(UserMessageRequest request, CancellationToken cancellationToken);
}
