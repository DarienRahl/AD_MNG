using System.Security.Cryptography;
using System.Text.Json;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using static AdMng.Mail.Tests.MailTestData;

namespace AdMng.Mail.Tests;

/// <summary>
/// A Polish message with a PNG and a JPEG through direct SMTP into Mailpit (U1c): Mailpit shows two inline parts with the
/// content ids of the HTML, no attachments, and the bytes of each part equal the stripped image (SHA-256). Skipped unless
/// ADMNG_TEST_SMTP_HOST and ADMNG_TEST_MAILPIT_API are set (tools/testlab/watchdog/start-mailpit.sh).
/// </summary>
public sealed class MailpitInlineImageTests : IAsyncLifetime
{
    private const string SkipReason = "ADMNG_TEST_SMTP_HOST / ADMNG_TEST_MAILPIT_API are not set - start Mailpit with tools/testlab/watchdog/start-mailpit.sh.";

    private static readonly string? Host = Environment.GetEnvironmentVariable("ADMNG_TEST_SMTP_HOST") is { Length: > 0 } h ? h : null;
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("ADMNG_TEST_SMTP_PORT"), out var p) ? p : 1025;
    private static readonly Uri? Api = Environment.GetEnvironmentVariable("ADMNG_TEST_MAILPIT_API") is { Length: > 0 } a ? new Uri(a) : null;

    private readonly HttpClient _http = new();
    private DirectSmtpOptions _options = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (Host is null || Api is null)
        {
            return;
        }

        _http.BaseAddress = Api;
        var probe = await new SmtpUserMessageTransport(new DirectSmtpOptions { Host = Host, Port = Port, LocalDomain = "admng.contoso.test" }, null)
            .TestConnectionAsync(Token);
        _options = new DirectSmtpOptions
        {
            Host = Host,
            Port = Port,
            Certificate = SmtpCertificateCheck.PinnedSha256,
            PinnedSha256 = probe.CertificateSha256,
            LocalDomain = "admng.contoso.test",
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PngAndJpeg_ArriveAsInlineParts_NotAttachments()
    {
        Assert.SkipWhen(Host is null || Api is null, SkipReason);
        var logo = MessageImage.Create(MessageImageSamples.Png(24, 8, seed: 0x44, withMetadata: true), "clip_image001.png");
        var photo = MessageImage.Create(MessageImageSamples.Jpeg(32, 24, withMetadata: true), "zrzut-ekranu.jpg");
        var subject = "Obrazy w treści – zażółć gęślą jaźń " + Guid.NewGuid().ToString("N")[..8];
        var message = Message() with
        {
            Subject = subject,
            HtmlBody = $"<!DOCTYPE html><html lang=\"pl\"><body><p>Dzień dobry Łukasz,</p><p><img src=\"cid:{logo.ContentId}\" alt=\"Logo – Dział IT\"></p><p>Okno zmiany hasła: <img src=\"cid:{photo.ContentId}\" alt=\"Zrzut\"></p></body></html>",
            InlineImages = [logo, photo],
        };

        await using (var connection = await new SmtpUserMessageTransport(_options, null).ConnectAsync(Token))
        {
            var delivery = await connection.SendAsync(message, Token);
            Assert.True(delivery.IsAccepted, delivery.Code);
        }

        var id = await FindAsync(subject);
        Assert.NotNull(id);
        using var details = await GetJsonAsync($"api/v1/message/{id}");
        var root = details.RootElement;
        Assert.Equal(0, root.GetProperty("Attachments").GetArrayLength());
        var inline = root.GetProperty("Inline").EnumerateArray().ToList();
        Assert.Equal(2, inline.Count);
        Assert.Equal(
            new[] { logo.ContentId, photo.ContentId }.Order(StringComparer.Ordinal),
            inline.Select(i => i.GetProperty("ContentID").GetString()!).Order(StringComparer.Ordinal));
        Assert.Contains($"cid:{logo.ContentId}", root.GetProperty("HTML").GetString(), StringComparison.Ordinal);
        foreach (var part in inline)
        {
            var image = part.GetProperty("ContentID").GetString() == logo.ContentId ? logo : photo;
            Assert.Equal(image.FileName, part.GetProperty("FileName").GetString());
            Assert.Equal(image.MediaType, part.GetProperty("ContentType").GetString());
            var bytes = await _http.GetByteArrayAsync($"api/v1/message/{id}/part/{part.GetProperty("PartID").GetString()}", Token);
            Assert.Equal(image.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync(path, Token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
    }

    /// <summary>The Mailpit id of the message with exactly this subject (waits up to 10 s).</summary>
    private async Task<string?> FindAsync(string subject)
    {
        for (var i = 0; i < 50; i++)
        {
            using var page = await GetJsonAsync("api/v1/messages?limit=200");
            foreach (var found in page.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (found.GetProperty("Subject").GetString() == subject)
                {
                    return found.GetProperty("ID").GetString();
                }
            }

            await Task.Delay(200, Token);
        }

        return null;
    }
}
