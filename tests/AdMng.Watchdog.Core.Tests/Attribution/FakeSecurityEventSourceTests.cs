using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class FakeSecurityEventSourceTests
{
    private const string Dc = "dc01.contoso.local";
    private readonly FakeSecurityEventSource _source = new();

    private async Task<List<long>> ReadIds(SecurityEventQuery query, string host = Dc)
    {
        var parser = new SecurityEventParser();
        var ids = new List<long>();
        await foreach (var xml in _source.ReadAsync(host, query, TestContext.Current.CancellationToken))
        {
            ids.Add(parser.Parse(xml, host)!.Event.RecordId);
        }

        return ids;
    }

    [Fact]
    public async Task FiltersLikeTheWindowsReader()
    {
        foreach (var fixture in SecurityEventFixtures.All)
        {
            _source.Add(Dc, fixture);
        }

        Assert.Equal([175408, 175413, 175703, 410204], await ReadIds(new SecurityEventQuery { LogName = "Security" }));
        Assert.Equal([175703, 410204], await ReadIds(new SecurityEventQuery { LogName = "Security", AfterRecordId = 175413 }));
        Assert.Equal([175413], await ReadIds(new SecurityEventQuery { LogName = "Security", EventCodes = [4738] }));
        Assert.Equal([410204, 175703], await ReadIds(new SecurityEventQuery { LogName = "Security", NewestFirst = true, MaxRecords = 2 }));
        Assert.Equal([175408, 175413], await ReadIds(new SecurityEventQuery { LogName = "Security", DataEquals = [("SubjectLogonId", "0x30DC2")] }));
        Assert.Equal([175703], await ReadIds(new SecurityEventQuery
        {
            LogName = "Security",
            From = new DateTimeOffset(2015, 8, 21, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2015, 8, 22, 0, 0, 0, TimeSpan.Zero),
        }));
        Assert.Equal([175408, 175413, 175703], await ReadIds(new SecurityEventQuery { LogName = "Security", SuppressAttributes = ["userAccountControl"] }));
        Assert.Empty(await ReadIds(new SecurityEventQuery { LogName = "ForwardedEvents" }));
        Assert.Equal(8, _source.Queries.Count);
        Assert.All(_source.Queries, q => Assert.StartsWith("<QueryList>", q.QueryXml, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogInfo_AndClearingRestartsRecordIds()
    {
        _source.SetNextRecordId(Dc, 1000);
        var first = _source.Add(Dc, id => SecurityEventXml.Create(4724, id, DateTimeOffset.UnixEpoch, Dc, [("TargetSid", "S-1-5-21-1-2-3-500")]));
        _source.Add(Dc, id => SecurityEventXml.Create(4724, id, DateTimeOffset.UnixEpoch, Dc, [("TargetSid", "S-1-5-21-1-2-3-500")]));

        var info = await _source.GetLogInfoAsync(Dc, "Security", TestContext.Current.CancellationToken);
        Assert.Equal(1000, first);
        Assert.Equal(1000, info.OldestRecordNumber);
        Assert.Equal(2, info.RecordCount);

        _source.Clear(Dc, restartAt: 5);
        Assert.Equal(5, _source.Add(Dc, id => SecurityEventXml.LogCleared(id, DateTimeOffset.UnixEpoch, Dc, "S-1-5-18", "DC01$", "CONTOSO", "0x3e7")));
        Assert.Equal(5, (await _source.GetLogInfoAsync(Dc, "Security", TestContext.Current.CancellationToken)).OldestRecordNumber);
    }

    [Fact]
    public async Task FailingHosts_ThrowSecurityLogAccessException()
    {
        _source.Fail(Dc, FakeSecurityEventSource.AccessDenied);

        var info = await Assert.ThrowsAsync<SecurityLogAccessException>(() => _source.GetLogInfoAsync(Dc, "Security", TestContext.Current.CancellationToken));
        Assert.Equal(5, info.Win32Error);
        Assert.Equal(Dc, info.Host);
        await Assert.ThrowsAsync<SecurityLogAccessException>(() => ReadIds(new SecurityEventQuery { LogName = "Security" }));

        _source.Recover(Dc);
        Assert.Empty(await ReadIds(new SecurityEventQuery { LogName = "Security" }));
    }

    [Fact]
    public void InvalidFixtures_AreRejected() =>
        Assert.Throws<ArgumentException>(() => _source.Add(Dc, "<Event>niekompletne</Event>"));
}
