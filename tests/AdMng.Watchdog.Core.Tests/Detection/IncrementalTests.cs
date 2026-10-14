using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class IncrementalTests
{
    [Fact]
    public async Task ModifiedAttribute_IsOneAttributesChangedEventWithOldAndNewValues()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Kierownik działu IT");
        var result = await scenario.RunCycleAsync();
        var events = scenario.TakeEvents();

        Assert.Equal(DetectionMode.Incremental, result.Mode);
        var changed = Assert.Single(events);
        Assert.Equal(WatchEventKind.AttributesChanged, changed.Kind);
        Assert.Equal(DetectionSource.IncrementalPoll, changed.Source);
        Assert.Equal(scenario.Company.JanKowalski, changed.Object.Dn);
        var title = Assert.Single(changed.Changes);
        Assert.Equal(Attr.Title, title.Attribute, ignoreCase: true);
        Assert.Equal(["Administrator systemów"], title.OldValues);
        Assert.Equal(["Kierownik działu IT"], title.NewValues);
        Assert.Equal(AttributionStatus.Pending, changed.Attribution.Status);
        Assert.Equal(scenario.Directory.HighestCommittedUsn, scenario.Store.State!.HighestUsn);
    }

    [Fact]
    public async Task ChangedAttribute_CarriesOriginatingTimeAndDcFromMetadata()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromSeconds(7));

        await scenario.ReplaceAsync(scenario.Company.MalgorzataWojcik, Attr.Department, "Finanse");
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(DetectionScenario.Start.AddSeconds(7), changed.OriginatingTime);
        Assert.Equal(scenario.Directory.Info.Server, changed.OriginatingDc, ignoreCase: true);
    }

    [Fact]
    public async Task NoiseOnly_ProducesNoEventAndNoSnapshotWrite()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var before = scenario.Store.Commits.Count;

        scenario.Directory.SimulateSuccessfulLogon(scenario.Company.PawelLewandowski);
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Incremental, result.Mode);
        Assert.Empty(scenario.TakeEvents());
        var commits = scenario.Store.Commits.Skip(before).ToList();
        Assert.All(commits, c => Assert.Empty(c.Upserts));
        Assert.All(commits, c => Assert.Empty(c.Events));
        Assert.Equal(scenario.Directory.HighestCommittedUsn, scenario.Store.State!.HighestUsn);
    }

    [Fact]
    public async Task UsnOverlap_RereadsWithoutDuplicateEvents()
    {
        await using var scenario = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { UsnOverlap = 50 },
        });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        Assert.Single(await scenario.CycleEventsAsync());
        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.Office, "Łódź, pokój 12");
        var second = await scenario.CycleEventsAsync();

        var only = Assert.Single(second);
        Assert.Equal(scenario.Company.LukaszWisniewski, only.Object.Dn);
    }

    [Fact]
    public async Task CrashBetweenChunks_RepeatsTheCycleWithoutDuplicates()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.ReplaceAsync(scenario.Company.ItOu, Attr.Description, "Dział informatyki – Warszawa");
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        var watermark = scenario.Store.State!.HighestUsn;

        scenario.Store.FailCommit(2);
        await Assert.ThrowsAsync<IOException>(scenario.RunCycleAsync);
        Assert.Equal(watermark, scenario.Store.State!.HighestUsn);
        var events = scenario.TakeEvents().ToList();
        events.AddRange(await scenario.CycleEventsAsync());

        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.Object.Dn == scenario.Company.ItOu);
        Assert.Single(events, e => e.Object.Dn == scenario.Company.JanKowalski);
        Assert.Equal(scenario.Directory.HighestCommittedUsn, scenario.Store.State!.HighestUsn);
    }

    [Fact]
    public async Task CrashInTheFinalCommit_KeepsTheWatermarkAndEmitsOnce()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        var watermark = scenario.Store.State!.HighestUsn;

        scenario.Store.FailCommit(2);
        await Assert.ThrowsAsync<IOException>(scenario.RunCycleAsync);
        Assert.Equal(watermark, scenario.Store.State!.HighestUsn);
        var events = scenario.TakeEvents().ToList();
        events.AddRange(await scenario.CycleEventsAsync());

        Assert.Single(events);
    }

    [Fact]
    public async Task CreatedObjects_AreReportedAfterTheSettleWindow()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var user = scenario.Builder.AddUser("Krzysztof", "Zieliński", scenario.Company.ItOu);
        var computer = scenario.Builder.AddComputer("GDA-PC010", scenario.Company.ComputersOu);
        var group = scenario.Builder.AddGroup("GG_Projekt_Ślązak", scenario.Company.GroupsOu);
        var ou = scenario.Builder.AddOu("Logistyka", scenario.Company.EmployeesOu);
        var first = await scenario.CycleEventsAsync();
        scenario.Advance(TimeSpan.FromMinutes(3));
        var settled = await scenario.CycleEventsAsync();

        Assert.DoesNotContain(first, e => e.Kind is WatchEventKind.UserCreated or WatchEventKind.ComputerCreated or WatchEventKind.GroupCreated);
        Assert.Contains(settled, e => e.Kind == WatchEventKind.UserCreated && e.Object.Dn == user);
        Assert.Contains(settled, e => e.Kind == WatchEventKind.ComputerCreated && e.Object.Dn == computer);
        Assert.Contains(settled, e => e.Kind == WatchEventKind.GroupCreated && e.Object.Dn == group);
        Assert.Contains(first.Concat(settled), e => e.Kind == WatchEventKind.OrganizationalUnitCreated && e.Object.Dn == ou);
    }
}
