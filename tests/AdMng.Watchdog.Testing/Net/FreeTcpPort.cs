using System.Net;
using System.Net.Sockets;

namespace AdMng.Watchdog.Testing.Net;

/// <summary>Loopback ports for test servers that cannot report an ephemeral port themselves (SmtpServer).</summary>
public static class FreeTcpPort
{
    /// <summary>A port that was free on 127.0.0.1 a moment ago (bind to port 0, read it, release it).</summary>
    public static int Next()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
