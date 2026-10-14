using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class AttributionMonitorTests
{
    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public void Tracker_ReportsReadsFailuresLookupsAndCoverage()
    {
        var tracker = new DcLogHealthTracker(_time);
        tracker.RecordRead("DC01.contoso.test", 175_000);
        tracker.RecordLookup(Dc1, TimeSpan.FromMilliseconds(200), timedOut: false);
        tracker.RecordLookup(Dc1, TimeSpan.FromMilliseconds(400), timedOut: false);
        tracker.RecordLookup(Dc1, TimeSpan.FromSeconds(10), timedOut: true);
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordOutcome(Dc1, attributed: i < 4);
        }

        tracker.RecordFailure(Dc2, "5");

        var rows = tracker.Snapshot([Dc1, Dc2, "dc03.contoso.test"]);

        Assert.Equal(HealthState.Warning, rows[0].State);
        Assert.Equal(175_000, rows[0].LastRecordId);
        Assert.Equal(0.4, rows[0].Coverage24h);
        Assert.Equal(3, rows[0].SourceLookups24h);
        Assert.Equal(TimeSpan.FromMilliseconds(300), rows[0].SourceLookupAverage);
        Assert.Equal(1, rows[0].SourceLookupTimeouts24h);
        Assert.True(tracker.IsCoverageLow(Dc1));
        Assert.Equal(HealthState.Error, rows[1].State);
        Assert.Equal("5", rows[1].ErrorCode);
        Assert.Equal(HealthState.Unknown, rows[2].State);
    }

    [Fact]
    public void Tracker_ForgetsAfter24Hours()
    {
        var tracker = new DcLogHealthTracker(_time);
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordOutcome(Dc1, attributed: false);
        }

        _time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        Assert.Null(tracker.Coverage(Dc1));
        Assert.False(tracker.IsCoverageLow(Dc1));
    }

    [Fact]
    public void Degraded_IsRaisedOnTheTransitionOnly()
    {
        var tracker = new DcLogHealthTracker(_time);
        var monitor = new AttributionDegradedMonitor(tracker, _time);
        tracker.RecordRead(Dc1, 10);
        tracker.RecordFailure(Dc2, "1722");

        var first = monitor.Check([Dc1, Dc2], "contoso.test");
        var again = monitor.Check([Dc1, Dc2], "contoso.test");
        tracker.RecordRead(Dc2, 5);
        var recovered = monitor.Check([Dc1, Dc2], "contoso.test");
        tracker.RecordFailure(Dc2, "1722");
        var relapsed = monitor.Check([Dc1, Dc2], "contoso.test");

        var degraded = Assert.Single(first);
        Assert.Equal(WatchEventKind.HealthAttributionDegraded, degraded.Kind);
        Assert.Equal(Dc2, degraded.Object.Name);
        Assert.Equal([AttributionDegradedMonitor.Unreadable], degraded.Changes.Single().NewValues);
        Assert.Empty(again);
        Assert.Empty(recovered);
        Assert.Single(relapsed);
    }

    [Fact]
    public void Degraded_LowCoverage()
    {
        var tracker = new DcLogHealthTracker(_time);
        tracker.RecordRead(Dc1, 10);
        for (var i = 0; i < 12; i++)
        {
            tracker.RecordOutcome(Dc1, attributed: i < 2);
        }

        var degraded = Assert.Single(new AttributionDegradedMonitor(tracker, _time).Check([Dc1], null));

        Assert.Equal([AttributionDegradedMonitor.LowCoverage], degraded.Changes.Single().NewValues);
    }

    [Fact]
    public void ActorConditions_OnUncoverableKinds_Warn()
    {
        var monitor = new ActorConditionHealthMonitor(_time);
        var withActors = new RuleSettings
        {
            Id = "ou-gpo-bez-adminów",
            Name = "Zmiany OU bez administratorów",
            Kinds = [WatchEventKind.ObjectMoved, WatchEventKind.AttributesChanged],
            Actors = new ActorFilter { ExcludeActors = [AdminSid] },
        };
        var configuration = new DefaultConfigurationFactory().Create() with { Rules = [withActors] };

        Assert.Equal(HealthState.Ok, monitor.Evaluate(configuration).State);
        monitor.Record(WatchEventKind.ObjectMoved, AttributionReasons.SaclMissing);
        monitor.Record(WatchEventKind.AccountDisabled, AttributionReasons.AuditPolicyMissing);
        monitor.Record(WatchEventKind.AttributesChanged, AttributionReasons.NoMatch);
        var check = monitor.Evaluate(configuration);

        Assert.Equal(ActorConditionHealthMonitor.CheckCode, check.Code);
        Assert.Equal(HealthState.Warning, check.State);
        Assert.Equal(["ou-gpo-bez-adminów", nameof(WatchEventKind.ObjectMoved)], check.Args);

        _time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HealthState.Ok, monitor.Evaluate(configuration).State);
    }
}
