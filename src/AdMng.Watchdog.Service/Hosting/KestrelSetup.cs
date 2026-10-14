using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Kestrel listeners from <c>Hosting.Urls</c> (spec 17.1, 19.3): HTTP/1.1 only (Negotiate does not work on HTTP/2),
/// TLS 1.2/1.3 with the certificate chosen by <see cref="CertificateSelector"/> at every handshake, request bodies up to
/// <see cref="ApiLimits.MaxRequestBodyBytes"/>, no Server header. Plain http is accepted only on loopback listeners
/// (checked by <see cref="StartupChecks"/>).
/// </summary>
public static class KestrelSetup
{
    public static void Configure(WebApplicationBuilder builder, IReadOnlyList<ListenerEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoints);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = ApiLimits.MaxRequestBodyBytes;
            foreach (var endpoint in endpoints)
            {
                var https = endpoint.Https;
                void Listen(ListenOptions listen)
                {
                    listen.Protocols = HttpProtocols.Http1;
                    if (https)
                    {
                        listen.UseHttps(tls =>
                        {
                            tls.SslProtocols = Notifications.DeliveryTls.Protocols;
                            var selector = kestrel.ApplicationServices.GetRequiredService<CertificateSelector>();
                            tls.ServerCertificateSelector = (_, _) => selector.Current;
                        });
                    }
                }

                if (endpoint.Address is null)
                {
                    kestrel.ListenAnyIP(endpoint.Port, Listen);
                }
                else if (endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Url.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    kestrel.ListenLocalhost(endpoint.Port, Listen);
                }
                else
                {
                    kestrel.Listen(endpoint.Address, endpoint.Port, Listen);
                }
            }
        });
    }
}
