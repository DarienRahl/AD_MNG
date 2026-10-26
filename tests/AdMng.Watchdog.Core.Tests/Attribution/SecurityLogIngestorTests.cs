using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class SecurityLogIngestorTests
{
    private static readonly IngestionSource Security = new(Dc1, "Security");

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSecurityEventSource _source = new();
    private readonly FakeSecurityEventStore _store = new();
    private readonly FakeEventStore _events = new();
    private readonly FakeHealthRegistry _health = new();
    private readonly DcLogHealthTracker _tracker;
    private readonly SecurityLogIngestor _ingestor;

    public SecurityLogIngestorTests()
    {
        _tracker = new DcLogHealthTracker(_time);
        _ingestor = new SecurityLogIngestor(_source, _store, _events, new SecurityEventParser(), new EventLogGapDetector(_time),
            new EventLogOnlyDetector(new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts()), _time),
            _tracker, _health, _time, NullLogger<SecurityLogIngestor>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IngestionContext Context(AttributionSettings? settings = null, IReadOnlyCollection<WatchEventKind>? disabledKinds = null) => new()
    {
        Settings = settings ?? new AttributionSettings(),
        Reduction = new SecurityEventReduction { WatchedAttributes = new AdMng.Watchdog.Core.Detection.WatchedAttributeCatalog(new FakeConfigProvider(new WatchdogConfiguration())) },
        IsKnownDomainController = computer => HostNames.AreEqual(computer, Dc1) || HostNames.AreEqual(computer, Dc2),
        Domain = "contoso.test",
        DisabledKinds = disabledKinds ?? [],
    };

    private Task<IngestionResult> IngestAsync(IngestionContext? context = null) => _ingestor.IngestAsync(Security, context ?? Context(), Ct);

    private long AddRecord(DateTimeOffset at, int code = 4725, string computer = Dc1) =>
        _source.Add(Dc1, id => SecurityEventXml.Account(code, id, at, computer, UserSid, "lwisniewski", Admin));

    private void SetCursor(long last) => _store.SetCursor(new EventLogCursor { Source = Dc1, LastRecordId = last, UpdatedAt = Now.AddMinutes(-1) });

    [Fact]
    public async Task ANewSource_StartsAtTheReplicationLatencyBound()
    {
        AddRecord(Now.AddMinutes(-20));
        var recent = AddRecord(Now.AddMinutes(-10));

        var result = await IngestAsync();

        Assert.Equal(1, result.Stored);
        Assert.Equal(recent, Assert.Single(_store.All).RecordId);
        Assert.Equal(recent, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
        var query = Assert.Single(_source.Queries).Query;
        Assert.Equal(Now.AddMinutes(-15), query.From);
        Assert.Equal(SecurityLogIngestor.SuppressedAttributes, query.SuppressAttributes);
        Assert.Equal(HealthState.Ok, _health.Find("eventlog." + Dc1)!.State);
    }

    [Fact]
    public async Task AKnownCursor_ReadsAfterIt()
    {
        var old = AddRecord(Now.AddHours(-2));
        AddRecord(Now.AddHours(-1));
        SetCursor(old);

        var result = await IngestAsync();

        Assert.Equal(1, result.Stored);
        Assert.Equal(old, _source.Queries.Single().Query.AfterRecordId);
        Assert.Null(_source.Queries.Single().Query.From);
    }

    [Fact]
    public async Task NothingNew_DoesNotWrite()
    {
        var last = AddRecord(Now.AddHours(-1));
        SetCursor(last);

        var result = await IngestAsync();

        Assert.Equal(0, result.Read);
        Assert.Equal(0, _store.AppendCalls);
        Assert.Equal(HealthState.Ok, _health.Find("eventlog." + Dc1)!.State);
    }

    [Fact]
    public async Task OverwrittenRecords_RaiseAGapAndAreTracked()
    {
        _source.SetNextRecordId(Dc1, 500);
        AddRecord(Now.AddMinutes(-2));
        SetCursor(100);

        var result = await IngestAsync();

        var gap = Assert.Single(result.Events);
        Assert.Equal(WatchEventKind.HealthEventLogGap, gap.Kind);
        Assert.Equal([EventLogGapDetector.Overwritten], gap.Changes.Single().NewValues);
        Assert.True(_tracker.WasOverwrittenSince(Dc1, Now.AddMinutes(-1)));
        Assert.Equal(1, result.Stored);
    }

    [Fact]
    public async Task AClearedLog_RebasesAndIngestsThe1102()
    {
        SetCursor(175_000);
        _source.Clear(Dc1, restartAt: 1);
        _source.Add(Dc1, id => SecurityEventXml.LogCleared(id, Now.AddMinutes(-3), Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));
        AddRecord(Now.AddMinutes(-2));

        var result = await IngestAsync();

        Assert.Equal(
            [EventLogGapDetector.Regressed, EventLogGapDetector.LogCleared],
            result.Events.Where(e => e.Kind == WatchEventKind.HealthEventLogGap).Select(e => e.Changes.Single().NewValues.Single()));
        var cleared = Assert.Single(result.Events, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Equal(AdminSid, cleared.Attribution.Actor!.Sid);
        Assert.Equal(2, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
        Assert.Equal(Now.AddMinutes(-15), _source.Queries.Single().Query.From);
    }

    [Fact]
    public async Task ARebaseWithoutRecords_MovesTheCursorToTheNewLog()
    {
        SetCursor(175_000);
        _source.Clear(Dc1, restartAt: 1);
        AddRecord(Now.AddHours(-1));

        await IngestAsync();

        Assert.Equal(1, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
    }

    [Fact]
    public async Task ForeignAndMalformedRecords_AreDroppedButMoveTheCursor()
    {
        AddRecord(Now.AddMinutes(-3), computer: "evil.example.test");
        _source.AddMalformed(Dc1, 50, Now.AddMinutes(-2));
        var good = AddRecord(Now.AddMinutes(-1));

        var result = await IngestAsync();

        Assert.Equal(3, result.Read);
        Assert.Equal(1, result.Foreign);
        Assert.Equal(1, result.Malformed);
        Assert.Equal(1, result.Stored);
        Assert.Equal(good, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
        Assert.Equal(HealthState.Warning, _health.Find("eventlog." + Dc1 + ".foreignRecords")!.State);
    }

    [Fact]
    public async Task AnUnreadableLog_ReportsTheWin32Code()
    {
        _source.Fail(Dc1, FakeSecurityEventSource.AccessDenied);

        var result = await IngestAsync();

        Assert.True(result.Failed);
        var check = _health.Find("eventlog." + Dc1)!;
        Assert.Equal(HealthState.Error, check.State);
        Assert.Equal([Dc1, "5"], check.Args);
        Assert.Equal(HealthState.Error, _tracker.Snapshot([Dc1]).Single().State);
    }

    [Fact]
    public async Task Pages_AreReadUntilAShortPage()
    {
        SetCursor(0);
        for (var i = 0; i < SecurityLogIngestor.PageSize + 5; i++)
        {
            AddRecord(Now.AddMinutes(-30).AddMilliseconds(i));
        }

        var result = await IngestAsync();

        Assert.Equal(SecurityLogIngestor.PageSize + 5, result.Stored);
        Assert.Equal(2, _source.Queries.Count);
        Assert.Equal(2, _store.AppendCalls);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task TheBudget_StopsWithAFloodingGap()
    {
        SetCursor(0);
        for (var i = 0; i < 2 * SecurityLogIngestor.PageSize + 1; i++)
        {
            AddRecord(Now.AddMinutes(-30).AddMilliseconds(i));
        }

        var result = await IngestAsync(Context(new AttributionSettings { MaxRecordsPerPoll = SecurityLogIngestor.PageSize }));

        Assert.Equal(SecurityLogIngestor.PageSize, result.Stored);
        Assert.Equal([EventLogGapDetector.Flooding], Assert.Single(result.Events).Changes.Single().NewValues);
        Assert.Equal(SecurityLogIngestor.PageSize, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
    }

    private void AddLogons(int count, string host = Dc1)
    {
        for (var i = 0; i < count; i++)
        {
            _source.Add(host, id => SecurityEventXml.Logon(id, Now.AddMinutes(-1), host, "0x" + (0x91a2 + i).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
                3, "10.20.30.41", "KRK-WS01", UserSid, "lwisniewski"));
        }
    }

    [Fact]
    public async Task ABusyDcWithoutAccountChanges_KeepsTheCursorAtTheNewestRecord_AndReportsNoFalseOverwrite()
    {
        // attribution-rules-7: only 4624/4634/4769 between two account changes – the log wraps, nothing relevant is lost.
        var lastChange = AddRecord(Now.AddMinutes(-30));
        SetCursor(lastChange);
        AddLogons(60);

        await IngestAsync();
        Assert.Equal(lastChange + 60, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);

        _source.Overwrite(Dc1, lastChange + 40);   // the log wraps
        AddLogons(20);
        var result = await IngestAsync();

        Assert.DoesNotContain(result.Events, e => e.Kind == WatchEventKind.HealthEventLogGap);
        Assert.False(_tracker.WasOverwrittenSince(Dc1, Now.AddHours(-1)));
        Assert.Equal(lastChange + 80, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
    }

    [Fact]
    public async Task TheBudget_DoesNotMoveTheCursorPastUnreadRecords()
    {
        SetCursor(0);
        for (var i = 0; i < 2 * SecurityLogIngestor.PageSize + 1; i++)
        {
            AddRecord(Now.AddMinutes(-30).AddMilliseconds(i));
        }

        await IngestAsync(Context(new AttributionSettings { MaxRecordsPerPoll = SecurityLogIngestor.PageSize }));
        await IngestAsync(Context(new AttributionSettings { MaxRecordsPerPoll = SecurityLogIngestor.PageSize }));
        await IngestAsync(Context(new AttributionSettings { MaxRecordsPerPoll = SecurityLogIngestor.PageSize }));

        Assert.Equal(2 * SecurityLogIngestor.PageSize + 1, _store.All.Count);
    }

    [Fact]
    public async Task ALogClearedDetection_IsStoredBeforeTheCursorMoves_AndOnlyOnce()
    {
        // attribution-rules-8: the page with the 1102 is read, then storing it fails (or the service stops) – the
        // detection must not be lost with the pass, and the re-read must not repeat it.
        SetCursor(0);
        _source.Add(Dc1, id => SecurityEventXml.LogCleared(id, Now.AddMinutes(-3), Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));
        AddRecord(Now.AddMinutes(-2));
        _store.AppendFailure = _ => new InvalidOperationException("SQLite Error 5: database is locked");

        await Assert.ThrowsAsync<InvalidOperationException>(() => IngestAsync());
        Assert.Single(_events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Equal(0, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);

        _store.AppendFailure = null;
        await IngestAsync();
        await IngestAsync();

        Assert.Single(_events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Single(_events.All, e => e.Kind == WatchEventKind.HealthEventLogGap);
        Assert.Equal(2, (await _store.GetCursorAsync(Dc1, Ct))!.LastRecordId);
    }

    [Fact]
    public async Task ALaterPageFailing_KeepsTheDetectionsOfTheCommittedPages()
    {
        SetCursor(0);
        _source.Add(Dc1, id => SecurityEventXml.LogCleared(id, Now.AddMinutes(-40), Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));
        for (var i = 0; i < SecurityLogIngestor.PageSize + 5; i++)
        {
            AddRecord(Now.AddMinutes(-30).AddMilliseconds(i));
        }

        var reads = 0;
        _source.BeforeRead = (_, _, _) => ++reads == 2 ? Task.FromException(new InvalidOperationException("RPC zerwane")) : Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => IngestAsync());
        _source.BeforeRead = null;
        await IngestAsync();

        Assert.Single(_events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Equal(SecurityLogIngestor.PageSize + 6, _store.All.Count);
    }

    [Fact]
    public async Task AClearedLog_MarksTheLogOverwrittenFromTheClear()
    {
        // attribution-rules-15: evidence logged before the clear is gone – LOG_OVERWRITTEN, not NO_MATCH.
        SetCursor(175_000);
        _source.Clear(Dc1, restartAt: 1);
        _source.Add(Dc1, id => SecurityEventXml.LogCleared(id, Now.AddMinutes(-3), Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));

        await IngestAsync();

        Assert.True(_tracker.WasOverwrittenSince(Dc1, Now.AddMinutes(-5)));
    }

    [Fact]
    public async Task A1102_MarksTheLogOverwrittenAtTheTimeOfTheClear()
    {
        var cursor = AddRecord(Now.AddMinutes(-20));
        SetCursor(cursor);
        _source.Add(Dc1, id => SecurityEventXml.LogCleared(id, Now.AddMinutes(-10), Dc1, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId));

        await IngestAsync();

        Assert.True(_tracker.WasOverwrittenSince(Dc1, Now.AddMinutes(-11)));
        Assert.False(_tracker.WasOverwrittenSince(Dc1, Now.AddMinutes(-9)));   // a change after the clear: its evidence is there
    }

    [Fact]
    public async Task AServiceShutdown_IsRecordedForTheLogonSourceCache()
    {
        _source.Add(Dc1, id => SecurityEventXml.EventLogServiceRecord(1100, id, Now.AddMinutes(-4), Dc1));

        await IngestAsync();

        Assert.Equal(Now.AddMinutes(-4), _tracker.LastShutdown(Dc1));
    }

    private static readonly IngestionSource Collector = new("wec01.contoso.test", SecurityLogIngestor.ForwardedEventsLog);

    private Task<IngestionResult> IngestCollectorAsync() => _ingestor.IngestAsync(Collector, Context(), Ct);

    private void Forward(string dc, long recordId, DateTimeOffset at, int code = 4725) =>
        _source.Add(Collector.Host, SecurityEventXml.Account(code, recordId, at, dc, UserSid, "lwisniewski", Admin), SecurityLogIngestor.ForwardedEventsLog);

    [Fact]
    public async Task ForwardedEvents_RecordsOfDcsWithDifferentNumbering_AreAllIngested()
    {
        // attribution-rules-3: WEF keeps every DC's own EventRecordID – DC01 at 5 000 001, DC02 at 101.
        Forward(Dc1, 5_000_001, Now.AddMinutes(-2));
        Forward(Dc2, 101, Now.AddMinutes(-1));

        await IngestCollectorAsync();
        Forward(Dc2, 102, Now.AddSeconds(-20));
        _time.Advance(TimeSpan.FromSeconds(30));
        var second = await IngestCollectorAsync();

        Assert.Equal([101L, 102L, 5_000_001L], _store.All.Select(e => e.RecordId).Order());
        Assert.False(second.Failed);
        Assert.Null(_source.Queries[^1].Query.AfterRecordId);
        Assert.Equal(Now.AddMinutes(-1) - TimeSpan.FromMinutes(15), _source.Queries[^1].Query.From);   // last poll's newest record − overlap
        Assert.Equal(Now.AddSeconds(-20), SecurityLogIngestor.ReadTimeCursor(await _store.GetCursorAsync(Collector.Host, Ct)));
    }

    [Fact]
    public async Task ForwardedEvents_ALogClearedRecord_IsDetectedOnce_WithoutRegressionGaps()
    {
        Forward(Dc1, 5_000_001, Now.AddMinutes(-2));
        await IngestCollectorAsync();
        _source.Add(Collector.Host, SecurityEventXml.LogCleared(1, Now.AddSeconds(-5), Dc2, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId),
            SecurityLogIngestor.ForwardedEventsLog);

        for (var poll = 0; poll < 6; poll++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await IngestCollectorAsync();
        }

        Assert.Single(_events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.DoesNotContain(_events.All, e => e.Changes.Any(c => c.NewValues.Contains(EventLogGapDetector.Regressed) || c.NewValues.Contains(EventLogGapDetector.Overwritten)));
    }

    [Fact]
    public async Task ForwardedEvents_ADcClockAhead_DoesNotPushTheWindowPastTheOthers()
    {
        Forward(Dc1, 77, Now.AddHours(2));   // DC01's clock is two hours fast

        await IngestCollectorAsync();

        Assert.Equal(Now, SecurityLogIngestor.ReadTimeCursor(await _store.GetCursorAsync(Collector.Host, Ct)));
    }

    [Fact]
    public async Task ForwardedEvents_ABurstAboveTheBudget_DoesNotStallTheWindow()
    {
        // More records inside one overlap window than MaxRecordsPerPoll (a bulk import): every poll re-read the same
        // oldest records of the window, the time cursor never moved past them and the 1102 behind them was never read.
        var budget = Context(new AttributionSettings { MaxRecordsPerPoll = 5 });
        for (var i = 0; i < 12; i++)
        {
            Forward(Dc1, 1000 + i, Now.AddMinutes(-10).AddSeconds(i));
        }

        _source.Add(Collector.Host, SecurityEventXml.LogCleared(2000, Now.AddMinutes(-5), Dc2, AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId),
            SecurityLogIngestor.ForwardedEventsLog);

        for (var poll = 0; poll < 6; poll++)
        {
            await _ingestor.IngestAsync(Collector, budget, Ct);
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Single(_events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Equal(12, _store.All.Count(e => e.EventCode == 4725));
        Assert.Contains(_events.All, e => e.Kind == WatchEventKind.HealthEventLogGap
            && e.Changes.Any(c => c.NewValues.Contains(EventLogGapDetector.Flooding)));

        // Once the burst is read, the overlap window returns (records delivered late are read again).
        _time.Advance(TimeSpan.FromMinutes(20));
        Forward(Dc1, 3000, _time.GetUtcNow().AddMinutes(-1));
        await _ingestor.IngestAsync(Collector, budget, Ct);
        Forward(Dc2, 3001, _time.GetUtcNow().AddMinutes(-3));   // late WEF delivery, older than the newest record read
        _time.Advance(TimeSpan.FromSeconds(30));
        await _ingestor.IngestAsync(Collector, budget, Ct);
        Assert.Contains(_store.All, e => e.RecordId == 3001);
    }

    [Fact]
    public async Task DisabledEventLogKinds_AreNotCreated()
    {
        _source.Add(Dc1, id => SecurityEventXml.Create(4794, id, Now.AddMinutes(-1), Dc1, [.. Admin, ("Workstation", "DC01")]));

        var result = await IngestAsync(Context(disabledKinds: [WatchEventKind.DsrmPasswordChanged]));

        Assert.Empty(result.Events);
        Assert.Equal(1, result.Stored);
    }
}
