using System.Net;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary><c>Hosting.Urls</c> parsing.</summary>
public sealed class ListenerEndpointTests
{
    [Theory]
    [InlineData("https://admng-wd.contoso.com:8443", true, null, 8443, false)]
    [InlineData("https://+:8443", true, null, 8443, false)]
    [InlineData("http://*:5000", false, null, 5000, false)]
    [InlineData("http://127.0.0.1:0", false, "127.0.0.1", 0, true)]
    [InlineData("http://localhost:5080/", false, "127.0.0.1", 5080, true)]
    [InlineData("https://[::1]:8443", true, "::1", 8443, true)]
    [InlineData("https://10.0.0.5", true, "10.0.0.5", 443, false)]
    [InlineData("http://0.0.0.0:80", false, null, 80, false)]
    public void Parse(string url, bool https, string? address, int port, bool loopback)
    {
        var endpoint = ListenerEndpoint.Parse(url);

        Assert.Equal(https, endpoint.Https);
        Assert.Equal(address is null ? null : IPAddress.Parse(address), endpoint.Address);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(loopback, endpoint.IsLoopback);
    }

    [Theory]
    [InlineData("ftp://host:21")]
    [InlineData("admng-wd.contoso.com:8443")]
    [InlineData("https://host:99999")]
    [InlineData("https://host:port")]
    [InlineData("https://[::1:8443")]
    public void InvalidUrls_Throw(string url)
    {
        Assert.Throws<FormatException>(() => ListenerEndpoint.Parse(url));
    }

    [Fact]
    public void ParseAll_SplitsOnSemicolons()
    {
        Assert.Equal([5080, 8443], ListenerEndpoint.ParseAll(" http://localhost:5080 ; https://+:8443 ;").Select(e => e.Port));
        Assert.Empty(ListenerEndpoint.ParseAll(null));
    }

    [Fact]
    public void HealthUri_UsesTheConfiguredName_OrLocalhostForWildcards()
    {
        Assert.Equal(
            "https://admng-wd.contoso.com:8443/healthz",
            HealthCommand.HealthUri(new ServiceOptions { Hosting = new HostingOptions { Urls = "https://admng-wd.contoso.com:8443" } })?.ToString());
        Assert.Equal(
            "https://localhost:8443/healthz",
            HealthCommand.HealthUri(new ServiceOptions { Hosting = new HostingOptions { Urls = "https://+:8443" } })?.ToString());
        Assert.Null(HealthCommand.HealthUri(new ServiceOptions { Hosting = new HostingOptions { Urls = "nonsense" } }));
    }

    [Theory]
    [InlineData(new[] { "--console", "--environment", "Development" }, HostCommandKind.Run, true, 2)]
    [InlineData(new[] { "--smoke-test" }, HostCommandKind.SmokeTest, false, 0)]
    [InlineData(new[] { "--CHECK-CONFIG", "--Watchdog:Authentication=Negotiate" }, HostCommandKind.CheckConfig, false, 1)]
    [InlineData(new[] { "--health" }, HostCommandKind.Health, false, 0)]
    public void CommandLine(string[] args, HostCommandKind kind, bool console, int hostArgs)
    {
        var command = HostCommand.Parse(args);

        Assert.Equal(kind, command.Kind);
        Assert.Equal(console, command.Console);
        Assert.Equal(hostArgs, command.HostArgs.Count);
    }
}
