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
        Assert.NotNull(await session.GetAsync(LabSettings.DomainDn, [], ct));

        clock.Advance(LdapConnectionPool.IdleRecycleAfter + TimeSpan.FromSeconds(1));
        var afterIdle = await session.GetAsync(LabSettings.DomainDn, [Attr.Name], ct);

        Assert.NotNull(afterIdle);
        Assert.NotEmpty(logs.WithEventId(1104));
        Assert.True(((LdapDirectorySession)session).Pool.Count >= 1);
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
