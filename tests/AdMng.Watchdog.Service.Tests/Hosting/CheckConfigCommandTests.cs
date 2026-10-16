using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary><c>--check-config</c> exit codes (spec 19.1).</summary>
public sealed class CheckConfigCommandTests
{
    [Fact]
    public async Task ValidBootstrap_ExitsZero()
    {
        using var directory = new TestDataDirectory();

        var exitCode = await CheckConfigCommand.RunAsync(
        [
            $"--Watchdog:DataDirectory={directory.Path}",
            "--Watchdog:Hosting:Urls=https://admng-wd.contoso.com:8443",
            "--Watchdog:Hosting:Certificate:Subject=admng-wd.contoso.com",
            $"--Watchdog:Authorization:AdminSids:0={TestRoles.AdminGroup}",
        ]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task InvalidBootstrap_ExitsWithInvalidSettings()
    {
        using var directory = new TestDataDirectory();

        var exitCode = await CheckConfigCommand.RunAsync(
        [
            $"--Watchdog:DataDirectory={directory.Path}",
            "--Watchdog:Hosting:Urls=http://admng-wd.contoso.com:8080",
            "--Watchdog:Authorization:AdminSids:0=Domain Admins",
        ]);

        Assert.Equal(StartupExitCodes.InvalidSettings, exitCode);
    }
}
