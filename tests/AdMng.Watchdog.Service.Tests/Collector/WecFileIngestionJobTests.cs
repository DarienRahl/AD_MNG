using System.Runtime.Versioning;
using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Service.Attribution;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;
using static AdMng.Watchdog.Service.Tests.Collector.TailerFixture;

namespace AdMng.Watchdog.Service.Tests.Collector;

/// <summary>
/// The WEF tailer (stage B design 7.5) over real files and the SQLite store: file order and rotation, the cursor across
/// renames, at-least-once delivery with a crash at every step (nothing lost, nothing stored or detected twice), poison
/// input, partial and oversized lines, lost files, the progress file of the rotation and the collector health of 7.9.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class WecFileIngestionJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachDcDirectory_IsReadToItsLastCompleteLine()
    {
        await using var f = new TailerFixture();
        f.Append(f.Active(Client1), Line(f.Change(Dc1)), Line(f.Change(Dc1)));
        f.Append(f.Active(Client2), Line(f.Change(Dc2), Client2, Ip2), Line(f.Change(Dc2), Client2, Ip2)[..40]);   // the second one is being written

        var result = await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.False(result.Failed);
        Assert.Equal(TimeSpan.FromSeconds(2), result.NextRunIn);
        var stored = await f.StoredAsync(Ct);
        Assert.Equal(2, stored.Count(r => r.Computer == Dc1));
        Assert.Single(stored, r => r.Computer == Dc2);
        var cursor = WecFileCursor.Parse(await f.Store.Store.GetCursorAsync("wec:DC02@CONTOSO.TEST", Ct))!;
        Assert.Equal(new FileInfo(f.Active(Client2)).Length - 40, cursor.Offset);

        var status = f.Health.Collector!;
        Assert.Equal(HealthState.Ok, status.State);
        Assert.Equal([Dc1, Dc2], status.DomainControllers.Select(d => d.Dc));
        Assert.Equal("DC01@CONTOSO.TEST", status.DomainControllers[0].Client);
        Assert.Equal(2, status.DomainControllers[0].RecordsStored24h);
        Assert.Equal(TimeSpan.FromSeconds(30), status.DomainControllers[0].MedianLag);
        Assert.Equal(40, status.DomainControllers[1].BacklogBytes);
        Assert.Equal(HealthState.Ok, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecNotForwarding)).State);
    }

    [Fact]
    public async Task RotatedFilesComeFirstByStamp_AndTheCursorFollowsARename()
    {
        await using var f = new TailerFixture();
        f.Append(f.Active(Client1) + ".20261014T060000Z", Line(f.Change(Dc1, 1)));
        f.Append(f.Active(Client1) + ".20261014T050000Z", Line(f.Change(Dc1, 2)));
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 3)));
        var job = f.NewJob();
        await f.RunUntilIdleAsync(job, Ct);

        // OpenWEC appends, then admng-wec-rotate renames the active file and OpenWEC starts a new one.
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 4)));
        f.Rotate(Client1, TestStore.Start);
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 5)));
        await f.RunUntilIdleAsync(job, Ct);

        Assert.Equal([1L, 2, 3, 4, 5], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        var progress = WecProgressFile.Read(f.ProgressFile);
        Assert.Equal(
            ["security.jsonl.20261014T050000Z", "security.jsonl.20261014T060000Z", "security.jsonl.20261014T080000Z", "security.jsonl"],
            progress.Select(p => p.Name));
        Assert.Equal([true, true, true, false], progress.Select(p => p.Complete));
        Assert.All(progress, p => Assert.Equal(p.Size, p.Offset));
    }

    public static TheoryData<string, int> CrashPoints() => new()
    {
        { "attempt", 1 }, { "prepared", 1 }, { "detected", 1 }, { "committed", 1 }, { "progress", 1 },
        { "attempt", 2 }, { "prepared", 3 }, { "detected", 2 }, { "committed", 3 }, { "progress", 2 },
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task ACrashAtAnyStep_LosesAndDuplicatesNothing(string step, int occurrence)
    {
        await using var f = new TailerFixture();
        var expected = new List<long>();
        void Add(string path, int count)
        {
            var lines = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var id = f.NextId();
                expected.Add(id);
                lines.Append(Line(i == 0 && path.EndsWith("060000Z", StringComparison.Ordinal)
                    ? WindowsEventXml.LogCleared(id, TestStore.Start.AddMinutes(-2), Dc1, AdminSid, "adm.łukasz", "0x5a3f1")
                    : f.Change(Dc1, id)));
            }

            f.Append(path, lines.ToString());
        }

        Add(f.Active(Client1) + ".20261014T060000Z", 3);
        Add(f.Active(Client1) + ".20261014T070000Z", 2);
        Add(f.Active(Client1), 4);
        var seen = 0;
        var crashes = 0;
        var job = f.NewJob((name, _) =>
        {
            if (name == step && ++seen == occurrence)
            {
                crashes++;
                throw new IOException("injected crash at " + name);
            }
        });

        var first = await f.RunUntilIdleAsync(job, Ct);
        Assert.True(first.Failed);
        Assert.Equal(1, crashes);
        var restarted = await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.False(restarted.Failed);
        Assert.Equal(expected.Order(), (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        var events = await f.Store.Store.GetRangeAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, Ct).ToListAsync(Ct);
        Assert.Single(events, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Single(events, e => e.Kind == WatchEventKind.HealthEventLogGap);
        Assert.All(WecProgressFile.Read(f.ProgressFile), p => Assert.Equal(p.Size, p.Offset));
    }

    [Fact]
    public async Task ALineThatCrashesTheProcessEveryTime_IsSkippedAfterThreeAttempts()
    {
        await using var f = new TailerFixture();
        var poison = f.Change(Dc1, 9999);
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 1)), Line(poison), Line(f.Change(Dc1, 2)));
        void Crash(string step, IReadOnlyList<WecLine> lines)
        {
            if (step == "prepared" && lines.Any(l => Encoding.UTF8.GetString(l.Bytes.Span).Contains(">9999<", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("the parser hangs on this line");   // stands for a crash or a watchdog kill
            }
        }

        // Three starts fail at the same offset (attempts 1–3), the fourth processes the batch line by line: the poison
        // line fails alone, is skipped and counted, the others are stored.
        var crashes = 0;
        JobResult result = default;
        for (var start = 0; start < 6; start++)
        {
            result = await f.RunUntilIdleAsync(f.NewJob(Crash), Ct);
            if (!result.Failed)
            {
                break;
            }

            crashes++;
        }

        Assert.False(result.Failed);
        Assert.Equal(3, crashes);
        Assert.Equal([1L, 2], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        Assert.Equal(1, f.Health.Collector!.DomainControllers[0].MalformedLines24h);
        Assert.Equal(HealthState.Warning, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecMalformedLines)).State);
    }

    [Fact]
    public async Task AStoreFailure_IsRetriedWithoutSkippingAnything()
    {
        await using var f = new TailerFixture();
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 1)), Line(f.Change(Dc1, 2)));
        var failures = 0;
        var job = f.NewJob((step, _) =>
        {
            if (step == "committed" && failures++ < 5)
            {
                throw new IOException("SQLite Error 13: database or disk is full");
            }
        });

        for (var i = 0; i < 8; i++)
        {
            await job.RunOnceAsync(Ct);
        }

        Assert.Equal([1L, 2], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        Assert.Equal(0, f.Health.Collector!.DomainControllers[0].MalformedLines24h);
    }

    [Fact]
    public async Task LongPartialAndDamagedLines_AreSkippedAndCounted()
    {
        await using var f = new TailerFixture();
        var whole = Line(f.Change(Dc1, 1));
        f.Append(f.Active(Client1) + ".20261014T060000Z",
            new string('x', WecFiles.MaxLineBytes + 10) + "\n",                 // longer than 1 MiB
            "{\"meta\":{\"IpAddress\":\"10.0.1.10\"\n",                        // damaged
            whole[..60] + whole,                                               // openwecd died mid-write, then resumed
            Line(f.Change(Dc1, 2))[..50]);                                      // a tail that never got its line feed
        File.SetLastWriteTimeUtc(f.Active(Client1) + ".20261014T060000Z", TestStore.Start.AddMinutes(-30).UtcDateTime);
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 3)));

        await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.Equal([1L, 3], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        Assert.Equal(4, f.Health.Collector!.DomainControllers[0].MalformedLines24h);   // long + damaged + partial before the record + stale tail
    }

    [Fact]
    public async Task AnUnconsumedFileThatWasDeleted_IsReportedLost_AndReadingGoesOn()
    {
        await using var f = new TailerFixture();
        var rotated = f.Active(Client1) + ".20261014T060000Z";
        f.Append(rotated, Line(f.Change(Dc1, 1)), Line(f.Change(Dc1, 2)));
        // The cursor points into that file with unread bytes (the service was stopped), then MAX_UNCONSUMED_DAYS deleted it.
        var file = WecFiles.List(f.Directory(Client1), "security.jsonl").Single();
        await f.Store.Store.AppendAsync([], WecFileCursor.At(file, 10).ToEventLogCursor("DC01@CONTOSO.TEST", TestStore.Start), Ct);
        File.Delete(rotated);
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 3)));

        var job = f.NewJob();
        await f.RunUntilIdleAsync(job, Ct);

        Assert.Equal([3L], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        Assert.Equal(HealthState.Error, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecCollectorFileLost)).State);
        var events = await f.Store.Store.GetRangeAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, Ct).ToListAsync(Ct);
        var gap = Assert.Single(events, e => e.Kind == WatchEventKind.HealthEventLogGap);
        Assert.Equal(EventLogGapDetector.CollectorFileLost, gap.Changes[0].NewValues[0]);

        await f.RunUntilIdleAsync(job, Ct);   // reported once
        Assert.Single(await f.Store.Store.GetRangeAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, Ct).ToListAsync(Ct),
            e => e.Kind == WatchEventKind.HealthEventLogGap);
    }

    [Fact]
    public async Task AConsumedFileDeletedByTheRotation_IsNoLoss_AndAReusedInodeIsNotTheCursorsFile()
    {
        await using var f = new TailerFixture();
        var rotated = f.Active(Client1) + ".20261014T060000Z";
        f.Append(rotated, Line(f.Change(Dc1, 1)));
        var job = f.NewJob();
        await f.RunUntilIdleAsync(job, Ct);
        File.Delete(rotated);   // listed complete – admng-wec-rotate deleted it
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 2)));
        await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.Equal(HealthState.Ok, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecCollectorFileLost)).State);

        // A cursor with the active file's device and inode but another first line (the inode was reused) is not its file.
        var active = WecFiles.List(f.Directory(Client1), "security.jsonl").Single();
        var stale = WecFileCursor.At(active, active.Size) with { FirstLineSha256 = new string('0', 64) };
        await f.Store.Store.AppendAsync([], stale.ToEventLogCursor("DC01@CONTOSO.TEST", TestStore.Start), Ct);
        await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.Equal([1L, 2], (await f.StoredAsync(Ct)).Select(r => r.RecordId));   // read again from 0: absorbed by the store
        Assert.True(WecFileCursor.Parse(await f.Store.Store.GetCursorAsync("wec:DC01@CONTOSO.TEST", Ct))!.Matches(active));
    }

    [Fact]
    public async Task ForeignAndMismatchedRecords_AreRejectedAndReported()
    {
        await using var f = new TailerFixture();
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 1), ip: "10.66.66.66"), Line(f.Change(Dc1, 2)));
        f.Append(f.Active("SRV01$@CONTOSO.TEST"), Line(f.Change("srv01.contoso.test", 3), "SRV01$@CONTOSO.TEST", "10.0.4.4"));

        await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.Equal([2L], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        Assert.Equal(HealthState.Error, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecIdentityMismatch)).State);
        Assert.Equal(HealthState.Error, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec("srv01.contoso.test", HealthCheckCodes.WecIdentityMismatch)).State);
        Assert.Equal(1, f.Health.Collector!.DomainControllers[0].RejectedRecords24h);
        Assert.Equal(HealthState.Error, f.Health.Collector.State);
        Assert.Null(await f.Store.Store.GetCursorAsync("wec:SRV01@CONTOSO.TEST", Ct));   // a foreign client's files are not read

        // A DC the DC list did not know yet: its directory waits and is read once the list knows it.
        f.Append(f.Active("DC03$@CONTOSO.TEST"), Line(f.Change("dc03.contoso.test", 4), "DC03$@CONTOSO.TEST", "10.0.1.12"));
        var job = f.NewJob();
        await f.RunUntilIdleAsync(job, Ct);
        Assert.DoesNotContain(4L, (await f.StoredAsync(Ct)).Select(r => r.RecordId));
        f.DomainControllers = [.. f.DomainControllers, new("dc03.contoso.test", "DC03$", [System.Net.IPAddress.Parse("10.0.1.12")])];
        await f.RunUntilIdleAsync(job, Ct);
        Assert.Contains(4L, (await f.StoredAsync(Ct)).Select(r => r.RecordId));
    }

    [Fact]
    public async Task WithoutTheDcList_NothingIsReadOrDropped()
    {
        await using var f = new TailerFixture { DomainControllers = [] };
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 1)));
        var job = f.NewJob();

        await job.RunOnceAsync(Ct);
        Assert.Empty(await f.StoredAsync(Ct));
        Assert.Equal(HealthState.Unknown, f.Health.Collector!.State);

        f.DomainControllers = [new(Dc1, "DC01$", [System.Net.IPAddress.Parse(Ip1)])];
        await f.RunUntilIdleAsync(job, Ct);
        Assert.Equal([1L], (await f.StoredAsync(Ct)).Select(r => r.RecordId));
    }

    [Fact]
    public async Task SilentAndNeverForwardingDcs_AreReportedAndReadAsUnreachable()
    {
        await using var f = new TailerFixture();
        f.Append(f.Active(Client1), Line(f.Change(Dc1, 1), received: TestStore.Start));
        var job = f.NewJob();
        await f.RunUntilIdleAsync(job, Ct);

        Assert.Equal(HealthState.Error, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc2, HealthCheckCodes.WecNotForwarding)).State);
        Assert.Equal(HealthState.Ok, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecSilent)).State);

        f.Clock.Advance(TimeSpan.FromMinutes(20));
        await job.RunOnceAsync(Ct);
        Assert.Equal(HealthState.Warning, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecSilent)).State);

        f.Clock.Advance(TimeSpan.FromMinutes(45));
        await job.RunOnceAsync(Ct);
        Assert.Equal(HealthState.Error, f.Health.Checks.Single(c => c.Code == HealthCheckCodes.Wec(Dc1, HealthCheckCodes.WecSilent)).State);
        var tracker = (DcLogHealthTracker)f.Services.GetService(typeof(DcLogHealthTracker))!;
        Assert.True(tracker.WasUnreachableSince(Dc1, TestStore.Start));   // DC_UNREACHABLE (collector variant) for attribution
        Assert.True(tracker.WasUnreachableSince(Dc2, TestStore.Start));
    }

    [Fact]
    public async Task OverTheBudget_LogonRecordsAreCountedNotStored_AndFloodingIsRaised()
    {
        await using var f = new TailerFixture { MaxLogonRecordsPerDcPerMinute = 2 };
        var at = TestStore.Start.AddMinutes(-1);
        f.Append(f.Active(Client1),
            Line(WindowsEventXml.LogonFailed(f.NextId(), at, Dc1, "konto1", "10.0.7.8", null)),
            Line(WindowsEventXml.LogonFailed(f.NextId(), at, Dc1, "konto2", "10.0.7.8", null)),
            Line(WindowsEventXml.LogonFailed(f.NextId(), at, Dc1, "konto3", "10.0.7.8", null)),
            Line(f.Change(Dc1)));

        await f.RunUntilIdleAsync(f.NewJob(), Ct);

        Assert.Equal(3, (await f.StoredAsync(Ct)).Count);
        Assert.Equal(1, f.Health.Collector!.DomainControllers[0].DroppedLogonRecords24h);
        var events = await f.Store.Store.GetRangeAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, Ct).ToListAsync(Ct);
        Assert.Equal(EventLogGapDetector.Flooding, Assert.Single(events, e => e.Kind == WatchEventKind.HealthEventLogGap).Changes[0].NewValues[0]);
    }

    [Fact]
    public async Task AnotherAttributionSource_LeavesTheFilesAlone()
    {
        await using var f = new TailerFixture(new WatchdogConfiguration { Attribution = new AttributionSettings { Source = AttributionSourceKind.None } });
        f.Append(f.Active(Client1), Line(f.Change(Dc1)));

        var result = await f.NewJob().RunOnceAsync(Ct);

        Assert.Equal(TimeSpan.FromSeconds(30), result.NextRunIn);
        Assert.Empty(await f.StoredAsync(Ct));
        Assert.Equal(HealthState.Disabled, f.Health.Collector!.State);
        Assert.False(File.Exists(f.ProgressFile));
    }

    [Fact]
    public async Task TheJob_IsTheEnginesCollector()
    {
        await using var f = new TailerFixture();
        var job = f.NewJob();

        Assert.Equal("wec-ingestion", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(2), job.DefaultInterval);
        Assert.True(((ISecurityEventCollector)job).IsAvailable);
        Assert.True(CollectorMode.IsActive(new AttributionSettings(), job));
    }
}
