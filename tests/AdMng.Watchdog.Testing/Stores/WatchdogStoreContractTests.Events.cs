using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Xunit;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Testing.Stores;

// IEventStore (without the query filters, see WatchdogStoreContractTests.EventQuery.cs)
public abstract partial class WatchdogStoreContractTests<TStore>
{
    [Fact]
    public Task Append_AssignsAscendingIds_AndRoundTripsThePayload() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var full = Event(WatchEventKind.AttributesChanged, user, Now) with
        {
            ChangeSetId = Guid.Parse("01920000-0000-7000-8000-000000000001"),
            PreviousDn = "CN=Zofia Żak,OU=Kadry,DC=contoso,DC=test",
            Changes =
            [
                new AttributeChange { Attribute = "department", ValueKind = AttributeValueKind.Text, OldValues = ["Kadry"], NewValues = ["Księgowość"], OldCount = 1, NewCount = 1 },
                new AttributeChange { Attribute = "unicodePwd", Redacted = true },
            ],
            Attribution = Resolved(@"CONTOSO\adm.łukasz", "S-1-5-21-1004336348-1177238915-682003330-1104", "WS-ADM-01", "10.0.12.34"),
            Tags = [EventTags.Privileged, EventTags.SelfChange],
            OriginatingDc = "dc01.contoso.test",
            OriginatingTime = Now.AddSeconds(-30),
            LocalChangeTime = Now.AddSeconds(-29),
            Lockout = new LockoutInfo { LockedAt = Now, Sources = [new LockoutSource("10.0.0.12", "WS-ŻÓŁW", 5, 4771)], LockoutsToday = 2 },
        };

        var first = await store.AppendAsync(full, Ct);
        var second = await store.AppendAsync(Event(WatchEventKind.AccountDisabled, user, Now), Ct);

        Assert.True(first > 0);
        Assert.True(second > first);
        var loaded = await store.GetAsync(first, Ct);
        Assert.NotNull(loaded);
        AssertSameJson(full with { Id = first, OwnSeverity = full.Severity }, loaded);
        AssertSameJson(loaded, await store.GetByEventIdAsync(full.EventId, Ct));
        Assert.Null(await store.GetAsync(second + 100, Ct));
        Assert.Null(await store.GetByEventIdAsync(Guid.NewGuid(), Ct));
        Assert.Equal(second, await store.GetLatestIdAsync(Ct));
    });

    [Fact]
    public Task EventIds_AreNeverReused_AfterRetentionDeletes() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        await store.AppendAsync(Event(WatchEventKind.UserCreated, user, Now.AddDays(-400)), Ct);
        var newest = await store.AppendAsync(Event(WatchEventKind.AccountEnabled, user, Now.AddDays(-400)), Ct);

        await store.PurgeAsync(new RetentionSettings(), Now, Ct);
        var next = await store.AppendAsync(Event(WatchEventKind.AccountDisabled, user, Now), Ct);

        Assert.True(next > newest);
        Assert.Equal(next, await store.GetLatestIdAsync(Ct));
    });

    [Fact]
    public Task ChangeSet_ReturnsTheEventsOfOneDiffInIdOrder() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var changeSet = Guid.CreateVersion7(Now);
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, user, Now) with { ChangeSetId = changeSet },
            Event(WatchEventKind.AccountDisabled, user, Now),
            Event(WatchEventKind.PasswordChanged, user, Now) with { ChangeSetId = changeSet });

        var related = await store.GetChangeSetAsync(changeSet, Ct);

        Assert.Equal([stored[0].Id, stored[2].Id], related.Select(e => e.Id));
        Assert.Empty(await store.GetChangeSetAsync(Guid.Empty, Ct));
    });

    [Fact]
    public Task Range_IsHalfOpen_InIdOrder() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, user, Now.AddHours(-25)),
            Event(WatchEventKind.AccountEnabled, user, Now.AddHours(-24)),
            Event(WatchEventKind.AccountDisabled, user, Now.AddHours(-1)),
            Event(WatchEventKind.AttributesChanged, user, Now));

        var range = await ToListAsync(store.GetRangeAsync(Now.AddHours(-24), Now, Ct));

        Assert.Equal([stored[1].Id, stored[2].Id], range.Select(e => e.Id));
    });

    [Fact]
    public Task PendingAttribution_IsDueByNextAttempt_InDueOrder_AndFinalIsNeverDue() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, user, Now),
            Event(WatchEventKind.AccountEnabled, user, Now.AddSeconds(10)),
            Event(WatchEventKind.AccountDisabled, user, Now.AddSeconds(20)),
            Event(WatchEventKind.LockoutExpired, user, Now, attribution: EventAttribution.NotApplicable));

        await store.UpdateAttributionAsync(stored[0].Id, new AttributionUpdate { Attribution = EventAttribution.Pending, NextAttemptAt = Now.AddMinutes(2) }, Ct);
        await store.UpdateAttributionAsync(stored[2].Id, new AttributionUpdate { Attribution = Resolved(@"CONTOSO\adm.nowak", "S-1-5-21-1-2-3-1104") }, Ct);

        Assert.Equal([stored[1].Id], (await store.GetPendingAttributionAsync(Now.AddMinutes(1), 10, Ct)).Select(e => e.Id));
        Assert.Equal([stored[1].Id, stored[0].Id], (await store.GetPendingAttributionAsync(Now.AddMinutes(5), 10, Ct)).Select(e => e.Id));
        Assert.Single(await store.GetPendingAttributionAsync(Now.AddMinutes(5), 1, Ct));
    });

    [Fact]
    public Task UpdateAttribution_StoresActorTagsSeverityAndLockout_AndUpdatesTheSearchText() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = (await StoreEventsAsync(store, Event(WatchEventKind.PasswordChanged, user, Now, Severity.High, tags: [EventTags.Privileged])))[0];
        var lockout = new LockoutInfo { LockedAt = Now, Sources = [new LockoutSource("10.0.0.99", null, 3, 4771)] };

        await store.UpdateAttributionAsync(stored.Id, new AttributionUpdate
        {
            Attribution = Resolved(@"CONTOSO\zofia.zak", user.Sid!, "WS-KSIĘGOWOŚĆ-07", "10.0.44.7"),
            AddedTags = [EventTags.SelfChange, EventTags.Privileged],
            Severity = Severity.Info,
            Lockout = lockout,
        }, Ct);
        await store.UpdateAttributionAsync(stored.Id + 1000, new AttributionUpdate { Attribution = EventAttribution.Pending }, Ct);

        var loaded = await store.GetAsync(stored.Id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(AttributionStatus.Resolved, loaded.Attribution.Status);
        Assert.Equal(@"CONTOSO\zofia.zak", loaded.Attribution.Actor?.Account);
        Assert.Equal([EventTags.Privileged, EventTags.SelfChange], loaded.Tags);
        Assert.Equal((Severity.Info, Severity.Info), (loaded.OwnSeverity, loaded.Severity));
        Assert.Equal("10.0.0.99", Assert.Single(loaded.Lockout!.Sources).IpAddress);
        Assert.Single((await store.QueryAsync(new EventQuery { Text = "księgowość-07" }, Ct)).Items);
        Assert.Single((await store.QueryAsync(new EventQuery { Text = "10.0.44" }, Ct)).Items);

        await store.UpdateAttributionAsync(stored.Id, new AttributionUpdate { Attribution = loaded.Attribution }, Ct);
        Assert.Equal(Severity.Info, (await store.GetAsync(stored.Id, Ct))?.Severity);
        Assert.NotNull((await store.GetAsync(stored.Id, Ct))?.Lockout);
    });

    [Fact]
    public Task OwnSeverity_IsTheSeverityOnInsert_AndTheEvaluationRaisesOnlyTheEvaluatedSeverity() => WithStoreAsync(async store =>
    {
        // 12.1/12.2: the own severity is the input of every rule match, the evaluated one (overrides included) is displayed.
        var user = Snapshot();
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.KeyCredentialChanged, user, Now, Severity.Medium),
            Event(WatchEventKind.AttributesChanged, user, Now, Severity.High) with { OwnSeverity = Severity.Low });
        Assert.Equal((Severity.Medium, Severity.Medium), Severities(await store.GetAsync(stored[0].Id, Ct)));
        Assert.Equal((Severity.Low, Severity.High), Severities(await store.GetAsync(stored[1].Id, Ct)));

        await store.CommitEvaluationAsync([new EventEvaluation { EventId = stored[0].Id, Severity = Severity.Critical, MatchedRules = ["konta-vip"] }], [], Ct);

        Assert.Equal((Severity.Medium, Severity.Critical), Severities(await store.GetAsync(stored[0].Id, Ct)));
        Assert.Equal((Severity.Medium, Severity.Critical), Severities(await store.GetByEventIdAsync(stored[0].EventId, Ct)));
        Assert.Equal((Severity.Medium, Severity.Critical), Severities((await store.QueryAsync(new EventQuery { Limit = 10 }, Ct)).Items.Single(e => e.Id == stored[0].Id)));
        Assert.Equal((Severity.Medium, Severity.Critical), Severities((await store.GetPendingAttributionAsync(Now, 10, Ct)).Single(e => e.Id == stored[0].Id)));
        await foreach (var e in store.GetRangeAsync(Now.AddMinutes(-1), Now.AddMinutes(1), Ct))
        {
            Assert.NotNull(e.OwnSeverity);
        }
    });

    [Fact]
    public Task UpdateAttribution_StoresTheOwnAndTheEvaluatedSeverity() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = (await StoreEventsAsync(store, Event(WatchEventKind.PasswordChanged, user, Now, Severity.High, tags: [EventTags.Privileged])))[0];
        await store.CommitEvaluationAsync([new EventEvaluation { EventId = stored.Id, Severity = Severity.Critical, MatchedRules = ["hasla-vip"] }], [], Ct);

        // The user's own change: own Info, the override keeps the evaluated severity Critical.
        await store.UpdateAttributionAsync(stored.Id, new AttributionUpdate
        {
            Attribution = Resolved(@"CONTOSO\zofia.zak", user.Sid!, "WS-KADRY-03", "10.0.44.8"),
            AddedTags = [EventTags.SelfChange],
            Severity = Severity.Info,
            EvaluatedSeverity = Severity.Critical,
        }, Ct);
        Assert.Equal((Severity.Info, Severity.Critical), Severities(await store.GetAsync(stored.Id, Ct)));

        // Nothing re-resolved: both unchanged.
        await store.UpdateAttributionAsync(stored.Id, new AttributionUpdate { Attribution = Resolved(@"CONTOSO\zofia.zak", user.Sid!, null, null) }, Ct);
        Assert.Equal((Severity.Info, Severity.Critical), Severities(await store.GetAsync(stored.Id, Ct)));

        // An own severity without an evaluation (an event the rules have not evaluated yet): the evaluated one follows it.
        await store.UpdateAttributionAsync(stored.Id, new AttributionUpdate { Attribution = Resolved(@"CONTOSO\zofia.zak", user.Sid!, null, null), Severity = Severity.Low }, Ct);
        Assert.Equal((Severity.Low, Severity.Low), Severities(await store.GetAsync(stored.Id, Ct)));
    });

    private static (Severity? Own, Severity Evaluated) Severities(WatchEvent? watchEvent)
    {
        Assert.NotNull(watchEvent);
        return (watchEvent.OwnSeverity, watchEvent.Severity);
    }

    [Fact]
    public Task Evaluation_StoresSeverityAndMatchedRules_InsertsNotifications_AndIgnoresDuplicates() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, user, Now), Event(WatchEventKind.AccountEnabled, user, Now), Event(WatchEventKind.AccountDisabled, user, Now));
        Assert.Equal(stored.Select(e => e.Id), (await store.GetUnevaluatedAsync(10, Ct)).Select(e => e.Id));
        Assert.Equal([stored[0].Id], (await store.GetUnevaluatedAsync(1, Ct)).Select(e => e.Id));
        var helpdesk = EmailTarget("pomoc@contoso.test");

        await store.CommitEvaluationAsync(
            [
                new EventEvaluation { EventId = stored[0].Id, Severity = Severity.Medium, MatchedRules = ["new-accounts", "account-attributes"] },
                new EventEvaluation { EventId = stored[1].Id, Severity = Severity.Low },
            ],
            [Pending(stored[0].Id, "new-accounts", helpdesk), Pending(stored[0].Id, "other-rule", helpdesk), Pending(stored[0].Id, "new-accounts", WebhookTarget())],
            Ct);
        await store.CommitEvaluationAsync([], [Pending(stored[0].Id, "new-accounts", EmailTarget("POMOC@contoso.test"))], Ct);

        var first = await store.GetAsync(stored[0].Id, Ct);
        Assert.Equal(Severity.Medium, first?.Severity);
        Assert.Equal(["new-accounts", "account-attributes"], first?.MatchedRules);
        Assert.Equal([stored[2].Id], (await store.GetUnevaluatedAsync(10, Ct)).Select(e => e.Id));
        var pending = await store.GetAllAsync(Ct);
        Assert.Equal(2, pending.Count);
        Assert.Equal("new-accounts", pending[0].RuleId);
        Assert.Equal(helpdesk.Key, pending[0].Target.Key);
        Assert.Equal(WebhookTarget().Key, pending[1].Target.Key);
    });

    [Fact]
    public Task Acknowledge_StoresWhoAndWhen_IsIdempotent_AndFalseForUnknownIds() => WithStoreAsync(async store =>
    {
        var stored = (await StoreEventsAsync(store, Event(WatchEventKind.AccountLockedOut, Snapshot(), Now)))[0];

        Assert.True(await store.AcknowledgeAsync(stored.Id, @"CONTOSO\łucja.nowak", "Użytkownik zapomniał hasła", Now.AddMinutes(1), Ct));
        Assert.True(await store.AcknowledgeAsync(stored.Id, @"CONTOSO\inny", "drugi raz", Now.AddMinutes(2), Ct));
        Assert.False(await store.AcknowledgeAsync(stored.Id + 1000, @"CONTOSO\łucja.nowak", null, Now, Ct));

        var loaded = await store.GetAsync(stored.Id, Ct);
        Assert.Equal(Now.AddMinutes(1), loaded?.AcknowledgedAt);
        Assert.Equal(@"CONTOSO\łucja.nowak", loaded?.AcknowledgedBy);
        Assert.Equal("Użytkownik zapomniał hasła", loaded?.AcknowledgeComment);
    });

    [Fact]
    public Task AcknowledgeMany_CountsOnlyNewlyAcknowledgedEvents() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store, Event(WatchEventKind.AccountLockedOut, user, Now), Event(WatchEventKind.AccountLockedOut, user, Now), Event(WatchEventKind.AccountLockedOut, user, Now));
        await store.AcknowledgeAsync(stored[0].Id, "helpdesk", null, Now, Ct);

        var count = await store.AcknowledgeManyAsync([stored[0].Id, stored[1].Id, stored[2].Id, stored[2].Id, 9999], @"CONTOSO\łucja.nowak", "seria blokad", Now.AddMinutes(1), Ct);

        Assert.Equal(2, count);
        Assert.Equal("helpdesk", (await store.GetAsync(stored[0].Id, Ct))?.AcknowledgedBy);
        Assert.Equal("seria blokad", (await store.GetAsync(stored[2].Id, Ct))?.AcknowledgeComment);
    });

    [Fact]
    public Task CountForObject_CountsOneKindOfOneObjectSinceATime() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var other = Snapshot("CN=Jan Kowalski,OU=Pracownicy,DC=contoso,DC=test");
        await StoreEventsAsync(store,
            Event(WatchEventKind.AccountLockedOut, user, Now.AddHours(-2)),
            Event(WatchEventKind.AccountLockedOut, user, Now.AddMinutes(-30)),
            Event(WatchEventKind.AccountLockedOut, user, Now),
            Event(WatchEventKind.AccountUnlocked, user, Now),
            Event(WatchEventKind.AccountLockedOut, other, Now));

        Assert.Equal(2, await store.CountForObjectAsync(user.ObjectGuid, WatchEventKind.AccountLockedOut, Now.AddHours(-1), Ct));
    });

    [Fact]
    public Task Count_SummarizesSeveritiesAndUnacknowledgedEvents() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.UserCreated, user, Now.AddDays(-2), Severity.Critical),
            Event(WatchEventKind.UserCreated, user, Now, Severity.Low),
            Event(WatchEventKind.PrivilegeGranted, user, Now, Severity.Critical),
            Event(WatchEventKind.DelegationChanged, user, Now, Severity.High),
            Event(WatchEventKind.AccountLockedOut, user, Now, Severity.Low));
        await store.AcknowledgeAsync(stored[3].Id, "admin", null, Now, Ct);

        var counts = await store.CountAsync(Now.AddDays(-1), Ct);

        Assert.Equal(4, counts.Total);
        Assert.Equal(2, counts.BySeverity.GetValueOrDefault(Severity.Low));
        Assert.Equal(1, counts.BySeverity.GetValueOrDefault(Severity.Critical));
        Assert.Equal(1, counts.BySeverity.GetValueOrDefault(Severity.High));
        Assert.Equal(0, counts.BySeverity.GetValueOrDefault(Severity.Medium));
        Assert.Equal(3, counts.Unacknowledged);
        Assert.Equal(1, counts.UnacknowledgedHighOrAbove);
    });

    [Fact]
    public Task WaitForNewer_ReturnsAtOnce_WhenANewerEventExists() => WithStoreAsync(async store =>
    {
        var id = await store.AppendAsync(Event(WatchEventKind.UserCreated, Snapshot(), Now), Ct);

        await store.WaitForNewerAsync(id - 1, TimeSpan.FromSeconds(30), Ct);
    });

    [Fact]
    public Task WaitForNewer_CompletesWhenAnEventIsAppended() => WithStoreAsync(async store =>
    {
        var id = await store.AppendAsync(Event(WatchEventKind.UserCreated, Snapshot(), Now), Ct);
        var wait = store.WaitForNewerAsync(id, TimeSpan.FromSeconds(30), Ct);
        Assert.False(wait.IsCompleted);

        await store.AppendAsync(Event(WatchEventKind.AccountEnabled, Snapshot(), Now), Ct);

        await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(wait.IsCompletedSuccessfully);
    });

    [Fact]
    public Task WaitForNewer_CompletesAfterTheTimeoutOnTheClock() => WithStoreAsync(async store =>
    {
        var wait = store.WaitForNewerAsync(0, TimeSpan.FromSeconds(25), Ct);
        Clock.Advance(TimeSpan.FromSeconds(24));
        Assert.False(wait.IsCompleted);

        Clock.Advance(TimeSpan.FromSeconds(1));

        await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(wait.IsCompletedSuccessfully);
    });

    [Fact]
    public Task WaitForNewer_IsCancellable() => WithStoreAsync(async store =>
    {
        using var cancellation = new CancellationTokenSource();
        var wait = store.WaitForNewerAsync(0, TimeSpan.FromSeconds(25), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    });
}
