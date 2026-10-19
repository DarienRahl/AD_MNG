using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Scans;

/// <summary>MassLockout and MassDeletion (sections 10.9, 10.15).</summary>
public class BurstDetectorTests
{
    [Fact]
    public async Task FiveLockouts_AreAMassLockout_OnceInTheWindow()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        foreach (var user in Users(scenario).Take(5))
        {
            scenario.Directory.SimulateLockout(user);
        }

        Assert.Equal(5, (await scenario.CycleEventsAsync()).Count(e => e.Kind == WatchEventKind.AccountLockedOut));
        var burst = Assert.Single(await scenario.ScanEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(1));
        scenario.Directory.SimulateLockout(scenario.Company.MalgorzataWojcik);
        await scenario.CycleEventsAsync();
        var cooling = await scenario.ScanEventsAsync();

        Assert.Equal(WatchEventKind.MassLockout, burst.Kind);
        Assert.Equal(Severity.Medium, burst.Severity);
        Assert.Equal(5, burst.Burst!.Count);
        Assert.Equal(TimeSpan.FromMinutes(10), burst.Burst.Window);
        Assert.Equal(DetectionSource.BurstDetector, burst.Source);
        Assert.Empty(cooling);
    }

    [Fact]
    public async Task PercentageOfEnabledUsers_RaisesTheThreshold()
    {
        await using var scenario = new DetectionScenario();
        scenario.Config.Update(c => c with { Scans = c.Scans with { MassLockout = c.Scans.MassLockout with { Threshold = 2, PercentOfEnabledUsers = 100 } } });
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        foreach (var user in Users(scenario).Take(5))
        {
            scenario.Directory.SimulateLockout(user);
        }

        await scenario.CycleEventsAsync();

        Assert.Empty(await scenario.ScanEventsAsync());
    }

    [Fact]
    public async Task TenDeletedUsers_AreACriticalMassDeletion()
    {
        var users = new List<string>();
        await using var scenario = new DetectionScenario(seed: b => users.AddRange(Enumerable.Range(1, 10).Select(i => b.AddUser("Stażysta", $"Nr{i:D2}", b.UsersDn))));
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        foreach (var user in users)
        {
            await scenario.DeleteAsync(user);
        }

        await scenario.CycleEventsAsync();
        var burst = Assert.Single(await scenario.ScanEventsAsync());

        Assert.Equal(WatchEventKind.MassDeletion, burst.Kind);
        Assert.Equal(Severity.Critical, burst.Severity);
        Assert.Equal(10, burst.Burst!.Count);
        Assert.Equal(10, burst.Burst.Objects.Count);
    }

    [Fact]
    public async Task TenDeletedComputers_AreRoutine_FiftyAreHigh()
    {
        var computers = new List<string>();
        await using var scenario = new DetectionScenario(seed: b => computers.AddRange(Enumerable.Range(1, 50).Select(i => b.AddComputer($"OLD-PC{i:D3}", b.ComputersDn))));
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        foreach (var computer in computers.Take(10))
        {
            await scenario.DeleteAsync(computer);
        }

        await scenario.CycleEventsAsync();
        Assert.Empty(await scenario.ScanEventsAsync());
        foreach (var computer in computers.Skip(10))
        {
            await scenario.DeleteAsync(computer);
        }

        await scenario.CycleEventsAsync();
        var burst = Assert.Single(await scenario.ScanEventsAsync());

        Assert.Equal(WatchEventKind.MassDeletion, burst.Kind);
        Assert.Equal(WatchObjectKind.Computer, burst.Object.Kind);
        Assert.Equal(Severity.High, burst.Severity);
        Assert.Equal(50, burst.Burst!.Count);
        Assert.Equal(50, burst.Burst.Objects.Count);
    }

    [Fact]
    public async Task ComputerBurstOfOneActor_IsSuggestedAsCleanupAccount()
    {
        var computers = new List<string>();
        await using var scenario = new DetectionScenario(seed: b => computers.AddRange(Enumerable.Range(1, 50).Select(i => b.AddComputer($"OLD-PC{i:D3}", b.ComputersDn))));
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();
        foreach (var computer in computers)
        {
            await scenario.DeleteAsync(computer);
        }

        foreach (var deleted in await scenario.CycleEventsAsync())
        {
            await scenario.Store.UpdateAttributionAsync(deleted.Id, new AttributionUpdate
            {
                Attribution = new EventAttribution
                {
                    Status = AttributionStatus.Resolved,
                    Actor = new PrincipalRef { Sid = "S-1-5-21-3623811015-3361044348-30300820-1400", Account = @"CONTOSO\svc-czyszczenie" },
                },
            }, TestContext.Current.CancellationToken);
        }

        var burst = Assert.Single(await scenario.ScanEventsAsync());

        Assert.Equal(AttributionStatus.Resolved, burst.Attribution.Status);
        var suggestion = Assert.Single(scenario.Services.GetRequiredService<ExclusionSuggestionRegistry>().Current);
        Assert.Equal("bursts", suggestion.RuleId);
        Assert.Equal(ExclusionSuggestionRegistry.ComputerCleanupAccount, suggestion.ReasonCode);
        Assert.Equal(@"CONTOSO\svc-czyszczenie", suggestion.ActorAccount);
    }

    [Fact]
    public async Task AfterTheWindow_ANewBurstIsReportedAgain()
    {
        var users = new List<string>();
        await using var scenario = new DetectionScenario(seed: b => users.AddRange(Enumerable.Range(1, 20).Select(i => b.AddUser("Sezonowy", $"Nr{i:D2}", b.UsersDn))));
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();
        foreach (var user in users.Take(10))
        {
            await scenario.DeleteAsync(user);
        }

        await scenario.CycleEventsAsync();
        Assert.Single(await scenario.ScanEventsAsync());

        scenario.Advance(TimeSpan.FromMinutes(11));
        foreach (var user in users.Skip(10))
        {
            await scenario.DeleteAsync(user);
        }

        await scenario.CycleEventsAsync();

        Assert.Single(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.MassDeletion);
    }

    [Fact]
    public async Task DeletionsSpreadOverHours_FoundTogetherAfterAnOutage_AreNoBurst()
    {
        var users = new List<string>();
        await using var scenario = new DetectionScenario(seed: b => users.AddRange(Enumerable.Range(1, 10).Select(i => b.AddUser("Odchodzący", $"Nr{i:D2}", b.UsersDn))));
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        // The watchdog host is down for two hours; one deletion every 12 minutes, then one catch-up cycle.
        foreach (var user in users)
        {
            await scenario.DeleteAsync(user);
            scenario.Advance(TimeSpan.FromMinutes(12));
        }

        Assert.Equal(10, (await scenario.CycleEventsAsync()).Count(e => e.Kind == WatchEventKind.ObjectDeleted));

        Assert.Empty(await scenario.ScanEventsAsync());
    }

    [Fact]
    public async Task DeletionsFoundByTheReconciliation_AreNoBurst()
    {
        var users = new List<string>();
        await using var scenario = new DetectionScenario(
            seed: b => users.AddRange(Enumerable.Range(1, 12).Select(i => b.AddUser("Łukasz", $"Odejście{i:D2}", b.UsersDn))),
            configuration: new WatchdogConfiguration { Directory = new DirectorySettings { TrackDeletedObjects = false } });
        await scenario.BaselineAsync();
        foreach (var user in users)
        {
            await scenario.DeleteAsync(user);
            await scenario.CycleEventsAsync();
            scenario.Advance(TimeSpan.FromHours(1));
        }

        scenario.Advance(TimeSpan.FromHours(6));
        Assert.Equal(DetectionMode.Reconciliation, (await scenario.RunCycleAsync()).Mode);
        Assert.Equal(12, scenario.TakeEvents().Count(e => e.Kind == WatchEventKind.ObjectDeleted));

        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.MassDeletion);
    }

    [Fact]
    public async Task ABurstDuringAnOutage_IsReportedOnceWhenFound()
    {
        var users = new List<string>();
        await using var scenario = new DetectionScenario(seed: b => users.AddRange(Enumerable.Range(1, 10).Select(i => b.AddUser("Zwolniony", $"Nr{i:D2}", b.UsersDn))));
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        foreach (var user in users)
        {
            await scenario.DeleteAsync(user);
            scenario.Advance(TimeSpan.FromSeconds(20));
        }

        scenario.Advance(TimeSpan.FromHours(2));
        await scenario.CycleEventsAsync();
        var burst = Assert.Single(await scenario.ScanEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(10));
        var cooling = await scenario.ScanEventsAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));
        var later = await scenario.ScanEventsAsync();

        Assert.Equal(WatchEventKind.MassDeletion, burst.Kind);
        Assert.Equal(10, burst.Burst!.Count);
        Assert.Empty(cooling);
        Assert.Empty(later);
    }

    [Fact]
    public async Task LockoutsSpreadOverAnOutage_AreNoMassLockout()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.ScanEventsAsync();

        foreach (var user in Users(scenario))
        {
            scenario.Directory.SimulateLockout(user);
            scenario.Advance(TimeSpan.FromMinutes(15));
        }

        Assert.Equal(5, (await scenario.CycleEventsAsync()).Count(e => e.Kind == WatchEventKind.AccountLockedOut));

        Assert.Empty(await scenario.ScanEventsAsync());
    }

    private static IEnumerable<string> Users(DetectionScenario scenario) =>
    [
        scenario.Company.JanKowalski, scenario.Company.LukaszWisniewski, scenario.Company.GrazynaDabrowska,
        scenario.Company.PawelLewandowski, scenario.Company.BartlomiejKozlowski,
    ];
}
