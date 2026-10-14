using System.Net;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The named <see cref="HttpClient"/> of the Teams and webhook transports (spec 14.5, 15.7): redirects are never followed,
/// TLS 1.2/1.3 only, no cookies, pooled connections recycled every 5 minutes, no client-wide timeout (each attempt has
/// its own on the <see cref="TimeProvider"/>), and the factory's request loggers removed – the transports log
/// <c>scheme://host/path</c> themselves (a Teams URL's query carries the <c>sig</c> credential).
/// </summary>
public static class DeliveryHttpClient
{
    public const string Name = "AdMng.Watchdog.Delivery";

    /// <summary>Registers the named client.</summary>
    public static IServiceCollection AddDeliveryHttpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient(Name)
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestVersion = HttpVersion.Version11;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .RemoveAllLoggers();
        return services;
    }

    /// <summary>The primary handler (also used by tests with a custom certificate callback).</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        SslOptions = { EnabledSslProtocols = DeliveryTls.Protocols },
    };

    /// <summary><c>scheme://host[:port]/path</c> – the only form of a channel URL that is ever logged.</summary>
    public static string ForLog(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.GetLeftPart(UriPartial.Path);
    }
}
