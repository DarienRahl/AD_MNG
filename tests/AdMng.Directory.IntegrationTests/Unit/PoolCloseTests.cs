using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// Audit finding DLR-4: the pool never disposes a connection while a request runs on it. S.DS.P frees an attribute name
/// twice when its connection is disposed while it builds the entries of a response (<c>LdapConnection.ConstructEntry</c>,
/// dotnet/runtime#35189) - on Linux glibc aborts the process. The requests here never reach a server: the sender is a fake
/// whose requests end when the test says so, and each connection records whether a request of it still ran when it was
/// disposed. <c>CloseWhileRequestRunsTests</c> shows the same against the lab. Audit finding LDAP-MARKBROKEN-WINDOW: a request
/// never goes out on a connection that has just been dropped under another request (it moves to another connection).
/// </summary>
public sealed class PoolCloseTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisposeAsync_ReleasesTheCaller_AndDisposesTheConnectionOnlyOnceItsRequestEnded()
    {
        var sender = new FakeSender();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromSeconds(30));
        using var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken);
        var caller = lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
        var request = await sender.NextAsync();

        var closing = pool.DisposeAsync().AsTask();

        // The caller does not wait for a request that runs on: it learns at once that the session is gone ...
        await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.WaitAsync(Soon, TestContext.Current.CancellationToken));
        Assert.True(request.Cancelled.IsCancellationRequested);

        // ... a request that waits for the server is woken (Linux: the socket is shut down) ...
        await WaitUntilAsync(() => sender.Interrupted.Contains(connection));

        // ... while the connection stays open under the request that still builds its response, and so does the pool.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, connection.Disposals);
        Assert.False(closing.IsCompleted);

        request.End();

        await closing.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
    }

    [Fact]
    public async Task DisposeAsync_StopsWaitingAfterItsBound_AndTheConnectionIsDisposedWhenItsRequestEnds()
    {
        var sender = new FakeSender();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromMilliseconds(200));
        using var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken);
        _ = lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
        var request = await sender.NextAsync();

        // A request that never ends (a silent server) does not hold the close up beyond its bound ...
        await pool.DisposeAsync().AsTask().WaitAsync(Soon, TestContext.Current.CancellationToken);

        // ... and still its connection is not disposed under it, but when it ends.
        Assert.Equal(0, connection.Disposals);
        request.Fail(new LdapException(LdapErrors.ServerDown, "server down"));
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
    }

    [Fact]
    public async Task DisposeAsync_DisposesConnectionsWithoutARequestAtOnce_AlsoUnderAHeldLease()
    {
        var sender = new FakeSender();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromSeconds(30));
        using var lease = await pool.AcquireAsync(LeaseKind.PagedSearch, TestContext.Current.CancellationToken);

        await pool.DisposeAsync().AsTask().WaitAsync(Soon, TestContext.Current.CancellationToken);

        Assert.Equal(1, connection.Disposals);

        // A lease kept across the close sends nothing on the disposed connection.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None).WaitAsync(Soon, TestContext.Current.CancellationToken));
        Assert.Equal(0, sender.Sent);
    }

    [Fact]
    public async Task DisposeAsync_RequestsQueuedBehindARunningOne_GiveUpUnsent()
    {
        var sender = new FakeSender();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromSeconds(30));
        var leases = new List<LdapConnectionPool.ConnectionLease>();
        var running = new List<FakeRequest>();
        for (var i = 0; i < LdapConnectionPool.RequestsPerConnection; i++)
        {
            leases.Add(await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken));
            _ = leases[^1].SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
            running.Add(await sender.NextAsync());
        }

        // Every request slot of the only connection is taken: the next request waits for one.
        leases.Add(await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken));
        var queued = leases[^1].SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);

        var closing = pool.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<RequestNotSentException>(() => queued.WaitAsync(Soon, TestContext.Current.CancellationToken));
        Assert.Equal(LdapConnectionPool.RequestsPerConnection, sender.Sent);
        running.ForEach(r => r.End());
        await closing.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
        leases.ForEach(l => l.Dispose());
    }

    [Fact]
    public async Task ABrokenConnection_IsDisposedOnlyOnceTheLibraryIsDoneWithItsRequest()
    {
        // WinLDAP (the APM path) ends a request for its caller when it is abandoned or fails, while S.DS.P's task that
        // polls for the response and builds it can still run: Completion ends before Finished.
        var sender = new FakeSender { SeparateFinish = true };
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromSeconds(30));
        using (var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken))
        {
            var caller = lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
            var request = await sender.NextAsync();

            // "Server down": the pool drops the connection, its caller sees the error ...
            request.FailForCaller(new LdapException(LdapErrors.ServerDown, "server down"));
            await Assert.ThrowsAsync<LdapException>(() => caller.WaitAsync(Soon, TestContext.Current.CancellationToken));
            lease.Dispose();
            Assert.Equal(0, pool.Count);

            // ... and the connection is disposed when S.DS.P's task has ended, not before.
            Assert.Equal(0, connection.Disposals);
            request.End();
        }

        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AlsoWakesABrokenConnectionWhoseRequestStillRuns_AndDisposesItAfterwards()
    {
        var sender = new FakeSender();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromSeconds(30));
        using var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken);
        _ = lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
        var request = await sender.NextAsync();

        // The connection stopped answering (a silent server): it left the pool, its request still waits for an answer.
        lease.MarkBroken("no answer within the request timeout");
        Assert.Equal(0, pool.Count);

        var closing = pool.DisposeAsync().AsTask();

        // Closing the pool reaches it all the same: the waiting request is woken (Linux: the socket is shut down) ...
        await WaitUntilAsync(() => sender.Interrupted.Contains(connection));
        Assert.Equal(0, connection.Disposals);

        // ... and the connection is disposed once that request has ended.
        request.Fail(new LdapException(LdapErrors.ServerDown, "server down"));
        await closing.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
    }

    [Fact]
    public async Task IdleRecycling_LeavesAConnectionWhoseAbandonedRequestStillRuns()
    {
        var sender = new FakeSender { SeparateFinish = true };
        var clock = new ManualTimeProvider();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, connection, closeWait: TimeSpan.FromSeconds(30), clock);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        FakeRequest request;
        using (var lease = await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken))
        {
            var caller = lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), cts.Token);
            request = await sender.NextAsync();

            // The caller cancels: WinLDAP abandons the request and ends it for the caller at once; S.DS.P's task goes on.
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.WaitAsync(Soon, TestContext.Current.CancellationToken));
        }

        clock.Advance(LdapConnectionPool.IdleRecycleAfter + TimeSpan.FromSeconds(1));
        using (await pool.AcquireAsync(LeaseKind.Request, TestContext.Current.CancellationToken))
        {
            // Not "idle" while S.DS.P still uses it: it is not recycled (disposed) under the request.
            Assert.Equal(0, connection.Disposals);
        }

        request.End();
        await pool.DisposeAsync();
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
    }

    [Fact]
    public async Task AWriteWhoseConnectionBreaksAfterItPassedTheGate_IsNotSentThere_ButOnAnotherConnection()
    {
        // MarkBroken marks a connection broken under the pool's lock and wakes the connection's waiters only after it. A request
        // that had passed the connection's gate and its "dropped" check in that moment (Windows: eight requests share a
        // connection) was sent on the broken connection - a write the session must never repeat once sent then failed, or
        // waited for its whole timeout on a connection that no longer answered. The seam breaks the connection in that moment.
        var sender = new FakeSender();
        var first = new RecordingConnection(sender);
        var second = new RecordingConnection(sender);
        var broke = 0;
        // A short close wait: had the delete gone out on the broken connection, its fake request would never end.
        var pool = NewPool(sender, [first, second], closeWait: TimeSpan.FromMilliseconds(200), gateEntered: lease =>
        {
            if (ReferenceEquals(lease.Connection, first) && Interlocked.Exchange(ref broke, 1) == 0)
            {
                lease.MarkBroken("server down");
            }
        });
        await using var session = NewSession(pool);

        var delete = session.DeleteAsync("CN=Grzegorz Brzęczyszczykiewicz,OU=Księgowość,DC=contoso,DC=test", subtree: false, TestContext.Current.CancellationToken);
        var request = await sender.NextAsync();

        Assert.Equal(1, Volatile.Read(ref broke));
        Assert.True(ReferenceEquals(request.Connection, second), "The delete went out on the connection that had just broken.");
        request.End();
        await delete.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.Equal(1, sender.Sent);

        // The broken connection left the pool and was disposed once its lease was returned, nothing running on it.
        Assert.Equal(1, pool.Count);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, first.DisposedWhileRunning);
    }

    [Fact]
    public async Task APagedSearchWhoseConnectionBreaksAfterItPassedTheGate_StillTriesItsOwnConnection()
    {
        // A paged search cannot move: the server keeps its result set on this connection. It sends on it and learns of the
        // failure from its own request, as before.
        var sender = new FakeSender();
        var connection = new RecordingConnection(sender);
        var pool = NewPool(sender, [connection], closeWait: TimeSpan.FromSeconds(30), gateEntered: lease => lease.MarkBroken("server down"));
        using (var lease = await pool.AcquireAsync(LeaseKind.PagedSearch, TestContext.Current.CancellationToken))
        {
            var page = lease.SendAsync<SearchResponse>(Search(), TimeSpan.FromMinutes(1), CancellationToken.None);
            var request = await sender.NextAsync();

            Assert.Same(connection, request.Connection);
            request.Fail(new LdapException(LdapErrors.ServerDown, "server down"));
            await Assert.ThrowsAsync<LdapException>(() => page.WaitAsync(Soon, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, pool.Count);
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.DisposedWhileRunning);
        await pool.DisposeAsync();
    }

    private static LdapConnectionPool NewPool(FakeSender sender, RecordingConnection connection, TimeSpan closeWait, TimeProvider? clock = null) =>
        NewPool(sender, [connection], closeWait, clock);

    private static LdapConnectionPool NewPool(
        FakeSender sender,
        RecordingConnection[] connections,
        TimeSpan closeWait,
        TimeProvider? clock = null,
        Action<LdapConnectionPool.ConnectionLease>? gateEntered = null)
    {
        var settings = ConnectionSettings.Create(new ConnectionOptions { Server = "dc1.contoso.test" }, credential: null, OperatingSystem.IsWindows());
        var factory = new LdapConnectionFactory(settings, "dc1.contoso.test", "contoso.test", NullLogger.Instance);
        var pool = new LdapConnectionPool(factory, maxConnections: connections.Length, NullLogger.Instance, clock ?? TimeProvider.System)
        {
            Sender = sender,
            CloseWait = closeWait,
            RequestGateEntered = gateEntered,
        };
        foreach (var connection in connections)
        {
            pool.Add(connection);
        }

        return pool;
    }

    /// <summary>A session over <paramref name="pool"/>, as the connector hands it out (its retries and error handling).</summary>
    private static LdapDirectorySession NewSession(LdapConnectionPool pool)
    {
        var info = new SessionInfo
        {
            Server = "dc1.contoso.test",
            Port = 389,
            DomainDnsName = "contoso.test",
            DefaultNamingContext = "DC=contoso,DC=test",
            ConfigurationNamingContext = "CN=Configuration,DC=contoso,DC=test",
            SchemaNamingContext = "CN=Schema,CN=Configuration,DC=contoso,DC=test",
            BoundIdentity = "łukasz.wójcik@contoso.test",
            Authentication = AuthenticationMode.Credentials,
        };
        return new LdapDirectorySession(info, pool, channelEncrypted: true, NullLogger.Instance, TimeProvider.System);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Soon;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static SearchRequest Search() => new("DC=contoso,DC=test", "(objectClass=user)", System.DirectoryServices.Protocols.SearchScope.Subtree, null);

    /// <summary>An LDAP connection that never connects and records whether a request of it ran when it was disposed.</summary>
    private sealed class RecordingConnection(FakeSender sender) : LdapConnection(new LdapDirectoryIdentifier("127.0.0.1", 1))
    {
        private int _disposals;
        private int _disposedWhileRunning;

        public int Disposals => Volatile.Read(ref _disposals);

        public int DisposedWhileRunning => Volatile.Read(ref _disposedWhileRunning);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposals);
                if (sender.IsRunningOn(this))
                {
                    Interlocked.Increment(ref _disposedWhileRunning);
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Requests that end when the test ends them. Like the blocking path of <see cref="LdapAsync"/> by default: the caller's
    /// await ends on cancellation, the request itself only when ended. With <see cref="SeparateFinish"/> like the APM path:
    /// the request can end for its caller (and the pool's request slot) before S.DS.P is done with it.
    /// </summary>
    private sealed class FakeSender : ILdapRequestSender
    {
        private readonly ConcurrentQueue<FakeRequest> _requests = new();
        private readonly ConcurrentDictionary<FakeRequest, LdapConnection> _running = new();
        private readonly SemaphoreSlim _sent = new(0);
        private int _count;

        public bool SeparateFinish { get; init; }

        public ConcurrentBag<LdapConnection> Interrupted { get; } = [];

        public int Sent => Volatile.Read(ref _count);

        public PendingResponse<TResponse> Send<TResponse>(LdapConnection connection, DirectoryRequest request, TimeSpan timeout, CancellationToken cancellationToken)
            where TResponse : DirectoryResponse
        {
            var fake = new FakeRequest(connection, cancellationToken, this);
            _running[fake] = connection;
            Interlocked.Increment(ref _count);
            _requests.Enqueue(fake);
            _sent.Release();
            if (!SeparateFinish)
            {
                // Blocking path: Response is the request awaited with the caller's token, Completion = Finished = the request.
                var work = fake.Work.ContinueWith(
                    static ended =>
                    {
                        ended.GetAwaiter().GetResult();
                        return (TResponse)null!;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return new PendingResponse<TResponse>(work.WaitAsync(cancellationToken), work);
            }

            // APM path: the caller's result ends on abort or failure (and then frees the request slot); S.DS.P's task later.
            var completion = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.FailCaller = error => completion.TrySetException(error);
            _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            fake.Work.ContinueWith(_ => completion.TrySetResult(null!), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new PendingResponse<TResponse>(completion.Task, completion.Task, fake.Work);
        }

        public void Interrupt(LdapConnection connection) => Interrupted.Add(connection);

        public bool IsRunningOn(LdapConnection connection) => _running.Values.Any(c => ReferenceEquals(c, connection));

        public void Ended(FakeRequest request) => _running.TryRemove(request, out _);

        public async Task<FakeRequest> NextAsync()
        {
            Assert.True(await _sent.WaitAsync(Soon, TestContext.Current.CancellationToken), "No request was sent.");
            Assert.True(_requests.TryDequeue(out var request));
            return request;
        }
    }

    /// <summary>One request of <see cref="FakeSender"/>.</summary>
    private sealed class FakeRequest(LdapConnection connection, CancellationToken cancelled, FakeSender sender)
    {
        private readonly TaskCompletionSource _work = new();

        /// <summary>The connection the request was sent on.</summary>
        public LdapConnection Connection { get; } = connection;

        public CancellationToken Cancelled { get; } = cancelled;

        public Task Work => _work.Task;

        /// <summary>APM path: fails what the caller awaits (set by the sender).</summary>
        public Action<Exception>? FailCaller { get; set; }

        /// <summary>The request ended (S.DS.P returned): its connection is free.</summary>
        public void End()
        {
            sender.Ended(this);
            _work.TrySetResult();
        }

        /// <summary>The request failed and ended.</summary>
        public void Fail(Exception error)
        {
            sender.Ended(this);
            _work.TrySetException(error);
        }

        /// <summary>APM path: the caller gets the error while S.DS.P's task still runs.</summary>
        public void FailForCaller(Exception error) => FailCaller!(error);
    }
}
