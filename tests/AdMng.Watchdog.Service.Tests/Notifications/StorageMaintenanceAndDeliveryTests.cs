using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Retention;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>
/// Storage maintenance next to delivery (spec 16.3): the daily backup and the compaction must not hold the single-writer
/// gate for their whole duration – every outbox write (lease, sent, retry) waits behind it – and the storage health
/// checks have one definition, so the hourly guard and the retention job never flap the same check.
/// </summary>
public sealed class StorageMaintenanceAndDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backup_CopiesTheDatabase_WithoutWaitingForTheWriterGate()
    {
        await using var store = TestStore.Create();
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        using var gateHeld = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Somebody (here: the test, in production e.g. a long purge batch) holds the writer gate.
        var holder = Task.Run(() => store.Database.ExclusiveAsync(_ =>
        {
            gateHeld.Set();
            return release.Wait(TimeSpan.FromSeconds(60));
        }, Ct), Ct);
        Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(30), Ct));
        var backup = Task.Run(() => store.Store.BackupAsync(Ct), Ct);

        var copied = SpinWait.SpinUntil(
            () => System.IO.Directory.Exists(store.Database.BackupDirectory) && System.IO.Directory.GetFiles(store.Database.BackupDirectory, "watchdog-*.db").Length == 1,
            TimeSpan.FromSeconds(30));
        release.Set();
        await holder;
        var name = await backup;

        Assert.True(copied, "VACUUM INTO waited for the writer gate.");
        Assert.Equal("watchdog-20261014-0800.db", name);
        Assert.Equal(TestStore.Start, (await store.Store.GetStatusAsync(Ct)).LastBackupAt);
    }

    [Fact]
    public async Task OutboxWrites_GoOnWhileTheBackupRuns()
    {
        await using var store = TestStore.Create();
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);

        var backup = store.Store.BackupAsync(Ct);
        var id = await store.Store.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(TestStore.Start).ToString("N"),
            ChannelType = DeliveryChannelType.Webhook,
            ChannelId = "siem",
            Kind = DeliveryKind.Health,
            Http = new RenderedHttpMessage { Body = "{\"type\":\"watchdog.heartbeat\"}"u8.ToArray() },
            CreatedAt = TestStore.Start,
            NextAttemptAt = TestStore.Start,
        }, Ct);
        await backup;

        Assert.True(id > 0);
        Assert.Single(System.IO.Directory.GetFiles(store.Database.BackupDirectory, "watchdog-*.db"));
    }

    [Fact]
    public async Task Compaction_FreesEveryFreePage()
    {
        await using var store = TestStore.Create();
        var events = Enumerable.Range(0, 3000).Select(i => TestData.Event(WatchEventKind.AttributesChanged)).ToArray();
        await store.Store.CommitAsync(new DetectionCommit { Events = events }, Ct);
        await store.Database.WriteAsync((c, t) => SqliteValues.Execute(c, t, "DELETE FROM events;"), Ct);
        Assert.True(FreePages(store) > 0);

        await store.Store.Maintenance.CompactAsync(Ct);

        Assert.Equal(0, FreePages(store));
    }

    [Fact]
    public async Task StorageGuard_ReportsTheSameSizeAndFreeChecks_AsTheRetentionJob()
    {
        // 1 MB cap; the database file still holds the deleted rows (no compaction yet): used pages are far below 80 %,
        // the file is not – the guard must not report Ok while the retention job reports Warning/Error every hour.
        await using var store = TestStore.Create();
        var events = Enumerable.Range(0, 3000).Select(i => TestData.Event(WatchEventKind.AttributesChanged)).ToArray();
        await store.Store.CommitAsync(new DetectionCommit { Events = events }, Ct);
        await store.Database.WriteAsync((c, t) => SqliteValues.Execute(c, t, "DELETE FROM events;"), Ct);
        var retention = new RetentionSettings { MaxDatabaseMb = 1 };
        var health = new FakeHealthRegistry();
        var job = new StorageGuardJob(store.Store, new FakeConfigProvider(new WatchdogConfiguration { Retention = retention }), health,
            new DataDirectory(store.Directory.Path), store.Clock, new TestLog().For<StorageGuardJob>());

        await job.RunOnceAsync(Ct);

        var status = await store.Store.GetStatusAsync(Ct);
        var expectedSize = StorageHealth.SizeCheck(status, retention, store.Clock.GetUtcNow());
        var expectedFree = StorageHealth.FreeCheck(status, store.Clock.GetUtcNow());
        Assert.NotEqual(HealthState.Ok, expectedSize.State);
        var size = Assert.Single(health.Checks, c => c.Code == StorageHealth.SizeCode);
        var free = Assert.Single(health.Checks, c => c.Code == StorageHealth.FreeCode);
        Assert.Equal(expectedSize.State, size.State);
        Assert.Equal(expectedSize.Args, size.Args);
        Assert.Equal(expectedFree.State, free.State);
        Assert.Equal(expectedFree.Args, free.Args);
    }

    private static long FreePages(TestStore store) => store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "PRAGMA freelist_count;"));
}
