using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Failover, failback, restore and rollback of the watched DC; directory health (sections 10.1–10.3).</summary>
public class DomainControllerChangeTests
{
    private const string Dc02 = "dc02.contoso.test";
    private static readonly Guid Dc02Invocation = new("b0c1d2e3-0000-4000-8000-000000000002");

    [Fact]
    public async Task FailoverToANeverSeenDc_IsResyncWithAHealthEventAndNoChangeEvents()
    {
        await using var scenario = new DetectionScenario(seed: b => b.AddDomainController(Dc02, Dc02Invocation));
        await scenario.BaselineAsync();

        scenario.Directory.SwitchServer(Dc02);
        var result = await scenario.RunCycleAsync();
        var events = scenario.TakeEvents();

        Assert.Equal(DetectionMode.Resync, result.Mode);
        var resync = Assert.Single(events);
        Assert.Equal(WatchEventKind.HealthResync, resync.Kind);
        Assert.Contains(resync.Changes, c => c.Attribute == "reason" && c.NewValues.SequenceEqual([DetectionModeSelector.ReasonDcChanged]));
        var state = scenario.Store.State!;
        Assert.Equal(scenario.Directory.DsServiceName, state.DsServiceName);
        Assert.Equal(Dc02Invocation, state.InvocationId);
        Assert.Equal(scenario.Directory.HighestCommittedUsn, state.HighestUsn);
    }

    [Fact]
    public async Task ChangesDuringTheResync_AreReportedAsReconciliation()
    {
        await using var scenario = new DetectionScenario(seed: b => b.AddDomainController(Dc02, Dc02Invocation));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        scenario.Directory.SwitchServer(Dc02);
        var events = await scenario.CycleEventsAsync();

        var changed = Assert.Single(events, e => e.Kind == WatchEventKind.AttributesChanged);
        Assert.Equal(DetectionSource.Reconciliation, changed.Source);
        Assert.Contains(EventTags.Reconciliation, changed.Tags);
    }

    [Fact]
    public async Task FailbackToTheKnownDc_ContinuesFromItsOwnWatermarkWithoutDuplicates()
    {
        await using var scenario = new DetectionScenario(seed: b => b.AddDomainController(Dc02, Dc02Invocation));
        var pdc = scenario.Directory.Info.Server;
        await scenario.BaselineAsync();
        scenario.Directory.SwitchServer(Dc02);
        await scenario.CycleEventsAsync();
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        Assert.Single(await scenario.CycleEventsAsync());

        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.Title, "Starszy programista");
        scenario.Directory.SwitchServer(pdc);
        var result = await scenario.RunCycleAsync();
        var events = scenario.TakeEvents();

        Assert.Equal(DetectionMode.Failback, result.Mode);
        var changed = Assert.Single(events);
        Assert.Equal(scenario.Company.LukaszWisniewski, changed.Object.Dn);
        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.HealthResync);
    }

    [Fact]
    public async Task ResyncOnAStaleDc_KeepsTheNewerSnapshotValue()
    {
        await using var scenario = new DetectionScenario(seed: b => b.AddDomainController(Dc02, Dc02Invocation));
        await scenario.BaselineAsync();
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        Assert.Single(await scenario.CycleEventsAsync());

        scenario.Directory.SwitchServer(Dc02);
        scenario.Directory.SimulateStaleReplica(scenario.Company.JanKowalski, Attr.Title, "Administrator systemów");
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Resync, result.Mode);
        Assert.Contains("staleDc", result.Warnings);
        Assert.DoesNotContain(scenario.TakeEvents(), e => e.Kind == WatchEventKind.AttributesChanged);
        Assert.Equal("Architekt", scenario.Snapshot(scenario.Company.JanKowalski)!.Attributes[Attr.Title].Values[0]);
    }

    [Fact]
    public async Task RestoredDc_IsResyncInvocationIdChanged()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var invocation = scenario.Directory.SimulateRestore();
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Resync, result.Mode);
        var resync = Assert.Single(scenario.TakeEvents());
        Assert.Contains(resync.Changes, c => c.NewValues.SequenceEqual([DetectionModeSelector.ReasonInvocationIdChanged]));
        Assert.Equal(invocation, scenario.Store.State!.InvocationId);
    }

    [Fact]
    public async Task UsnRollback_IsResync()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Directory.OverrideHighestCommittedUsn(scenario.Store.State!.HighestUsn - 10);
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Resync, result.Mode);
        Assert.Equal(WatchEventKind.HealthResync, Assert.Single(scenario.TakeEvents()).Kind);
    }

    [Fact]
    public async Task CrashMidResync_RepeatsTheResyncAndNeverRunsIncrementalFromAForeignWatermark()
    {
        await using var scenario = new DetectionScenario(seed: b => b.AddDomainController(Dc02, Dc02Invocation));
        await scenario.BaselineAsync();
        var dc01State = scenario.Store.State!;

        scenario.Directory.SwitchServer(Dc02);
        scenario.Store.FailCommit(1);
        await Assert.ThrowsAsync<IOException>(scenario.RunCycleAsync);
        Assert.Equal(dc01State.DsServiceName, scenario.Store.State!.DsServiceName);
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Resync, result.Mode);
        Assert.Equal(Dc02Invocation, scenario.Store.State!.InvocationId);
    }

    [Fact]
    public async Task NotSynchronizedDc_SkipsTheCycleWithAWarning()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        var watermark = scenario.Store.State!.HighestUsn;

        scenario.Directory.IsSynchronized = false;
        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.NoChange, result.Mode);
        Assert.Empty(scenario.TakeEvents());
        Assert.Equal(watermark, scenario.Store.State!.HighestUsn);
        Assert.Equal(HealthState.Warning, scenario.Health.Get(ChangeDetectionEngine.HealthNotSynchronized)!.State);
        scenario.Directory.IsSynchronized = true;
        Assert.Single(await scenario.CycleEventsAsync());
        Assert.Equal(HealthState.Ok, scenario.Health.Get(ChangeDetectionEngine.HealthNotSynchronized)!.State);
    }

    [Fact]
    public async Task UnreachableDc_ReportsHealthAndOneUnavailableEventAfterFailoverAfter()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Sessions.FailWith(DirectoryErrorKind.ServerUnavailable);
        var first = await scenario.RunJobAsync();
        Assert.True(first.Failed);
        Assert.Equal(HealthState.Error, scenario.Health.Get(ChangeDetectionEngine.HealthConnection)!.State);
        Assert.Empty(scenario.TakeEvents());

        scenario.Advance(TimeSpan.FromMinutes(11));
        await scenario.RunJobAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));
        await scenario.RunJobAsync();
        var unavailable = Assert.Single(scenario.TakeEvents());
        Assert.Equal(WatchEventKind.HealthDirectoryUnavailable, unavailable.Kind);
        Assert.Equal(Severity.High, unavailable.Severity);

        scenario.Sessions.Recover();
        var recovered = await scenario.RunJobAsync();
        Assert.False(recovered.Failed);
        Assert.Equal(HealthState.Ok, scenario.Health.Get(ChangeDetectionEngine.HealthConnection)!.State);
    }

    [Fact]
    public async Task DeniedChangeSearch_IsAPermissionGapAndKeepsTheWatermark()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        var watermark = scenario.Store.State!.HighestUsn;

        scenario.Directory.FailNext(FakeOperationKind.Search, scenario.Builder.DomainDn, DirectoryErrorKind.AccessDenied, times: 2);
        var denied = await scenario.RunJobAsync();
        var again = await scenario.RunJobAsync();

        Assert.True(denied.Failed);
        Assert.Equal("permission", denied.FailureCode);
        Assert.True(again.Failed);
        var gap = Assert.Single(scenario.TakeEvents());
        Assert.Equal(WatchEventKind.HealthPermissionGap, gap.Kind);
        Assert.Equal(watermark, scenario.Store.State!.HighestUsn);
        Assert.Single(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task StoredEvents_RaiseEventsStoredAndTheDirectoryStatusIsReported()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var before = scenario.Signals.Count(WatchdogSignal.EventsStored);

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");
        await scenario.RunCycleAsync();

        Assert.Equal(before + 1, scenario.Signals.Count(WatchdogSignal.EventsStored));
        var status = scenario.Health.Directory;
        Assert.Equal(HealthState.Ok, status.State);
        Assert.True(status.IsPdc);
        Assert.Equal(scenario.Store.State!.HighestUsn, status.HighestUsn);
        Assert.True(status.SnapshotObjects > 20);
    }

    [Fact]
    public async Task WakeUpContainers_RaisePollRequestedOnChanges()
    {
        await using var scenario = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { WakeUpContainers = ["OU=IT,OU=Pracownicy,OU=Firma,{domain}"] },
        });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");

        Assert.True(scenario.Signals.Count(WatchdogSignal.PollRequested) >= 1);
    }

    [Fact]
    public async Task IdleCycle_NeedsOnlyTwoBaseReads()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Directory.ClearOperations();

        await scenario.RunCycleAsync();

        var operations = scenario.Directory.Operations;
        Assert.Equal(2, operations.Count);
        Assert.All(operations, o => Assert.Equal(FakeOperationKind.Get, o.Kind));
    }
}
