using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>The daily reconciliation (sections 10.4, 10.9).</summary>
public class ReconcilerTests
{
    private static readonly TimeSpan ToNextReconciliation = TimeSpan.FromHours(17);

    [Fact]
    public async Task Reconciliation_WithoutChanges_ProducesNoEventsAndMovesLastFullSync()
    {
        string? template = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            template = b.AddCertificateTemplate("Kontroler-domeny");
            b.AddDomainController("dc02.contoso.test", new Guid("b0c1d2e3-0000-4000-8000-000000000002"));
        });
        await scenario.BaselineAsync();
        var pki = scenario.Store.Snapshots.Count(s => s.Kind == WatchObjectKind.PublicKeyInfrastructure);
        var dcs = scenario.Store.Snapshots.Count(s => s.Kind == WatchObjectKind.DomainController);
        Assert.True(pki >= 1);
        Assert.Equal(2, dcs);

        scenario.Advance(ToNextReconciliation);
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Reconciliation, result.Mode);
        Assert.Empty(scenario.TakeEvents());
        Assert.Equal(pki, scenario.Store.Snapshots.Count(s => s.Kind == WatchObjectKind.PublicKeyInfrastructure && !s.IsDeleted));
        Assert.Equal(dcs, scenario.Store.Snapshots.Count(s => s.Kind == WatchObjectKind.DomainController && !s.IsDeleted));
        Assert.Equal(scenario.Time.GetUtcNow(), scenario.Store.State!.LastFullSyncAt);
    }

    [Fact]
    public async Task Reconciliation_FindsAChangeTheIncrementMissed()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Directory.HideAttributes(scenario.Company.MalgorzataWojcik, Attr.UsnChanged);
        await scenario.ReplaceAsync(scenario.Company.MalgorzataWojcik, Attr.Title, "Dyrektor finansowa");
        Assert.Empty(await scenario.CycleEventsAsync());

        scenario.Advance(ToNextReconciliation);
        var events = await scenario.CycleEventsAsync();

        var changed = Assert.Single(events);
        Assert.Equal(WatchEventKind.AttributesChanged, changed.Kind);
        Assert.Equal(DetectionSource.Reconciliation, changed.Source);
        Assert.Contains(EventTags.Reconciliation, changed.Tags);
    }

    [Fact]
    public async Task Reconciliation_AnObjectFoundLiveWhereItWasButNotByTheScan_IsNoMove()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var guid = scenario.Snapshot(scenario.Company.GrazynaDabrowska)!.ObjectGuid;

        // The scan no longer matches it (objectClass unreadable), the GUID lookup still finds it at its old DN.
        scenario.Directory.HideAttributes(scenario.Company.GrazynaDabrowska, Attr.ObjectClass);
        scenario.Advance(ToNextReconciliation);
        var events = await scenario.CycleEventsAsync();

        var gone = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectDisappeared, gone.Kind);
        Assert.Equal(scenario.Company.GrazynaDabrowska, gone.PreviousDn);
        Assert.DoesNotContain(scenario.Store.Snapshots, s => s.ObjectGuid == guid);
    }

    [Fact]
    public async Task Reconciliation_RemovesTheRowOfADisappearedGroupAndItsMemberList()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var group = scenario.Snapshot(scenario.Company.GroupAccounting)!.ObjectGuid;
        scenario.Directory.DenyDeletedObjects = true;
        await scenario.DeleteAsync(scenario.Company.GroupAccounting);
        await scenario.CycleEventsAsync();

        scenario.Advance(ToNextReconciliation);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(WatchEventKind.ObjectDisappeared, Assert.Single(events).Kind);
        Assert.Empty(await scenario.Store.GetMembersAsync(group, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(scenario.Store.Snapshots, s => s.ObjectGuid == group);
    }
}
