using System.Net;
using System.Security.Claims;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;

using RoleOptions = AdMng.Watchdog.Service.Hosting.AuthorizationOptions;

namespace AdMng.Watchdog.Service.Tests.Auth;

/// <summary>Roles from group SIDs, the deny list first (spec 17.3), and the remote-address subnets.</summary>
public sealed class SidRoleRequirementTests
{
    private static readonly RoleOptions Roles = new()
    {
        ViewerSids = [TestRoles.ViewerGroup],
        AdminSids = [TestRoles.AdminGroup.ToLowerInvariant()],
        DeniedSids = [TestRoles.DeniedUser],
    };

    [Fact]
    public void AdminImpliesViewer_AndSidsCompareCaseInsensitively()
    {
        Assert.Equal([WatchdogRole.Viewer, WatchdogRole.Admin], SidRoles.Evaluate(User(groups: [TestRoles.AdminGroup]), Roles));
        Assert.Equal([WatchdogRole.Viewer], SidRoles.Evaluate(User(groups: [TestRoles.ViewerGroup]), Roles));
        Assert.Empty(SidRoles.Evaluate(User(groups: [TestRoles.OtherGroup]), Roles));
    }

    [Fact]
    public void PrimaryGroupSid_Counts_TheUserSidDoesNot()
    {
        var primary = User(groups: []);
        ((ClaimsIdentity)primary.Identity!).AddClaim(new Claim(ClaimTypes.PrimaryGroupSid, TestRoles.ViewerGroup));
        var userSid = User(groups: [], userSid: TestRoles.AdminGroup);

        Assert.Equal([WatchdogRole.Viewer], SidRoles.Evaluate(primary, Roles));
        Assert.Empty(SidRoles.Evaluate(userSid, Roles));
    }

    [Fact]
    public void DeniedSids_WinOverEveryGrant()
    {
        var deniedUser = User(groups: [TestRoles.AdminGroup], userSid: TestRoles.DeniedUser);
        var deniedGroup = User(groups: [TestRoles.AdminGroup, TestRoles.DeniedUser]);

        Assert.Empty(SidRoles.Evaluate(deniedUser, Roles));
        Assert.Empty(SidRoles.Evaluate(deniedGroup, Roles));
    }

    [Fact]
    public void EmptyRoleLists_GrantNothing()
    {
        Assert.Empty(SidRoles.Evaluate(User(groups: [TestRoles.AdminGroup]), new RoleOptions()));
    }

    [Fact]
    public async Task Handler_FailsDeniedCallers_AndSucceedsMembers()
    {
        var handler = new SidRoleHandler(new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions { Authorization = Roles }));
        var requirement = new SidRoleRequirement(WatchdogRole.Admin);
        var admin = new AuthorizationHandlerContext([requirement], User(groups: [TestRoles.AdminGroup]), null);
        var viewer = new AuthorizationHandlerContext([requirement], User(groups: [TestRoles.ViewerGroup]), null);
        var denied = new AuthorizationHandlerContext([requirement], User(groups: [TestRoles.AdminGroup], userSid: TestRoles.DeniedUser), null);

        await handler.HandleAsync(admin);
        await handler.HandleAsync(viewer);
        await handler.HandleAsync(denied);

        Assert.True(admin.HasSucceeded);
        Assert.False(viewer.HasSucceeded);
        Assert.False(viewer.HasFailed);
        Assert.True(denied.HasFailed);
    }

    [Theory]
    [InlineData("10.20.30.40", true)]
    [InlineData("10.20.31.1", false)]
    [InlineData("::ffff:10.20.30.7", true)]
    [InlineData("2001:db8::5", true)]
    [InlineData("192.0.2.1", true)]
    [InlineData("192.0.2.2", false)]
    public void Subnets(string address, bool inside)
    {
        Assert.Equal(inside, RemoteAddressFilter.InSubnets(IPAddress.Parse(address), ["10.20.30.0/24", "2001:db8::/64", "192.0.2.1", "nonsense", " "]));
    }

    private static ClaimsPrincipal User(IReadOnlyList<string> groups, string? userSid = null)
    {
        var identity = new ClaimsIdentity("Kerberos");
        identity.AddClaim(new Claim(ClaimTypes.Name, @"CONTOSO\jan.kowalski"));
        if (userSid is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.PrimarySid, userSid));
        }

        foreach (var group in groups)
        {
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, group));
        }

        return new ClaimsPrincipal(identity);
    }
}
