using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class RolloutGateTests
{
    [Theory]
    [InlineData("tier0-privilege", true)]
    [InlineData("TIER0-krbtgt", true)]
    [InlineData("bursts", true)]
    [InlineData("health", true)]
    [InlineData("new-users", false)]
    [InlineData("tier0", false)]
    [InlineData(null, false)]
    public void AllowsImmediate_OnlyTier0BurstsAndHealth(string? ruleId, bool expected) =>
        Assert.Equal(expected, RolloutGate.AllowsImmediate(ruleId));

    [Fact]
    public void Until_IsBaselinePlusRolloutDays()
    {
        Assert.Equal(Now.AddDays(7), RolloutGate.Until(new GeneralSettings(), Now));
        Assert.Null(RolloutGate.Until(new GeneralSettings { RolloutEnded = true }, Now));
        Assert.Null(RolloutGate.Until(new GeneralSettings { RolloutDays = 0 }, Now));
        Assert.Null(RolloutGate.Until(new GeneralSettings(), null));
    }

    [Fact]
    public async Task Active_FromTheReportedBaselineForRolloutDays()
    {
        var harness = new NotificationHarness();
        harness.Health.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, BaselineCompletedAt = Now.AddDays(-6) });

        Assert.True(await harness.Rollout.IsActiveAsync(harness.Current, "contoso.com", Now, Ct));
        Assert.False(await harness.Rollout.IsActiveAsync(harness.Current, "contoso.com", Now.AddDays(1), Ct));
        Assert.False(await harness.Rollout.IsActiveAsync(
            harness.Current with { General = harness.Current.General with { RolloutEnded = true } }, "contoso.com", Now, Ct));
    }

    [Fact]
    public async Task BeforeTheFirstReport_UsesTheStoredDcState()
    {
        var harness = new NotificationHarness();
        await harness.Store.CommitAsync(new DetectionCommit
        {
            NewState = new DcState
            {
                Domain = "contoso.com",
                DcHostName = "dc01.contoso.com",
                DsServiceName = "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=com",
                InvocationId = Guid.NewGuid(),
                HighestUsn = 12_345,
                BaselineCompletedAt = Now.AddDays(-1),
            },
        }, Ct);

        Assert.True(await harness.Rollout.IsActiveAsync(harness.Current, "contoso.com", Now, Ct));
        Assert.False(await harness.Rollout.IsActiveAsync(harness.Current, null, Now, Ct));
    }
}
