using System.Net;
using System.Net.Sockets;

namespace AdMng.Watchdog.Testing.Net;

/// <summary>
/// Loopback ports for tests. The test projects run side by side (and the tests of one project in parallel), so a port
/// number says little by itself: <see cref="Next"/> is for a test server that cannot report an ephemeral port itself
/// and must be ready to find the port taken; <see cref="Closed"/> is for a test that needs a port where nothing listens.
/// </summary>
public static class FreeTcpPort
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<int> HandedOut = [];

    /// <summary>
    /// A port that was free on 127.0.0.1 a moment ago (bind to port 0, read it, release it), never one this process was
    /// given before - by this method or as a <see cref="Closed"/> port. Between this call and the caller's own bind
    /// another process may take it: a caller that cannot bind asks for the next one (<c>SmtpSink</c> does). Not for
    /// "nothing listens here" - see <see cref="Closed"/>.
    /// </summary>
    public static int Next()
    {
        while (true)
        {
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                try
                {
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                }
                finally
                {
                    listener.Stop();
                }
            }

            if (TryTake(port))
            {
                return port;
            }
        }
    }

    /// <summary>
    /// A port of 127.0.0.1 where nothing listens for as long as the returned object lives: a connection to it is
    /// refused. A port that was only free a moment ago may be somebody's listener by the time the test connects; this
    /// one is held by a socket that is bound to it and never listens. No test server of this process is given the port
    /// (<see cref="Next"/> skips it), and on Windows no other process can bind it either.
    /// </summary>
    public static ClosedTcpPort Closed()
    {
        while (true)
        {
            var closed = new ClosedTcpPort();
            if (TryTake(closed.Port))
            {
                return closed;
            }

            // A number this process handed out before: its test server may be about to listen there.
            closed.Dispose();
        }
    }

    private static bool TryTake(int port)
    {
        lock (Gate)
        {
            return HandedOut.Add(port);
        }
    }
}

/// <summary>A loopback port that refuses connections while this object lives: a socket bound to it that never listens.</summary>
public sealed class ClosedTcpPort : IDisposable
{
    private readonly Socket _socket;

    internal ClosedTcpPort()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Nobody binds the same port next to this socket (Windows allows that to a socket that asks for it).
                // Elsewhere a second socket may be bound to a port that is held without a listener, so there the
                // port is kept from this process's own test servers only (FreeTcpPort.Next skips it).
                _socket.ExclusiveAddressUse = true;
            }

            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public int Port { get; }

    public void Dispose() => _socket.Dispose();
}
