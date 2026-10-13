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
    private readonly List<ResponseGate> _gates = [];
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

    /// <summary>
    /// Holds back the server's answers on every connection proxied so far (requests still reach the server).
    /// Connections accepted afterwards are not affected - a DC that stopped answering on one connection only.
    /// </summary>
    public void StallExisting()
    {
        lock (_gate)
        {
            _gates.ForEach(g => g.Stall());
        }
    }

    /// <summary>Delivers everything held back by <see cref="StallExisting"/> and lets traffic flow again.</summary>
    public void ResumeAll()
    {
        lock (_gate)
        {
            _gates.ForEach(g => g.Resume());
        }
    }

    public async ValueTask DisposeAsync()
    {
        ResumeAll();
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

            var responses = new ResponseGate();
            lock (_gate)
            {
                _open.Add(inbound);
                _open.Add(outbound);
                _gates.Add(responses);
            }

            _ = PumpAsync(inbound, outbound, null);
            _ = PumpAsync(outbound, inbound, responses);
        }
    }

    private static async Task PumpAsync(TcpClient from, TcpClient to, ResponseGate? gate)
    {
        var buffer = new byte[81920];
        try
        {
            var source = from.GetStream();
            var target = to.GetStream();
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                if (gate is not null)
                {
                    await gate.WhenOpen;
                }

                await target.WriteAsync(buffer.AsMemory(0, read));
            }
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

    /// <summary>Open/stalled switch of one server-to-client direction.</summary>
    private sealed class ResponseGate
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource _open = Opened();

        public Task WhenOpen
        {
            get
            {
                lock (_lock)
                {
                    return _open.Task;
                }
            }
        }

        public void Stall()
        {
            lock (_lock)
            {
                if (_open.Task.IsCompleted)
                {
                    _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                _open.TrySetResult();
            }
        }

        private static TaskCompletionSource Opened()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
