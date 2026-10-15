using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Rules;
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
    public async Task FollowUpsOfOnePass_AreQueuedTogether()
    {
        // Two events of one alert attributed in the same pass: their follow-ups share a bucket and must reach the queue
        // in one call, or the dispatch job (woken by the first) sends two follow-ups for one alert (live lab run).
        AddRecord(4725, T, Admin);
        _h.Events.Add(Disabled());
        _h.Events.Add(Disabled());

        await _h.Engine.RunOnceAsync(Ct);

        Assert.Equal(2, _h.Queue.Added.Count);
        Assert.Equal(1, _h.Queue.AddCalls);
    }

    private WatchEvent ShadowCredentials(bool evaluated) => _h.Events.Add(Create(WatchEventKind.KeyCredentialChanged, severity: Severity.Medium) with
    {
        OriginatingTime = T,
        OriginatingDc = Dc1,
        Changes = [new AttributeChange { Attribute = "msDS-KeyCredentialLink", Redacted = true }],
        MatchedRules = evaluated ? [DefaultRules.SecurityReview] : [],
    }, evaluated);

    [Fact]
    public async Task ASeverityRaisedByAttribution_IsMatchedAgainstTheRulesAgain()
    {
        // Shadow credentials on an ordinary user: the provisional Medium matched only security-review (digest); written by
        // another account the final severity is High (10.6.4) and tier0-security must alert immediately.
        _h.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        var pending = ShadowCredentials(evaluated: true);

        await _h.Engine.RunOnceAsync(Ct);

        var attributed = _h.Events.Get(pending.Id);
        Assert.Equal(Severity.High, attributed.Severity);
        Assert.Equal([DefaultRules.SecurityReview, DefaultRules.Tier0Security], attributed.MatchedRules);
        var (evaluation, _) = Assert.Single(_h.Planner.Calls);
        Assert.Equal([DefaultRules.Tier0Security], evaluation.Matches.Select(m => m.Rule.Id));   // never the rules that already matched
        Assert.Equal(AttributionStatus.Resolved, evaluation.Event.Attribution.Status);
        var notification = Assert.Single(_h.Events.Notifications);
        Assert.Equal((pending.Id, DefaultRules.Tier0Security), (notification.EventId, notification.RuleId));
        Assert.Contains(WatchdogSignal.NotificationsQueued, _h.Signals.Raised);
    }

    [Fact]
    public async Task TagsAddedByAttribution_AreMatchedAgainstTheRulesAgain()
    {
        var resets = new RuleSettings
        {
            Id = "resety-hasel",
            Name = "Resety haseł przez helpdesk",
            Kinds = [WatchEventKind.PasswordChanged],
            RequiredTags = [EventTags.PasswordReset],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        var defaults = new DefaultConfigurationFactory().Create();
        _h.Config.Set(defaults with { Rules = [.. defaults.Rules, resets] });
        AddRecord(4724, T, Admin);
        var pending = _h.Events.Add(Create(WatchEventKind.PasswordChanged) with { OriginatingTime = T, OriginatingDc = Dc1, MatchedRules = [DefaultRules.Passwords] }, evaluated: true);

        await _h.Engine.RunOnceAsync(Ct);

        Assert.Contains(EventTags.PasswordReset, _h.Events.Get(pending.Id).Tags);
        Assert.Equal([DefaultRules.Passwords, "resety-hasel"], _h.Events.Get(pending.Id).MatchedRules);
        Assert.Equal("resety-hasel", Assert.Single(_h.Events.Notifications).RuleId);
    }

    [Fact]
    public async Task AnEventNotEvaluatedYet_IsEvaluatedOnceWithItsFinalState()
    {
        _h.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        var pending = ShadowCredentials(evaluated: false);

        await _h.Engine.RunOnceAsync(Ct);

        Assert.Equal([DefaultRules.Tier0Security, DefaultRules.SecurityReview], _h.Events.Get(pending.Id).MatchedRules);
        Assert.Empty(await _h.Events.GetUnevaluatedAsync(10, Ct));
        Assert.Equal(DefaultRules.Tier0Security, Assert.Single(_h.Events.Notifications).RuleId);
    }

    [Fact]
    public async Task AnUnchangedSeverity_IsNotEvaluatedAgain()
    {
        AddRecord(4725, T, Admin);
        _h.Events.Add(Disabled(), evaluated: true);

        await _h.Engine.RunOnceAsync(Ct);

        Assert.Empty(_h.Planner.Calls);
        Assert.Equal(0, _h.Events.CommitCalls);
    }

    [Fact]
    public async Task APassInterruptedAfterTheReevaluation_DoesNotPlanTheRuleTwice()
    {
        _h.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        var pending = ShadowCredentials(evaluated: true);
        _h.Events.UpdateAttributionFailure = _ => new InvalidOperationException("SQLite Error 5: database is locked");

        var failed = await _h.Engine.RunOnceAsync(Ct);
        _h.Events.UpdateAttributionFailure = null;
        await _h.Engine.RunOnceAsync(Ct);

        Assert.True(failed.Failed);
        Assert.Equal(AttributionStatus.Resolved, _h.Events.Get(pending.Id).Attribution.Status);
        Assert.Equal(Severity.High, _h.Events.Get(pending.Id).Severity);
        Assert.Single(_h.Events.Notifications);
        Assert.Single(_h.Planner.Calls);
    }

    [Fact]
    public async Task APassInterruptedByAStoreError_StillQueuesTheFollowUpsOfEventsAlreadyAttributed()
    {
        // notifications-6: the first event is final in the store; its follow-up must not be lost with the pass.
        AddRecord(4725, T, Admin);
        var first = _h.Events.Add(Disabled());
        var second = _h.Events.Add(Disabled());
        _h.Events.UpdateAttributionFailure = id => id == second.Id ? new InvalidOperationException("SQLite Error 13: disk full") : null;

        var result = await _h.Engine.RunOnceAsync(Ct);

        Assert.True(result.Failed);
        Assert.Equal(AttributionStatus.Resolved, _h.Events.Get(first.Id).Attribution.Status);
        Assert.Equal(first.Id, Assert.Single(_h.Queue.Added).EventId);
        Assert.Equal(AttributionStatus.Pending, _h.Events.Get(second.Id).Attribution.Status);
    }

    [Fact]
    public async Task AStopDuringTheStorePhase_DoesNotInterruptIt()
    {
        AddRecord(4725, T, Admin);
        var first = _h.Events.Add(Disabled());
        var second = _h.Events.Add(Disabled());
        using var stop = new CancellationTokenSource();
        _h.Events.UpdateAttributionFailure = id =>
        {
            if (id == first.Id)
            {
                stop.Cancel();   // the service stops while the pass stores its results
            }

            return null;
        };

        await _h.Engine.RunOnceAsync(stop.Token);

        Assert.Equal(AttributionStatus.Resolved, _h.Events.Get(second.Id).Attribution.Status);
        Assert.Equal(2, _h.Queue.Added.Count);
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
