using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>Where the Development scheme is refused (spec 8.4, 17.3).</summary>
public sealed class DevelopmentAuthGuardTests
{
    private static readonly ServiceOptions Development = new() { Authentication = WatchdogAuthenticationMode.Development };

    [Fact]
    public void LoopbackDevelopmentConsole_IsAllowed()
    {
        Assert.Null(DevelopmentAuthGuard.Check(Development, "Development", Context("http://127.0.0.1:5080;http://localhost:5081")));
    }

    [Fact]
    public void ANonLoopbackListener_IsRefused()
    {
        Assert.NotNull(DevelopmentAuthGuard.Check(Development, "Development", Context("http://127.0.0.1:5080;https://+:8443")));
    }

    [Fact]
    public void OutsideDevelopment_IsRefused()
    {
        Assert.NotNull(DevelopmentAuthGuard.Check(Development, "Production", Context("http://127.0.0.1:5080")));
    }

    [Fact]
    public void AsAWindowsService_IsRefused()
    {
        Assert.NotNull(DevelopmentAuthGuard.Check(Development, "Development", Context("http://127.0.0.1:5080") with { IsWindowsService = true }));
    }

    [Fact]
    public void AsASystemdUnit_IsRefused()
    {
        Assert.NotNull(DevelopmentAuthGuard.Check(Development, "Development", Context("http://127.0.0.1:5080") with { IsSystemdService = true }));
    }

    [Fact]
    public void OtherModes_AreNotConcerned()
    {
        Assert.Null(DevelopmentAuthGuard.Check(new ServiceOptions(), "Production", Context("https://+:8443") with { IsWindowsService = true }));
    }

    private static StartupContext Context(string urls) => new() { Endpoints = ListenerEndpoint.ParseAll(urls) };
}
