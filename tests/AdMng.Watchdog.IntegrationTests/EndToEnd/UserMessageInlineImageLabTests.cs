using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// A Polish message to a user with a PNG and a JPEG through the real service (U1c, design note u1c-u1d section 21): the
/// lab service on Kestrel with its SQLite store and outbox job, SMTP to Mailpit, the AD_MNG client. Mailpit shows two
/// <c>Inline</c> parts with the content ids of the HTML and no attachment, each part's bytes are the stripped image
/// (SHA-256); afterwards the outbox row has no payload and <c>image_bytes</c> 0, and the log holds no image byte. Skipped
/// without the lab (<c>ADMNG_TEST_LDAP_*</c>) and Mailpit (<c>ADMNG_TEST_SMTP_HOST</c>, <c>ADMNG_TEST_MAILPIT_API</c>).
/// </summary>
public sealed class UserMessageInlineImageLabTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMessageWithImages_ArrivesWithInlineParts_AndTheServiceKeepsNoImage()
    {
        WatchdogLab.RequireLab();
        Assert.SkipWhen(WatchdogLab.SmtpHost is null || WatchdogLab.MailpitApi is null, "Mailpit is not configured (ADMNG_TEST_SMTP_HOST / ADMNG_TEST_MAILPIT_API).");
        var run = Guid.NewGuid().ToString("N")[..8];
        var recipient = $"zofia.zak.{run}@contoso.test";
        var logo = MessageImage.Create(MessageImageSamples.Png(32, 12, seed: 0x5A, withMetadata: true), "logo-działu.png");
        var screenshot = MessageImage.Create(MessageImageSamples.Jpeg(64, 40, withMetadata: true), "zrzut-ekranu.jpg");

        await using var mailbox = MailpitMailbox.FromEnvironment()!;
        using var ldapPassword = new EnvironmentVariableScope(DirectoryConnectionOptionsFactory.PasswordVariable, WatchdogLab.ReadOnlyPassword);
        await using var service = new LabServiceFactory();
        service.Start();
        using var client = service.Client();

        var envelope = await client.GetConfigAsync(Ct);
        await client.SaveConfigAsync(envelope with
        {
            Configuration = envelope.Configuration with
            {
                Smtp = mailbox.Settings("watchdog.obrazy@contoso.test", $"Hasło-{run}") with { From = "AD Watchdog <watchdog@contoso.test>", LocalDomain = "watchdog.contoso.test" },
                UserMessages = new UserMessagingSettings { Enabled = true, AllowedSenders = ["@contoso.test"] },
            },
        }, Ct);
        var status = await client.GetUserMessagingStatusAsync(Ct);
        Assert.True(status.Available);
        Assert.Contains(UserMessagingCapabilities.InlineImages, status.Capabilities);

        var footer = BuiltInMessageTemplates.Footer(MessageLanguage.Polish);
        var fragment = $"<p>Dzień dobry, Zofio! Zażółć gęślą jaźń.</p>\n<p><img src=\"cid:{screenshot.ContentId}\" alt=\"Okno zmiany hasła\"></p>\n<p><img src=\"cid:{logo.ContentId}\" alt=\"Logo – Dział IT\"></p>\n";
        var request = new UserMessageRequest
        {
            Id = Guid.NewGuid(),
            From = new UserMessageAddressDto { Address = "helpdesk@contoso.test", DisplayName = "Dział IT" },
            To = [new UserMessageAddressDto { Address = recipient, DisplayName = "Zofia Żak" }],
            Subject = $"Zmiana hasła – instrukcja ze zrzutem ekranu {run}",
            TextBody = UserMessageRules.TextDocument("Dzień dobry, Zofio! Zażółć gęślą jaźń.\n[Okno zmiany hasła]\n[Logo – Dział IT]", footer),
            HtmlBody = UserMessageRules.HtmlDocument(fragment, footer, MessageLanguage.Polish),
            Language = "pl",
            InlineImages = [Dto(screenshot), Dto(logo)],
        };

        var sent = await client.SendUserMessageAsync(request, Ct);
        Assert.Contains(sent.State, new[] { UserMessageState.Delivered, UserMessageState.Queued });

        var mail = (await Poll.UntilAsync(() => mailbox.ReceivedAsync(recipient, Ct), mails => mails.Count > 0, Timeout, "the message with images", Ct)).Single();
        Assert.Equal(request.Subject, mail.Subject);
        Assert.Contains($"cid:{logo.ContentId}", mail.Html, StringComparison.Ordinal);

        using var http = new HttpClient { BaseAddress = WatchdogLab.MailpitApi, Timeout = TimeSpan.FromSeconds(30) };
        using var search = (await http.GetFromJsonAsync<JsonDocument>($"api/v1/search?query={Uri.EscapeDataString($"to:\"{recipient}\"")}", Ct))!;
        var id = search.RootElement.GetProperty("messages")[0].GetProperty("ID").GetString();
        using var details = (await http.GetFromJsonAsync<JsonDocument>($"api/v1/message/{id}", Ct))!;
        Assert.Equal(0, details.RootElement.GetProperty("Attachments").GetArrayLength());
        var inline = details.RootElement.GetProperty("Inline").EnumerateArray().ToList();
        Assert.Equal(
            new[] { logo.ContentId, screenshot.ContentId }.Order(StringComparer.Ordinal),
            inline.Select(i => i.GetProperty("ContentID").GetString()!).Order(StringComparer.Ordinal));
        foreach (var part in inline)
        {
            var image = part.GetProperty("ContentID").GetString() == logo.ContentId ? logo : screenshot;
            Assert.Equal(image.MediaType, part.GetProperty("ContentType").GetString());
            var bytes = await http.GetByteArrayAsync($"api/v1/message/{id}/part/{part.GetProperty("PartID").GetString()}", Ct);
            Assert.Equal(image.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        // The images lived only in the outbox payload, which is gone once sent.
        var store = service.Services.GetRequiredService<SqliteWatchdogStore>();
        var (payload, imageBytes) = await Poll.UntilAsync(
            () => Task.FromResult(store.Database.Read(c => (
                SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM outbox WHERE payload IS NOT NULL;"),
                SqliteValues.Scalar<long>(c, null, "SELECT coalesce(sum(image_bytes), 0) FROM outbox;")))),
            r => r.Item1 == 0,
            Timeout,
            "the payload dropped after sending",
            Ct);
        Assert.Equal((0L, 0L), (payload, imageBytes));
        Assert.Equal(0, (await client.GetUserMessagingStatusAsync(Ct)).StoredImageBytes);

        var log = service.Log.Text;
        Assert.Contains("2 inline image(s)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(recipient, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(logo.Data)[..32], log, StringComparison.Ordinal);
    }

    private static UserMessageImageDto Dto(MessageImage image) => new()
    {
        ContentId = image.ContentId,
        MediaType = image.MediaType,
        Data = image.Data,
        FileName = image.FileName,
    };
}
