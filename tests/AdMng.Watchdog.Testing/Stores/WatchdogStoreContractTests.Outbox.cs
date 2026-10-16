using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// IOutboxStore
public abstract partial class WatchdogStoreContractTests<TStore>
{
    [Fact]
    public Task Outbox_LeasedMessage_IsSentOnce() => WithStoreAsync(async store =>
    {
        var id = await store.EnqueueAsync(Message(), Ct);

        var leased = await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(id, Assert.Single(leased).Id);

        await store.MarkSentAsync(id, Now, Ct);

        Assert.Empty(await store.LeaseDueAsync(Now.AddMinutes(5), 10, TimeSpan.FromMinutes(1), Ct));
        var item = Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(OutboxStatus.Sent, item.Status);
        Assert.Equal(1, item.Attempts);
    });

    [Fact]
    public Task Outbox_Enqueue_RoundTripsTheRenderedPayload() => WithStoreAsync(async store =>
    {
        var events = await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, Snapshot(), Now));
        var message = Message("email", eventIds: [events[0].Id], ruleId: "new-accounts") with
        {
            TargetKey = EmailTarget("jan.kowalski@contoso.test").Key,
            SentWithPendingAttribution = true,
            Severity = Severity.Low,
            Email = Message("email").Email! with { InReplyTo = "0192f3a45b6c7d8e9f00112233445566" },
        };

        var id = await store.EnqueueAsync(message, Ct);

        var leased = Assert.Single(await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(id, leased.Id);
        Assert.Equal(message.MessageId, leased.MessageId);
        Assert.Equal(DeliveryChannelType.Email, leased.ChannelType);
        Assert.Equal("email", leased.ChannelId);
        Assert.Equal(message.TargetKey, leased.TargetKey);
        Assert.True(leased.SentWithPendingAttribution);
        Assert.Equal(DeliveryKind.Alert, leased.Kind);
        Assert.Equal(message.Recipients, leased.Recipients);
        Assert.Equal("new-accounts", leased.RuleId);
        Assert.Equal([events[0].Id], leased.EventIds);
        Assert.Equal(Severity.Low, leased.Severity);
        Assert.Equal(0, leased.Attempts);
        Assert.Equal(Now, leased.CreatedAt);
        Assert.Equal(Now, leased.NextAttemptAt);
        Assert.NotNull(leased.Email);
        Assert.Equal(message.Email.Subject, leased.Email.Subject);
        Assert.Equal(message.Email.HtmlBody, leased.Email.HtmlBody);
        Assert.Equal(message.Email.TextBody, leased.Email.TextBody);
        Assert.Equal("0192f3a45b6c7d8e9f00112233445566", leased.Email.InReplyTo);
        Assert.Equal("new-accounts", leased.Email.Headers["x-admng-rule"]);
        Assert.Null(leased.Http);

        var webhook = Message();
        await store.EnqueueAsync(webhook, Ct);
        var http = Assert.Single(await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct)).Http;
        Assert.NotNull(http);
        Assert.Equal(webhook.Http!.Body, http.Body);
        Assert.Equal(webhook.Http.ContentType, http.ContentType);
        Assert.Equal("ad.user.created", http.WebhookEventType);
        Assert.Equal("Nowe konto", http.Title);
    });

    [Fact]
    public Task Outbox_DuplicateMessageId_IsRejected() => WithStoreAsync(async store =>
    {
        var message = Message();
        await store.EnqueueAsync(message, Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => store.EnqueueAsync(message, Ct));
        Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
    });

    [Fact]
    public Task Outbox_Lease_TakesDueMessagesInOrder_UpToTheLimit_WithoutCountingAnAttempt() => WithStoreAsync(async store =>
    {
        var late = await store.EnqueueAsync(Message(nextAttemptAt: Now.AddMinutes(2)), Ct);
        var second = await store.EnqueueAsync(Message(nextAttemptAt: Now.AddSeconds(-10)), Ct);
        var first = await store.EnqueueAsync(Message(nextAttemptAt: Now.AddSeconds(-30)), Ct);
        var third = await store.EnqueueAsync(Message(nextAttemptAt: Now), Ct);

        var leased = await store.LeaseDueAsync(Now, 2, TimeSpan.FromMinutes(5), Ct);

        Assert.Equal([first, second], leased.Select(m => m.Id));
        Assert.All(leased, m => Assert.Equal(0, m.Attempts));
        Assert.Equal([third], (await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct)).Select(m => m.Id));
        Assert.Empty(await store.LeaseDueAsync(Now.AddMinutes(1), 10, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal([late], (await store.LeaseDueAsync(Now.AddMinutes(2), 10, TimeSpan.FromMinutes(5), Ct)).Select(m => m.Id));
        var sending = (await store.QueryAsync(new OutboxQuery { Status = OutboxStatus.Sending }, Ct)).Items;
        Assert.Equal(4, sending.Count);
    });

    [Fact]
    public Task Outbox_ExpiredLease_BecomesDueAgain() => WithStoreAsync(async store =>
    {
        var id = await store.EnqueueAsync(Message(), Ct);
        Assert.Single(await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct));

        Assert.Empty(await store.LeaseDueAsync(Now.AddMinutes(4), 10, TimeSpan.FromMinutes(5), Ct));
        var again = Assert.Single(await store.LeaseDueAsync(Now.AddMinutes(5), 10, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal(id, again.Id);
        Assert.Equal(0, again.Attempts);
        Assert.NotNull(again.Http);
    });

    [Fact]
    public Task Outbox_Sent_ClearsThePayload_AndKeepsTheMetadata() => WithStoreAsync(async store =>
    {
        var events = await StoreEventsAsync(store, Event(WatchEventKind.UserCreated, Snapshot(), Now), Event(WatchEventKind.UserCreated, Snapshot(), Now));
        var id = await store.EnqueueAsync(Message("email", eventIds: [events[0].Id, events[1].Id], ruleId: "new-accounts"), Ct);
        await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct);

        await store.MarkSentAsync(id, Now.AddSeconds(3), Ct);

        var message = Assert.Single(await store.GetMessagesForEventAsync(events[0].Id, Ct));
        Assert.Null(message.Email);
        Assert.Null(message.Http);
        Assert.Equal(1, message.Attempts);
        var item = Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(OutboxStatus.Sent, item.Status);
        Assert.Equal(Now.AddSeconds(3), item.SentAt);
        Assert.Equal("[AD Watchdog][NISKI] Nowe konto: Zofia Żak (zofia.zak)", item.Subject);
        Assert.Equal(2, item.EventCount);
        Assert.Equal(2, item.RecipientCount);
        Assert.Equal(DeliveryChannelType.Email, item.ChannelType);
        Assert.Equal(DeliveryKind.Alert, item.Kind);
        Assert.Equal(Now, item.CreatedAt);
    });

    [Fact]
    public Task Outbox_Retry_CountsAttemptsOnlyWhenAsked_AndWaitsForTheNextAttempt() => WithStoreAsync(async store =>
    {
        var id = await store.EnqueueAsync(Message(), Ct);
        await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct);

        await store.MarkRetryAsync(id, Now.AddSeconds(30), "http.503", "HTTP 503", countAttempt: true, Ct);
        Assert.Empty(await store.LeaseDueAsync(Now.AddSeconds(29), 10, TimeSpan.FromMinutes(5), Ct));
        var leased = Assert.Single(await store.LeaseDueAsync(Now.AddSeconds(30), 10, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(1, leased.Attempts);
        await store.MarkRetryAsync(id, Now.AddMinutes(1), "rateLimit", null, countAttempt: false, Ct);

        var item = Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(OutboxStatus.Retrying, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(Now.AddMinutes(1), item.NextAttemptAt);
        Assert.Equal("rateLimit", item.LastErrorCode);
        Assert.Null(item.LastError);
        Assert.Equal(1, Assert.Single(await store.LeaseDueAsync(Now.AddMinutes(1), 10, TimeSpan.FromMinutes(5), Ct)).Attempts);
    });

    [Fact]
    public Task Outbox_Dead_KeepsThePayload_AndRequeueStartsOver() => WithStoreAsync(async store =>
    {
        var id = await store.EnqueueAsync(Message(), Ct);
        await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct);
        await store.MarkRetryAsync(id, Now, "http.503", null, countAttempt: true, Ct);
        await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct);

        await store.MarkDeadAsync(id, "http.410", "HTTP 410 Gone", Ct);

        var dead = Assert.Single((await store.QueryAsync(new OutboxQuery { Status = OutboxStatus.Dead }, Ct)).Items);
        Assert.Equal(2, dead.Attempts);
        Assert.Equal("http.410", dead.LastErrorCode);
        Assert.Equal("HTTP 410 Gone", dead.LastError);
        Assert.Empty(await store.LeaseDueAsync(Now.AddDays(1), 10, TimeSpan.FromMinutes(5), Ct));

        Assert.True(await store.RequeueAsync(id, Now.AddHours(1), Ct));
        Assert.False(await store.RequeueAsync(id, Now.AddHours(1), Ct));
        Assert.False(await store.RequeueAsync(id + 100, Now, Ct));
        Assert.Empty(await store.LeaseDueAsync(Now.AddMinutes(59), 10, TimeSpan.FromMinutes(5), Ct));
        var requeued = Assert.Single(await store.LeaseDueAsync(Now.AddHours(1), 10, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(0, requeued.Attempts);
        Assert.NotNull(requeued.Http);
    });

    [Fact]
    public Task Outbox_Query_FiltersByStatusAndChannel_AndPagesBackwards() => WithStoreAsync(async store =>
    {
        var ids = new List<long>();
        foreach (var channel in new[] { "siem", "teams-it", "siem", "email", "siem" })
        {
            ids.Add(await store.EnqueueAsync(Message(channel), Ct));
        }

        await store.LeaseDueAsync(Now, 1, TimeSpan.FromMinutes(5), Ct);
        await store.MarkSentAsync(ids[0], Now, Ct);

        var newest = await store.QueryAsync(new OutboxQuery { Limit = 2 }, Ct);
        Assert.Equal([ids[4], ids[3]], newest.Items.Select(i => i.Id));
        Assert.True(newest.HasMore);
        var older = await store.QueryAsync(new OutboxQuery { Before = ids[3], Limit = 2 }, Ct);
        Assert.Equal([ids[2], ids[1]], older.Items.Select(i => i.Id));
        Assert.True(older.HasMore);
        Assert.Equal([ids[4], ids[2], ids[0]], (await store.QueryAsync(new OutboxQuery { ChannelId = "siem" }, Ct)).Items.Select(i => i.Id));
        Assert.Equal([ids[0]], (await store.QueryAsync(new OutboxQuery { Status = OutboxStatus.Sent }, Ct)).Items.Select(i => i.Id));
        Assert.Equal([ids[4], ids[2]], (await store.QueryAsync(new OutboxQuery { Status = OutboxStatus.Pending, ChannelId = "siem" }, Ct)).Items.Select(i => i.Id));
        var pending = (await store.QueryAsync(new OutboxQuery { ChannelId = "email" }, Ct)).Items[0];
        Assert.Equal(Now, pending.NextAttemptAt);
        Assert.Null(pending.SentAt);
    });

    [Fact]
    public Task Outbox_DeliveriesForEvent_ListEveryLinkedMessage() => WithStoreAsync(async store =>
    {
        var events = await StoreEventsAsync(store, Event(WatchEventKind.PrivilegeGranted, Snapshot(), Now), Event(WatchEventKind.UserCreated, Snapshot(), Now));
        var email = await store.EnqueueAsync(Message("email", eventIds: [events[0].Id], ruleId: "tier0-privilege"), Ct);
        var webhook = await store.EnqueueAsync(Message("siem", eventIds: [events[0].Id, events[1].Id], ruleId: "tier0-privilege"), Ct);
        await store.EnqueueAsync(Message("teams-it", eventIds: [events[1].Id]), Ct);
        await store.LeaseDueAsync(Now, 1, TimeSpan.FromMinutes(5), Ct);
        await store.MarkDeadAsync(email, "smtp.550", null, Ct);

        var deliveries = await store.GetDeliveriesForEventAsync(events[0].Id, Ct);

        Assert.Equal([email, webhook], deliveries.Select(d => d.OutboxId));
        Assert.Equal(OutboxStatus.Dead, deliveries[0].Status);
        Assert.Equal("smtp.550", deliveries[0].LastErrorCode);
        Assert.Equal(DeliveryChannelType.Email, deliveries[0].ChannelType);
        Assert.Equal("email", deliveries[0].ChannelId);
        Assert.Equal(DeliveryKind.Alert, deliveries[0].Kind);
        Assert.Equal(Now, deliveries[0].CreatedAt);
        Assert.Equal(OutboxStatus.Pending, deliveries[1].Status);
        Assert.Empty(await store.GetDeliveriesForEventAsync(events[1].Id + 100, Ct));
    });

    [Fact]
    public Task Outbox_WasNotifiedAboutObject_LooksAtImmediateMessagesOfTheRuleSinceATime() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var other = Snapshot("CN=Jan Kowalski,OU=Pracownicy,DC=contoso,DC=test");
        var events = await StoreEventsAsync(store, Event(WatchEventKind.AccountLockedOut, user, Now), Event(WatchEventKind.AccountLockedOut, other, Now));
        await store.EnqueueAsync(Message("email", eventIds: [events[0].Id], ruleId: "lockouts", createdAt: Now.AddMinutes(-30)), Ct);
        await store.EnqueueAsync(Message("email", DeliveryKind.Digest, eventIds: [events[1].Id], ruleId: "lockouts", createdAt: Now.AddMinutes(-30)), Ct);

        Assert.True(await store.WasNotifiedAboutObjectAsync("lockouts", user.ObjectGuid, Now.AddHours(-1), Ct));
        Assert.False(await store.WasNotifiedAboutObjectAsync("lockouts", user.ObjectGuid, Now.AddMinutes(-29), Ct));
        Assert.False(await store.WasNotifiedAboutObjectAsync("expiry", user.ObjectGuid, Now.AddHours(-1), Ct));
        Assert.False(await store.WasNotifiedAboutObjectAsync("lockouts", other.ObjectGuid, Now.AddHours(-1), Ct));
    });

    [Fact]
    public Task Outbox_CountSent_CountsSentMessagesPerChannelAndRuleSinceATime() => WithStoreAsync(async store =>
    {
        var a = await store.EnqueueAsync(Message("email", ruleId: "new-accounts"), Ct);
        var b = await store.EnqueueAsync(Message("email", ruleId: "new-accounts"), Ct);
        var c = await store.EnqueueAsync(Message("email", ruleId: "lockouts"), Ct);
        var d = await store.EnqueueAsync(Message("siem", ruleId: "new-accounts"), Ct);
        await store.EnqueueAsync(Message("email", ruleId: "new-accounts"), Ct);
        await store.LeaseDueAsync(Now, 4, TimeSpan.FromMinutes(5), Ct);
        await store.MarkSentAsync(a, Now.AddMinutes(-90), Ct);
        await store.MarkSentAsync(b, Now.AddMinutes(-10), Ct);
        await store.MarkSentAsync(c, Now.AddMinutes(-10), Ct);
        await store.MarkSentAsync(d, Now.AddMinutes(-10), Ct);

        Assert.Equal(1, await store.CountSentAsync("email", "new-accounts", Now.AddHours(-1), Ct));
        Assert.Equal(2, await store.CountSentAsync("email", null, Now.AddHours(-1), Ct));
        Assert.Equal(3, await store.CountSentAsync("email", null, Now.AddHours(-2), Ct));
        Assert.Equal(1, await store.CountSentAsync("siem", "new-accounts", Now.AddHours(-1), Ct));
    });

    [Fact]
    public Task Outbox_PendingByChannel_CountsUnfinishedMessages() => WithStoreAsync(async store =>
    {
        var sent = await store.EnqueueAsync(Message("siem"), Ct);
        await store.EnqueueAsync(Message("siem"), Ct);
        var retrying = await store.EnqueueAsync(Message("email"), Ct);
        var dead = await store.EnqueueAsync(Message("teams-it"), Ct);
        await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct);
        await store.MarkSentAsync(sent, Now, Ct);
        await store.MarkRetryAsync(retrying, Now.AddMinutes(1), "smtp.451", null, true, Ct);
        await store.MarkDeadAsync(dead, "http.404", null, Ct);

        var pending = await store.CountPendingByChannelAsync(Ct);

        Assert.Equal(1, pending.GetValueOrDefault("siem"));
        Assert.Equal(1, pending.GetValueOrDefault("email"));
        Assert.Equal(0, pending.GetValueOrDefault("teams-it"));
    });
}
