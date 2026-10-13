using System.Net;
using System.Net.Sockets;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Minimal TCP forwarder in front of the lab DC. <see cref="DropAll"/> resets every proxied connection, which
/// the LDAP client sees exactly like a DC restart or a firewall dropping an idle session.
/// </summary>
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<TcpClient> _open = [];
    private readonly Task _acceptLoop;
    private int _accepted;

    public TcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>Connections accepted so far (including TCP reachability probes).</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>Resets (RST) all currently proxied connections.</summary>
    public void DropAll()
    {
        TcpClient[] open;
        lock (_gate)
        {
            open = [.. _open];
            _open.Clear();
        }

        foreach (var client in open)
        {
            try
            {
                // RST instead of FIN: the peer sees "connection reset", like a crashed or restarted DC.
                if (client.Client is { } socket)
                {
                    socket.LingerState = new LingerOption(true, 0);
                }

                client.Close();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                // Already closed by its pump.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAll();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            var outbound = new TcpClient();
            try
            {
                await outbound.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                inbound.Dispose();
                outbound.Dispose();
                continue;
            }

            lock (_gate)
            {
                _open.Add(inbound);
                _open.Add(outbound);
            }

            _ = PumpAsync(inbound, outbound);
            _ = PumpAsync(outbound, inbound);
        }
    }

    private static async Task PumpAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            // Connection reset - the other pump closes its side as well.
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }
}
