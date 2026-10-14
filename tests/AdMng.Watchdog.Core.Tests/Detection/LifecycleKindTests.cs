using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Lifecycle kinds (100–115) end to end through the fake directory.</summary>
public class LifecycleKindTests
{
    [Fact]
    public async Task UserCreated_CarriesTheFinalStateAfterTheSettleWindow()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var user = scenario.Builder.AddUser("Krzysztof", "Zieliński", scenario.Company.ItOu);
        Assert.Empty(await scenario.CycleEventsAsync());
        await scenario.ReplaceAsync(user, Attr.Title, "Administrator sieci");
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(3));
        var events = await scenario.CycleEventsAsync();

        var created = Assert.Single(events);
        Assert.Equal(WatchEventKind.UserCreated, created.Kind);
        Assert.Equal(Severity.Low, created.Severity);
        Assert.Contains(created.Changes, c => c.Attribute.Equals(Attr.Title, StringComparison.OrdinalIgnoreCase) && c.NewValues.SequenceEqual(["Administrator sieci"]));
        Assert.Null(scenario.Snapshot(user)!.CreationPendingUntil);
    }

    [Fact]
    public async Task ContactCreated_IsRecordedOnlyWhenEnabled()
    {
        await using var silent = new DetectionScenario();
        await silent.BaselineAsync();
        silent.Builder.AddContact("Ewa", "Nowak-Ślęzak", silent.Company.SalesOu);
        Assert.DoesNotContain(await silent.SettleAsync(), e => e.Kind == WatchEventKind.ContactCreated);

        await using var enabled = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { EnabledOptionalKinds = [WatchEventKind.ContactCreated] },
        });
        await enabled.BaselineAsync();
        var contact = enabled.Builder.AddContact("Ewa", "Nowak-Ślęzak", enabled.Company.SalesOu);
        var created = Assert.Single(await enabled.SettleAsync());
        Assert.Equal(WatchEventKind.ContactCreated, created.Kind);
        Assert.Equal(contact, created.Object.Dn);
        Assert.Equal(WatchObjectKind.Contact, created.Object.Kind);
    }

    [Fact]
    public async Task ServiceAccountCreated_IsMediumForGmsaAndHighForDmsa()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var gmsa = scenario.Builder.AddServiceAccount("gmsa-sql01");
        var dmsa = scenario.Builder.AddServiceAccount("dmsa-app01", ObjectClasses.DelegatedManagedServiceAccount);
        var events = await scenario.SettleAsync();

        Assert.Equal(Severity.Medium, Assert.Single(events, e => e.Object.Dn == gmsa && e.Kind == WatchEventKind.ServiceAccountCreated).Severity);
        Assert.Equal(Severity.High, Assert.Single(events, e => e.Object.Dn == dmsa && e.Kind == WatchEventKind.ServiceAccountCreated).Severity);
    }

    [Fact]
    public async Task ObjectDeleted_ComesFromTheTombstoneWithTheLastLiveDn()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var guid = scenario.Snapshot(scenario.Company.BartlomiejKozlowski)!.ObjectGuid;

        await scenario.DeleteAsync(scenario.Company.BartlomiejKozlowski);
        var events = await scenario.CycleEventsAsync();

        var deleted = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectDeleted, deleted.Kind);
        Assert.Equal(Severity.Medium, deleted.Severity);
        Assert.Equal(guid, deleted.Object.ObjectGuid);
        Assert.Equal(scenario.Company.BartlomiejKozlowski, deleted.PreviousDn);
        Assert.Equal(DetectionSource.DeletedObjects, deleted.Source);
        Assert.True(scenario.Store.Snapshots.Single(s => s.ObjectGuid == guid).IsDeleted);
        var sales = scenario.Snapshot(scenario.Company.GroupSales)!.ObjectGuid;
        Assert.DoesNotContain(await scenario.Store.GetMembersAsync(sales, TestContext.Current.CancellationToken), m => m.Key == guid.ToString("N"));
    }

    [Fact]
    public async Task DeletedMember_IsRemovedSilentlyFromItsGroups()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.DeleteAsync(scenario.Company.ZanetaSzymanska);
        var events = await scenario.CycleEventsAsync();

        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.GroupMemberRemoved);
        Assert.Single(events, e => e.Kind == WatchEventKind.ObjectDeleted);
    }

    [Fact]
    public async Task ObjectRestored_ReinsertsMembershipsWithoutMemberEvents()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var guid = scenario.Snapshot(scenario.Company.PawelLewandowski)!.ObjectGuid;
        await scenario.DeleteAsync(scenario.Company.PawelLewandowski);
        await scenario.CycleEventsAsync();
        var tombstone = scenario.Directory.DeletedDistinguishedNames.Single(d => d.Contains("Paweł", StringComparison.Ordinal));

        await scenario.RestoreAsync(tombstone, scenario.Company.PawelLewandowski);
        var events = await scenario.CycleEventsAsync();

        var restored = Assert.Single(events, e => e.Kind == WatchEventKind.ObjectRestored);
        Assert.Equal(guid, restored.Object.ObjectGuid);
        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.UserCreated);
        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.GroupMemberAdded);
        Assert.False(scenario.Snapshot(scenario.Company.PawelLewandowski)!.IsDeleted);
        var sales = scenario.Snapshot(scenario.Company.GroupSales)!.ObjectGuid;
        Assert.Contains(await scenario.Store.GetMembersAsync(sales, TestContext.Current.CancellationToken), m => m.Key == guid.ToString("N"));
    }

    [Fact]
    public async Task ObjectMoved_ReportsTheOldAndNewDn()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var moved = await scenario.MoveAsync(scenario.Company.GrazynaDabrowska, scenario.Company.SalesOu);
        var events = await scenario.CycleEventsAsync();

        var move = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectMoved, move.Kind);
        Assert.Equal(scenario.Company.GrazynaDabrowska, move.PreviousDn);
        Assert.Equal(moved, move.Object.Dn);
    }

    [Fact]
    public async Task ObjectRenamed_IsReportedWithoutAttributeNoise()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var renamed = await scenario.MoveAsync(scenario.Company.GroupIt, null, "CN=GG_IT_Administracja");
        var events = await scenario.CycleEventsAsync();

        var rename = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectRenamed, rename.Kind);
        Assert.Equal(renamed, rename.Object.Dn);
        Assert.Equal(scenario.Company.GroupIt, rename.PreviousDn);
    }

    [Fact]
    public async Task OuRename_DoesNotReportItsChildren()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var renamed = await scenario.MoveAsync(scenario.Company.AccountingOu, null, "OU=Finanse");
        var events = await scenario.CycleEventsAsync();

        var rename = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectRenamed, rename.Kind);
        Assert.Equal(renamed, rename.Object.Dn);
        var child = DistinguishedName.Parse(renamed).Child("CN", "Małgorzata Wójcik").ToString();
        Assert.NotNull(scenario.Snapshot(child));
    }

    [Fact]
    public async Task ChildMovedAndParentRenamedInOneCycle_ReportsBoth()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var moved = await scenario.MoveAsync(scenario.Company.LukaszWisniewski, scenario.Company.SalesOu);
        var renamedSales = await scenario.MoveAsync(scenario.Company.SalesOu, null, "OU=Handel");
        var events = await scenario.CycleEventsAsync();

        Assert.Single(events, e => e.Kind == WatchEventKind.ObjectRenamed && e.Object.Dn == renamedSales);
        var move = Assert.Single(events, e => e.Kind == WatchEventKind.ObjectMoved);
        Assert.Equal(DistinguishedName.Parse(renamedSales).Child("CN", "Łukasz Wiśniewski").ToString(), move.Object.Dn);
        Assert.Equal(scenario.Company.LukaszWisniewski, move.PreviousDn);
        Assert.Equal(2, events.Count);
        Assert.NotEqual(moved, move.Object.Dn);
    }

    [Fact]
    public async Task MoveOutOfScope_RemovesTheRowAndMoveBackIsObjectAppeared()
    {
        string? archive = null;
        await using var scenario = new DetectionScenario(seed: b => archive = b.AddOu("Archiwum"));
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });
        await scenario.BaselineAsync();
        var guid = scenario.Snapshot(scenario.Company.ZbigniewKazmierczak)!.ObjectGuid;

        var archived = await scenario.MoveAsync(scenario.Company.ZbigniewKazmierczak, archive);
        var outEvents = await scenario.CycleEventsAsync();
        var back = await scenario.MoveAsync(archived, scenario.Company.AccountingOu);
        var inEvents = await scenario.CycleEventsAsync();

        var moveOut = Assert.Single(outEvents);
        Assert.Equal(WatchEventKind.ObjectMoved, moveOut.Kind);
        Assert.Contains(EventTags.OutOfScope, moveOut.Tags);
        var appeared = Assert.Single(inEvents);
        Assert.Equal(WatchEventKind.ObjectAppeared, appeared.Kind);
        Assert.Equal(back, appeared.Object.Dn);
        Assert.Equal(guid, appeared.Object.ObjectGuid);
    }

    [Fact]
    public async Task ObjectDisappeared_IsFoundByTheReconciliation()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var guid = scenario.Snapshot(scenario.Company.Laptop)!.ObjectGuid;
        scenario.Directory.DenyDeletedObjects = true;

        await scenario.DeleteAsync(scenario.Company.Laptop);
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromHours(17));
        var result = await scenario.RunCycleAsync();
        var events = scenario.TakeEvents();

        Assert.Equal(DetectionMode.Reconciliation, result.Mode);
        var gone = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectDisappeared, gone.Kind);
        Assert.Equal(guid, gone.Object.ObjectGuid);
        Assert.Equal(DetectionSource.Reconciliation, gone.Source);
        Assert.DoesNotContain(scenario.Store.Snapshots, s => s.ObjectGuid == guid);
    }

    [Fact]
    public async Task DeletionWithoutDeletedObjectsAccess_IsObjectDeletedFromTheReconciliationLookup()
    {
        await using var scenario = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { TrackDeletedObjects = false },
        });
        await scenario.BaselineAsync();
        var guid = scenario.Snapshot(scenario.Company.Computer2)!.ObjectGuid;

        await scenario.DeleteAsync(scenario.Company.Computer2);
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromHours(17));
        var events = await scenario.CycleEventsAsync();

        var deleted = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectDeleted, deleted.Kind);
        Assert.Equal(DetectionSource.Reconciliation, deleted.Source);
        Assert.Equal(Severity.Low, deleted.Severity);
        Assert.True(scenario.Store.Snapshots.Single(s => s.ObjectGuid == guid).IsDeleted);
    }
}
