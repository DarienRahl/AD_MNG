using System.Net;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Directory;

/// <summary>Stage B design 5.3: <c>Directory.Servers</c> order, failover after <c>FailoverAfter</c>, and the health of a
/// refused (unsealed) Kerberos bind (5.1).</summary>
public sealed class DirectoryServersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ServerList_UsesServersElseTheSingleServer()
    {
        Assert.Equal(["dc01.contoso.com", "dc02.contoso.com"],
            ServiceDirectorySessionProvider.ServerList(new DirectoryBootstrapOptions { Servers = [" dc01.contoso.com. ", "", "DC01.contoso.com", "dc02.contoso.com"], Server = "x" }));
        Assert.Equal(["dc09.contoso.com"], ServiceDirectorySessionProvider.ServerList(new DirectoryBootstrapOptions { Server = "dc09.contoso.com" }));
        Assert.Empty(ServiceDirectorySessionProvider.ServerList(new DirectoryBootstrapOptions()));
    }

    [Fact]
    public void Candidates_KeepThePinnedDcUntilFailoverAfter_ThenTryTheNextOnes()
    {
        string[] servers = ["dc01", "dc02", "dc03"];
        var after = TimeSpan.FromMinutes(10);

        Assert.Equal(servers, ServiceDirectorySessionProvider.Candidates(servers, null, null, after));
        Assert.Equal(["dc02"], ServiceDirectorySessionProvider.Candidates(servers, "DC02", TimeSpan.FromMinutes(3), after));
        Assert.Equal(["dc02", "dc03", "dc01"], ServiceDirectorySessionProvider.Candidates(servers, "dc02", TimeSpan.FromMinutes(11), after));
        Assert.Equal(servers, ServiceDirectorySessionProvider.Candidates(servers, "dc09", TimeSpan.FromMinutes(3), after));
    }

    [Fact]
    public async Task UnreachableFirstServer_UsesTheNext_AndFailsOverOnlyAfterFailoverAfter()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var connector = new ScriptedConnector { Down = { "dc01.contoso.com" } };
        await using var provider = Provider(connector, clock, new DirectoryBootstrapOptions { Servers = ["dc01.contoso.com", "dc02.contoso.com"], PreferPdc = false });

        var first = await provider.GetSessionAsync(Ct);
        Assert.Equal(["dc01.contoso.com", "dc02.contoso.com"], connector.Attempts);

        // dc02 fails: retried alone until FailoverAfter (10 min), then dc01 is tried again.
        connector.Attempts.Clear();
        connector.Down.Clear();
        connector.Down.Add("dc02.contoso.com");
        provider.ReportFailure(first, new DirectoryException(DirectoryErrorKind.ServerUnavailable, "DC02 nie odpowiada"));
        await Assert.ThrowsAsync<DirectoryException>(async () => await provider.GetSessionAsync(Ct));
        Assert.Equal(["dc02.contoso.com"], connector.Attempts);

        clock.Advance(TimeSpan.FromMinutes(11));
        connector.Attempts.Clear();
        var second = await provider.GetSessionAsync(Ct);
        Assert.Equal(["dc02.contoso.com", "dc01.contoso.com"], connector.Attempts);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task RefusedUnsealedBind_IsDirectoryNotSealed()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The SSF check is the Linux path.");
        }

        var health = new FakeHealthRegistry();
        var connector = new ScriptedConnector { NotSealed = { "dc01.contoso.com" } };
        await using var provider = Provider(connector, TimeProvider.System, new DirectoryBootstrapOptions { Servers = ["dc01.contoso.com"] }, health);

        var error = await Assert.ThrowsAsync<DirectoryException>(async () => await provider.GetSessionAsync(Ct));

        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, error.Kind);
        var check = Assert.Single(health.Checks, c => c.Code == HealthCheckCodes.DirectoryNotSealed);
        Assert.Equal(HealthState.Error, check.State);
        Assert.Equal(["dc01.contoso.com"], check.Args);
    }

    [Fact]
    public void Validation_RequiresDcHostNamesOnLinux()
    {
        var issues = new List<ConfigIssue>();
        ServiceOptionsValidator.CheckDirectoryServers(issues, new DirectoryBootstrapOptions(), linux: true);
        ServiceOptionsValidator.CheckDirectoryServers(issues, new DirectoryBootstrapOptions { Servers = ["10.0.1.10"] }, linux: true);
        ServiceOptionsValidator.CheckDirectoryServers(issues, new DirectoryBootstrapOptions(), linux: false);
        ServiceOptionsValidator.CheckDirectoryServers(issues, new DirectoryBootstrapOptions { Disabled = true }, linux: true);

        Assert.Equal(["required", "ipLiteral"], issues.Select(i => i.Code));
    }

    private static ServiceDirectorySessionProvider Provider(
        ScriptedConnector connector, TimeProvider clock, DirectoryBootstrapOptions directory, FakeHealthRegistry? health = null)
    {
        var options = new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions { Directory = directory });
        return new ServiceDirectorySessionProvider(
            connector,
            new DirectoryConnectionOptionsFactory(options, new FakeHostEnvironment()),
            options,
            new TestLog().For<ServiceDirectorySessionProvider>(),
            health ?? new FakeHealthRegistry(),
            new FakeConfigProvider(new WatchdogConfiguration()),
            clock);
    }

    private sealed class ScriptedConnector : IDirectoryConnector
    {
        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NotSealed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Attempts { get; } = [];

        public string? GetComputerDomain() => "contoso.com";

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
        {
            var server = options.Server ?? "located";
            Attempts.Add(server);
            if (NotSealed.Contains(server))
            {
                throw new DirectoryException(DirectoryErrorKind.StrongAuthRequired, "SSF 0");
            }

            return Down.Contains(server)
                ? throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, server + " nie odpowiada")
                : Task.FromResult<IDirectorySession>(new FakeDirectoryBuilder().Build());
        }
    }
}
