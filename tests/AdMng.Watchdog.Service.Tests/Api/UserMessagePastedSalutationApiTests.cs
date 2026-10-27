using System.Net;
using System.Security.Cryptography;
using AdMng.Core.Messaging;
using AdMng.Testing.RichHtml;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.Tests.Notifications;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>
/// U1c × U1d through AdMng Watchdog (design note section 22.4): the message AD Manager plans from an Outlook paste with
/// salutation fields and a logo passes the service's own validation unchanged in substance and is delivered with
/// "Szanowny Panie Profesorze" in the pasted formatting and the logo as an inline part.
/// </summary>
public sealed class UserMessagePastedSalutationApiTests : IAsyncLifetime
{
    private readonly SmtpSink _sink = new();
    private readonly WatchdogFactory _factory = new(services: s => s.AddSingleton<IWatchdogJob>(p => p.GetRequiredService<OutboxSenderJob>()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _sink.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sink.DisposeAsync();
    }

    [Fact]
    public async Task ThePastedSalutationMessage_IsDeliveredWithItsInlineLogo()
    {
        var (message, logo) = await OutlookSalutationSample.PlanForProfessorAsync(Ct);
        var configuration = SmtpEmailTransportTests.Configuration(_sink.Port, SmtpSecurity.None) with
        {
            UserMessages = new UserMessagingSettings { Enabled = true, AllowedSenders = ["@contoso.test"] },
        };
        using (var admin = _factory.Admin())
        {
            using var configured = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = configuration }), Ct);
            Assert.True(configured.StatusCode == HttpStatusCode.OK, $"{configured.StatusCode}: {await configured.Content.ReadAsStringAsync(Ct)}");
        }

        // The request as AD Manager's Watchdog transport builds it from the planned message.
        var request = new UserMessageRequest
        {
            Id = message.Id,
            From = new UserMessageAddressDto { Address = message.From.Address, DisplayName = message.From.DisplayName },
            To = [.. message.To.Select(a => new UserMessageAddressDto { Address = a.Address, DisplayName = a.DisplayName })],
            Subject = message.Subject,
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            Language = MessageLanguages.Code(message.Language),
            InlineImages = [.. message.InlineImages.Select(i => new UserMessageImageDto { ContentId = i.ContentId, MediaType = i.MediaType, Data = i.Data, FileName = i.FileName })],
        };
        using var viewer = _factory.Viewer();

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(request), Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(UserMessageState.Delivered, (await ApiJson.ReadAsync<UserMessageResult>(response)).State);
        var mime = MimeMessage.Load(new MemoryStream(Assert.Single(_sink.Messages).Data), Ct);
        Assert.Equal("Zmiana hasła – Panie", mime.Subject);
        var related = Assert.IsType<MultipartRelated>(Assert.IsType<MultipartAlternative>(mime.Body)[1]);
        Assert.Equal(["text/html", "image/png"], related.Select(p => p.ContentType.MimeType));
        Assert.Contains(OutlookSalutationSample.ProfessorSalutationHtml, mime.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Adresat: prof. dr hab. Stanisław Kowalczyk", mime.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"src=\"cid:{logo.ContentId}\"", mime.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Szanowny Panie Profesorze,", mime.TextBody, StringComparison.Ordinal);
        var part = Assert.Single(related.OfType<MimePart>(), p => p.ContentType.MediaType == "image");
        Assert.Equal((logo.ContentId, ContentDisposition.Inline), (part.ContentId, part.ContentDisposition!.Disposition));
        using var bytes = new MemoryStream();
        part.Content!.DecodeTo(bytes, Ct);
        Assert.Equal(logo.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        Assert.Empty(mime.Attachments);
    }
}
