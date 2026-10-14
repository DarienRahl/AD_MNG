using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// INotificationQueueStore
public abstract partial class WatchdogStoreContractTests<TStore>
{
    [Fact]
    public Task Queue_AddedRows_RoundTripEveryColumn() => WithStoreAsync(async store =>
    {
        var stored = (await StoreEventsAsync(store, Event(WatchEventKind.PasswordChanged, Snapshot(), Now)))[0];
        var target = new NotificationTarget
        {
            ChannelType = DeliveryChannelType.Email,
            ChannelId = "email",
            Recipients = ["łucja.nowak@contoso.test", "jan.kowalski@contoso.test"],
            DetailLevel = DetailLevel.Standard,
        };
        var row = Pending(stored.Id, "privileged-accounts", target, followUp: true) with
        {
            Severity = Severity.High,
            HoldForAttributionUntil = Now.AddSeconds(90),
            ReplyToMessageId = "0192f3a45b6c7d8e9f00112233445566",
            DueAt = Now.AddSeconds(10),
        };

        await store.AddAsync([row], Ct);

        var loaded = Assert.Single(await store.GetAllAsync(Ct));
        Assert.True(loaded.Id > 0);
        Assert.Equal(stored.Id, loaded.EventId);
        Assert.Equal("privileged-accounts", loaded.RuleId);
        Assert.Equal(DeliveryChannelType.Email, loaded.Target.ChannelType);
        Assert.Equal("email", loaded.Target.ChannelId);
        Assert.Equal(target.Recipients, loaded.Target.Recipients);
        Assert.Equal(DetailLevel.Standard, loaded.Target.DetailLevel);
        Assert.Equal(target.Key, loaded.Target.Key);
        Assert.Equal(Severity.High, loaded.Severity);
        Assert.Equal(row.BucketKey, loaded.BucketKey);
        Assert.Equal(Now.AddSeconds(10), loaded.DueAt);
        Assert.Equal(Now.AddSeconds(90), loaded.HoldForAttributionUntil);
        Assert.True(loaded.IsFollowUp);
        Assert.Equal("0192f3a45b6c7d8e9f00112233445566", loaded.ReplyToMessageId);
        Assert.Equal(Now, loaded.CreatedAt);
    });

    [Fact]
    public Task Queue_IgnoresDuplicatesByEventTargetAndFollowUp() => WithStoreAsync(async store =>
    {
        var stored = await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, Snapshot(), Now), Event(WatchEventKind.AccountEnabled, Snapshot(), Now));
        var admins = EmailTarget("adm@contoso.test");

        await store.AddAsync([Pending(stored[0].Id, "new-accounts", admins), Pending(stored[0].Id, "other", admins)], Ct);
        await store.AddAsync(
            [
                Pending(stored[0].Id, "new-accounts", admins),
                Pending(stored[0].Id, "new-accounts", admins, followUp: true),
                Pending(stored[1].Id, "new-accounts", admins),
                Pending(stored[0].Id, "new-accounts", EmailTarget("helpdesk@contoso.test")),
            ],
            Ct);

        var rows = await store.GetAllAsync(Ct);
        Assert.Equal(4, rows.Count);
        Assert.Equal("new-accounts", rows[0].RuleId);
        Assert.Equal(rows.Select(r => r.Id).Order(), rows.Select(r => r.Id));
    });

    [Fact]
    public Task Queue_Complete_DeletesRowsAndEnqueuesTheMessageLinkedToTheEvents() => WithStoreAsync(async store =>
    {
        var stored = await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, Snapshot(), Now), Event(WatchEventKind.UserCreated, Snapshot(), Now));
        await store.AddAsync([Pending(stored[0].Id, "new-accounts", WebhookTarget()), Pending(stored[1].Id, "new-accounts", WebhookTarget())], Ct);
        var rows = await store.GetAllAsync(Ct);
        var message = Message(eventIds: [stored[0].Id, stored[1].Id], ruleId: "new-accounts") with { TargetKey = WebhookTarget().Key };

        await store.CompleteAsync([.. rows.Select(r => r.Id)], message, Ct);

        Assert.Empty(await store.GetAllAsync(Ct));
        var sent = Assert.Single(await store.GetMessagesForEventAsync(stored[1].Id, Ct));
        Assert.Equal(message.MessageId, sent.MessageId);
        Assert.Equal([stored[0].Id, stored[1].Id], sent.EventIds.Order());
        Assert.Equal(WebhookTarget().Key, sent.TargetKey);
        Assert.Equal(OutboxStatus.Pending, Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items).Status);
    });

    [Fact]
    public Task Queue_CompleteWithoutMessage_OnlyDeletesTheRows() => WithStoreAsync(async store =>
    {
        var stored = (await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, Snapshot(), Now)))[0];
        await store.AddAsync([Pending(stored.Id, "new-accounts", WebhookTarget()), Pending(stored.Id, "new-accounts", EmailTarget("a@contoso.test"))], Ct);
        var rows = await store.GetAllAsync(Ct);

        await store.CompleteAsync([rows[0].Id], null, Ct);

        Assert.Equal(rows[1].Id, Assert.Single(await store.GetAllAsync(Ct)).Id);
        Assert.Empty((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
    });

    [Fact]
    public Task Queue_DropAndReschedule_ChangeOnlyTheGivenRows() => WithStoreAsync(async store =>
    {
        var stored = await StoreEventsAsync(store,
            Event(WatchEventKind.AccountLockedOut, Snapshot(), Now), Event(WatchEventKind.AccountLockedOut, Snapshot(), Now), Event(WatchEventKind.AccountLockedOut, Snapshot(), Now));
        await store.AddAsync([.. stored.Select(e => Pending(e.Id, "lockouts", EmailTarget("pomoc@contoso.test")))], Ct);
        var rows = await store.GetAllAsync(Ct);

        await store.DropAsync([rows[0].Id], Ct);
        await store.RescheduleAsync([rows[1].Id], Now.AddMinutes(15), Ct);

        var remaining = await store.GetAllAsync(Ct);
        Assert.Equal([rows[1].Id, rows[2].Id], remaining.Select(r => r.Id));
        Assert.Equal(Now.AddMinutes(15), remaining[0].DueAt);
        Assert.Equal(rows[2].DueAt, remaining[1].DueAt);
    });
}
