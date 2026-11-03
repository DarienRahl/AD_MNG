using AdMng.Core.EventLogs;
using AdMng.Testing;
using AdMng.Testing.EventLogs;
using static AdMng.Testing.EventLogs.EventLogSamples;

namespace AdMng.Core.Tests.EventLogs;

public class FakeEventLogConnectorTests
{
    private readonly FakeDirectorySession _session = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeEventLogConnector Connector() =>
        new FakeEventLogConnector()
            .Add([.. LockoutScenario(), .. FailedLogonScenario()])
            .AddHost(Dc01, SystemChannel)
            .AddHost("WAW-PC001.contoso.test", GroupPolicyChannel);

    [Fact]
    public async Task ReadsApplyTheQuery_RecordsCarryTheOpenedHostName()
    {
        var connector = Connector();
        await using var reader = await connector.OpenComputerAsync("dc01.CONTOSO.test", _session, Ct);

        var records = await ReadAllAsync(reader, new EventLogQuery
        {
            Channel = SecurityChannel,
            EventIds = [4771, 4740],
            Data = [new EventDataCondition("TargetUserName", [JanKowalski])],
        }, Ct);

        Assert.Equal("dc01.CONTOSO.test", reader.Host);
        Assert.Equal([9_004L, 9_003, 9_002, 9_001, 20_000], records.Select(r => r.RecordId));
        Assert.All(records, r => Assert.Equal("dc01.CONTOSO.test", r.Host));
        Assert.All(records, r => Assert.Equal(Dc01, r.Computer));
        var read = Assert.Single(connector.Reads);
        Assert.Equal("dc01.CONTOSO.test", read.Host);
        Assert.DoesNotContain(JanKowalski, read.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Channels_AndComputedCoverage()
    {
        var connector = Connector();
        await using var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);

        Assert.Equal([SecurityChannel, SystemChannel], await reader.GetChannelsAsync(Ct));
        var coverage = await reader.GetCoverageAsync("security", Ct);
        var dc01 = LockoutScenario().Concat(FailedLogonScenario()).Where(r => r.Host == Dc01).ToList();
        Assert.Equal(dc01.Count, coverage.RecordCount);
        Assert.Equal(dc01.Min(r => r.TimeCreated), coverage.Oldest);
        Assert.Equal(dc01.Max(r => r.TimeCreated), coverage.Newest);
        Assert.Equal(EventLogMode.Circular, coverage.Mode);
        Assert.True(coverage.Covers(Morning.AddHours(1)));
        Assert.False(coverage.Covers(Morning.AddHours(-1)));
        var empty = await reader.GetCoverageAsync(SystemChannel, Ct);
        Assert.Equal(0, empty.RecordCount);
        Assert.Null(empty.Oldest);
    }

    [Fact]
    public async Task SetCoverage_ReplacesTheComputedOne()
    {
        var coverage = new EventLogCoverage { Host = Dc02, Channel = SecurityChannel, Oldest = Morning.AddHours(2), IsFull = true, Mode = EventLogMode.Retain };
        var connector = Connector().SetCoverage(Dc02, SecurityChannel, coverage);
        await using var reader = await connector.OpenComputerAsync(Dc02, _session, Ct);

        Assert.Same(coverage, await reader.GetCoverageAsync(SecurityChannel, Ct));
    }

    [Fact]
    public async Task HostFailures_UnknownHostsAndNotSupported()
    {
        var connector = Connector().FailHost(Dc02, EventLogAccessError.RpcUnavailable, nativeErrorCode: 1722);

        var rpc = await Assert.ThrowsAsync<EventLogAccessException>(() => connector.OpenComputerAsync(Dc02, _session, Ct));
        Assert.Equal(EventLogAccessError.RpcUnavailable, rpc.Error);
        Assert.Equal(1722, rpc.NativeErrorCode);
        Assert.DoesNotContain("DC02", rpc.Message, StringComparison.OrdinalIgnoreCase);

        var unknown = await Assert.ThrowsAsync<EventLogAccessException>(() => connector.OpenComputerAsync("krk-pc101.contoso.test", _session, Ct));
        Assert.Equal(EventLogAccessError.Unreachable, unknown.Error);

        connector.IsSupported = false;
        var unsupported = await Assert.ThrowsAsync<EventLogAccessException>(() => connector.OpenComputerAsync(Dc01, _session, Ct));
        Assert.Equal(EventLogAccessError.NotSupported, unsupported.Error);
        Assert.Equal([Dc02, "krk-pc101.contoso.test", Dc01], connector.OpenedHosts);
    }

    [Fact]
    public async Task ChannelFailures_AndUnknownChannels()
    {
        var connector = Connector().FailChannel(Dc01, SecurityChannel, EventLogAccessError.AccessDenied);
        await using var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);

        var denied = await Assert.ThrowsAsync<EventLogAccessException>(() => ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel }, Ct));
        Assert.Equal(EventLogAccessError.AccessDenied, denied.Error);
        await Assert.ThrowsAsync<EventLogAccessException>(() => reader.GetCoverageAsync(SecurityChannel, Ct));
        var missing = await Assert.ThrowsAsync<EventLogAccessException>(() => ReadAllAsync(reader, new EventLogQuery { Channel = "Directory Service" }, Ct));
        Assert.Equal(EventLogAccessError.ChannelNotFound, missing.Error);
        Assert.Empty(await ReadAllAsync(reader, new EventLogQuery { Channel = SystemChannel }, Ct));
    }

    [Fact]
    public async Task InvalidQueries_AreRejectedLikeTheTarget()
    {
        var connector = Connector();
        await using var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);

        var invalid = await Assert.ThrowsAsync<EventLogAccessException>(() =>
            ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel, Data = [new EventDataCondition("TargetUserName", ["o'ne\"ill"])] }, Ct));
        Assert.Equal(EventLogAccessError.InvalidQuery, invalid.Error);
        var raw = await Assert.ThrowsAsync<EventLogAccessException>(() =>
            ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel, RawXPath = "*[contains(EventData, 'x')]" }, Ct));
        Assert.Equal(EventLogAccessError.InvalidQuery, raw.Error);
        Assert.Empty(connector.Reads);

        Assert.Equal(LockoutScenario().Concat(FailedLogonScenario()).Count(r => r.Host == Dc01),
            (await ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel, RawXPath = "*[System[EventID=4740]]" }, Ct)).Count);
    }

    [Fact]
    public async Task Files_ReadForwardIgnoringTheChannel_HostIsTheFileName()
    {
        var connector = Connector()
            .AddFile(@"C:\Eksport\DC01-zabezpieczenia.evtx", LockoutScenario().Where(r => r.Host == Dc01))
            .AddFile(@"C:\Eksport\DC02-zabezpieczenia.evtx", LockoutScenario().Where(r => r.Host == Dc02));
        await using var reader = connector.OpenFiles([@"C:\Eksport\DC01-zabezpieczenia.evtx", @"C:\Eksport\DC02-zabezpieczenia.evtx"]);

        var records = await ReadAllAsync(reader, new EventLogQuery { Channel = "dowolny", EventIds = [4771], NewestFirst = false }, Ct);

        Assert.Equal("DC01-zabezpieczenia.evtx, DC02-zabezpieczenia.evtx", reader.Host);
        Assert.Equal([9_001L, 5_001, 9_002, 9_003], records.Select(r => r.RecordId));
        Assert.Equal(["DC01-zabezpieczenia.evtx", "DC02-zabezpieczenia.evtx", "DC01-zabezpieczenia.evtx", "DC01-zabezpieczenia.evtx"],
            records.Select(r => r.Host));
        Assert.Equal([SecurityChannel], await reader.GetChannelsAsync(Ct));
        Assert.Equal(LockoutScenario().Count, (await reader.GetCoverageAsync(SecurityChannel, Ct)).RecordCount);
    }

    [Fact]
    public async Task UnreadableOrUnknownFiles()
    {
        var connector = Connector().AddUnreadableFile(@"D:\uszkodzony.evtx");
        await using var damaged = connector.OpenFiles([@"D:\uszkodzony.evtx"]);
        await using var missing = connector.OpenFiles([@"D:\brak.evtx"]);

        Assert.Equal(EventLogAccessError.FileUnreadable,
            (await Assert.ThrowsAsync<EventLogAccessException>(() => ReadAllAsync(damaged, new EventLogQuery { Channel = SecurityChannel }, Ct))).Error);
        Assert.Equal(EventLogAccessError.FileUnreadable,
            (await Assert.ThrowsAsync<EventLogAccessException>(() => missing.GetChannelsAsync(Ct))).Error);
        Assert.Throws<ArgumentException>(() => connector.OpenFiles([]));
    }

    [Fact]
    public async Task FormatMessage_IsDeterministic_AndConfigurable()
    {
        var connector = Connector();
        await using var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);
        var lockout = LockoutScenario().Single(r => r.EventId == 4740);

        var message = await reader.FormatMessageAsync(lockout, Ct);

        Assert.Equal(message, await reader.FormatMessageAsync(lockout, Ct));
        Assert.StartsWith("Zdarzenie 4740 (Microsoft-Windows-Security-Auditing). TargetUserName: jan.kowalski;", message, StringComparison.Ordinal);
        connector.MessageFormatter = _ => null;
        Assert.Null(await reader.FormatMessageAsync(lockout, Ct));
    }

    [Fact]
    public async Task CancellationDuringARead()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = Connector();
        connector.BeforeRead = (_, _, token) => gate.Task.WaitAsync(token);
        await using var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var read = ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel }, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task ReadsAreBatched_AndCancellableBetweenBatches()
    {
        var connector = Connector();
        connector.BatchSize = 2;
        await using var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in reader.ReadAsync(new EventLogQuery { Channel = SecurityChannel }, cancellation.Token))
            {
                if (++seen == 3)
                {
                    await cancellation.CancelAsync();
                }
            }
        });
        Assert.Equal(4, seen);
    }

    [Fact]
    public async Task DisposedReaders_RefuseCalls_AndAreCounted()
    {
        var connector = Connector();
        var reader = await connector.OpenComputerAsync(Dc01, _session, Ct);
        Assert.Equal(1, connector.OpenReaders);

        await reader.DisposeAsync();
        await reader.DisposeAsync();

        Assert.Equal(0, connector.OpenReaders);
        Assert.True(((FakeEventLogReader)reader).IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.GetChannelsAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel }, Ct));
    }

    [Fact]
    public async Task ParallelReadsOfSeveralHosts()
    {
        var connector = Connector();
        var tasks = new[] { Dc01, Dc02, Dc01, Dc02 }.Select(async host =>
        {
            await using var reader = await connector.OpenComputerAsync(host, _session, Ct);
            return (await ReadAllAsync(reader, new EventLogQuery { Channel = SecurityChannel, Outcome = EventAuditOutcome.Failure }, Ct)).Count;
        });

        var counts = await Task.WhenAll(tasks);

        Assert.Equal(counts[0], counts[2]);
        Assert.Equal(counts[1], counts[3]);
        Assert.Equal(4, connector.Reads.Count);
        Assert.Equal(0, connector.OpenReaders);
    }

    private static async Task<List<EventLogRecord>> ReadAllAsync(IEventLogReader reader, EventLogQuery query, CancellationToken cancellationToken)
    {
        var records = new List<EventLogRecord>();
        await foreach (var record in reader.ReadAsync(query, cancellationToken))
        {
            records.Add(record);
        }

        return records;
    }
}
