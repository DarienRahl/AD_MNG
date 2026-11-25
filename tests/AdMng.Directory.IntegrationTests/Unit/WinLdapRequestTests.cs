using System.Collections.Concurrent;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// Audit finding LDAP-WIN-APM-FINISHED: the Windows path of <see cref="LdapAsync"/> (WinLDAP, <c>BeginSendRequest</c> and
/// <c>Abort</c>) with a real connection, a real request and the real sender, against a server that accepts the connection,
/// reads every LDAP message the client sends and never answers. <c>PoolCloseTests</c> drive the pool with a fake sender; here
/// <see cref="PendingResponse{TResponse}.Finished"/> comes from S.DS.P itself: the result's wait handle after an abort, the
/// completion callback after the library's own timeout. Windows only - on Linux and macOS a search takes the blocking path
/// (<c>PoolCloseTests</c> and the lab's <c>CloseWhileRequestRunsTests</c> cover it there).
/// </summary>
public sealed class WinLdapRequestTests
{
    /// <summary>The bound of every wait on the test server, and a guard against a hang (see <c>SslStreamTlsProbeTests</c>).</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ClosingThePool_ReleasesTheCallerAtOnce_AbandonsTheSearch_AndDisposesTheConnectionOnlyAfterTheRequestFinished()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinLDAP's request path (BeginSendRequest, Abort) runs on Windows only.");

        // The request's own timeout is the product's default: the caller must be released long before it, by the close.
        var requestTimeout = TimeSpan.FromSeconds(60);
        await using var server = SilentLdapServer.Start();
        var sender = new FinishedRecorder();
        var connection = new RecordingConnection(sender, server.Port);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromMinutes(2));
        using var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken);

        // BeginSendRequest connects first (no bind: AutoBind is off) - on a pool thread, so a connect that blocks fails the
        // test instead of hanging it.
        var caller = Task.Run(() => lease.SendAsync<SearchResponse>(Search(), requestTimeout, CancellationToken.None));
        var search = await server.WaitForAsync(SilentLdapServer.SearchRequest, caller);
        var finished = sender.FinishedOf(connection);
        Assert.False(finished.IsCompleted, "The search finished although the server never answered.");

        var stopwatch = Stopwatch.StartNew();
        var closing = pool.DisposeAsync().AsTask();

        // The caller learns at once that the session is gone ...
        await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.WaitAsync(Generous, TestContext.Current.CancellationToken));
        var callerReleased = stopwatch.Elapsed;

        // ... WinLDAP abandons the search, and S.DS.P's task of the request ends (by the abort, at the latest by the request's
        // own timeout, which its polling loop still counts) ...
        await server.WaitForAsync(message => message.Operation == SilentLdapServer.AbandonRequest && message.AbandonedId == search.Id);
        await finished.WaitAsync(requestTimeout + Generous, TestContext.Current.CancellationToken);
        var requestFinished = stopwatch.Elapsed;

        // ... and only then is the connection disposed (the close waited for it: its bound is longer than the request's timeout).
        await closing.WaitAsync(Generous, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"After the close: the caller was released after {callerReleased.TotalMilliseconds:F0} ms, S.DS.P's task ended after " +
            $"{requestFinished.TotalMilliseconds:F0} ms, the close returned after {stopwatch.Elapsed.TotalMilliseconds:F0} ms. {server.Seen}.");
        Assert.True(callerReleased < requestTimeout / 2, $"The caller was released only after {callerReleased}, not by the close.");
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedBeforeFinished);
    }

    [Fact]
    public async Task ARequestThatTimesOut_IsAbandonedByTheLibrary_DropsItsConnection_ThatIsDisposedOnlyAfterTheRequestFinished()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinLDAP's request path (BeginSendRequest, Abort) runs on Windows only.");

        var requestTimeout = TimeSpan.FromSeconds(2);
        await using var server = SilentLdapServer.Start();
        var sender = new FinishedRecorder();
        var connection = new RecordingConnection(sender, server.Port);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromMinutes(2));
        using (var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken))
        {
            var caller = Task.Run(() => lease.SendAsync<SearchResponse>(Search(), requestTimeout, CancellationToken.None));
            var search = await server.WaitForAsync(SilentLdapServer.SearchRequest, caller);

            // S.DS.P gives up after the request's timeout: it abandons the message and the caller gets the client timeout ...
            var error = await Assert.ThrowsAnyAsync<Exception>(() => caller.WaitAsync(requestTimeout + Generous, TestContext.Current.CancellationToken));
            Assert.True(LdapErrors.IsClientTimeout(error), error.ToString());
            await server.WaitForAsync(message => message.Operation == SilentLdapServer.AbandonRequest && message.AbandonedId == search.Id);

            // ... after S.DS.P's task had ended (the completion callback is its last step) ...
            Assert.True(sender.FinishedOf(connection).IsCompleted, "The caller saw the timeout before S.DS.P's task of the request ended.");

            // ... and the connection, which did not answer in time, left the pool; the lease still holds it.
            Assert.Equal(0, pool.Count);
            Assert.Equal(0, connection.Disposals);
        }

        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedBeforeFinished);
        await pool.DisposeAsync();
    }

    private static LdapConnectionPool NewPool(FinishedRecorder sender, RecordingConnection connection, TimeSpan closeWait)
    {
        var settings = ConnectionSettings.Create(new ConnectionOptions { Server = "dc1.contoso.test" }, credential: null, OperatingSystem.IsWindows());
        var factory = new LdapConnectionFactory(settings, "dc1.contoso.test", "contoso.test", NullLogger.Instance);
        var pool = new LdapConnectionPool(factory, maxConnections: 1, NullLogger.Instance, TimeProvider.System)
        {
            Sender = sender,
            CloseWait = closeWait,
        };
        pool.Add(connection);
        return pool;
    }

    private static SearchRequest Search() =>
        new("OU=Księgowość,DC=contoso,DC=test", "(sAMAccountName=łukasz.wójcik)", System.DirectoryServices.Protocols.SearchScope.Subtree, null);

    /// <summary>The real sender, keeping each connection's <see cref="PendingResponse{TResponse}.Finished"/>.</summary>
    private sealed class FinishedRecorder : ILdapRequestSender
    {
        private readonly ConcurrentDictionary<LdapConnection, Task> _finished = new();

        public PendingResponse<TResponse> Send<TResponse>(LdapConnection connection, DirectoryRequest request, TimeSpan timeout, CancellationToken cancellationToken)
            where TResponse : DirectoryResponse
        {
            var pending = LdapRequestSender.Instance.Send<TResponse>(connection, request, timeout, cancellationToken);
            _finished[connection] = pending.Finished;
            return pending;
        }

        public void Interrupt(LdapConnection connection) => LdapRequestSender.Instance.Interrupt(connection);

        /// <summary>The last request's Finished on <paramref name="connection"/> (completed when none was sent).</summary>
        public Task FinishedOf(LdapConnection connection) => _finished.TryGetValue(connection, out var finished) ? finished : Task.CompletedTask;
    }

    /// <summary>A real LDAP connection to the test server that records whether S.DS.P was done with its request when it was disposed.</summary>
    private sealed class RecordingConnection : LdapConnection
    {
        private readonly FinishedRecorder _sender;
        private int _disposals;
        private int _disposedBeforeFinished;

        public RecordingConnection(FinishedRecorder sender, int port)
            : base(new LdapDirectoryIdentifier("127.0.0.1", port))
        {
            _sender = sender;

            // As LdapConnectionFactory.Configure, but without a bind: the first request connects and sends at once.
            SessionOptions.ProtocolVersion = 3;
            SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            AutoBind = false;
        }

        public int Disposals => Volatile.Read(ref _disposals);

        public int DisposedBeforeFinished => Volatile.Read(ref _disposedBeforeFinished);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposals);
                if (!_sender.FinishedOf(this).IsCompleted)
                {
                    Interlocked.Increment(ref _disposedBeforeFinished);
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>One LDAP message the server received: its id, the tag of its operation and, for an abandon, the id it abandons.</summary>
    private readonly record struct LdapMessageSeen(int Id, byte Operation, int? AbandonedId);

    /// <summary>
    /// A TCP server on the loopback address (the pattern of <c>SslStreamTlsProbeTests.RawServer</c>) that reads the LDAP messages of
    /// every connection and never sends a byte: a DC that stopped answering. It tells the messages apart by their BER framing.
    /// </summary>
    private sealed class SilentLdapServer : IAsyncDisposable
    {
        /// <summary>[APPLICATION 3] SearchRequest (constructed).</summary>
        public const byte SearchRequest = 0x63;

        /// <summary>[APPLICATION 16] AbandonRequest (primitive: the message id it abandons).</summary>
        public const byte AbandonRequest = 0x50;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _connections = [];
        private readonly ConcurrentQueue<LdapMessageSeen> _messages = new();
        private readonly Task _accepting;
        private int _accepted;
        private long _bytes;

        private SilentLdapServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(async () =>
            {
                while (true)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    var connection = ReadAsync(socket, _stop.Token);
                    lock (_connections)
                    {
                        _connections.Add(connection);
                    }
                }
            });
        }

        public int Port { get; }

        /// <summary>What the server saw so far, for the output and the failure messages.</summary>
        public string Seen =>
            $"The server accepted {Volatile.Read(ref _accepted)} connection(s) on port {Port} and read {Interlocked.Read(ref _bytes)} bytes: " +
            string.Join(", ", _messages.Select(m => $"#{m.Id} 0x{m.Operation:X2}{(m.AbandonedId is { } id ? $" (abandons #{id})" : string.Empty)}"));

        public static SilentLdapServer Start() => new();

        /// <summary>The first message with the operation <paramref name="operation"/>; fails when <paramref name="caller"/> ends first.</summary>
        public Task<LdapMessageSeen> WaitForAsync(byte operation, Task caller) => WaitForAsync(message => message.Operation == operation, caller);

        /// <summary>The first message that matches <paramref name="match"/>, within <see cref="Generous"/>.</summary>
        public async Task<LdapMessageSeen> WaitForAsync(Func<LdapMessageSeen, bool> match, Task? caller = null)
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                foreach (var message in _messages)
                {
                    if (match(message))
                    {
                        return message;
                    }
                }

                if (caller is { IsCompleted: true })
                {
                    Assert.Fail($"The request ended before the server saw it ({caller.Exception?.InnerException?.ToString() ?? caller.Status.ToString()}). {Seen}.");
                }

                Assert.True(deadline.Elapsed < Generous, $"The server did not see the expected message. {Seen}.");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            Task[] connections;
            lock (_connections)
            {
                connections = [.. _connections];
            }

            try
            {
                await Task.WhenAll([_accepting, .. connections]).WaitAsync(Generous);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or TimeoutException)
            {
            }

            _stop.Dispose();
        }

        private async Task ReadAsync(Socket socket, CancellationToken cancellationToken)
        {
            try
            {
                using var owned = socket;
                var received = new List<byte>();
                var parsed = 0;
                var buffer = new byte[4096];
                int read;
                while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken)) > 0)
                {
                    Interlocked.Add(ref _bytes, read);
                    received.AddRange(buffer.AsSpan(0, read));
                    while (TryReadMessage(received, ref parsed, out var message))
                    {
                        _messages.Enqueue(message);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The client went away, or the server was stopped.
            }
        }

        /// <summary>Reads one LDAPMessage (SEQUENCE { messageID INTEGER, protocolOp, ... }) when it is complete.</summary>
        private static bool TryReadMessage(List<byte> data, ref int offset, out LdapMessageSeen message)
        {
            message = default;
            var at = offset;
            if (!TryReadHeader(data, ref at, out var tag, out var length) || at + length > data.Count)
            {
                return false;
            }

            var end = at + length;
            Assert.True(tag == 0x30, $"Not an LDAP message: tag 0x{tag:X2}.");
            Assert.True(TryReadHeader(data, ref at, out var idTag, out var idLength) && idTag == 0x02, "An LDAP message without a message id.");
            var id = ReadInteger(data, at, idLength);
            at += idLength;
            Assert.True(TryReadHeader(data, ref at, out var operation, out var operationLength), "An LDAP message without an operation.");
            message = new LdapMessageSeen(id, operation, operation == AbandonRequest ? ReadInteger(data, at, operationLength) : null);
            offset = end;
            return true;
        }

        /// <summary>A BER tag and its length (short or long form); false when the bytes are not all there yet.</summary>
        private static bool TryReadHeader(List<byte> data, ref int at, out byte tag, out int length)
        {
            tag = 0;
            length = 0;
            if (at + 2 > data.Count)
            {
                return false;
            }

            tag = data[at];
            var first = data[at + 1];
            var next = at + 2;
            if (first < 0x80)
            {
                length = first;
            }
            else
            {
                var count = first & 0x7F;
                if (next + count > data.Count)
                {
                    return false;
                }

                for (var i = 0; i < count; i++)
                {
                    length = (length << 8) | data[next + i];
                }

                next += count;
            }

            at = next;
            return true;
        }

        private static int ReadInteger(List<byte> data, int at, int length)
        {
            var value = 0;
            for (var i = 0; i < length; i++)
            {
                value = (value << 8) | data[at + i];
            }

            return value;
        }
    }
}
