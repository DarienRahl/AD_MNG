using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting.Systemd;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// <see cref="ISystemdNotifier"/> over the datagram socket named by <c>NOTIFY_SOCKET</c> (<c>sd_notify</c>): one datagram
/// per state (<c>READY=1</c>, <c>STOPPING=1</c>, <c>WATCHDOG=1</c>). A leading <c>@</c> names an abstract socket. The socket
/// path is passed explicitly in tests (a local listener); <see cref="SystemdLifetime"/> uses this notifier as well.
/// </summary>
public sealed class SystemdNotifySocket : ISystemdNotifier
{
    private readonly string? _socketPath;

    /// <summary>The socket of the unit (<c>NOTIFY_SOCKET</c>).</summary>
    public SystemdNotifySocket()
        : this(Environment.GetEnvironmentVariable(SystemdSetup.NotifySocketVariable))
    {
    }

    /// <summary>An explicit socket path (tests); null or empty = disabled.</summary>
    public SystemdNotifySocket(string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return;
        }

        // An abstract socket ("@name") is addressed with a leading NUL byte.
        _socketPath = socketPath[0] == '@' ? "\0" + socketPath[1..] : socketPath;
    }

    public bool IsEnabled => _socketPath is not null;

    /// <exception cref="SocketException">The socket is gone (systemd stopped listening).</exception>
    public void Notify(ServiceState state)
    {
        if (_socketPath is null)
        {
            return;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
        socket.Send(Encoding.UTF8.GetBytes(state.ToString()));
    }
}
