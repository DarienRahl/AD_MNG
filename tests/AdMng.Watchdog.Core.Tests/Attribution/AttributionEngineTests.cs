using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public sealed class AttributionEngineTests : IDisposable
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-2);

    private readonly AttributionHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private static WatchEvent Disabled(DateTimeOffset? detectedAt = null) => Create(WatchEventKind.AccountDisabled, detectedAt: detectedAt) with
    {
        OriginatingTime = T,
        OriginatingDc = Dc1,
        Changes = [new AttributeChange { Attribute = "userAccountControl", OldValues = ["512"], NewValues = ["514"] }],
    };

    private void AddRecord(int code, DateTimeOffset at, IEnumerable<(string, string?)> subject, string host = Dc1) =>
        _h.Source.Add(host, id => SecurityEventXml.Account(code, id, at, host, UserSid, "lwisniewski", subject));

    [Fact]
    public void Job_IsDeclared()
    {
        Assert.Equal("attribution", _h.Engine.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), _h.Engine.DefaultInterval);
        Assert.Equal([WatchdogSignal.EventsStored], _h.Engine.WakeSignals);
    }

    [Fact]
    public async Task ACycle_IngestsCorrelatesAndPlansFollowUps()
    {
        AddRecord(4725, T, Admin);
        var pending = _h.Events.Add(Disabled());

        var result = await _h.Engine.RunOnceAsync(Ct);

        Assert.False(result.Failed);
        Assert.Equal(TimeSpan.FromSeconds(30), result.NextRunIn);
        Assert.Single(_h.SecurityStore.All);
        var attributed = _h.Events.Get(pending.Id);
        Assert.Equal(AttributionStatus.Resolved, attributed.Attribution.Status);
        Assert.Equal(AdminSid, attributed.Attribution.Actor!.Sid);
        Assert.Null(_h.Events.NextAttributionAt(pending.Id));
        Assert.Equal(pending.Id, Assert.Single(_h.FollowUps.Calls).Id);
        Assert.True(Assert.Single(_h.Queue.Added).IsFollowUp);
        Assert.Contains(WatchdogSignal.AttributionUpdated, _h.Signals.Raised);
        Assert.Contains(WatchdogSignal.NotificationsQueued, _h.Signals.Raised);
        Assert.DoesNotContain(WatchdogSignal.EventsStored, _h.Signals.Raised);
        Assert.True(_h.Health.Attribution.Enabled);
        Assert.Equal([Dc1, Dc2], _h.Health.Attribution.DomainControllers.Select(d => d.Host));
        Assert.Equal(HealthState.Ok, _h.Health.Find(ActorConditionHealthMonitor.CheckCode)!.State);
    }

    [Fact]
    public async Task NotYetLogged_IsRetriedLater()
    {
        var pending = _h.Events.Add(Disabled());

        await _h.Engine.RunOnceAsync(Ct);
        Assert.Equal(Now.AddSeconds(30), _h.Events.NextAttributionAt(pending.Id));
        Assert.Empty(_h.FollowUps.Calls);

        AddRecord(4725, T, Admin, Dc1);
        _h.Time.Advance(TimeSpan.FromSeconds(30));
        await _h.Engine.RunOnceAsync(Ct);

        Assert.Equal(AttributionStatus.Resolved, _h.Events.Get(pending.Id).Attribution.Status);
    }

    [Fact]
    public async Task EventLogOnlyDetections_AreAppended()
    {
        _h.Source.Add(Dc2, id => SecurityEventXml.LogCleared(id, Now.AddMinutes(-1), Dc2, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));

        await _h.Engine.RunOnceAsync(Ct);

        var cleared = Assert.Single(_h.Events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Equal("contoso.test", cleared.Domain);
        Assert.Equal(AttributionStatus.Resolved, cleared.Attribution.Status);
        Assert.Single(_h.Events.All, e => e.Kind == WatchEventKind.HealthEventLogGap);
        Assert.Contains(WatchdogSignal.EventsStored, _h.Signals.Raised);
    }

    [Fact]
    public async Task AnOperationDisambiguatesItsSiblings()
    {
        var changeSet = Guid.NewGuid();
        AddRecord(4720, T, Admin);
        AddRecord(4722, T.AddSeconds(1), Admin);
        AddRecord(4722, T.AddSeconds(1), OtherAdmin, Dc2);
        var created = _h.Events.Add(Create(WatchEventKind.UserCreated) with { ChangeSetId = changeSet, OriginatingTime = T, OriginatingDc = Dc1 });
        var enabled = _h.Events.Add(Create(WatchEventKind.AccountEnabled) with { ChangeSetId = changeSet, LocalChangeTime = T });

        await _h.Engine.RunOnceAsync(Ct);

        Assert.Equal(AttributionStatus.Resolved, _h.Events.Get(created.Id).Attribution.Status);
        var sibling = _h.Events.Get(enabled.Id).Attribution;
        Assert.Equal(AttributionStatus.Resolved, sibling.Status);
        Assert.Equal(AdminSid, sibling.Actor!.Sid);
    }

    [Fact]
    public async Task AnUnreadableDc_DegradesAttribution()
    {
        _h.Source.Fail(Dc2, FakeSecurityEventSource.RpcUnavailable);

        await _h.Engine.RunOnceAsync(Ct);
        await _h.Engine.RunOnceAsync(Ct);

        var degraded = Assert.Single(_h.Events.All, e => e.Kind == WatchEventKind.HealthAttributionDegraded);
        Assert.Equal(Dc2, degraded.Object.Name);
        Assert.Equal(HealthState.Error, _h.Health.Find("eventlog." + Dc2)!.State);
        Assert.Equal(HealthState.Error, _h.Health.Attribution.DomainControllers.Single(d => d.Host == Dc2).State);
    }

    [Fact]
    public async Task AFullBatch_RunsAgainAtOnce()
    {
        for (var i = 0; i < AttributionEngine.CorrelationBatchSize; i++)
        {
            _h.Events.Add(Create(WatchEventKind.PasswordExpiring));
        }

        var result = await _h.Engine.RunOnceAsync(Ct);

        Assert.Equal(TimeSpan.Zero, result.NextRunIn);
    }

    [Fact]
    public async Task WithoutAReader_PendingEventsEndSourceDisabled()
    {
        _h.Source.IsAvailable = false;
        var old = _h.Events.Add(Disabled(detectedAt: Now.AddMinutes(-1)));
        var fresh = _h.Events.Add(Disabled(detectedAt: Now.AddSeconds(-10)));
        var notApplicable = _h.Events.Add(Create(WatchEventKind.PasswordExpiring, detectedAt: Now.AddMinutes(-1)));

        var result = await _h.Engine.RunOnceAsync(Ct);

        Assert.False(result.Failed);
        Assert.Equal(AttributionReasons.SourceDisabled, _h.Events.Get(old.Id).Attribution.Reason);
        Assert.Equal(AttributionStatus.Unknown, _h.Events.Get(old.Id).Attribution.Status);
        Assert.Equal(AttributionStatus.Pending, _h.Events.Get(fresh.Id).Attribution.Status);
        Assert.Equal(AttributionStatus.NotApplicable, _h.Events.Get(notApplicable.Id).Attribution.Status);
        Assert.Equal(2, _h.FollowUps.Calls.Count);
        Assert.Equal(HealthState.Disabled, _h.Health.Find("attribution.source")!.State);
        Assert.False(_h.Health.Attribution.Enabled);
        Assert.Contains(WatchdogSignal.AttributionUpdated, _h.Signals.Raised);
        Assert.Empty(_h.Source.Queries);
    }

    [Fact]
    public async Task AttributionOff_InTheConfiguration()
    {
        _h.Config.Set(new WatchdogConfiguration { Attribution = new AttributionSettings { Enabled = false } });
        var old = _h.Events.Add(Disabled(detectedAt: Now.AddMinutes(-1)));

        await _h.Engine.RunOnceAsync(Ct);

        Assert.Equal(AttributionReasons.SourceDisabled, _h.Events.Get(old.Id).Attribution.Reason);
        Assert.Equal("Attribution is disabled in the configuration.", _h.Health.Find("attribution.source")!.Detail);
    }

    [Fact]
    public async Task Failures_AreReportedWithASafeCode()
    {
        _h.DomainControllers.Failure = new InvalidOperationException("CN=Łukasz Wiśniewski,OU=Kraków,DC=contoso,DC=test");

        var result = await _h.Engine.RunOnceAsync(Ct);

        Assert.True(result.Failed);
        Assert.Equal(SafeExceptionLog.ErrorCode(_h.DomainControllers.Failure), result.FailureCode);
        Assert.DoesNotContain("Kraków", result.FailureCode ?? string.Empty, StringComparison.Ordinal);
    }
}
