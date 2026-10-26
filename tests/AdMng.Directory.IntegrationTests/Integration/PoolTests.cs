using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class PoolTests
{
    [Fact]
    public async Task Pool_GrowsWhileBusy_UpToMaxConnections()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var options = LabSettings.Plain(LabSettings.AdminUser) with { MaxConnections = 3 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);
        var pool = ((LdapDirectorySession)session).Pool;
        var leases = new List<LdapConnectionPool.ConnectionLease>();
        try
        {
            // Every lease keeps its connection busy; a busy pool opens one more connection in the background.
            for (var i = 0; i < 12 && pool.Count < 3; i++)
            {
                leases.Add(await pool.AcquireAsync(LeaseKind.Request, ct));
                await WaitUntilAsync(() => pool.Count > 1 || i == 0, ct);
            }

            await WaitUntilAsync(() => pool.Count == 3, ct);
            Assert.Equal(3, pool.Count);

            // Never more than MaxConnections, however busy.
            for (var i = 0; i < 6; i++)
            {
                leases.Add(await pool.AcquireAsync(LeaseKind.Request, ct));
            }

            await Task.Delay(200, ct);
            Assert.Equal(3, pool.Count);
            Assert.Equal(3, leases.Select(l => l.ConnectionId).Distinct().Count());
        }
        finally
        {
            leases.ForEach(l => l.Dispose());
        }

        // Released connections stay usable.
        Assert.NotNull(await session.GetAsync(LabSettings.DomainDn, [Attr.Name], ct));
    }

    [Fact]
    public async Task PagedSearchSlots_AreLimitedPerConnection()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var options = LabSettings.Plain(LabSettings.AdminUser) with { MaxConnections = 1 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);
        var pool = ((LdapDirectorySession)session).Pool;
        var leases = new List<LdapConnectionPool.ConnectionLease>();
        try
        {
            for (var i = 0; i < LdapConnectionPool.PagedSearchesPerConnection; i++)
            {
                leases.Add(await pool.AcquireAsync(LeaseKind.PagedSearch, ct));
            }

            // The single connection has no free result set slot: the next paged search waits ...
            var waiting = pool.AcquireAsync(LeaseKind.PagedSearch, ct).AsTask();
            await Task.Delay(200, ct);
            Assert.False(waiting.IsCompleted);

            // ... ordinary requests do not.
            using (var request = await pool.AcquireAsync(LeaseKind.Request, ct))
            {
                Assert.Equal(leases[0].ConnectionId, request.ConnectionId);
            }

            leases[0].Dispose();
            using var granted = await waiting.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(leases[0].ConnectionId, granted.ConnectionId);
        }
        finally
        {
            leases.ForEach(l => l.Dispose());
        }
    }

    [Fact]
    public async Task SingleConnectionPool_StillServesConcurrentRequests()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var options = LabSettings.Plain(LabSettings.AdminUser) with { MaxConnections = 1 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct)));

        Assert.All(results, r => Assert.Equal("Firma", r!.GetString(Attr.Ou)));
        Assert.Equal(1, ((LdapDirectorySession)session).Pool.Count);
    }

    [Fact]
    public async Task IdleConnections_AreRecycledBeforeTheServerDropsThem()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var logs = new CapturingLoggerFactory();
        await using var session = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            logs,
            clock,
            ct);
        var pool = ((LdapDirectorySession)session).Pool;

        // Connecting leaves work behind (pool growth, abandons of paged searches) that could still touch a connection
        // after the clock jumped. Start from a quiet pool.
        await WaitUntilIdleAsync(pool, ct);
        var connections = pool.Count;
        Assert.NotNull(await session.GetAsync(LabSettings.DomainDn, [], ct));

        clock.Advance(LdapConnectionPool.IdleRecycleAfter + TimeSpan.FromSeconds(1));
        var afterIdle = await session.GetAsync(LabSettings.DomainDn, [Attr.Name], ct);

        // Every connection was reopened before the next read - also the one the last read used.
        Assert.NotNull(afterIdle);
        Assert.Equal(connections, logs.WithEventId(1104).Count);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task SequentialRequests_FindTheirConnectionFree_AndDoNotGrowThePool()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var options = LabSettings.Plain(LabSettings.AdminUser) with { MaxConnections = 2 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);
        var pool = ((LdapDirectorySession)session).Pool;
        await WaitUntilIdleAsync(pool, ct);
        var connections = pool.Count;

        // A request that returned has freed its connection, so the next one does not find it busy (which would open
        // another connection, or queue behind the finished request on a single connection).
        for (var i = 1; i <= 10; i++)
        {
            Assert.NotNull(await session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct));
            Assert.True(pool.IsIdle, $"Read {i} returned while its connection was still busy.");
        }

        Assert.Equal(connections, pool.Count);
    }

    [Fact]
    public async Task DroppedConnections_AreReplacedTransparentlyForReads()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new TcpProxy(LabSettings.Server!, 389);
        var logs = new CapturingLoggerFactory();
        var options = LabSettings.Plain(LabSettings.AdminUser) with { Server = "127.0.0.1", Port = proxy.Port, MaxConnections = 2 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, logs, cancellationToken: ct);
        Assert.NotNull(await session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct));

        // The DC "restarts": every TCP connection of the session is reset.
        proxy.DropAll();

        var read = await session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct);
        Assert.Equal("Firma", read!.GetString(Attr.Ou));
        Assert.NotEmpty(logs.WithEventId(1101)); // connection dropped from the pool

        // Paged searches recover as long as no page was delivered yet.
        proxy.DropAll();
        var count = 0;
        await foreach (var _ in session.SearchAsync(new DirectorySearch { BaseDn = LabSettings.EmployeesOu, SizeLimit = 30, PageSize = 10 }, ct))
        {
            count++;
        }

        Assert.Equal(30, count);
    }

    [Fact]
    public async Task CancelledRead_StillRunning_DoesNotHoldUpTheNextRequest()
    {
        LabSettings.RequireLab();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "WinLDAP abandons a cancelled request and frees the connection at once.");
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new TcpProxy(LabSettings.Server!, 389);
        var clock = new ManualTimeProvider();
        var options = LabSettings.Plain(LabSettings.AdminUser) with { Server = "127.0.0.1", Port = proxy.Port, MaxConnections = 2 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, timeProvider: clock, cancellationToken: ct);
        var pool = ((LdapDirectorySession)session).Pool;
        try
        {
            // Start from exactly one connection: recycle the ones opened while connecting.
            await WaitUntilIdleAsync(pool, ct);
            clock.Advance(LdapConnectionPool.IdleRecycleAfter + TimeSpan.FromSeconds(1));
            Assert.NotNull(await session.GetAsync(LabSettings.DomainDn, [], ct));
            Assert.Equal(1, pool.Count);
            await WaitUntilIdleAsync(pool, ct);

            // That connection's answers are held back from now on; a second one, opened while it is busy, is fine.
            proxy.StallExisting();
            using (await pool.AcquireAsync(LeaseKind.Request, ct))
            using (await pool.AcquireAsync(LeaseKind.Request, ct))
            {
                await WaitUntilAsync(() => pool.Count == 2 && !pool.IsGrowing, ct);
            }

            Assert.Equal(2, pool.Count);
            Assert.True(pool.IsIdle);

            // A read on the stalled connection is cancelled. The blocking S.DS.P call behind it keeps running
            // (and keeps the connection's only request slot) until the server answers.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stuck = session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], cts.Token);
            await Task.Delay(200, ct);
            Assert.False(stuck.IsCompleted);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck);

            // The next read uses the free connection instead of queueing behind the abandoned one.
            var read = await session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("Firma", read!.GetString(Attr.Ou));
        }
        finally
        {
            proxy.ResumeAll();
        }
    }

    [Fact]
    public async Task SilentlyDeadConnections_AreReplacedAfterATimeout()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new TcpProxy(LabSettings.Server!, 389);
        var logs = new CapturingLoggerFactory();
        var options = LabSettings.Plain(LabSettings.AdminUser) with
        {
            Server = "127.0.0.1",
            Port = proxy.Port,
            MaxConnections = 2,
            RequestTimeout = TimeSpan.FromSeconds(5),
        };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, logs, cancellationToken: ct);
        var pool = ((LdapDirectorySession)session).Pool;
        try
        {
            // Nothing may run while the connections are stalled (e.g. abandons of the connect searches).
            await WaitUntilIdleAsync(pool, ct);

            // Every existing connection stops answering without being reset (VPN switch, firewall drop).
            proxy.StallExisting();
            for (var attempt = 0; attempt < pool.MaxConnections; attempt++)
            {
                var timeout = await Assert.ThrowsAsync<DirectoryException>(() => session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct));
                Assert.Equal(DirectoryErrorKind.Timeout, timeout.Kind);
                if (pool.Count == 0)
                {
                    break;
                }
            }

            // Each timed out connection was dropped, so the next request runs on a fresh one (connect + bind) instead of
            // waiting for the request timeout of a stalled connection. The margin leaves room for slow CI runners.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var read = await session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct);
            Assert.Equal("Firma", read!.GetString(Attr.Ou));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3.5), $"Took {stopwatch.Elapsed} (request timeout 5 s).");
            Assert.NotEmpty(logs.WithEventId(1101));
        }
        finally
        {
            proxy.ResumeAll();
        }
    }

    [Fact]
    public async Task RequestsQueuedOnAConnectionThatStoppedAnswering_MoveToANewOne()
    {
        LabSettings.RequireLab();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "WinLDAP sends all of them on the shared connection; each times out by itself.");
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new TcpProxy(LabSettings.Server!, 389);
        var options = LabSettings.Plain(LabSettings.AdminUser) with
        {
            Server = "127.0.0.1",
            Port = proxy.Port,
            MaxConnections = 1,
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);
        var pool = ((LdapDirectorySession)session).Pool;
        try
        {
            // Nothing may run while the connection is stalled (e.g. abandons of the connect searches).
            await WaitUntilIdleAsync(pool, ct);
            proxy.StallExisting();

            // The first read is stuck on the only connection; the others queue behind it (one request at a time).
            var stuck = session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct);
            await Task.Delay(100, ct);
            var queued = Enumerable.Range(0, 3).Select(_ => session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct)).ToList();

            var timeout = await Assert.ThrowsAsync<DirectoryException>(() => stuck);
            Assert.Equal(DirectoryErrorKind.Timeout, timeout.Kind);

            // Dropping the silent connection released the queued reads, which ran on a new connection.
            var results = await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(15), ct);
            Assert.All(results, r => Assert.Equal("Firma", r!.GetString(Attr.Ou)));
        }
        finally
        {
            proxy.ResumeAll();
        }
    }

    [Fact]
    public async Task UnreachableServer_AfterConnect_IsServerUnavailable()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var proxy = new TcpProxy(LabSettings.Server!, 389);
        var options = LabSettings.Plain(LabSettings.AdminUser) with { Server = "127.0.0.1", Port = proxy.Port, MaxConnections = 1 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);

        await proxy.DisposeAsync(); // listener gone and connections reset - the DC is down

        var error = await Assert.ThrowsAsync<DirectoryException>(() => session.GetAsync(LabSettings.CompanyOu, [Attr.Ou], ct));
        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task SessionDispose_ClearsTheCredential()
    {
        LabSettings.RequireLab();
        var session = (LdapDirectorySession)await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken);
        var settings = session.Pool.Factory.Settings;
        Assert.NotNull(settings.Credential);

        await session.DisposeAsync();

        Assert.Null(settings.Credential);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Pool.Factory.OpenAsync(false, TestContext.Current.CancellationToken));
    }

    private static async Task WaitUntilIdleAsync(LdapConnectionPool pool, CancellationToken ct)
    {
        await WaitUntilAsync(() => pool.IsIdle, ct);
        Assert.True(pool.IsIdle, "The pool is still busy (background requests or growth).");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }
    }

    [Fact]
    public async Task Connect_WithRequestTimeout_PropagatesToRequests()
    {
        LabSettings.RequireLab();
        var options = LabSettings.Plain(LabSettings.AdminUser) with { RequestTimeout = TimeSpan.FromSeconds(30) };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(30), ((LdapDirectorySession)session).Pool.Factory.Settings.Options.RequestTimeout);
        Assert.Equal(AuthenticationMode.SimpleBind, session.Info.Authentication);
    }
}
