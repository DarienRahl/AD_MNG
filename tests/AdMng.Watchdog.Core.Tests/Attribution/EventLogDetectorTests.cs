using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class EventLogDetectorTests
{
    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventLogCursor Cursor(long last) => new() { Source = Dc1, LastRecordId = last, UpdatedAt = Now.AddHours(-1) };

    [Fact]
    public void Overwritten_WhenTheOldestRecordIsPastTheCursor()
    {
        Assert.True(EventLogGapDetector.IsOverwritten(Cursor(100), new SecurityLogInfo(150, 1000, false, null)));
        Assert.False(EventLogGapDetector.IsOverwritten(Cursor(100), new SecurityLogInfo(101, 1000, false, null)));
        Assert.False(EventLogGapDetector.IsOverwritten(null, new SecurityLogInfo(150, 1000, false, null)));
    }

    [Fact]
    public void Regressed_WhenTheNewestRecordIsBelowTheCursor()
    {
        Assert.True(EventLogGapDetector.IsRegressed(Cursor(175_000), new SecurityLogInfo(1, 12, false, null)));
        Assert.True(EventLogGapDetector.IsRegressed(Cursor(175_000), new SecurityLogInfo(null, 0, false, null)));
        Assert.False(EventLogGapDetector.IsRegressed(Cursor(175_000), new SecurityLogInfo(170_000, 5001, false, null)));
        Assert.Equal(175_000, EventLogGapDetector.NewestRecordId(new SecurityLogInfo(170_000, 5001, false, null)));
    }

    [Fact]
    public void ServiceRecords_HaveGapReasons()
    {
        Assert.Equal(EventLogGapDetector.LogCleared, EventLogGapDetector.ReasonOf(Parse(SecurityEventXml.LogCleared(NextId(), Now, Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId))));
        Assert.Equal(EventLogGapDetector.LogFull, EventLogGapDetector.ReasonOf(Parse(SecurityEventXml.EventLogServiceRecord(1104, NextId(), Now, Dc1))));
        Assert.Equal(EventLogGapDetector.ServiceShutdown, EventLogGapDetector.ReasonOf(Parse(SecurityEventXml.EventLogServiceRecord(1100, NextId(), Now, Dc1))));
        Assert.Null(EventLogGapDetector.ReasonOf(Account(4725, Now, Admin)));
    }

    [Fact]
    public void Gaps_AreRaisedOncePerHourPerDcAndReason()
    {
        var detector = new EventLogGapDetector(_time);

        var gap = detector.Gap("DC01.contoso.test.", EventLogGapDetector.Overwritten, "contoso.test")!;
        Assert.Null(detector.Gap(Dc1, EventLogGapDetector.Overwritten, "contoso.test"));
        Assert.NotNull(detector.Gap(Dc1, EventLogGapDetector.Flooding, "contoso.test"));
        _time.Advance(EventLogGapDetector.RepeatAfter);
        Assert.NotNull(detector.Gap(Dc1, EventLogGapDetector.Overwritten, "contoso.test"));

        Assert.Equal(WatchEventKind.HealthEventLogGap, gap.Kind);
        Assert.Equal(Dc1, gap.Object.Name);
        Assert.Equal(WatchObjectKind.Watchdog, gap.Object.Kind);
        Assert.Equal([EventLogGapDetector.Overwritten], gap.Changes.Single().NewValues);
        Assert.Equal(AttributionStatus.NotApplicable, gap.Attribution.Status);
        Assert.Equal(DetectionSource.Health, gap.Source);
    }

    [Fact]
    public async Task LogCleared_IsCriticalAndAttributedToTheClearer()
    {
        var detector = new EventLogOnlyDetector(new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts()), _time);
        var record = Parse(SecurityEventXml.LogCleared(NextId(), Now.AddSeconds(-30), Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));

        var detected = (await detector.DetectAsync(record, "contoso.test", Ct))!;

        Assert.Equal(WatchEventKind.SecurityLogCleared, detected.Kind);
        Assert.Equal(Severity.Critical, detected.Severity);
        Assert.Equal(WatchObjectKind.DomainController, detected.Object.Kind);
        Assert.Equal(Dc1, detected.Object.Name);
        Assert.Equal(DetectionSource.SecurityLog, detected.Source);
        Assert.Equal(AttributionStatus.Resolved, detected.Attribution.Status);
        Assert.Equal(AdminSid, detected.Attribution.Actor!.Sid);
        Assert.Equal(1102, Assert.Single(detected.Attribution.Evidence).EventCode);
        Assert.Equal(Now.AddSeconds(-30), detected.OriginatingTime);
    }

    [Fact]
    public async Task AuditPolicyChange_KeepsTheSubcategoryAndTheChanges()
    {
        var detector = new EventLogOnlyDetector(new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts()), _time);
        var record = Parse(SecurityEventXml.Create(4719, NextId(), Now, Dc1,
            [.. LocalSystem, ("CategoryId", "%%8274"), ("SubcategoryId", "%%14081"), ("SubcategoryGuid", "{0CCE923C-69AE-11D9-BED3-505054503030}"), ("AuditPolicyChanges", "%%8448, %%8450")]));

        var detected = (await detector.DetectAsync(record, "contoso.test", Ct))!;

        Assert.Equal(WatchEventKind.AuditPolicyChanged, detected.Kind);
        Assert.Equal(Severity.Critical, detected.Severity);
        Assert.Equal(AttributionReasons.System, detected.Attribution.Reason);
        Assert.Equal(["SubcategoryGuid", "AuditPolicyChanges"], detected.Changes.Select(c => c.Attribute));
        Assert.Equal(2, detected.Changes[1].NewValues.Count);
    }

    [Fact]
    public async Task DsrmPassword_CarriesTheWorkstation()
    {
        var detector = new EventLogOnlyDetector(new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts()), _time);
        var record = Parse(SecurityEventXml.Create(4794, NextId(), Now, Dc1, [.. Admin, ("Workstation", "DC01"), ("Status", "0x0")]));

        var detected = (await detector.DetectAsync(record, "contoso.test", Ct))!;

        Assert.Equal(WatchEventKind.DsrmPasswordChanged, detected.Kind);
        Assert.Equal(Severity.High, detected.Severity);
        Assert.Equal("DC01", detected.Attribution.Workstation);
        Assert.Equal(AttributionConfidence.Low, detected.Attribution.SourceConfidence);
    }

    [Fact]
    public async Task OtherRecords_AreNoDetections()
    {
        var detector = new EventLogOnlyDetector(new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts()), _time);

        Assert.Null(await detector.DetectAsync(Account(4725, Now, Admin), "contoso.test", Ct));
        Assert.Null(await detector.DetectAsync(Parse(SecurityEventXml.EventLogServiceRecord(1104, NextId(), Now, Dc1)), "contoso.test", Ct));
    }
}
