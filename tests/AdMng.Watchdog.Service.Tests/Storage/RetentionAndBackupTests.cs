using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Storage;

public sealed class RetentionAndBackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Purge_DeletesInBatches_AndKeepsRecentEvents()
    {
        await using var store = TestStore.Create();
        var old = Enumerable.Range(0, 12_001).Select(_ => TestData.Event(WatchEventKind.AttributesChanged, detectedAt: TestStore.Start.AddDays(-400))).ToArray();
        await store.Store.CommitAsync(new DetectionCommit { Events = old }, Ct);
        var recent = await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);

        var result = await store.Store.PurgeAsync(new RetentionSettings(), TestStore.Start, Ct);

        Assert.Equal(12_001, result.Events);
        var status = await store.Store.GetStatusAsync(Ct);
        Assert.Equal(1, status.EventCount);
        Assert.NotNull(await store.Store.GetAsync(recent, Ct));
        Assert.True(await store.Store.AppendAsync(TestData.Event(WatchEventKind.AccountDisabled), Ct) > recent);
    }

    [Fact]
    public async Task Backups_AreRotatedToBackupsToKeep()
    {
        await using var store = TestStore.Create();
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        await store.Store.PurgeAsync(new RetentionSettings { BackupsToKeep = 2 }, TestStore.Start, Ct);

        var names = new List<string>();
        for (var day = 0; day < 3; day++)
        {
            store.Clock.Advance(TimeSpan.FromDays(1));
            names.Add(await store.Store.BackupAsync(Ct));
        }

        var remaining = System.IO.Directory.GetFiles(store.Database.BackupDirectory).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["watchdog-20261015-0800.db", "watchdog-20261016-0800.db", "watchdog-20261017-0800.db"], names);
        Assert.Equal(["watchdog-20261016-0800.db", "watchdog-20261017-0800.db"], remaining);
        Assert.Equal(TestStore.Start.AddDays(3), (await store.Store.GetStatusAsync(Ct)).LastBackupAt);
    }

    [Fact]
    public async Task TwoBackupsInOneMinute_GetDistinctNames()
    {
        await using var store = TestStore.Create();

        var first = await store.Store.BackupAsync(Ct);
        var second = await store.Store.BackupAsync(Ct);

        Assert.Equal("watchdog-20261014-0800.db", first);
        Assert.Equal("watchdog-20261014-0800-2.db", second);
    }

    [Fact]
    public async Task Purge_KeepsTheNewestPreMigrationBackup_AndDeletesOlderOnesAfterTheirRetention()
    {
        await using var store = TestStore.Create();
        System.IO.Directory.CreateDirectory(store.Database.BackupDirectory);
        foreach (var name in new[] { "pre-migration-1-20260101120000.db", "pre-migration-1-20260801120000.db", "pre-migration-2-20260901120000.db", "pre-migration-3-20261010120000.db" })
        {
            await File.WriteAllBytesAsync(Path.Combine(store.Database.BackupDirectory, name), [1, 2, 3], Ct);
        }

        await store.Store.PurgeAsync(new RetentionSettings { PreMigrationBackupDays = 30 }, TestStore.Start, Ct);

        var remaining = System.IO.Directory.GetFiles(store.Database.BackupDirectory).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["pre-migration-3-20261010120000.db"], remaining);

        // Only one left: even an old newest backup is kept.
        await store.Store.PurgeAsync(new RetentionSettings { PreMigrationBackupDays = 1 }, TestStore.Start.AddYears(1), Ct);
        Assert.Single(System.IO.Directory.GetFiles(store.Database.BackupDirectory));
    }

    [Fact]
    public async Task SizeCap_PurgesTheOldestSecurityEventsUntilBelowNinetyPercent()
    {
        await using var store = TestStore.Create();
        var random = new Random(42);   // incompressible, deterministic payloads
        for (var batch = 0; batch < 6; batch++)
        {
            var records = Enumerable.Range(0, 1000).Select(i => new SecurityEvent
            {
                Source = "dc01.contoso.test",
                Computer = "dc01.contoso.test",
                RecordId = (batch * 1000) + i,
                EventCode = 5136,
                TimeCreated = TestStore.Start.AddMinutes((batch * 1000) + i),
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["AttributeValue"] = Convert.ToBase64String(RandomBytes(random, 450)),
                    ["ObjectDN"] = $"CN=Użytkownik {i},OU=Księgowość,DC=contoso,DC=test",
                },
            }).ToList();
            await store.Store.AppendAsync(records, new EventLogCursor { Source = "dc01.contoso.test", LastRecordId = (batch * 1000) + 999, UpdatedAt = TestStore.Start }, Ct);
        }

        var before = store.Store.Maintenance.UsedBytes();
        Assert.True(before > 2 * 1024 * 1024, $"test data too small: {before}");

        var (securityEvents, outbox) = await store.Store.Maintenance.EnforceSizeCapAsync(1, Ct);

        Assert.True(securityEvents > 0);
        Assert.Equal(0, outbox);
        Assert.True(store.Store.Maintenance.UsedBytes() <= 1024 * 1024 * 9 / 10);
        var left = await store.Store.FindAsync(new SecurityEventCriteria { From = TestStore.Start, To = TestStore.Start.AddYears(1), Limit = 10_000 }, Ct);
        Assert.Equal(6000 - securityEvents, left.Count);
        Assert.Equal(5999, left[^1].RecordId);   // the newest survive
        Assert.Equal(0, (await store.Store.Maintenance.EnforceSizeCapAsync(1, Ct)).SecurityEvents);
    }

    [Fact]
    public async Task SentMessage_HasNoPayloadColumnLeft()
    {
        await using var store = TestStore.Create();
        var eventId = await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        var id = await store.Store.EnqueueAsync(TestData.Message(DeliveryChannelType.Email, "email", [eventId]), Ct);
        await store.Store.LeaseDueAsync(TestStore.Start, 10, TimeSpan.FromMinutes(5), Ct);

        await store.Store.MarkSentAsync(id, TestStore.Start, Ct);

        Assert.True(store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT payload IS NULL FROM outbox WHERE id = $id;", ("$id", id))) == 1);
        Assert.Equal(OutboxStatus.Sent, Assert.Single((await store.Store.QueryAsync(new OutboxQuery(), Ct)).Items).Status);
    }

    [Fact]
    public async Task PurgedValues_AreGoneFromTheDatabaseAndWalFiles()
    {
        await using var store = TestStore.Create();
        const string Marker = "Grzegorz Brzęczyszczykiewicz";
        var target = TestData.User(Marker, "OU=Chrząszczyrzewoszyce,DC=contoso,DC=test");
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, target, TestStore.Start.AddDays(-400)), Ct);
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, TestData.Zofia), Ct);
        Assert.True(FilesContain(store, Marker), "the marker must be on disk before the purge");

        var result = await store.Store.PurgeAsync(new RetentionSettings(), TestStore.Start, Ct);

        Assert.Equal(1, result.Events);
        Assert.False(FilesContain(store, Marker));
        Assert.True(FilesContain(store, "Zofia Żak".ToLowerInvariant()));   // the live event's search text is still there
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }

    private static bool FilesContain(TestStore store, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text.ToLowerInvariant());
        var needleOriginal = Encoding.UTF8.GetBytes(text);
        foreach (var path in new[] { store.Database.DatabasePath, store.Database.DatabasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            if (bytes.AsSpan().IndexOf(needle) >= 0 || bytes.AsSpan().IndexOf(needleOriginal) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
