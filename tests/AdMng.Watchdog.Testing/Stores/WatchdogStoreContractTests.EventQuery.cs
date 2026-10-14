using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Xunit;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Testing.Stores;

// IEventStore.QueryAsync: paging and every EventQuery filter.
public abstract partial class WatchdogStoreContractTests<TStore>
{
    [Fact]
    public Task Query_After_ReturnsAscendingIds_WithHasMoreAndLatestId() => WithStoreAsync(async store =>
    {
        var ids = await StoreManyAsync(store, 5);

        var page = await store.QueryAsync(new EventQuery { After = ids[0], Limit = 2 }, Ct);

        Assert.Equal([ids[1], ids[2]], page.Items.Select(e => e.Id));
        Assert.True(page.HasMore);
        Assert.Equal(ids[4], page.LatestId);
        var last = await store.QueryAsync(new EventQuery { After = ids[2], Limit = 2 }, Ct);
        Assert.Equal([ids[3], ids[4]], last.Items.Select(e => e.Id));
        Assert.False(last.HasMore);
    });

    [Fact]
    public Task Query_WithoutCursor_IsNewestFirst_AndBeforePagesBack() => WithStoreAsync(async store =>
    {
        var ids = await StoreManyAsync(store, 5);

        var newest = await store.QueryAsync(new EventQuery { Limit = 2 }, Ct);
        var older = await store.QueryAsync(new EventQuery { Before = ids[3], Limit = 2 }, Ct);
        var oldest = await store.QueryAsync(new EventQuery { Before = ids[1], Limit = 2 }, Ct);

        Assert.Equal([ids[4], ids[3]], newest.Items.Select(e => e.Id));
        Assert.True(newest.HasMore);
        Assert.Equal([ids[2], ids[1]], older.Items.Select(e => e.Id));
        Assert.Equal([ids[0]], oldest.Items.Select(e => e.Id));
        Assert.False(oldest.HasMore);
    });

    [Fact]
    public Task Query_AfterWinsOverBefore_AndTheLimitIsCappedAt500() => WithStoreAsync(async store =>
    {
        var ids = await StoreManyAsync(store, 501);

        var page = await store.QueryAsync(new EventQuery { After = 0, Before = ids[1], Limit = 1000 }, Ct);

        Assert.Equal(500, page.Items.Count);
        Assert.Equal(ids[0], page.Items[0].Id);
        Assert.True(page.HasMore);
    });

    [Fact]
    public Task Query_LatestId_IgnoresTheFilters() => WithStoreAsync(async store =>
    {
        var ids = await StoreManyAsync(store, 3);

        var page = await store.QueryAsync(new EventQuery { After = 0, Kinds = [WatchEventKind.TrustChanged] }, Ct);

        Assert.Empty(page.Items);
        Assert.Equal(ids[2], page.LatestId);
    });

    [Fact]
    public Task Query_FiltersByKindCategoryObjectKindAndSeverity() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var group = Snapshot("CN=Domain Admins,CN=Users,DC=contoso,DC=test", WatchObjectKind.Group);
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, user, Now, Severity.Low),
            Event(WatchEventKind.GroupMemberAdded, group, Now, Severity.Info),
            Event(WatchEventKind.PrivilegeGranted, user, Now, Severity.Critical),
            Event(WatchEventKind.HealthStorage, user, Now, Severity.High) with { Object = new ObjectRef { Dn = "", Kind = WatchObjectKind.Watchdog } });

        Assert.Equal([stored[0].Id], await IdsAsync(store, new EventQuery { Kinds = [WatchEventKind.UserCreated] }));
        Assert.Equal([stored[2].Id, stored[1].Id], await IdsAsync(store, new EventQuery { Categories = [EventCategory.Membership] }));
        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { ObjectKinds = [WatchObjectKind.Group] }));
        Assert.Equal([stored[3].Id, stored[2].Id], await IdsAsync(store, new EventQuery { MinSeverity = Severity.High }));
        Assert.Equal([stored[3].Id], await IdsAsync(store, new EventQuery { Categories = [EventCategory.Health], ObjectKinds = [WatchObjectKind.Watchdog] }));
    });

    [Fact]
    public Task Query_Text_MatchesNameSamDnActorWorkstationAndIp_CaseInsensitively() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var other = Snapshot("CN=Jan Kowalski,OU=Sprzedaż,DC=contoso,DC=test");
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.AttributesChanged, user, Now),
            Event(WatchEventKind.AttributesChanged, other, Now, attribution: Resolved(@"CONTOSO\adm.żaneta", "S-1-5-21-1-2-3-1199", "WS-ŁÓDŹ-12", "10.20.30.40")));

        Assert.Equal([stored[0].Id], await IdsAsync(store, new EventQuery { Text = "ZOFIA ŻAK" }));
        Assert.Equal([stored[0].Id], await IdsAsync(store, new EventQuery { Text = "zofia.zak" }));
        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { Text = "ou=sprzedaż" }));
        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { Text = "adm.ŻANETA" }));
        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { Text = "łódź-12" }));
        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { Text = "10.20.30" }));
        Assert.Empty(await IdsAsync(store, new EventQuery { Text = "nieistniejący" }));
    });

    [Fact]
    public Task Query_Subtree_MatchesTheDnOrThePreviousDnAtOrBelow() => WithStoreAsync(async store =>
    {
        var inside = Snapshot("CN=Jan Kowalski,OU=Oddział Gdańsk,DC=contoso,DC=test");
        var ou = Snapshot("OU=Oddział Gdańsk,DC=contoso,DC=test", WatchObjectKind.OrganizationalUnit);
        var movedAway = Snapshot("CN=Ewa Łącka,OU=Centrala,DC=contoso,DC=test");
        var elsewhere = Snapshot("CN=Adam Gość,OU=Oddział Gdynia,DC=contoso,DC=test");
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, inside, Now),
            Event(WatchEventKind.OrganizationalUnitCreated, ou, Now),
            Event(WatchEventKind.ObjectMoved, movedAway, Now) with { PreviousDn = "CN=Ewa Łącka,OU=Oddział Gdańsk,DC=contoso,DC=test" },
            Event(WatchEventKind.UserCreated, elsewhere, Now));

        Assert.Equal([stored[2].Id, stored[1].Id, stored[0].Id], await IdsAsync(store, new EventQuery { Subtree = "ou=ODDZIAŁ GDAŃSK,dc=contoso,dc=test" }));
    });

    [Fact]
    public Task Query_Tags_RequireAll_AndAttributionStatusesAnyOf() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.PasswordChanged, user, Now, tags: [EventTags.Privileged, EventTags.PasswordReset]),
            Event(WatchEventKind.PasswordChanged, user, Now, tags: [EventTags.Privileged], attribution: Resolved(@"CONTOSO\a", "S-1-5-21-1-2-3-500")),
            Event(WatchEventKind.PasswordChanged, user, Now, attribution: new EventAttribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.NoMatch }));

        Assert.Equal([stored[0].Id], await IdsAsync(store, new EventQuery { Tags = [EventTags.Privileged, EventTags.PasswordReset] }));
        Assert.Equal([stored[1].Id, stored[0].Id], await IdsAsync(store, new EventQuery { Tags = [EventTags.Privileged] }));
        Assert.Equal([stored[2].Id, stored[0].Id],
            await IdsAsync(store, new EventQuery { AttributionStatuses = [AttributionStatus.Pending, AttributionStatus.Unknown] }));
    });

    [Fact]
    public Task Query_TimeRange_IsHalfOpen() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, user, Now.AddHours(-2)),
            Event(WatchEventKind.UserCreated, user, Now.AddHours(-1)),
            Event(WatchEventKind.UserCreated, user, Now));

        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { From = Now.AddHours(-1), To = Now }));
        Assert.Equal([stored[2].Id, stored[1].Id], await IdsAsync(store, new EventQuery { From = Now.AddHours(-1) }));
        Assert.Equal([stored[0].Id], await IdsAsync(store, new EventQuery { To = Now.AddHours(-1) }));
    });

    [Fact]
    public Task Query_FiltersByRuleObjectActorAndAcknowledgement() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var other = Snapshot("CN=Jan Kowalski,OU=Sprzedaż,DC=contoso,DC=test");
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.AccountLockedOut, user, Now),
            Event(WatchEventKind.AccountLockedOut, other, Now, attribution: Resolved(@"CONTOSO\adm.nowak", "S-1-5-21-1-2-3-1104")),
            Event(WatchEventKind.AccountDisabled, other, Now));
        await store.CommitEvaluationAsync([new EventEvaluation { EventId = stored[0].Id, Severity = Severity.Low, MatchedRules = ["lockouts"] }], [], Ct);
        await store.AcknowledgeAsync(stored[2].Id, "helpdesk", null, Now, Ct);

        Assert.Equal([stored[0].Id], await IdsAsync(store, new EventQuery { RuleId = "LOCKOUTS" }));
        Assert.Equal([stored[2].Id, stored[1].Id], await IdsAsync(store, new EventQuery { ObjectGuid = other.ObjectGuid }));
        Assert.Equal([stored[1].Id], await IdsAsync(store, new EventQuery { ActorSid = "s-1-5-21-1-2-3-1104" }));
        Assert.Equal([stored[1].Id, stored[0].Id], await IdsAsync(store, new EventQuery { UnacknowledgedOnly = true }));
    });

    private static async Task<List<long>> StoreManyAsync(TStore store, int count)
    {
        var user = Snapshot();
        var events = Enumerable.Range(0, count).Select(i => Event(WatchEventKind.AttributesChanged, user, Now.AddSeconds(i))).ToArray();
        return [.. (await StoreEventsAsync(store, events)).Select(e => e.Id)];
    }

    private static async Task<List<long>> IdsAsync(TStore store, EventQuery query) =>
        [.. (await store.QueryAsync(query, Ct)).Items.Select(e => e.Id)];
}
