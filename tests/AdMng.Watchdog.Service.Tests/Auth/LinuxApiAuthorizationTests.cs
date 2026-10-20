using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Auth;

/// <summary>
/// Linux API rules around the role resolution (stage B design 4.3 item 1, 6.1, 6.2): loopback and the host's own
/// addresses are not exempt from <c>AllowedRemoteSubnets</c> under Negotiate, the realm is required, the gMSA host group
/// is obsolete, and the tokenGroups permission probe is enabled.
/// </summary>
public sealed class LinuxApiAuthorizationTests
{
    private const string ForgedTicket = "YIIBhwYGKwYBBQUCoIIBezCCAXegDTALBgkqhkiG9xIBAgI=";

    [Fact]
    public void LocalCallers_AreExemptEverywhereButLinuxNegotiate()
    {
        Assert.False(RemoteAddressFilter.ExemptsLocalCallers(new ServiceOptions(), linux: true));
        Assert.True(RemoteAddressFilter.ExemptsLocalCallers(new ServiceOptions(), linux: false));
        Assert.True(RemoteAddressFilter.ExemptsLocalCallers(new ServiceOptions { Authentication = WatchdogAuthenticationMode.Development }, linux: true));
        Assert.True(RemoteAddressFilter.ExemptsLocalCallers(new ServiceOptions { Authentication = WatchdogAuthenticationMode.Test }, linux: true));
    }

    /// <summary>A forged-name ticket presented from the server itself (4.3: a local process holding the shared keytab) never
    /// reaches the Negotiate handler: 403 before authentication, also with an empty subnet list.</summary>
    [Theory]
    [InlineData("127.0.0.1", new[] { "10.0.20.0/24" }, false)]
    [InlineData("::1", new[] { "10.0.20.0/24" }, false)]
    [InlineData("127.0.0.1", new string[0], false)]
    [InlineData("127.0.0.1", new[] { "127.0.0.1/32" }, true)]
    [InlineData("10.0.20.7", new[] { "10.0.20.0/24" }, true)]
    [InlineData("10.0.30.7", new[] { "10.0.20.0/24" }, false)]
    [InlineData("10.0.30.7", new string[0], true)]
    public async Task ForgedTicketOverLoopback_Is403_OnLinux(string remote, string[] subnets, bool passes)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The loopback rule applies to the Linux service.");
        var (status, called) = await InvokeAsync(remote, WatchdogRoutes.Me, subnets);

        Assert.Equal(passes, called);
        Assert.Equal(passes ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task Health_StillAnswersLoopback_OnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The loopback rule applies to the Linux service.");

        var (status, called) = await InvokeAsync("127.0.0.1", WatchdogRoutes.Health, ["10.0.20.0/24"]);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task InProcessRequests_WithoutRemoteAddress_CountAsLocal()
    {
        var (status, called) = await InvokeAsync(null, WatchdogRoutes.Me, ["10.0.20.0/24"]);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void Validation_RequiresTheRealmAndFlagsTheHostGroup_OnLinux()
    {
        var options = new ServiceOptions { Authorization = new AuthorizationOptions { WatchdogHostGroupSids = ["S-1-5-21-1-2-3-1500"] } };
        var issues = new List<ConfigIssue>();

        ServiceOptionsValidator.CheckLinuxAuthorization(issues, options, linux: true);

        Assert.Equal(IssueSeverity.Error, Assert.Single(issues, i => i.Path == "Watchdog:Kerberos:Realm").Severity);
        Assert.Equal(IssueSeverity.Warning, Assert.Single(issues, i => i.Code == "obsoleteOnLinux").Severity);

        issues.Clear();
        ServiceOptionsValidator.CheckLinuxAuthorization(issues, options with { Kerberos = new KerberosBootstrapOptions { Realm = "CONTOSO.COM" } }, linux: false);
        Assert.Empty(issues);
    }

    [Fact]
    public void DetectionHostOptions_IgnoreTheHostGroupAndProbeTokenGroups_OnLinux()
    {
        var options = new ServiceOptions
        {
            Authorization = new AuthorizationOptions { AdminSids = [TestRoles.AdminGroup], WatchdogHostGroupSids = ["S-1-5-21-1-2-3-1500"] },
        };

        var linux = ServiceRegistration.DetectionHostOptionsFrom(options, linux: true);
        var windows = ServiceRegistration.DetectionHostOptionsFrom(options, linux: false);

        Assert.Equal([TestRoles.AdminGroup], linux.RoleGroupSids);
        Assert.True(linux.ProbeTokenGroups);
        Assert.Equal([TestRoles.AdminGroup, "S-1-5-21-1-2-3-1500"], windows.RoleGroupSids);
        Assert.False(windows.ProbeTokenGroups);
    }

    private static async Task<(int Status, bool Called)> InvokeAsync(string? remote, string path, string[] subnets)
    {
        var options = new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions { Hosting = new HostingOptions { AllowedRemoteSubnets = subnets } });
        var called = false;
        var filter = new RemoteAddressFilter(_ => { called = true; return Task.CompletedTask; }, options, new MachineAddresses(new FakeTimeProvider()));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Negotiate " + ForgedTicket;

        await filter.InvokeAsync(context);

        return (context.Response.StatusCode, called);
    }
}
