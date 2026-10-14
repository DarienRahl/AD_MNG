using System.Security.Authentication;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>The TLS versions of every outgoing delivery connection (SMTP, Teams, webhooks): 1.2 and 1.3 only.</summary>
public static class DeliveryTls
{
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;
}
