using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class HealthRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Checks_AreKeptPerCode_LatestWins_OrderedByCode()
    {
        var registry = new HealthRegistry();
        var changes = new List<string>();
        registry.Changed += (_, code) => changes.Add(code);

        registry.Report(new HealthCheckDto { Code = "storage.free", State = HealthState.Ok, CheckedAt = Now });
        registry.Report(new HealthCheckDto { Code = "directory.connection", State = HealthState.Error, CheckedAt = Now });
        registry.Report(new HealthCheckDto { Code = "STORAGE.free", State = HealthState.Warning, Args = ["850"], CheckedAt = Now });

        Assert.Equal(["directory.connection", "STORAGE.free"], registry.Checks.Select(c => c.Code));
        Assert.Equal(HealthState.Warning, registry.GetCheck("storage.free")?.State);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void Channels_AreKeptPerChannelId()
    {
        var registry = new HealthRegistry();

        registry.ReportChannel(new ChannelHealthDto { ChannelId = "teams-it", ChannelType = DeliveryChannelType.Teams, State = HealthState.Ok });
        registry.ReportChannel(new ChannelHealthDto { ChannelId = "email", ChannelType = DeliveryChannelType.Email, State = HealthState.Error, ConsecutiveFailures = 2 });
        registry.ReportChannel(new ChannelHealthDto { ChannelId = "teams-it", ChannelType = DeliveryChannelType.Teams, State = HealthState.Warning });

        Assert.Equal(["email", "teams-it"], registry.Channels.Select(c => c.ChannelId));
        Assert.Equal(HealthState.Warning, registry.GetChannel("teams-it")?.State);
        Assert.Null(registry.GetChannel("siem"));
    }

    [Fact]
    public void DirectoryAndAttribution_StartUnknown_AndKeepTheLatestReport()
    {
        var registry = new HealthRegistry();
        Assert.Equal(HealthState.Unknown, registry.Directory.State);
        Assert.Empty(registry.Attribution.DomainControllers);

        registry.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, DcHostName = "dc01.contoso.test", BaselineCompletedAt = Now });
        registry.ReportAttribution(new AttributionStatusDto { Enabled = true, DomainControllers = [new DcLogStatusDto { Host = "dc01.contoso.test", State = HealthState.Ok }] });

        Assert.Equal("dc01.contoso.test", registry.Directory.DcHostName);
        Assert.Equal(Now, registry.Directory.BaselineCompletedAt);
        Assert.True(registry.Attribution.Enabled);
    }

    [Fact]
    public void InvalidReports_AreRejected()
    {
        var registry = new HealthRegistry();

        Assert.Throws<ArgumentException>(() => registry.Report(new HealthCheckDto { Code = " ", State = HealthState.Ok }));
        Assert.Throws<ArgumentNullException>(() => registry.ReportChannel(null!));
    }
}
