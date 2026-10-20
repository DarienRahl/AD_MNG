using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// IGpoContentStore (stage B design 8.8, migration 0005)
public abstract partial class WatchdogStoreContractTests<TStore>
{
    private static readonly Guid GpoA = Guid.Parse("6AC1786C-016F-11D2-945F-00C04FB984F9");
    private static readonly Guid GpoB = Guid.Parse("31B2F340-016D-11D2-945F-00C04FB984F9");

    [Fact]
    public Task GpoContent_CommitStoresTheOnlySnapshot_AppendsItsEvents_AndDequeues() => WithStoreAsync(async store =>
    {
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.Changed, Now), Ct);
        var gpo = Snapshot("CN={" + GpoA + "},CN=Policies,CN=System," + DomainDn, WatchObjectKind.GroupPolicy);
        var change = Event(WatchEventKind.GroupPolicySettingsChanged, gpo, Now) with
        {
            Changes = [new AttributeChange { Attribute = "Computer/Security/System Access/MinimumPasswordLength", OldValues = ["8"], NewValues = ["12"], OldCount = 1, NewCount = 1 }],
        };

        var stored = await store.CommitGpoContentAsync(GpoSnapshot(GpoA, "12"), [change], Ct);
        await store.CommitGpoContentAsync(GpoSnapshot(GpoA, "14") with { GpcVersion = 7 }, [], Ct);

        Assert.True(Assert.Single(stored).Id > 0);
        Assert.Equal(change.EventId, (await store.GetAsync(stored[0].Id, Ct))!.EventId);
        Assert.Empty(await store.GetGpoQueueAsync(Ct));
        var content = (await store.GetGpoContentAsync(GpoA, Ct))!;
        Assert.Null(content.DeletedAt);
        Assert.Equal(7, content.Snapshot.GpcVersion);
        Assert.Equal(65537, content.Snapshot.GptVersion);
        Assert.Equal("dc01.contoso.test", content.Snapshot.Dc);
        Assert.Equal(GpoContentState.Partial, content.Snapshot.State);
        Assert.Equal(Now, content.Snapshot.ReadAt);
        Assert.Equal(["GPT.INI", "Machine/Registry.pol"], content.Snapshot.Inventory.Select(i => i.Path));
        Assert.Equal(Now.AddMinutes(-5), content.Snapshot.Inventory[1].ModifiedAt);
        var length = Assert.Single(content.Snapshot.Settings, s => s.Key.EndsWith("MinimumPasswordLength", StringComparison.Ordinal));
        Assert.Equal(("14", GpoSettingCategory.AccountPolicy, "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf"), (length.Value, length.Category, length.Source));
        var rights = Assert.Single(content.Snapshot.Settings, s => s.IsList);
        Assert.Equal(["S-1-5-32-544", "S-1-5-32-551"], rights.Elements());
        var masked = Assert.Single(content.Snapshot.Settings, s => s.Kind == GpoSettingKind.Masked);
        Assert.Equal(string.Empty, masked.Value);
        Assert.Single(await store.ListGpoContentAsync(includeSettings: true, Ct));
        var light = Assert.Single(await store.ListGpoContentAsync(includeSettings: false, Ct));
        Assert.Empty(light.Snapshot.Settings);
        Assert.Equal(2, light.Snapshot.Inventory.Count);
    });

    [Fact]
    public Task GpoContent_EnqueueIsIdempotentByGuid_AndMergesTriggers() => WithStoreAsync(async store =>
    {
        var changeSet = Guid.NewGuid();
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.Baseline, Now.AddMinutes(5)) with { Attempts = 1, FirstAttemptAt = Now }, Ct);
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.PathChanged, Now) with { ChangeSetId = changeSet, ObjectGuid = GpoB }, Ct);
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.Changed, Now.AddMinutes(9)), Ct);
        await store.EnqueueGpoContentAsync(Queued(GpoB, GpoQueueReason.OutOfBand, Now.AddMinutes(1)), Ct);

        var queue = await store.GetGpoQueueAsync(Ct);
        Assert.Equal([GpoA, GpoB], queue.Select(q => q.GpoGuid));
        var merged = queue[0];
        Assert.Equal(GpoQueueReason.PathChanged, merged.Reason);
        Assert.Equal(Now, merged.DueAt);
        Assert.Equal(changeSet, merged.ChangeSetId);
        Assert.Equal(GpoB, merged.ObjectGuid);
        Assert.Equal(1, merged.Attempts);
        Assert.Equal(Now, merged.FirstAttemptAt);
        Assert.Equal(Now.AddMinutes(5), merged.EnqueuedAt);

        // A parked entry (attempts exhausted) is replaced by the next trigger.
        await store.UpdateGpoQueueEntryAsync(merged with { Attempts = GpoContentJob.MaxAttempts, DueAt = DateTimeOffset.MaxValue }, Ct);
        Assert.Equal(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), (await store.GetGpoQueueAsync(Ct))[^1].DueAt.ToUnixTimeMilliseconds());
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.Changed, Now.AddMinutes(2)), Ct);
        var fresh = Assert.Single(await store.GetGpoQueueAsync(Ct), q => q.GpoGuid == GpoA);
        Assert.Equal((0, GpoQueueReason.Changed, (DateTimeOffset?)null, (Guid?)GpoB), (fresh.Attempts, fresh.Reason, fresh.FirstAttemptAt, fresh.ObjectGuid));

        // Update touches existing entries only; remove.
        await store.UpdateGpoQueueEntryAsync(Queued(Guid.NewGuid(), GpoQueueReason.Changed, Now), Ct);
        Assert.Equal(2, (await store.GetGpoQueueAsync(Ct)).Count);
        await store.RemoveGpoQueueEntryAsync(GpoB, Ct);
        await store.RemoveGpoQueueEntryAsync(GpoB, Ct);
        Assert.Equal(GpoA, Assert.Single(await store.GetGpoQueueAsync(Ct)).GpoGuid);
    });

    [Fact]
    public Task GpoContent_DeletionMark_ClearedByTheNextCommit_PurgedAfterDeletedSnapshotDays() => WithStoreAsync(async store =>
    {
        await store.CommitGpoContentAsync(GpoSnapshot(GpoA, "12"), [], Ct);
        await store.CommitGpoContentAsync(GpoSnapshot(GpoB, "12"), [], Ct);
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.Changed, Now), Ct);

        await store.MarkGpoContentDeletedAsync(GpoA, Now.AddDays(-21), Ct);
        await store.MarkGpoContentDeletedAsync(GpoA, Now, Ct); // the first mark stays
        await store.MarkGpoContentDeletedAsync(Guid.NewGuid(), Now, Ct);
        Assert.Equal(Now.AddDays(-21), (await store.GetGpoContentAsync(GpoA, Ct))!.DeletedAt);
        Assert.Empty(await store.GetGpoQueueAsync(Ct));

        await store.MarkGpoContentDeletedAsync(GpoB, Now.AddDays(-30), Ct);
        await store.CommitGpoContentAsync(GpoSnapshot(GpoB, "14"), [], Ct); // restored and read again
        Assert.Null((await store.GetGpoContentAsync(GpoB, Ct))!.DeletedAt);

        var result = await store.PurgeAsync(new RetentionSettings { DeletedSnapshotDays = 20 }, Now, Ct);
        Assert.True(result.Snapshots >= 1);
        Assert.Null(await store.GetGpoContentAsync(GpoA, Ct));
        Assert.NotNull(await store.GetGpoContentAsync(GpoB, Ct));
    });

    [Fact]
    public Task GpoContent_JobState_RoundTrips() => WithStoreAsync(async store =>
    {
        Assert.Equal(new GpoContentJobState(), await store.GetGpoContentStateAsync(Ct));
        var state = new GpoContentJobState { EventCursor = 42, BaselineCompletedAt = Now, LastReconciliationAt = Now.AddDays(1), LastReadAt = Now.AddHours(2) };
        await store.SaveGpoContentStateAsync(state, Ct);
        Assert.Equal(state, await store.GetGpoContentStateAsync(Ct));
        await store.SaveGpoContentStateAsync(new GpoContentJobState { EventCursor = 43 }, Ct);
        Assert.Equal(new GpoContentJobState { EventCursor = 43 }, await store.GetGpoContentStateAsync(Ct));
    });

    [Fact]
    public Task GpoContent_CompleteQueueEntry_AppendsEventsWithoutTouchingTheSnapshot() => WithStoreAsync(async store =>
    {
        await store.CommitGpoContentAsync(GpoSnapshot(GpoA, "12"), [], Ct);
        await store.EnqueueGpoContentAsync(Queued(GpoA, GpoQueueReason.PathChanged, Now), Ct);
        var redirected = Event(WatchEventKind.GroupPolicyPathRedirected, Snapshot("CN={" + GpoA + "},CN=Policies,CN=System," + DomainDn, WatchObjectKind.GroupPolicy), Now);

        var stored = await store.CompleteGpoQueueEntryAsync(GpoA, [redirected], Ct);

        Assert.Equal(WatchEventKind.GroupPolicyPathRedirected, (await store.GetAsync(Assert.Single(stored).Id, Ct))!.Kind);
        Assert.Empty(await store.GetGpoQueueAsync(Ct));
        Assert.Equal("12", (await store.GetGpoContentAsync(GpoA, Ct))!.Snapshot.Settings.Single(s => s.Key.EndsWith("Length", StringComparison.Ordinal)).Value);
    });

    private static GpoQueueEntry Queued(Guid gpo, GpoQueueReason reason, DateTimeOffset dueAt) =>
        new() { GpoGuid = gpo, Reason = reason, DueAt = dueAt, EnqueuedAt = dueAt };

    private static GpoContentSnapshot GpoSnapshot(Guid gpo, string minimumPasswordLength) => new()
    {
        GpoGuid = gpo,
        Dc = "dc01.contoso.test",
        GpcVersion = 65537,
        GptVersion = 65537,
        ReadAt = Now,
        State = GpoContentState.Partial,
        Inventory =
        [
            new GpoInventoryEntry { Path = "GPT.INI", Size = 22, ModifiedAt = Now.AddMinutes(-6) },
            new GpoInventoryEntry { Path = "Machine/Registry.pol", Size = 126, ModifiedAt = Now.AddMinutes(-5) },
        ],
        Settings =
        [
            new GpoSetting
            {
                Key = "Computer/Preferences/Groups/{A1B2C3D4-0000-4000-8000-000000000001}/cpassword",
                Kind = GpoSettingKind.Masked,
                Category = GpoSettingCategory.PreferenceGroups,
                Source = "Machine/Preferences/Groups/Groups.xml",
            },
            new GpoSetting
            {
                Key = "Computer/Security/Privilege Rights/SeDebugPrivilege",
                Value = "S-1-5-32-544" + GpoSetting.ListSeparator + "S-1-5-32-551",
                IsList = true,
                Category = GpoSettingCategory.PrivilegeRights,
                Source = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf",
            },
            new GpoSetting
            {
                Key = "Computer/Security/System Access/MinimumPasswordLength",
                Value = minimumPasswordLength,
                Category = GpoSettingCategory.AccountPolicy,
                Source = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf",
            },
        ],
    };
}
