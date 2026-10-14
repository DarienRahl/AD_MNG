using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Stage-by-stage test sends (never queued).</summary>
public interface IDeliveryTester
{
    Task<DeliveryTestResult> TestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken);
    Task<DeliveryTestResult> TestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken);
}
