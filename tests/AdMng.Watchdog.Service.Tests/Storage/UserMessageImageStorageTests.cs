using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Storage.Migrations;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.UserMessages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>
/// Schema 7 (U1c): <c>outbox.image_bytes</c> added to existing databases, written on insert, 0 once the payload is dropped,
/// gone with a purged row; the submission's cap of the stored images (<c>userMessages.maxStoredImageMb</c>) refuses only
/// messages with images, inside the same transaction as the limits.
/// </summary>
public sealed class UserMessageImageStorageTests : IAsyncLifetime
{
    private readonly TestStore _store = TestStore.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTimeOffset Now => _store.Clock.GetUtcNow();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    public async Task Migration0007_AddsTheColumnToADatabaseOfSchema6_KeepingItsRows()
    {
        using var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);
        new MigrationRunner(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance, [.. Migration.All.Where(m => m.Version <= 6)]).Run();
        Assert.Equal(6L, Scalar(database, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM pragma_table_info('outbox') WHERE name = 'image_bytes';"));
        await database.WriteAsync(
            (c, t) => SqliteValues.Execute(c, t,
                """
                INSERT INTO outbox (message_id, channel_type, channel_id, kind, severity, status, created_at, next_attempt_at, payload)
                VALUES ('0192f3a45b6c7d8e9f00eeeeeeeeee01', 0, 'email', $kind, 0, 0, 0, 0, x'00');
                """,
                ("$kind", (int)DeliveryKind.UserMessage)),
            Ct);

        var result = new MigrationRunner(database, new FakeTimeProvider(TestStore.Start.AddMinutes(1)), NullLogger<MigrationRunner>.Instance).Run();

        Assert.Equal((6, Migration.LatestVersion), (result.FromVersion, result.ToVersion));
        Assert.True(Migration.LatestVersion >= Migration0007UserMessageImages.Version);
        Assert.Equal(1L, Scalar(database, "SELECT count(*) FROM pragma_table_info('outbox') WHERE name = 'image_bytes' AND \"notnull\" = 1 AND dflt_value = '0';"));
        Assert.Equal(1L, Scalar(database, "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_outbox_image_bytes';"));
        Assert.Equal((1L, 0L), (Scalar(database, "SELECT count(*) FROM outbox;"), Scalar(database, "SELECT image_bytes FROM outbox;")));
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ImageBytes_AreWrittenOnInsert_AndClearedWhenSent()
    {
        var store = _store.Store.UserMessages;
        var id = Guid.NewGuid();

        var created = await store.SubmitAsync(Record(id), Message(id, 3000, 1000), 100, 100, long.MaxValue, Ct);

        Assert.Equal(UserMessageSubmitOutcome.Created, created.Outcome);
        Assert.Equal(4000L, ImageBytes(created.OutboxId!.Value));
        Assert.Equal(4000L, await store.StoredImageBytesAsync(Ct));
        _ = await _store.Store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct);
        await _store.Store.MarkSentAsync(created.OutboxId.Value, Now, Ct);
        Assert.Equal(0L, ImageBytes(created.OutboxId.Value));
        Assert.Equal(0L, await store.StoredImageBytesAsync(Ct));
    }

    [Fact]
    public async Task ADeadMessage_KeepsItsImagesCounted_UntilItIsPurged()
    {
        var id = Guid.NewGuid();
        var created = await _store.Store.UserMessages.SubmitAsync(Record(id), Message(id, 2048), 100, 100, long.MaxValue, Ct);
        _ = await _store.Store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct);
        await _store.Store.MarkDeadAsync(created.OutboxId!.Value, "smtp.552.5.3.4", null, Ct);
        Assert.Equal(2048L, await _store.Store.UserMessages.StoredImageBytesAsync(Ct));

        await _store.Store.PurgeAsync(new RetentionSettings { OutboxDeadDays = 1 }, Now.AddDays(2), Ct);

        Assert.Equal(0L, await _store.Store.UserMessages.StoredImageBytesAsync(Ct));
    }

    [Fact]
    public async Task TheCap_RefusesOnlyMessagesWithImages_AndNothingOfThemIsStored()
    {
        var store = _store.Store.UserMessages;
        const long cap = 10_000;
        var first = Guid.NewGuid();
        var firstResult = await store.SubmitAsync(Record(first), Message(first, 6000), 100, 100, cap, Ct);
        Assert.Equal(UserMessageSubmitOutcome.Created, firstResult.Outcome);

        var refusedId = Guid.NewGuid();
        var refused = await store.SubmitAsync(Record(refusedId), Message(refusedId, 4001), 100, 100, cap, Ct);
        var fitsId = Guid.NewGuid();
        var fits = await store.SubmitAsync(Record(fitsId), Message(fitsId, 4000), 100, 100, cap, Ct);
        var textId = Guid.NewGuid();
        var text = await store.SubmitAsync(Record(textId), Message(textId), 100, 100, cap, Ct);

        Assert.Equal((UserMessageSubmitOutcome.ImageStorageFull, TimeSpan.FromHours(1), null), (refused.Outcome, refused.RetryAfter, refused.OutboxId));
        Assert.Equal(UserMessageSubmitOutcome.Created, fits.Outcome);
        Assert.Equal(UserMessageSubmitOutcome.Created, text.Outcome);
        Assert.Equal(10_000L, await store.StoredImageBytesAsync(Ct));
        Assert.Equal(3L, _store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM user_messages;")));
        Assert.Equal((3, 3), await store.CountAsync(Now, Ct));

        // The refused id can be submitted again later, once there is room.
        _ = await _store.Store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct);
        await _store.Store.MarkSentAsync(firstResult.OutboxId!.Value, Now, Ct);
        Assert.Equal(UserMessageSubmitOutcome.Created, (await store.SubmitAsync(Record(refusedId), Message(refusedId, 4001), 100, 100, cap, Ct)).Outcome);
    }

    [Fact]
    public void TheCapOfTheSettings_IsInMegabytes_WithinItsRange()
    {
        Assert.Equal(256L * 1024 * 1024, UserMessageService.MaxStoredImageBytes(new UserMessagingSettings()));
        Assert.Equal(16L * 1024 * 1024, UserMessageService.MaxStoredImageBytes(new UserMessagingSettings { MaxStoredImageMb = 1 }));
        Assert.Equal(4096L * 1024 * 1024, UserMessageService.MaxStoredImageBytes(new UserMessagingSettings { MaxStoredImageMb = 100_000 }));
    }

    private long ImageBytes(long outboxId) =>
        _store.Database.Read(c => SqliteValues.Scalar<long>(c, null, $"SELECT image_bytes FROM outbox WHERE id = {outboxId};"));

    private static long Scalar(SqliteDatabase database, string sql) => database.Read(c => SqliteValues.Scalar<long>(c, null, sql));

    private UserMessageAuditRecord Record(Guid id) => new()
    {
        MessageId = id,
        SubmittedAt = Now,
        BySid = "S-1-5-21-1-2-3-1106",
        ByAccount = TestRoles.ViewerAccount,
        RecipientCount = 1,
        RecipientsHash = new string('a', 64),
        ContentHash = new string('c', 64),
    };

    /// <summary>A message to a user carrying images of the given sizes (the store does not look into the bytes).</summary>
    private OutboxMessage Message(Guid id, params int[] imageSizes) => new()
    {
        MessageId = id.ToString("N"),
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Kind = DeliveryKind.UserMessage,
        Email = new RenderedEmail
        {
            Subject = "Instrukcja – zażółć gęślą jaźń",
            TextBody = "Dzień dobry",
            HtmlBody = string.Concat(imageSizes.Select((_, i) => $"<img src=\"cid:img-{i:x16}@admng\">")),
            UserMessage = new UserMessageEnvelope
            {
                From = new UserMessageMailbox { Address = "helpdesk@contoso.test" },
                To = [new UserMessageMailbox { Address = "zofia.zak@contoso.test", DisplayName = "Zofia Żak" }],
            },
            InlineImages = [.. imageSizes.Select((size, i) => new RenderedInlineImage
            {
                ContentId = $"img-{i:x16}@admng",
                MediaType = "image/png",
                FileName = $"obraz{i + 1}.png",
                Data = new byte[size],
            })],
        },
        CreatedAt = Now,
        NextAttemptAt = Now,
    };
}
