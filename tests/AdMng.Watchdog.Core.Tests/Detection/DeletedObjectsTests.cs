using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>The Show Deleted pass (section 10.9).</summary>
public class DeletedObjectsTests
{
    [Fact]
    public async Task UnknownTombstoneInScope_IsObjectDeletedUnderItsLastKnownParent()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Krótko", "Żyjący", scenario.Company.ItOu);

        await scenario.DeleteAsync(user);
        var events = await scenario.CycleEventsAsync();

        var deleted = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectDeleted, deleted.Kind);
        Assert.Equal(user, deleted.PreviousDn);
        Assert.Equal(WatchObjectKind.User, deleted.Object.Kind);
    }

    [Fact]
    public async Task WithoutRecycleBin_AnUnknownTombstoneIsReportedFromItsMangledName()
    {
        // Samba and Windows without the Recycle Bin: no msDS-LastKnownRDN, only "CN=name\0ADEL:<guid>" (lab-verified).
        await using var scenario = new DetectionScenario();
        scenario.Directory.Info = scenario.Directory.Info with { RecycleBinEnabled = false };
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Krótko", "Żyjący", scenario.Company.SalesOu);

        await scenario.DeleteAsync(user);
        var deleted = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ObjectDeleted, deleted.Kind);
        Assert.Equal(user, deleted.PreviousDn);
        Assert.Equal(user, deleted.Object.Dn);
        Assert.Equal("Krótko Żyjący", deleted.Object.Name);
    }

    [Fact]
    public async Task WithoutRecycleBin_ANewOuDeletedWithItsUserIsResolvedThroughTheParentTombstone()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.Info = scenario.Directory.Info with { RecycleBinEnabled = false };
        await scenario.BaselineAsync();
        var ou = scenario.Builder.AddOu("Projekt Łoś", scenario.Company.SalesOu);
        var user = scenario.Builder.AddUser("Ola", "Tymczasowa", ou);

        await scenario.DeleteAsync(ou, subtree: true);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(WatchEventKind.ObjectDeleted, e.Kind));
        Assert.Single(events, e => e.PreviousDn == ou);
        Assert.Single(events, e => e.PreviousDn == user);
    }

    [Fact]
    public async Task UnknownTombstone_IsNotReportedAgainWhenRecycled()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Krótko", "Żyjący", scenario.Company.SalesOu);
        await scenario.DeleteAsync(user);
        Assert.Equal(WatchEventKind.ObjectDeleted, Assert.Single(await scenario.CycleEventsAsync()).Kind);
        scenario.Advance(TimeSpan.FromMinutes(1));

        scenario.Directory.Recycle(scenario.Directory.DeletedDistinguishedNames.Last());

        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task KnownObject_RecycledAfterItsDeletedRowWasPurged_IsNotReportedAgain()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Company.BartlomiejKozlowski;
        var guid = scenario.Snapshot(user)!.ObjectGuid;
        await scenario.DeleteAsync(user);
        Assert.Equal(WatchEventKind.ObjectDeleted, Assert.Single(await scenario.CycleEventsAsync()).Kind);
        scenario.Advance(TimeSpan.FromMinutes(1));

        // Retention purges the deleted row (DeletedSnapshotDays), then the DC recycles the object (deleted object lifetime).
        await scenario.Store.CommitAsync(new DetectionCommit { Removals = [guid] }, TestContext.Current.CancellationToken);
        scenario.Directory.Recycle(scenario.Directory.DeletedDistinguishedNames.Last());

        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task CreatedDeletedAndPurgedBetweenTwoPolls_IsStillReportedOnce()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Krótko", "Żyjący", scenario.Company.SalesOu);
        await scenario.DeleteAsync(user);
        scenario.Directory.Recycle(scenario.Directory.DeletedDistinguishedNames.Last());

        var deleted = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ObjectDeleted, deleted.Kind);
        Assert.Equal(user, deleted.PreviousDn);
    }

    [Fact]
    public async Task UnknownTombstone_IsNotDuplicatedWhenTheCycleRepeats()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Krótko", "Żyjący", scenario.Company.SalesOu);
        await scenario.DeleteAsync(user);
        scenario.Store.FailNextFinalCommit();

        await Assert.ThrowsAsync<IOException>(() => scenario.RunCycleAsync());
        var crashed = scenario.TakeEvents();
        var repeated = await scenario.CycleEventsAsync();

        Assert.Single(crashed.Concat(repeated), e => e.Kind == WatchEventKind.ObjectDeleted);
    }

    [Fact]
    public async Task UnknownObjectRestoredFromTheRecycleBin_IsObjectRestored()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Krótko", "Żyjący", scenario.Company.SalesOu);
        await scenario.DeleteAsync(user);
        Assert.Single(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(1));

        await scenario.RestoreAsync(scenario.Directory.DeletedDistinguishedNames.Last(), user);
        var restored = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ObjectRestored, restored.Kind);
        Assert.Equal(user, restored.Object.Dn);
        Assert.Empty(restored.Changes);
    }

    [Fact]
    public async Task RecycledObject_ProducesNothing()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.DeleteAsync(scenario.Company.Computer1);
        Assert.Single(await scenario.CycleEventsAsync());

        var tombstone = scenario.Directory.DeletedDistinguishedNames.Single(d => d.Contains("WAW-PC001", StringComparison.Ordinal));
        scenario.Directory.Recycle(tombstone);

        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task TreeDeleteOfAComputerWithSystemChildren_IsOneEvent()
    {
        await using var scenario = new DetectionScenario();
        var computer = scenario.Company.Laptop;
        foreach (var i in Enumerable.Range(1, 3))
        {
            scenario.Directory.Seed(DistinguishedName.Parse(computer).Child("CN", $"2026-10-0{i}T08:00:00-08:00{{6A1F7E1D-0000-4000-8000-00000000000{i}}}").ToString(),
                new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "msFVE-RecoveryInformation" } });
        }

        await scenario.BaselineAsync();

        await scenario.DeleteAsync(computer, subtree: true);
        var events = await scenario.CycleEventsAsync();

        var deleted = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectDeleted, deleted.Kind);
        Assert.Equal(computer, deleted.PreviousDn);
    }

    [Fact]
    public async Task DnsNodeChurn_ProducesNoEvents()
    {
        await using var scenario = new DetectionScenario();
        var dns = scenario.Builder.AddContainer("MicrosoftDNS", scenario.Builder.SystemDn);
        var zone = scenario.Directory.Seed(DistinguishedName.Parse(dns).Child("DC", "contoso.test").ToString(),
            new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "dnsZone" } });
        await scenario.BaselineAsync();

        var node = scenario.Directory.Seed(DistinguishedName.Parse(zone).Child("DC", "waw-pc099").ToString(),
            new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "dnsNode" } });
        await scenario.DeleteAsync(node);

        Assert.Empty(await scenario.SettleAsync());
    }

    [Fact]
    public async Task UnknownTombstoneUnderADeletedUser_IsDropped()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Directory.Seed(DistinguishedName.Parse(scenario.Company.PawelLewandowski).Child("CN", "Kontakt prywatny").ToString(),
            new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.Contact },
                [Attr.DisplayName] = "Kontakt prywatny",
            });

        await scenario.DeleteAsync(scenario.Company.PawelLewandowski, subtree: true);
        var events = await scenario.CycleEventsAsync();

        var deleted = Assert.Single(events);
        Assert.Equal(scenario.Company.PawelLewandowski, deleted.PreviousDn);
    }

    [Fact]
    public async Task DeletedOu_IsHighAndItsDeletedChildrenAreReportedOnce()
    {
        string? ou = null;
        string? user = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            ou = b.AddOu("Praktykanci", b.DomainDn);
            user = b.AddUser("Adam", "Praktykant", ou);
        });
        await scenario.BaselineAsync();

        await scenario.DeleteAsync(ou!, subtree: true);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(Severity.High, Assert.Single(events, e => e.PreviousDn == ou).Severity);
        Assert.Single(events, e => e.PreviousDn == user);
    }
}
