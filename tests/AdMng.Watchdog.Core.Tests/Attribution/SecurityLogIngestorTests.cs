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
    private readonly FakeHealthRegistry _health = new();
    private readonly DcLogHealthTracker _tracker;
    private readonly SecurityLogIngestor _ingestor;

    public SecurityLogIngestorTests()
    {
        _tracker = new DcLogHealthTracker(_time);
        _ingestor = new SecurityLogIngestor(_source, _store, new SecurityEventParser(), new EventLogGapDetector(_time),
            new EventLogOnlyDetector(new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts()), _time),
            _tracker, _health, _time, NullLogger<SecurityLogIngestor>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IngestionContext Context(AttributionSettings? settings = null, IReadOnlyCollection<WatchEventKind>? disabledKinds = null) => new()
    {
        Settings = settings ?? new AttributionSettings(),
        Reduction = new SecurityEventReduction { WatchedAttributes = new ConfiguredWatchedAttributeCatalog(new FakeConfigProvider(new WatchdogConfiguration())) },
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

    [Fact]
    public async Task DisabledEventLogKinds_AreNotCreated()
    {
        _source.Add(Dc1, id => SecurityEventXml.Create(4794, id, Now.AddMinutes(-1), Dc1, [.. Admin, ("Workstation", "DC01")]));

        var result = await IngestAsync(Context(disabledKinds: [WatchEventKind.DsrmPasswordChanged]));

        Assert.Empty(result.Events);
        Assert.Equal(1, result.Stored);
    }
}
