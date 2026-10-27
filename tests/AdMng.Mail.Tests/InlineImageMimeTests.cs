using System.Security.Cryptography;
using System.Text;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using MimeKit;
using static AdMng.Mail.Tests.MailTestData;

namespace AdMng.Mail.Tests;

/// <summary>Inline images in the MIME body (U1c, design note section 8): structure, ids, inline disposition, order, no
/// attachments, unchanged structure without images; 552 classification.</summary>
public class InlineImageMimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(12, 4, seed: 0x31), "clip_image001.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(40, 30, withMetadata: true), "zdjecie.jpg");
    private static readonly MessageImage Unused = MessageImage.Create(MessageImageSamples.Gif1x1, "nieuzywany.gif");

    private static OutgoingUserMessage WithImages(params MessageImage[] images) => Message() with
    {
        HtmlBody = $"<!DOCTYPE html><html lang=\"pl\"><body><p>Zdjęcie: <img src=\"cid:{Photo.ContentId}\" alt=\"Zdjęcie\"></p><p>Logo: <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p></body></html>",
        InlineImages = images,
    };

    private static MimeMessage RoundTrip(OutgoingUserMessage message)
    {
        var mime = UserMimeMessageBuilder.Build(message, "admng.contoso.test", Now);
        using var stream = new MemoryStream();
        mime.WriteTo(FormatOptions.Default, stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        return MimeMessage.Load(stream, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Images_AreInlinePartsOfMultipartRelated()
    {
        var parsed = RoundTrip(WithImages(Logo, Photo));

        var alternative = Assert.IsType<MultipartAlternative>(parsed.Body);
        Assert.Equal(2, alternative.Count);
        Assert.Equal("text/plain", alternative[0].ContentType.MimeType);
        var related = Assert.IsType<MultipartRelated>(alternative[1]);
        Assert.Equal("text/html", related.ContentType.Parameters["type"]);
        Assert.Equal(["text/html", "image/jpeg", "image/png"], related.Select(p => p.ContentType.MimeType));

        // In order of first reference: the photo comes first in the HTML.
        var images = related.OfType<MimePart>().Where(p => p.ContentType.MediaType == "image").ToList();
        Assert.Equal([Photo.ContentId, Logo.ContentId], images.Select(p => p.ContentId));
        foreach (var (part, image) in images.Zip(new[] { Photo, Logo }))
        {
            Assert.Equal(ContentDisposition.Inline, part.ContentDisposition!.Disposition);
            Assert.Equal(image.FileName, part.ContentDisposition.FileName);
            Assert.Equal(image.FileName, part.ContentType.Name);
            Assert.Equal(ContentEncoding.Base64, part.ContentTransferEncoding);
            Assert.Null(part.ContentLocation);
            Assert.Null(part.Headers["Content-Description"]);
            using var content = new MemoryStream();
            part.Content!.DecodeTo(content, TestContext.Current.CancellationToken);
            Assert.Equal(image.Sha256, Convert.ToHexStringLower(SHA256.HashData(content.ToArray())));
        }

        Assert.Empty(parsed.Attachments);
    }

    [Fact]
    public void TheRawMessage_HasContentIdsMatchingTheHtml()
    {
        var mime = UserMimeMessageBuilder.Build(WithImages(Photo, Logo), "admng.contoso.test", Now);
        using var stream = new MemoryStream();
        mime.WriteTo(FormatOptions.Default, stream, TestContext.Current.CancellationToken);
        var raw = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains($"Content-Id: <{Logo.ContentId}>", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Content-Id: <{Photo.ContentId}>", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Disposition: inline; filename=clip_image001.png", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Disposition: attachment", raw, StringComparison.OrdinalIgnoreCase);
        // The stripped JPEG travels: no Exif (GPS) bytes in the message.
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.ASCII.GetBytes("GPS 52.2297N")), raw, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreferencedImages_AreNotAdded()
    {
        var parsed = RoundTrip(WithImages(Logo, Unused, Photo, Logo));
        var related = Assert.IsType<MultipartRelated>(Assert.IsType<MultipartAlternative>(parsed.Body)[1]);
        Assert.Equal(3, related.Count);
        Assert.DoesNotContain(related.OfType<MimePart>(), p => p.ContentId == Unused.ContentId);
    }

    [Fact]
    public void WithoutImages_TheStructureIsUnchanged()
    {
        var parsed = RoundTrip(Message());
        var alternative = Assert.IsType<MultipartAlternative>(parsed.Body);
        Assert.Equal(["text/plain", "text/html"], alternative.Select(p => p.ContentType.MimeType));
        Assert.IsType<TextPart>(UserMessageMimeBody.Create("tekst", null, [Logo]));
    }

    [Fact]
    public void OrderOfReference_IgnoresCaseAndDuplicates()
    {
        var html = $"<img src=\"CID:{Logo.ContentId.ToUpperInvariant()}\"><img src=\"cid:{Photo.ContentId}\"><img src=\"cid:{Logo.ContentId}\">";
        Assert.Equal([Logo, Photo], UserMessageMimeBody.OrderOfReference(html, [Photo, Logo, Logo, Unused]));
    }

    [Theory]
    [InlineData(552, "5.3.4 Message size exceeds fixed maximum message size", "smtp.552.5.3.4")]
    [InlineData(552, "Message exceeds the maximum size allowed by the server", "smtp.552")]
    [InlineData(554, "5.3.4 Message too big for system", "smtp.554.5.3.4")]
    public void TooLarge_IsAPermanentRejectionWithItsHint(int status, string reply, string code)
    {
        var delivery = SmtpFailureClassifier.ClassifyReply(status, reply);
        Assert.Equal((UserMessageDeliveryOutcome.Rejected, code, DeliveryHints.MessageTooLarge), (delivery.Outcome, delivery.Code, delivery.Hint));
    }

    [Fact]
    public void ATemporaryLimit_StaysTransient() =>
        Assert.Equal(UserMessageDeliveryOutcome.Transient, SmtpFailureClassifier.ClassifyReply(452, "4.3.1 Insufficient system storage").Outcome);
}
