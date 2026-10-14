using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Client;

/// <summary>
/// HTTPS + Negotiate implementation of <see cref="IWatchdogClient"/> (section 5.5). Scaffold stub – replaced by
/// W-service: not configured, every API call throws <see cref="NotImplementedException"/>.
/// </summary>
public sealed class WatchdogHttpClient : IWatchdogClient
{
    public WatchdogHttpClient(TimeProvider timeProvider, ILogger<WatchdogHttpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
    }

    public bool IsConfigured => false;

    public Uri? BaseAddress => null;

    public void Configure(WatchdogClientOptions? options) => throw NotImplemented();

    public Task<MeDto> GetMeAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task<IReadOnlyList<EventKindInfo>> GetCatalogAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task<EventPage> GetEventsAsync(EventQuery query, CancellationToken cancellationToken) => throw NotImplemented();

    public Task<EventDetails> GetEventAsync(long id, CancellationToken cancellationToken) => throw NotImplemented();

    public Task AcknowledgeAsync(long id, AcknowledgeRequest request, CancellationToken cancellationToken) => throw NotImplemented();

    public Task AcknowledgeManyAsync(BulkAcknowledgeRequest request, CancellationToken cancellationToken) => throw NotImplemented();

    public Task<ConfigEnvelope> GetConfigAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task<WatchdogConfiguration> GetDefaultConfigAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task<ConfigEnvelope> SaveConfigAsync(ConfigEnvelope envelope, CancellationToken cancellationToken) => throw NotImplemented();

    public Task<ConfigValidationResult> ValidateConfigAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken) =>
        throw NotImplemented();

    public Task<ConfigAuditPage> GetConfigAuditAsync(long? before, int limit, CancellationToken cancellationToken) => throw NotImplemented();

    public Task<DeliveryTestResult> SendTestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken) => throw NotImplemented();

    public Task<DeliveryTestResult> SendTestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken) =>
        throw NotImplemented();

    public Task<RulePreviewResult> PreviewRuleAsync(RulePreviewRequest request, CancellationToken cancellationToken) => throw NotImplemented();

    public Task<DigestPreviewDto> PreviewDigestAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task SendDigestNowAsync(CancellationToken cancellationToken) => throw NotImplemented();

    public Task<OutboxPage> GetOutboxAsync(OutboxQuery query, CancellationToken cancellationToken) => throw NotImplemented();

    public Task RetryOutboxAsync(long id, CancellationToken cancellationToken) => throw NotImplemented();

    /// <summary>Nothing to release yet (no handler is ever created by the stub); never throws so DI disposal stays safe.</summary>
    public void Dispose()
    {
    }

    private static NotImplementedException NotImplemented() => new($"{nameof(WatchdogHttpClient)} is a scaffold stub.");
}
