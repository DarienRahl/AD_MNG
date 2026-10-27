using System.Security.Cryptography;
using AdMng.Testing.RichHtml;
using MimeKit;

namespace AdMng.Mail.Tests;

/// <summary>
/// U1c × U1d through direct SMTP (design note section 22.4): an Outlook message with the salutation fields and a logo,
/// pasted and planned for a professor, becomes <c>multipart/related</c> with "Szanowny Panie Profesorze" in the pasted
/// formatting, the logo as an inline part (never an attachment) and the salutation in the text part too.
/// </summary>
public sealed class PastedSalutationMimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePastedSalutationMessage_IsHtmlWithItsInlineLogo()
    {
        var (message, logo) = await OutlookSalutationSample.PlanForProfessorAsync(Ct);

        var mime = UserMimeMessageBuilder.Build(message, "admng.contoso.test", new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));
        using var stream = new MemoryStream();
        mime.WriteTo(FormatOptions.Default, stream, Ct);
        stream.Position = 0;
        var parsed = MimeMessage.Load(stream, Ct);

        Assert.Equal("Zmiana hasła – Panie", parsed.Subject);
        var alternative = Assert.IsType<MultipartAlternative>(parsed.Body);
        var related = Assert.IsType<MultipartRelated>(alternative[1]);
        Assert.Equal(["text/html", "image/png"], related.Select(p => p.ContentType.MimeType));
        Assert.Contains(OutlookSalutationSample.ProfessorSalutationHtml, parsed.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("aby <span style=\"background-color:yellow\">Pan</span> zmienił hasło", parsed.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"src=\"cid:{logo.ContentId}\"", parsed.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Szanowny Panie Profesorze,", parsed.TextBody, StringComparison.Ordinal);

        var part = Assert.Single(related.OfType<MimePart>(), p => p.ContentType.MediaType == "image");
        Assert.Equal(logo.ContentId, part.ContentId);
        Assert.Equal(ContentDisposition.Inline, part.ContentDisposition!.Disposition);
        using var bytes = new MemoryStream();
        part.Content!.DecodeTo(bytes, Ct);
        Assert.Equal(logo.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        Assert.Empty(parsed.Attachments);
    }
}
