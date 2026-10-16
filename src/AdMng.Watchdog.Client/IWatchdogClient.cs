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
}
