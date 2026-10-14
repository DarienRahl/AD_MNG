using System.Net;
using System.Net.Sockets;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Testing.Certificates;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>The delivery handler of the service (no redirects, TLS 1.2/1.3) that additionally trusts the in-memory test
/// certificate – every other certificate goes through normal validation. No proxy. <see cref="ConnectToPort"/> sends
/// every connection to a loopback sink whatever the URL host is (Power Platform URLs in the Entra tests).</summary>
public sealed class TestHttpClientFactory : IHttpClientFactory
{
    public int Created { get; private set; }

    /// <summary>When set, every connection goes to 127.0.0.1 on this port (TLS still uses the URL host for SNI).</summary>
    public int? ConnectToPort { get; set; }

    public HttpClient CreateClient(string name)
    {
        Created++;
        var handler = DeliveryHttpClient.CreateHandler();
        handler.UseProxy = false;
        var trusted = TestCertificates.Localhost.Thumbprint;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None
            || string.Equals(certificate?.GetCertHashString(), trusted, StringComparison.OrdinalIgnoreCase);
        if (ConnectToPort is { } port)
        {
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
