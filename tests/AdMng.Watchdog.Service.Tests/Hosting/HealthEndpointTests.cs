using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary><c>/healthz</c>: anonymous for loopback, the machine's own addresses and the monitoring subnets only.</summary>
public sealed class HealthEndpointTests : IAsyncLifetime
{
    private readonly WatchdogFactory _factory = new(new Dictionary<string, string?>
    {
        ["Watchdog:Hosting:MonitoringSubnets:0"] = "10.20.30.0/24",
        ["Watchdog:Hosting:AllowedRemoteSubnets:0"] = "10.10.0.0/16",
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Healthz_AnswersOk_ToLoopback_WithoutAuthentication()
    {
        using var client = _factory.Client(account: null);

        using var response = await client.GetAsync(WatchdogRoutes.Health, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", Assert.Single(response.Headers.GetValues(WatchdogRoutes.ApiVersionHeader)));
    }

    [Theory]
    [InlineData("10.20.30.40", HttpStatusCode.OK)]
    [InlineData("10.10.1.1", HttpStatusCode.Forbidden)]
    [InlineData("192.0.2.7", HttpStatusCode.Forbidden)]
    [InlineData("::ffff:10.20.30.41", HttpStatusCode.OK)]
    public async Task Healthz_FromTheNetwork_OnlyForMonitoringSubnets(string remote, HttpStatusCode expected)
    {
        using var client = _factory.Client(account: null);
        client.DefaultRequestHeaders.Add(RemoteAddressStartupFilter.Header, remote);

        using var response = await client.GetAsync(WatchdogRoutes.Health, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("10.10.200.1", HttpStatusCode.OK)]
    [InlineData("10.20.30.40", HttpStatusCode.Forbidden)]
    public async Task Api_FromTheNetwork_OnlyForAllowedSubnets(string remote, HttpStatusCode expected)
    {
        using var client = _factory.Viewer();
        client.DefaultRequestHeaders.Add(RemoteAddressStartupFilter.Header, remote);

        using var response = await client.GetAsync(WatchdogRoutes.Me, Ct);

        Assert.Equal(expected, response.StatusCode);
    }
}
