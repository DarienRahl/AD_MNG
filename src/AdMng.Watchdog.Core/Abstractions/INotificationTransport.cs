using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Sends one outbox message over one channel type (SMTP, Teams, webhook).</summary>
public interface INotificationTransport
{
    DeliveryChannelType ChannelType { get; }
    Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken);
}
