using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public sealed class PendingAttributionProcessorTests : IDisposable
{
    private static readonly AttributionSettings Settings = new();
    private static readonly string[] Sources = [Dc1, Dc2];
    private static readonly DateTimeOffset T = Now.AddSeconds(-40);

    private readonly AttributionHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private static WatchEvent Disabled(params string[] tags) => Create(WatchEventKind.AccountDisabled, tags: tags) with
    {
        OriginatingTime = T,
        OriginatingDc = Dc1,
        Changes = [new AttributeChange { Attribute = "userAccountControl", OldValues = ["512"], NewValues = ["514"] }],
    };

    private Task<ProcessedAttribution> ProcessAsync(WatchEvent e) => _h.Processor.ProcessAsync(_h.Events.Add(e), Sources, Settings, Ct);

    [Fact]
    public async Task AMatchingRecord_ResolvesActorAndLogonSource()
    {
        _h.Detection.AddPrincipal(AdminSid, "adm.jkowalska", "Joanna Kowalska (admin)", "CN=Joanna Kowalska (admin),OU=Admini,DC=contoso,DC=test");
        _h.SecurityStore.Add(Account(4725, T, Admin));
        _h.Source.Add(Dc1, id => SecurityEventXml.Logon(id, T.AddHours(-1), Dc1, AdminLogonId, 3, "10.20.30.41", "PC-ŁÓDŹ-01", AdminSid, "adm.jkowalska"));

        var processed = await ProcessAsync(Disabled());

        Assert.True(processed.IsFinal);
        var attribution = processed.Update.Attribution;
        Assert.Equal(AttributionStatus.Resolved, attribution.Status);
        Assert.Equal(AttributionConfidence.High, attribution.Confidence);
        Assert.Equal("Joanna Kowalska (admin)", attribution.Actor!.DisplayName);
        Assert.Equal("10.20.30.41", attribution.IpAddress);
        Assert.Equal("PC-ŁÓDŹ-01", attribution.Workstation);
        Assert.Equal("4624", attribution.SourceMethod);
        Assert.Equal(AttributionConfidence.High, attribution.SourceConfidence);
        Assert.Equal(3, attribution.LogonType);
        Assert.Equal(4725, Assert.Single(attribution.Evidence).EventCode);
        Assert.Equal(Now, attribution.ResolvedAt);
        Assert.Equal(1.0, _h.Tracker.Coverage(Dc1));
    }

    [Fact]
    public async Task NoCandidateYet_IsRetriedAtTheNextStep()
    {
        var processed = await ProcessAsync(Disabled());

        Assert.False(processed.IsFinal);
        Assert.Equal(Now.AddSeconds(30), processed.Update.NextAttemptAt);
        Assert.Equal(AttributionStatus.Pending, processed.Update.Attribution.Status);
    }

    [Fact]
    public async Task AfterTheLastRetry_EndsUnknownWithAReason()
    {
        _h.Time.Advance(TimeSpan.FromMinutes(31));
        _h.SecurityStore.Add(Account(4725, Now.AddDays(-1), Admin));

        var processed = await ProcessAsync(Disabled());

        Assert.True(processed.IsFinal);
        Assert.Equal(AttributionStatus.Unknown, processed.Update.Attribution.Status);
        Assert.Equal(AttributionReasons.NoMatch, processed.Update.Attribution.Reason);
        Assert.Equal(0.0, _h.Tracker.Coverage(Dc1));
    }

    [Fact]
    public async Task TwoSubjects_AreAmbiguousWithCandidates()
    {
        _h.SecurityStore.Add(Account(4725, T, Admin), Account(4725, T, OtherAdmin));

        var processed = await ProcessAsync(Disabled());

        Assert.True(processed.IsFinal);
        var attribution = processed.Update.Attribution;
        Assert.Equal(AttributionStatus.Ambiguous, attribution.Status);
        Assert.Equal(AttributionReasons.Ambiguous, attribution.Reason);
        Assert.Null(attribution.Actor);
        Assert.Equal([AdminSid, OtherAdminSid], attribution.Candidates.Select(c => c.Sid).Order());
        Assert.Equal(2, attribution.Evidence.Count);
    }

    [Fact]
    public async Task Lockouts_AreNotApplicableWithSources()
    {
        var lockedAt = Now.AddMinutes(-1);
        _h.SecurityStore.Add(Parse(SecurityEventXml.Create(4740, NextId(), lockedAt, Dc1,
            [("TargetUserName", "lwisniewski"), ("TargetDomainName", "PC-KRAKOW-07"), ("TargetSid", UserSid), .. LocalSystem])));
        var lockout = Create(WatchEventKind.AccountLockedOut) with { Lockout = new LockoutInfo { LockedAt = lockedAt } };

        var processed = await ProcessAsync(lockout);

        Assert.True(processed.IsFinal);
        Assert.Equal(AttributionStatus.NotApplicable, processed.Update.Attribution.Status);
        Assert.Single(processed.Update.Attribution.Evidence);
        Assert.Equal("PC-KRAKOW-07", processed.Update.Lockout!.CallerComputer);
        Assert.Equal("PC-KRAKOW-07", processed.Updated.Lockout!.CallerComputer);
    }

    [Fact]
    public async Task ALockoutWhose4740HasNotArrivedYet_IsRetried_ThenGetsItsCallerAndSources()
    {
        // attribution-rules-9: WEF delivers the 4740 and the 4771s about 30 s after the lockout woke the engine.
        var lockedAt = Now.AddSeconds(-5);
        var lockout = _h.Events.Add(Create(WatchEventKind.AccountLockedOut) with { Lockout = new LockoutInfo { LockedAt = lockedAt } });

        var first = await _h.Processor.ProcessAsync(lockout, Sources, Settings, Ct);

        Assert.False(first.IsFinal);
        Assert.Equal(Now.AddSeconds(30), first.Update.NextAttemptAt);
        Assert.Equal(AttributionStatus.Pending, first.Update.Attribution.Status);
        Assert.Null(first.Update.Lockout);

        _h.SecurityStore.Add(Parse(SecurityEventXml.Create(4740, NextId(), lockedAt, Dc1,
            [("TargetUserName", "lwisniewski"), ("TargetDomainName", "PC-KRAKOW-07"), ("TargetSid", UserSid), .. LocalSystem])));
        _h.Time.Advance(TimeSpan.FromSeconds(30));
        var second = await _h.Processor.ProcessAsync(lockout, Sources, Settings, Ct);

        Assert.True(second.IsFinal);
        Assert.Equal(AttributionStatus.NotApplicable, second.Update.Attribution.Status);
        Assert.Equal("PC-KRAKOW-07", second.Update.Lockout!.CallerComputer);
    }

    [Fact]
    public async Task ALockoutWithoutA4740_IsFinalAfterTheLastRetry()
    {
        var lockout = _h.Events.Add(Create(WatchEventKind.AccountLockedOut, detectedAt: Now.AddMinutes(-31)) with
        {
            Lockout = new LockoutInfo { LockedAt = Now.AddMinutes(-31) },
        });

        var processed = await _h.Processor.ProcessAsync(lockout, Sources, Settings, Ct);

        Assert.True(processed.IsFinal);
        Assert.Equal(AttributionStatus.NotApplicable, processed.Update.Attribution.Status);
        Assert.Null(processed.Update.Lockout!.CallerComputer);
    }

    [Fact]
    public async Task KindsWithoutAnActor_AreNotApplicable()
    {
        var processed = await ProcessAsync(Create(WatchEventKind.PasswordExpiring));

        Assert.True(processed.IsFinal);
        Assert.Equal(AttributionStatus.NotApplicable, processed.Update.Attribution.Status);
    }

    [Fact]
    public async Task PrivilegeEvents_InheritTheirCause()
    {
        var changeSet = Guid.NewGuid();
        _h.Events.Add(Membership(WatchEventKind.GroupMemberAdded, "CN=Domain Admins,CN=Users,DC=contoso,DC=test") with
        {
            ChangeSetId = changeSet,
            Attribution = Resolved(AdminSid),
        });

        var processed = await ProcessAsync(Create(WatchEventKind.PrivilegeGranted) with { ChangeSetId = changeSet });

        Assert.True(processed.IsFinal);
        Assert.Equal(AdminSid, processed.Update.Attribution.Actor!.Sid);
    }

    [Fact]
    public async Task APendingCause_IsWaitedFor()
    {
        var changeSet = Guid.NewGuid();
        _h.Events.Add(Membership(WatchEventKind.GroupMemberAdded, "CN=Domain Admins,CN=Users,DC=contoso,DC=test") with { ChangeSetId = changeSet });

        var waiting = await ProcessAsync(Create(WatchEventKind.PrivilegeGranted) with { ChangeSetId = changeSet });
        _h.Time.Advance(TimeSpan.FromMinutes(31));
        var givenUp = await ProcessAsync(Create(WatchEventKind.PrivilegeGranted) with { ChangeSetId = changeSet, DetectedAt = Now });

        Assert.Equal(Now.AddSeconds(30), waiting.Update.NextAttemptAt);
        Assert.Equal(AttributionStatus.Unknown, givenUp.Update.Attribution.Status);
        Assert.Equal(AttributionReasons.NoMatch, givenUp.Update.Attribution.Reason);
    }

    [Fact]
    public async Task APrivilegedPasswordReset_RaisesTheSeverityAndTags()
    {
        _h.SecurityStore.Add(Account(4724, T, Admin));
        var reset = Create(WatchEventKind.PasswordChanged, severity: Severity.Medium, tags: [EventTags.Privileged]) with { OriginatingTime = T, OriginatingDc = Dc1 };

        var processed = await ProcessAsync(reset);

        Assert.Equal([EventTags.PasswordReset], processed.Update.AddedTags);
        Assert.Equal(Severity.High, processed.Update.Severity);
        Assert.Equal(Severity.High, processed.Updated.Severity);
        Assert.Contains(EventTags.PasswordReset, processed.Updated.Tags);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 120)]
    [InlineData(119, 120)]
    [InlineData(1799, 1800)]
    public void NextAttempt_IsTheNextStepAfterNow(int elapsedSeconds, int expectedSeconds) =>
        Assert.Equal(Now.AddSeconds(expectedSeconds), PendingAttributionProcessor.NextAttempt(Now, Settings.PendingRetries, Now.AddSeconds(elapsedSeconds)));

    [Fact]
    public void NextAttempt_AfterTheLastStep_IsNull() =>
        Assert.Null(PendingAttributionProcessor.NextAttempt(Now, Settings.PendingRetries, Now.AddMinutes(30)));

    [Fact]
    public void Disabled_AttributableKindsEndUnknown()
    {
        var privileged = Create(WatchEventKind.PasswordChanged, severity: Severity.Medium, tags: [EventTags.Privileged]);

        var update = PendingAttributionProcessor.Disabled(privileged, Now);
        var expiring = PendingAttributionProcessor.Disabled(Create(WatchEventKind.PasswordExpiring), Now);

        Assert.Equal(AttributionStatus.Unknown, update.Attribution.Status);
        Assert.Equal(AttributionReasons.SourceDisabled, update.Attribution.Reason);
        Assert.Equal(Severity.High, update.Severity);
        Assert.Null(update.NextAttemptAt);
        Assert.Equal(EventAttribution.NotApplicable, expiring.Attribution);
    }
}
