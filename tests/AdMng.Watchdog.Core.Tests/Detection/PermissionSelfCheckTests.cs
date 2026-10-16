using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>The read-permission probes (section 10.12).</summary>
public class PermissionSelfCheckTests
{
    [Fact]
    public async Task FullyReadableDirectory_IsOkEverywhereWithoutEvents()
    {
        await using var scenario = new DetectionScenario();

        var results = await RunAsync(scenario);

        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.Equal(HealthState.Ok, r.State));
        Assert.Empty(scenario.TakeEvents());
        Assert.Equal(HealthState.Ok, scenario.Health.Get(PermissionSelfCheck.UsnChanged)!.State);
    }

    [Fact]
    public async Task HiddenUsnChanged_IsAnErrorWithTheCountAndOneGapEvent()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.HideAttributes(scenario.Company.MalgorzataWojcik, Attr.UsnChanged);
        scenario.Directory.HideAttributes(scenario.Company.PawelLewandowski, Attr.UsnChanged);

        var results = await RunAsync(scenario);
        await RunAsync(scenario);

        var usn = Assert.Single(results, r => r.Code == PermissionSelfCheck.UsnChanged);
        Assert.Equal(HealthState.Error, usn.State);
        Assert.Equal(["2"], usn.Args);
        var gap = Assert.Single(scenario.TakeEvents());
        Assert.Equal(WatchEventKind.HealthPermissionGap, gap.Kind);
        Assert.Contains(gap.Changes, c => c.Attribute == "code" && c.NewValues.SequenceEqual([PermissionSelfCheck.UsnChanged]));
    }

    [Fact]
    public async Task DeletionBetweenTheTwoSearches_IsNotAGap()
    {
        await using var scenario = new DetectionScenario();
        var deleted = false;
        scenario.Directory.BeforeOperation = operation =>
        {
            if (!deleted && operation.Kind == FakeOperationKind.Search && operation.Search?.Filter.Contains("uSNChanged>=1", StringComparison.Ordinal) == true)
            {
                deleted = true;
                scenario.Directory.DeleteAsync(scenario.Company.BartlomiejKozlowski, false, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            }
        };

        var results = await RunAsync(scenario);

        Assert.True(deleted);
        Assert.Equal(HealthState.Ok, Assert.Single(results, r => r.Code == PermissionSelfCheck.UsnChanged).State);
    }

    [Fact]
    public async Task DeniedDeletedObjects_IsAWarning()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.DenyDeletedObjects = true;

        var results = await RunAsync(scenario);

        Assert.Equal(HealthState.Warning, Assert.Single(results, r => r.Code == PermissionSelfCheck.DeletedObjects).State);
        Assert.Single(scenario.TakeEvents(), e => e.Kind == WatchEventKind.HealthPermissionGap);
    }

    [Fact]
    public async Task UnreadablePsoContainerAndPolicy_AreWarnings()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.FailNext(FakeOperationKind.Get, scenario.Builder.PasswordSettingsContainerDn, AdMng.Core.Directory.DirectoryErrorKind.AccessDenied);
        scenario.Directory.HideAttributes(scenario.Builder.DomainDn, WatchdogAttributes.LockoutDuration);

        var results = await RunAsync(scenario);

        Assert.Equal(HealthState.Warning, Assert.Single(results, r => r.Code == PermissionSelfCheck.PasswordSettings).State);
        Assert.Equal(HealthState.Warning, Assert.Single(results, r => r.Code == PermissionSelfCheck.DomainPolicy).State);
        Assert.Equal(2, scenario.TakeEvents().Count);
    }

    [Fact]
    public async Task RecoveryAndRelapse_AppendAnotherGapEvent()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.DenyDeletedObjects = true;
        await RunAsync(scenario);
        scenario.Directory.DenyDeletedObjects = false;
        await RunAsync(scenario);
        scenario.Directory.DenyDeletedObjects = true;

        await RunAsync(scenario);

        Assert.Equal(2, scenario.TakeEvents().Count(e => e.Kind == WatchEventKind.HealthPermissionGap));
    }

    [Fact]
    public async Task HiddenAccountRestrictions_IsAnError()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.HideAttributes(scenario.Builder.AdministratorDn, Attr.PwdLastSet);

        var results = await RunAsync(scenario);

        Assert.Equal(HealthState.Error, Assert.Single(results, r => r.Code == PermissionSelfCheck.AccountRestrictions).State);
    }

    private static async Task<IReadOnlyList<HealthCheckDto>> RunAsync(DetectionScenario scenario)
    {
        var token = TestContext.Current.CancellationToken;
        var dc = await scenario.Services.GetRequiredService<DcStateReader>().ReadAsync(scenario.Directory, token);
        return await scenario.Services.GetRequiredService<PermissionSelfCheck>().RunAsync(scenario.Directory, dc.Facts, token);
    }
}
