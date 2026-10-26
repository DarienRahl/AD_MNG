using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.UserMessages;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary><c>user_messages</c> (schema 6, README U1b): idempotency per caller and content, the sliding daily limit, the
/// payload of a message to a user (its own envelope, nothing in the recipients / subject columns) and the purge after
/// <c>retention.userMessageAuditDays</c>.</summary>
public sealed class UserMessageStoreTests : IAsyncLifetime
{
    private readonly TestStore _store = TestStore.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTimeOffset Now => _store.Clock.GetUtcNow();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    public async Task Submit_StoresTheOutboxMessageAndTheAuditRow_RepeatsAreDuplicatesOrConflicts()
    {
        var id = Guid.NewGuid();
        var store = _store.Store.UserMessages;

        var created = await store.SubmitAsync(Record(id), Message(id), 10, 10, Ct);
        var duplicate = await store.SubmitAsync(Record(id), Message(id), 10, 10, Ct);
        var otherContent = await store.SubmitAsync(Record(id) with { ContentHash = "inna-treść" }, Message(id), 10, 10, Ct);
        var otherCaller = await store.SubmitAsync(Record(id) with { BySid = "S-1-5-21-1-2-3-1999", ByAccount = @"CONTOSO\obcy" }, Message(id), 10, 10, Ct);

        Assert.Equal(UserMessageSubmitOutcome.Created, created.Outcome);
        Assert.Equal((UserMessageSubmitOutcome.Duplicate, created.OutboxId), (duplicate.Outcome, duplicate.OutboxId));
        Assert.Equal(UserMessageSubmitOutcome.Conflict, otherContent.Outcome);
        Assert.Equal(UserMessageSubmitOutcome.Conflict, otherCaller.Outcome);
        var leased = Assert.Single(await _store.Store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(("zofia.zak@contoso.test", "Zofia Żak"), (leased.Email!.UserMessage!.To[0].Address, leased.Email.UserMessage.To[0].DisplayName));
        Assert.Empty(leased.Recipients);
        var item = Assert.Single((await _store.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal((DeliveryKind.UserMessage, null, 2), (item.Kind, item.Subject, item.RecipientCount));
    }

    [Fact]
    public async Task DailyLimit_SlidesWithTheClock()
    {
        var store = _store.Store.UserMessages;
        Assert.Equal(UserMessageSubmitOutcome.Created, (await Submit(store)).Outcome);
        _store.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(UserMessageSubmitOutcome.Created, (await Submit(store)).Outcome);

        var limited = await Submit(store);

        Assert.Equal((UserMessageSubmitOutcome.DailyLimit, TimeSpan.FromHours(19)), (limited.Outcome, limited.RetryAfter));
        Assert.Equal((1, 2), await store.CountAsync(Now, Ct));
        _store.Clock.Advance(TimeSpan.FromHours(19) + TimeSpan.FromSeconds(1));
        Assert.Equal(UserMessageSubmitOutcome.Created, (await Submit(store)).Outcome);
    }

    [Fact]
    public async Task Purge_DeletesAuditRowsOlderThanTheRetention()
    {
        await Submit(_store.Store.UserMessages);
        _store.Clock.Advance(TimeSpan.FromDays(31));
        await Submit(_store.Store.UserMessages);

        await _store.Store.PurgeAsync(new RetentionSettings { UserMessageAuditDays = 30 }, Now, Ct);

        Assert.Equal(1L, _store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM user_messages;")));
    }

    private Task<UserMessageSubmission> Submit(IUserMessageStore store)
    {
        var id = Guid.NewGuid();
        return store.SubmitAsync(Record(id), Message(id), 100, 2, Ct);
    }

    private UserMessageAuditRecord Record(Guid id) => new()
    {
        MessageId = id,
        SubmittedAt = Now,
        BySid = "S-1-5-21-1-2-3-1106",
        ByAccount = TestRoles.ViewerAccount,
        RecipientCount = 2,
        RecipientsHash = new string('a', 64),
        ContentHash = new string('b', 64),
    };

    private OutboxMessage Message(Guid id) => new()
    {
        MessageId = id.ToString("N"),
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Kind = DeliveryKind.UserMessage,
        Email = new RenderedEmail
        {
            Subject = "Hasło wygasa – Zażółć",
            TextBody = "Dzień dobry",
            HtmlBody = string.Empty,
            UserMessage = new UserMessageEnvelope
            {
                From = new UserMessageMailbox { Address = "watchdog@contoso.test" },
                To = [new UserMessageMailbox { Address = "zofia.zak@contoso.test", DisplayName = "Zofia Żak" }],
                Cc = [new UserMessageMailbox { Address = "jan.kowalski@contoso.test" }],
            },
        },
        CreatedAt = Now,
        NextAttemptAt = Now,
    };
}
