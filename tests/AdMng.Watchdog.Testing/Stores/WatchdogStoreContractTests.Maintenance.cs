using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// IStoreMaintenance
public abstract partial class WatchdogStoreContractTests<TStore>
{
    private static readonly RetentionSettings ShortRetention = new()
    {
        EventsDays = 30,
        SecurityEventsDays = 7,
        OutboxSentDays = 3,
        OutboxDeadDays = 10,
        DeletedSnapshotDays = 20,
        ConfigAuditDays = 60,
    };

    [Fact]
    public Task Purge_DeletesOldEvents_WithTheirPendingRowsAndOutboxLinks() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var events = await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, user, Now.AddDays(-31)), Event(WatchEventKind.AccountEnabled, user, Now.AddDays(-29)));
        await store.AddAsync([Pending(events[0].Id, "new-accounts", WebhookTarget()), Pending(events[1].Id, "new-accounts", WebhookTarget())], Ct);
        var outboxId = await store.EnqueueAsync(Message(eventIds: [events[0].Id, events[1].Id], createdAt: Now.AddDays(-1)), Ct);

        var result = await store.PurgeAsync(ShortRetention, Now, Ct);

        Assert.Equal(1, result.Events);
        Assert.Null(await store.GetAsync(events[0].Id, Ct));
        Assert.NotNull(await store.GetAsync(events[1].Id, Ct));
        Assert.Equal(events[1].Id, Assert.Single(await store.GetAllAsync(Ct)).EventId);
        Assert.Empty(await store.GetDeliveriesForEventAsync(events[0].Id, Ct));
        var item = Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(outboxId, item.Id);
        Assert.Equal(1, item.EventCount);
        Assert.Equal(1, (await store.GetStatusAsync(Ct)).EventCount);
    });

    [Fact]
    public Task Purge_DeletesOldSecurityEvents() => WithStoreAsync(async store =>
    {
        await store.AppendAsync(
            [SecurityRecord(Dc01, 1, 4738, Now.AddDays(-8)), SecurityRecord(Dc01, 2, 4738, Now.AddDays(-6))],
            Cursor(Dc01, 2),
            Ct);

        var result = await store.PurgeAsync(ShortRetention, Now, Ct);

        Assert.Equal(1, result.SecurityEvents);
        Assert.Equal([2L], (await store.FindAsync(new SecurityEventCriteria { From = Now.AddDays(-30), To = Now }, Ct)).Select(e => e.RecordId));
        Assert.Equal(2, (await store.GetCursorAsync(Dc01, Ct))?.LastRecordId);
    });

    [Fact]
    public Task Purge_DeletesSentAndDeadOutboxMessagesByTheirOwnRetention() => WithStoreAsync(async store =>
    {
        var oldSent = await store.EnqueueAsync(Message(createdAt: Now.AddDays(-5)), Ct);
        var recentSent = await store.EnqueueAsync(Message(createdAt: Now.AddDays(-5)), Ct);
        var oldDead = await store.EnqueueAsync(Message(createdAt: Now.AddDays(-11)), Ct);
        var recentDead = await store.EnqueueAsync(Message(createdAt: Now.AddDays(-9)), Ct);
        var oldPending = await store.EnqueueAsync(Message(createdAt: Now.AddDays(-50), nextAttemptAt: Now.AddDays(1)), Ct);
        await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct);
        await store.MarkSentAsync(oldSent, Now.AddDays(-4), Ct);
        await store.MarkSentAsync(recentSent, Now.AddDays(-2), Ct);
        await store.MarkDeadAsync(oldDead, "http.404", null, Ct);
        await store.MarkDeadAsync(recentDead, "http.404", null, Ct);

        var result = await store.PurgeAsync(ShortRetention, Now, Ct);

        Assert.Equal(2, result.OutboxMessages);
        Assert.Equal([oldPending, recentDead, recentSent], (await store.QueryAsync(new OutboxQuery(), Ct)).Items.Select(i => i.Id));
    });

    [Fact]
    public Task Purge_DeletesOldDeletedSnapshots_WithTheirMemberRows() => WithStoreAsync(async store =>
    {
        var oldGroup = Snapshot("CN=Stara Grupa,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        var recentUser = Snapshot("CN=Ewa Łącka,OU=Pracownicy,DC=contoso,DC=test");
        var live = Snapshot();
        await store.CommitAsync(new DetectionCommit
        {
            Upserts = [oldGroup, recentUser, live],
            Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>> { [oldGroup.ObjectGuid] = [Member(live)] },
        }, Ct);
        await store.CommitAsync(new DetectionCommit { Deletions = [(oldGroup.ObjectGuid, Now.AddDays(-21)), (recentUser.ObjectGuid, Now.AddDays(-19))] }, Ct);

        var result = await store.PurgeAsync(ShortRetention, Now, Ct);

        Assert.Equal(1, result.Snapshots);
        var remaining = await store.GetSnapshotsAsync([oldGroup.ObjectGuid, recentUser.ObjectGuid, live.ObjectGuid], Ct);
        Assert.Equal(2, remaining.Count);
        Assert.False(remaining.ContainsKey(oldGroup.ObjectGuid));
        Assert.Empty(await store.GetMembersAsync(oldGroup.ObjectGuid, Ct));
    });

    [Fact]
    public Task Purge_DeletesNoticesThirtyDaysAfterTheExpiry_AndOldAuditEntries() => WithStoreAsync(async store =>
    {
        var purgeAt = Now.AddDays(61);
        var guid = Snapshot().ObjectGuid;
        await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, purgeAt.AddDays(-31).ToFileTime(), 1, Now), Ct);
        await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, purgeAt.AddDays(-29).ToFileTime(), 1, Now), Ct);
        await store.SaveAsync(new WatchdogConfiguration(), 0, Admin, "initial", [], Ct);
        Clock.Advance(TimeSpan.FromDays(61));
        await store.SaveAsync(new WatchdogConfiguration(), 1, Admin, "rules changed", [], Ct);

        var result = await store.PurgeAsync(ShortRetention, purgeAt, Ct);

        Assert.Equal(1, result.Notices);
        Assert.Equal(1, result.AuditEntries);
        Assert.True(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, purgeAt.AddDays(-31).ToFileTime(), 1, purgeAt), Ct));
        Assert.False(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, purgeAt.AddDays(-29).ToFileTime(), 1, purgeAt), Ct));
        Assert.Equal(["rules changed"], (await store.GetAuditAsync(null, 10, Ct)).Items.Select(a => a.Summary));
        Assert.Equal(2, (await ((IConfigStore)store).GetAsync(Ct))?.Version);
    });

    [Fact]
    public Task Status_ReportsEventsAndTheLastBackup() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, user, Now.AddDays(-3)), Event(WatchEventKind.AccountEnabled, user, Now));
        Assert.Null((await store.GetStatusAsync(Ct)).LastBackupAt);
        Clock.Advance(TimeSpan.FromMinutes(15));

        var backup = await store.BackupAsync(Ct);
        var status = await store.GetStatusAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(backup));
        Assert.Equal(2, status.EventCount);
        Assert.Equal(Now.AddDays(-3), status.OldestEventAt);
        Assert.Equal(Now.AddMinutes(15), status.LastBackupAt);
        Assert.False(string.IsNullOrWhiteSpace(status.DataDirectory));
    });
}
