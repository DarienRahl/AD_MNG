using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.Tests.Notifications;
using AdMng.Watchdog.Service.UserMessages;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>
/// Inline images of messages to users through the API (U1c, design note u1c-u1d section 9): accepted and delivered by the
/// outbox job as inline parts of <c>multipart/related</c>, every new issue code, the <c>allowInlineImages</c> switch, no
/// attachments still, the 6 MiB limit of this route only, idempotency over the images, the stored-image cap (429
/// <c>imageStorageFull</c>), the status capabilities – and no image byte left behind in the outbox, the audit or the log.
/// </summary>
public sealed class UserMessageInlineImageApiTests : IAsyncLifetime
{
    private const string Zofia = "zofia.zak@contoso.test";

    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(24, 8, seed: 0x61, withMetadata: true), "logo-działu.png");
    private static readonly MessageImage Screenshot = MessageImage.Create(MessageImageSamples.Jpeg(48, 32, withMetadata: true), "zrzut-ekranu.jpg");

    private readonly SmtpSink _sink = new();
    private readonly WatchdogFactory _factory;

    public UserMessageInlineImageApiTests()
    {
        _factory = new WatchdogFactory(services: s => s.AddSingleton<IWatchdogJob>(p => p.GetRequiredService<OutboxSenderJob>()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _sink.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sink.DisposeAsync();
    }

    [Fact]
    public async Task Images_AreDeliveredAsInlineParts_AndNothingOfThemIsKept()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(UserMessageState.Delivered, (await ApiJson.ReadAsync<UserMessageResult>(response)).State);
        var mime = MimeMessage.Load(new MemoryStream(Assert.Single(_sink.Messages).Data), Ct);
        var alternative = Assert.IsType<MultipartAlternative>(mime.Body);
        var related = Assert.IsType<MultipartRelated>(alternative[1]);
        Assert.Equal(["text/html", "image/jpeg", "image/png"], related.Select(p => p.ContentType.MimeType));
        var parts = related.OfType<MimePart>().Where(p => p.ContentType.MediaType == "image").ToList();
        Assert.Equal([Screenshot.ContentId, Logo.ContentId], parts.Select(p => p.ContentId));
        foreach (var (part, image) in parts.Zip([Screenshot, Logo]))
        {
            Assert.Equal(ContentDisposition.Inline, part.ContentDisposition!.Disposition);
            using var bytes = new MemoryStream();
            part.Content!.DecodeTo(bytes, Ct);
            Assert.Equal(image.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        }

        Assert.Empty(mime.Attachments);
        Assert.Contains($"src=\"cid:{Logo.ContentId}\"", mime.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Zażółć gęślą jaźń", mime.HtmlBody, StringComparison.Ordinal);

        await SettledOutboxAsync();
        var (payloads, imageBytes) = _factory.Store.Database.Read(c => (
            SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM outbox WHERE payload IS NOT NULL;"),
            SqliteValues.Scalar<long>(c, null, "SELECT coalesce(sum(image_bytes), 0) FROM outbox;")));
        Assert.Equal((0, 0), (payloads, imageBytes));
        var status = await ApiJson.ReadAsync<UserMessagingStatusDto>(await viewer.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct));
        Assert.Equal(0, status.StoredImageBytes);

        var log = string.Join('\n', _factory.Log.Entries.Select(e => e.Message));
        Assert.Contains("2 recipient(s), 2 inline image(s)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Logo.Data)[..24], log, StringComparison.Ordinal);
        Assert.DoesNotContain("logo-", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServiceSends_TheSanitizedHtml_AndTheStrippedImages()
    {
        // A cosmetic difference (class names, upper-case tags, an unknown style) is repaired by the shared sanitizer, the
        // received image bytes lose their metadata exactly as AD Manager strips them.
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        var raw = MessageImageSamples.Png(8, 8, seed: 0x62, withMetadata: true);
        var stripped = MessageImage.Create(raw, "z-metadanymi.png");
        // A client that did not strip the metadata itself: the id is derived from the bytes it sends.
        var rawId = MessageImage.ContentIdFor(Convert.ToHexStringLower(SHA256.HashData(raw)));
        var request = Request() with
        {
            HtmlBody = $"<html><body><P CLASS=MsoNormal style=\"mso-line-height-rule:exactly;color:#C00000\">Łódź <img src=\"cid:{rawId}\" alt=\"x\"></P></body></html>",
            InlineImages = [new UserMessageImageDto { ContentId = rawId, MediaType = "image/png", Data = raw, FileName = stripped.FileName }],
        };

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(request), Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var mime = MimeMessage.Load(new MemoryStream(Assert.Single(_sink.Messages).Data), Ct);
        Assert.Contains("Łódź", mime.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<p style=", mime.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("MsoNormal", mime.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("mso-", mime.HtmlBody, StringComparison.Ordinal);
        var part = mime.BodyParts.OfType<MimePart>().Single(p => p.ContentType.MediaType == "image");
        using var bytes = new MemoryStream();
        part.Content!.DecodeTo(bytes, Ct);
        Assert.Equal(stripped.Data, bytes.ToArray());
        Assert.True(bytes.Length < raw.Length);
        Assert.Equal(rawId, part.ContentId);
    }

    [Theory]
    [InlineData("notAnImage", "invalidImage")]
    [InlineData("svg", "invalidImage")]
    [InlineData("typeMismatch", "invalidImage")]
    [InlineData("truncated", "invalidImage")]
    [InlineData("pathFileName", "invalidImage")]
    [InlineData("sameImageTwice", "invalidImage")]
    [InlineData("noData", "invalidImage")]
    [InlineData("wrongId", "imageIdMismatch")]
    [InlineData("cidWithoutImage", "missingImage")]
    [InlineData("imageNotReferenced", "unreferencedImage")]
    [InlineData("elevenImages", "tooLarge")]
    [InlineData("overOneMiB", "tooLarge")]
    [InlineData("overTwoMiBTotal", "tooLarge")]
    [InlineData("noHtml", "required")]
    [InlineData("remoteImage", "forbiddenHtml")]
    [InlineData("positionFixed", "forbiddenHtml")]
    [InlineData("hiddenText", "forbiddenHtml")]
    [InlineData("deeplyNested", "forbiddenHtml")]
    [InlineData("longHtml", "tooLong")]
    public async Task InvalidImagesOrHtml_Are400_WithTheirIssue_AndNothingIsQueued(string variant, string code)
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        var request = Variant(variant);

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(request), Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Contains(code, await ApiJson.IssueCodesAsync(response));
        Assert.Empty((await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(0L, _factory.Store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM user_messages;")));
        Assert.Empty(_sink.Messages);
    }

    [Fact]
    public async Task ImagesSwitchedOff_Are400ImagesDisabled_AndTheStatusHasNoCapability_MessagesWithoutImagesStillGo()
    {
        await ConfigureAsync(_sink.Port, s => s with { AllowInlineImages = false });
        using var viewer = _factory.Viewer();

        var status = await ApiJson.ReadAsync<UserMessagingStatusDto>(await viewer.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct));
        using var withImages = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        using var withoutImages = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid()) with
        {
            HtmlBody = "<p>Dzień dobry, Zofio!</p>",
            InlineImages = [],
        }), Ct);

        Assert.Empty(status.Capabilities);
        Assert.Equal((0, 0L, 0L), (status.MaxInlineImages, status.MaxInlineImageBytes, status.MaxInlineImagesTotalBytes));
        Assert.Equal(HttpStatusCode.BadRequest, withImages.StatusCode);
        Assert.Equal([UserMessageIssueCodes.ImagesDisabled], await ApiJson.IssueCodesAsync(withImages));
        Assert.Equal(HttpStatusCode.OK, withoutImages.StatusCode);
        Assert.Single(_sink.Messages);
    }

    [Fact]
    public async Task TheStatus_ReportsTheCapability_TheLimits_AndTheStoredImages()
    {
        await ConfigureAsync(_sink.Port, s => s with { MaxStoredImageMb = 64 });
        using var viewer = _factory.Viewer();

        var status = await ApiJson.ReadAsync<UserMessagingStatusDto>(await viewer.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct));

        Assert.Equal([UserMessagingCapabilities.InlineImages], status.Capabilities);
        Assert.Equal((MessageImageLimits.MaxImagesPerMessage, (long)MessageImageLimits.MaxImageBytes, MessageImageLimits.MaxImagesBytesPerMessage),
            (status.MaxInlineImages, status.MaxInlineImageBytes, status.MaxInlineImagesTotalBytes));
        Assert.Equal(400_000, status.MaxHtmlLength);
        Assert.Equal((0L, 64L * 1024 * 1024), (status.StoredImageBytes, status.MaxStoredImageBytes));
    }

    [Fact]
    public async Task Attachments_AreStillInvalidJson_EvenWithImages()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        var json = JsonSerializer.Serialize(Request(), WatchdogJson.Options);
        using var content = new StringContent(json[..^1] + ",\"attachments\":[{\"name\":\"faktura.pdf.exe\",\"content\":\"TVqQ\"}]}", Encoding.UTF8, "application/json");
        var disposition = json.Replace("\"fileName\":", "\"disposition\":\"attachment\",\"fileName\":", StringComparison.Ordinal);
        using var asAttachment = new StringContent(disposition, Encoding.UTF8, "application/json");

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, content, Ct);
        using var imageAsAttachment = await viewer.PostAsync(WatchdogRoutes.UserMessages, asAttachment, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["invalidJson"], await ApiJson.IssueCodesAsync(response));
        Assert.Equal(HttpStatusCode.BadRequest, imageAsAttachment.StatusCode);
        Assert.Equal(["invalidJson"], await ApiJson.IssueCodesAsync(imageAsAttachment));
        Assert.Empty(_sink.Messages);
    }

    [Fact]
    public async Task ThisRouteTakesUpTo6MiB_EveryOtherRouteKeeps1MB()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        using var admin = _factory.Admin();
        // 3 MiB of images (4 MiB of base64): over the message limits, but within the route's body limit – a 400 with the
        // issue, not a 413.
        var big = Request() with
        {
            InlineImages = [.. Enumerable.Range(0, 3).Select(i => Dto(new byte[1024 * 1024 - 16]))],
        };
        var bigJson = JsonSerializer.Serialize(big, WatchdogJson.Options);
        Assert.InRange(Encoding.UTF8.GetByteCount(bigJson), ApiLimits.MaxRequestBodyBytes * 3, ApiLimits.MaxUserMessageBodyBytes);

        using var withinRoute = await viewer.PostAsync(WatchdogRoutes.UserMessages, new StringContent(bigJson, Encoding.UTF8, "application/json"), Ct);
        using var overRoute = await viewer.PostAsync(WatchdogRoutes.UserMessages, new StringContent(
            "{\"id\":\"0192f3a4-5b6c-7d8e-9f00-112233445566\",\"textBody\":\"" + new string('a', (int)ApiLimits.MaxUserMessageBodyBytes) + "\"}",
            Encoding.UTF8,
            "application/json"), Ct);
        using var otherRoute = await admin.PutAsync(WatchdogRoutes.Config, new StringContent(bigJson[..(int)(ApiLimits.MaxRequestBodyBytes + 1024)], Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, withinRoute.StatusCode);
        Assert.Contains(UserMessageIssueCodes.TooLarge, await ApiJson.IssueCodesAsync(withinRoute));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overRoute.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, otherRoute.StatusCode);
        Assert.Empty(_sink.Messages);
    }

    [Fact]
    public async Task ARepeatedIdWithTheSameImages_IsADuplicate_OtherImagesConflict()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        using var first = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var again = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        var other = MessageImage.Create(MessageImageSamples.Png(24, 8, seed: 0x63), "logo-działu.png");
        var changed = Request() with
        {
            HtmlBody = Request().HtmlBody!.Replace(Logo.ContentId, other.ContentId, StringComparison.Ordinal),
            InlineImages = [Dto(Screenshot), Dto(other)],
        };
        using var otherImage = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(changed), Ct);

        var repeated = await ApiJson.ReadAsync<UserMessageResult>(again);
        Assert.Equal((HttpStatusCode.OK, UserMessageState.Delivered, true), (again.StatusCode, repeated.State, repeated.Duplicate));
        Assert.Equal(HttpStatusCode.Conflict, otherImage.StatusCode);
        Assert.Equal(ApiProblem.IdempotencyConflict, (await ApiJson.ProblemAsync(otherImage)).GetProperty("type").GetString());
        Assert.Single(_sink.Messages);
    }

    [Fact]
    public async Task TheStoredImageCap_Is429ImageStorageFull_WithRetryAfter_MessagesWithoutImagesAreNotAffected()
    {
        await ConfigureAsync(port: 1, s => s with { MaxStoredImageMb = 16 });   // nothing listens: messages stay queued
        _factory.Services.GetRequiredService<UserMessageService>().Wait = TimeSpan.Zero;
        using var viewer = _factory.Viewer();

        using var queued = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var stored = Logo.Data.Length + Screenshot.Data.Length;
        Assert.Equal(stored, (await ApiJson.ReadAsync<UserMessagingStatusDto>(await viewer.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct))).StoredImageBytes);

        // The queue holds almost 16 MiB of images (as if many messages were waiting).
        await _factory.Store.Database.WriteAsync(
            (c, t) => SqliteValues.Execute(c, t, "UPDATE outbox SET image_bytes = $bytes;", ("$bytes", 16L * 1024 * 1024 - 100)),
            Ct);
        using var full = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        using var textOnly = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid()) with
        {
            HtmlBody = "<p>Bez obrazów – zażółć gęślą jaźń</p>",
            InlineImages = [],
        }), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(TimeSpan.FromHours(1), full.Headers.RetryAfter?.Delta);
        Assert.Equal([UserMessageIssueCodes.ImageStorageFull], await ApiJson.IssueCodesAsync(full));
        Assert.Equal(HttpStatusCode.Accepted, textOnly.StatusCode);
        Assert.Equal(2, (await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items.Count);
        Assert.Equal(2L, _factory.Store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM user_messages;")));
        Assert.Contains(_factory.Log.Entries, e => e.Message.Contains("reached userMessages.maxStoredImageMb", StringComparison.Ordinal));
    }

    private static UserMessageRequest Variant(string variant)
    {
        var request = Request();
        var html = request.HtmlBody!;
        return variant switch
        {
            "notAnImage" => WithOne("To nie jest obraz – zażółć"u8.ToArray(), "image/png"),
            "svg" => WithOne(MessageImageSamples.Svg, "image/svg+xml"),
            "typeMismatch" => WithOne(MessageImageSamples.Png(4, 4, seed: 0x64), "image/jpeg"),
            "truncated" => WithOne(MessageImageSamples.Png(4, 4, seed: 0x65)[..40], "image/png"),
            "pathFileName" => request with { InlineImages = [Dto(Screenshot), Dto(Logo) with { FileName = @"C:\Users\zofia\logo.png" }] },
            "sameImageTwice" => request with { InlineImages = [Dto(Screenshot), Dto(Logo), Dto(Logo)] },
            "noData" => request with { InlineImages = [Dto(Screenshot), Dto(Logo) with { Data = [] }] },
            "wrongId" => request with { InlineImages = [Dto(Screenshot), Dto(Logo) with { ContentId = "img-0123456789abcdef@admng" }] },
            "cidWithoutImage" => request with { InlineImages = [Dto(Screenshot)] },
            "imageNotReferenced" => request with { HtmlBody = html.Replace($"<img src=\"cid:{Logo.ContentId}\" alt=\"Logo – Dział IT\">", string.Empty, StringComparison.Ordinal) },
            "elevenImages" => ElevenImages(),
            "overOneMiB" => request with { InlineImages = [Dto(Screenshot), Dto(new byte[MessageImageLimits.MaxImageBytes + 1]) with { ContentId = Logo.ContentId }] },
            "overTwoMiBTotal" => request with { InlineImages = [Dto(Screenshot), Dto(new byte[1024 * 1024]), Dto(new byte[1024 * 1024]), Dto(Logo)] },
            "noHtml" => request with { HtmlBody = null },
            "remoteImage" => request with { HtmlBody = html.Replace("</p>", "<img src=\"https://tracker.example/p.gif\"></p>", StringComparison.Ordinal) },
            "positionFixed" => request with { HtmlBody = html.Replace("<p>", "<p style=\"position:fixed;top:0\">", StringComparison.Ordinal) },
            "hiddenText" => request with { HtmlBody = html + "<div style=\"display:none\">ukryte</div>" },
            "deeplyNested" => request with { HtmlBody = html.Replace("</p>", string.Concat(Enumerable.Repeat("<div>", 5_000)) + "głęboko</p>", StringComparison.Ordinal) },
            _ => request with { HtmlBody = "<p>" + new string('ż', UserMessageLimits.MaxHtmlLength) + "</p>" },
        };

        UserMessageRequest WithOne(byte[] data, string mediaType)
        {
            var id = MessageImage.ContentIdFor(Convert.ToHexStringLower(SHA256.HashData(data)));
            return request with
            {
                HtmlBody = html.Replace(Logo.ContentId, id, StringComparison.Ordinal),
                InlineImages = [Dto(Screenshot), new UserMessageImageDto { ContentId = id, MediaType = mediaType, Data = data, FileName = "obraz.png" }],
            };
        }

        UserMessageRequest ElevenImages()
        {
            var images = Enumerable.Range(1, MessageImageLimits.MaxImagesPerMessage + 1).Select(i => MessageImage.Create(MessageImageSamples.Png(2, 2, seed: (byte)(0x70 + i)))).ToList();
            return request with
            {
                HtmlBody = "<p>" + string.Concat(images.Select(i => $"<img src=\"cid:{i.ContentId}\" alt=\"{i.FileName}\">")) + "</p>",
                InlineImages = [.. images.Select(Dto)],
            };
        }
    }

    private async Task SettledOutboxAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while ((await _factory.Store.QueryAsync(new OutboxQuery(), timeout.Token)).Items.Any(i => i.Status == OutboxStatus.Sending))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private async Task ConfigureAsync(int port, Func<UserMessagingSettings, UserMessagingSettings>? change = null)
    {
        var settings = new UserMessagingSettings { Enabled = true, AllowedSenders = ["@contoso.test"] };
        var configuration = SmtpEmailTransportTests.Configuration(port, SmtpSecurity.None) with { UserMessages = change?.Invoke(settings) ?? settings };
        using var admin = _factory.Admin();
        using var response = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = configuration }), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static UserMessageImageDto Dto(MessageImage image) => new()
    {
        ContentId = image.ContentId,
        MediaType = image.MediaType,
        Data = image.Data,
        FileName = image.FileName,
    };

    /// <summary>Bytes that are no image, with the id derived from them (only the size checks come first).</summary>
    private static UserMessageImageDto Dto(byte[] data) => new()
    {
        ContentId = MessageImage.ContentIdFor(Convert.ToHexStringLower(SHA256.HashData(data))),
        MediaType = "image/png",
        Data = data,
        FileName = "duzy.png",
    };

    /// <summary>A Polish message with a screenshot and a logo, rendered like AD Manager renders it (document + footer).</summary>
    private static UserMessageRequest Request(Guid? id = null)
    {
        var footer = BuiltInMessageTemplates.Footer(MessageLanguage.Polish);
        var fragment = $"<p>Dzień dobry, Zofio! Zażółć gęślą jaźń.</p>\n<p><img src=\"cid:{Screenshot.ContentId}\" alt=\"Okno zmiany hasła\"></p>\n<p><img src=\"cid:{Logo.ContentId}\" alt=\"Logo – Dział IT\"></p>\n";
        return new UserMessageRequest
        {
            Id = id ?? Guid.Parse("0192f3a4-5b6c-7d8e-9f00-cccccccccc01"),
            From = new UserMessageAddressDto { Address = "helpdesk@contoso.test", DisplayName = "Dział IT" },
            To = [new UserMessageAddressDto { Address = Zofia, DisplayName = "Zofia Żak" }],
            Cc = [new UserMessageAddressDto { Address = "jan.kowalski@contoso.test", DisplayName = "Jan Kowalski" }],
            Subject = "Zmiana hasła – instrukcja ze zrzutem ekranu",
            TextBody = UserMessageRules.TextDocument("Dzień dobry, Zofio! Zażółć gęślą jaźń.\n[Okno zmiany hasła]\n[Logo – Dział IT]", footer),
            HtmlBody = UserMessageRules.HtmlDocument(fragment, footer, MessageLanguage.Polish),
            Language = "pl",
            InlineImages = [Dto(Screenshot), Dto(Logo)],
        };
    }
}
