using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// U1c: inline images of messages to users live only in the outbox payload; their bytes are counted (SQLite:
// outbox.image_bytes) while the payload exists – set on insert, 0 once sent, gone when the row is purged.
public abstract partial class WatchdogStoreContractTests<TStore>
{
    /// <summary>Bytes of inline images the store holds in outbox payloads (SQLite: <c>SUM(outbox.image_bytes)</c>).</summary>
    protected abstract Task<long> StoredImageBytesAsync(TStore store);

    /// <summary>A message to a user with two inline images (PNG, GIF).</summary>
    protected static OutboxMessage UserMessageWithImages(string messageId, params byte[][] images) => new()
    {
        MessageId = messageId,
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Kind = DeliveryKind.UserMessage,
        Email = new RenderedEmail
        {
            Subject = "Hasło wygasa – zażółć gęślą jaźń",
            TextBody = "Dzień dobry Zofio",
            HtmlBody = string.Concat(images.Select((_, i) => $"<img src=\"cid:img-{i:x16}@admng\">")),
            UserMessage = new UserMessageEnvelope
            {
                From = new UserMessageMailbox { Address = "helpdesk@contoso.test", DisplayName = "Dział IT" },
                To = [new UserMessageMailbox { Address = "zofia.zak@contoso.test", DisplayName = "Zofia Żak" }],
            },
            InlineImages = [.. images.Select((data, i) => new RenderedInlineImage
            {
                ContentId = $"img-{i:x16}@admng",
                MediaType = "image/png",
                FileName = $"obraz{i + 1}.png",
                Data = data,
            })],
        },
        CreatedAt = Now,
        NextAttemptAt = Now,
    };

    [Fact]
    public Task InlineImages_RoundTripThroughThePayload_AndAreCountedUntilSent() => WithStoreAsync(async store =>
    {
        var logo = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var photo = Enumerable.Range(0, 1200).Select(i => (byte)(i * 7)).ToArray();
        var id = await store.EnqueueAsync(UserMessageWithImages("0192f3a45b6c7d8e9f00aaaaaaaaaa01", logo, photo), Ct);
        Assert.Equal(4200, await StoredImageBytesAsync(store));

        var leased = Assert.Single(await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct));
        var images = leased.Email!.InlineImages;
        Assert.Equal(["img-0000000000000000@admng", "img-0000000000000001@admng"], images.Select(i => i.ContentId));
        Assert.Equal(logo, images[0].Data);
        Assert.Equal(photo, images[1].Data);
        Assert.Equal(("image/png", "obraz2.png"), (images[1].MediaType, images[1].FileName));

        await store.MarkSentAsync(id, Now, Ct);
        Assert.Equal(0, await StoredImageBytesAsync(store));
    });

    [Fact]
    public Task InlineImages_OfADeadMessage_StayUntilThePurge() => WithStoreAsync(async store =>
    {
        var id = await store.EnqueueAsync(UserMessageWithImages("0192f3a45b6c7d8e9f00aaaaaaaaaa02", new byte[500]), Ct);
        await store.EnqueueAsync(Message(), Ct);
        _ = await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct);
        await store.MarkDeadAsync(id, "smtp.552.5.3.4", null, Ct);
        Assert.Equal(500, await StoredImageBytesAsync(store));

        await store.PurgeAsync(new RetentionSettings { OutboxDeadDays = 1 }, Now.AddDays(2), Ct);
        Assert.Equal(0, await StoredImageBytesAsync(store));
    });

    [Fact]
    public Task MessagesWithoutImages_CountNothing() => WithStoreAsync(async store =>
    {
        await store.EnqueueAsync(Message("email"), Ct);
        await store.EnqueueAsync(UserMessageWithImages("0192f3a45b6c7d8e9f00aaaaaaaaaa03"), Ct);
        Assert.Equal(0, await StoredImageBytesAsync(store));
    });
}
