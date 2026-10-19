using System.ComponentModel;
using System.Net;
using System.Runtime.Versioning;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// The optional HTTP.sys server (spec 17.3, 19.2) with real Windows authentication – Windows only, skipped elsewhere and
/// when the process may not register an HTTP.sys URL. The CI runner is not domain-joined, so its default credentials
/// authenticate with NTLM: with <c>RequireKerberos</c> they are refused, without it they work.
/// </summary>
public sealed class HttpSysTests
{
    /// <summary>Authenticated Users – a group SID every network logon carries.</summary>
    private const string AuthenticatedUsers = "S-1-5-11";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Kerberos", "Kerberos")]
    [InlineData("NtLmSsp ", "NTLM")]
    public void TokenSourceNames_MapToTheirPackage(string sourceName, string package)
    {
        Assert.Equal(package, WindowsLogonPackageResolver.PackageOfTokenSource(sourceName + "\0"));
        Assert.Null(WindowsLogonPackageResolver.PackageOfTokenSource("User32  "));
        Assert.Null(WindowsLogonPackageResolver.PackageOfTokenSource(null));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RequireKerberos_ChallengesWithNegotiate_AndRefusesNtlm()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "HTTP.sys is Windows only.");
        await using var server = await StartAsync(requireKerberos: true);

        using var anonymous = new HttpClient { BaseAddress = server.Address };
        using var challenge = await anonymous.GetAsync(WatchdogRoutes.Me, Ct);
        using var ntlm = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { BaseAddress = server.Address };
        using var refused = await ntlm.GetAsync(WatchdogRoutes.Me, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains(challenge.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
        Assert.DoesNotContain(challenge.Headers.WwwAuthenticate, h => h.Scheme == "Kerberos");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WithoutRequireKerberos_NtlmCallersGetTheirRole()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "HTTP.sys is Windows only.");
        await using var server = await StartAsync(requireKerberos: false);

        using var ntlm = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { BaseAddress = server.Address };
        using var me = await ntlm.GetAsync(WatchdogRoutes.Me, Ct);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<RunningServer> StartAsync(bool requireKerberos)
    {
        var directory = new TestDataDirectory();
        var address = new Uri($"http://localhost:{FreeTcpPort.Next()}/");
        var settings = new Dictionary<string, string?>
        {
            ["Watchdog:DataDirectory"] = directory.Path,
            ["Watchdog:AllowInsecureDataDirectoryAcl"] = "true",
            ["Watchdog:Authentication"] = "Negotiate",
            ["Watchdog:Hosting:Server"] = "HttpSys",
            ["Watchdog:Hosting:Urls"] = address.ToString(),
            ["Watchdog:Hosting:RequireKerberos"] = requireKerberos ? "true" : "false",
            ["Watchdog:Directory:Disabled"] = "true",
            ["Watchdog:Authorization:ViewerSids:0"] = AuthenticatedUsers,
        };
        var app = WatchdogHost.Build(["--environment", "Development"], builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        });
        try
        {
            await app.StartAsync(Ct);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            await app.DisposeAsync();
            directory.Dispose();
            Assert.Skip("Registering an HTTP.sys URL needs an elevated process.");
        }

        return new RunningServer(app, directory, address);
    }

    private sealed class RunningServer(WebApplication app, TestDataDirectory directory, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            directory.Dispose();
        }
    }
}
