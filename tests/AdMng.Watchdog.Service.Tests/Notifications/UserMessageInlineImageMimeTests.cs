using System.Security.Cryptography;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Mail;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Testing.Smtp;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>
/// The service's MIME of a message to a user with inline images (U1c, design note u1c-u1d section 8) is the body AD
/// Manager's direct SMTP transport builds for the same input; the transport sends the related parts and refuses a
/// payload whose image no longer passes the checks.
/// </summary>
public sealed class UserMessageInlineImageMimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(20, 6, seed: 0x66), "logo-działu.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(32, 24, withMetadata: true), "zrzut.jpg");
    private static readonly MessageImage Gif = MessageImage.Create(MessageImageSamples.Gif(3, 2), "strzałka.gif");

    private static readonly string Html =
        $"<!DOCTYPE html><html lang=\"pl\"><body><p>Dzień dobry Łukaszu, zażółć gęślą jaźń.</p><p><img src=\"cid:{Photo.ContentId}\" alt=\"Zrzut\"> <img src=\"cid:{Gif.ContentId}\" alt=\"→\"></p><p><img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p></body></html>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RenderedEmail Email(params MessageImage[] images) => new()
    {
        Subject = "Zmiana hasła – zażółć gęślą jaźń",
        TextBody = "Dzień dobry Łukaszu, zażółć gęślą jaźń.",
        HtmlBody = Html,
        UserMessage = new UserMessageEnvelope
        {
            From = new UserMessageMailbox { Address = "helpdesk@contoso.test", DisplayName = "Dział IT" },
            To = [new UserMessageMailbox { Address = "lukasz.zielinski@contoso.test", DisplayName = "Łukasz Zieliński" }],
        },
        InlineImages = [.. images.Select(i => new RenderedInlineImage { ContentId = i.ContentId, MediaType = i.MediaType, FileName = i.FileName, Data = i.Data })],
    };

    private static OutgoingUserMessage Outgoing(params MessageImage[] images) => new()
    {
        Id = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-dddddddddd01"),
        From = new MessageAddress("helpdesk@contoso.test", "Dział IT"),
        To = [new MessageAddress("lukasz.zielinski@contoso.test", "Łukasz Zieliński")],
        Subject = "Zmiana hasła – zażółć gęślą jaźń",
        TextBody = "Dzień dobry Łukaszu, zażółć gęślą jaźń.",
        HtmlBody = Html,
        InlineImages = images,
    };

    /// <summary>The tree of a body: content types, ids, dispositions, file names and a hash of every leaf's content.</summary>
    private static IReadOnlyList<string> Shape(MimeEntity entity, string indent = "")
    {
        var lines = new List<string>();
        switch (entity)
        {
            case Multipart multipart:
                lines.Add($"{indent}{multipart.ContentType.MimeType} type={multipart.ContentType.Parameters["type"]}");
                foreach (var child in multipart)
                {
                    lines.AddRange(Shape(child, indent + "  "));
                }

                break;
            case MimePart part:
                using (var content = new MemoryStream())
                {
                    part.Content!.DecodeTo(content, Ct);
                    lines.Add($"{indent}{part.ContentType.MimeType} charset={part.ContentType.Charset} id={part.ContentId} " +
                              $"disposition={part.ContentDisposition?.Disposition} file={part.ContentDisposition?.FileName} " +
                              $"encoding={part.ContentTransferEncoding} sha256={Convert.ToHexStringLower(SHA256.HashData(content.ToArray()))}");
                }

                break;
        }

        return lines;
    }

    private static MimeMessage RoundTrip(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(FormatOptions.Default, stream, Ct);
        stream.Position = 0;
        return MimeMessage.Load(stream, Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheServicesBody_IsTheBodyOfAdManagersDirectSmtp(bool withImages)
    {
        var images = withImages ? new[] { Logo, Photo, Gif } : [];
        var service = RoundTrip(MimeMessageFactory.CreateUserMessage(Email(images), Email().UserMessage!, "0192f3a45b6c7d8e9f00dddddddddd01", "watchdog.contoso.test", Now));
        var direct = RoundTrip(UserMimeMessageBuilder.Build(Outgoing(images), "watchdog.contoso.test", Now));

        Assert.Equal(Shape(direct.Body!), Shape(service.Body!));
        Assert.Equal(direct.MessageId, service.MessageId);
        foreach (var header in new[] { "Auto-Submitted", "X-Auto-Response-Suppress", "X-AdMng-Message" })
        {
            Assert.Equal(direct.Headers[header], service.Headers[header]);
        }

        Assert.Empty(service.Attachments);
        if (withImages)
        {
            var related = Assert.IsType<MultipartRelated>(Assert.IsType<MultipartAlternative>(service.Body)[1]);
            // In order of first reference: photo, arrow, logo.
            Assert.Equal([Photo.ContentId, Gif.ContentId, Logo.ContentId], related.OfType<MimePart>().Skip(1).Select(p => p.ContentId));
        }
    }

    [Fact]
    public void AnImageThatNoLongerPasses_IsNeverPutIntoTheMessage()
    {
        var email = Email(Logo) with
        {
            InlineImages = [new RenderedInlineImage { ContentId = Logo.ContentId, MediaType = "image/png", FileName = "logo.png", Data = MessageImageSamples.Svg }],
        };

        Assert.ThrowsAny<FormatException>(() => MimeMessageFactory.CreateUserMessage(email, email.UserMessage!, "0192f3a45b6c7d8e9f00dddddddddd02", "watchdog.contoso.test", Now));
    }

    [Fact]
    public async Task TheTransport_SendsTheRelatedParts_AndRefusesAnInvalidPayloadPermanently()
    {
        await using var sink = new SmtpSink();
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(sink.Port, SmtpSecurity.None));
        OutboxMessage Message(string id, RenderedEmail email) => new()
        {
            MessageId = id,
            ChannelType = DeliveryChannelType.Email,
            ChannelId = "email",
            Kind = DeliveryKind.UserMessage,
            Email = email,
            CreatedAt = Now,
            NextAttemptAt = Now,
        };

        var delivered = await fixture.Smtp.SendAsync(Message("0192f3a45b6c7d8e9f00dddddddddd03", Email(Photo, Gif, Logo)), Ct);
        var invalid = await fixture.Smtp.SendAsync(Message("0192f3a45b6c7d8e9f00dddddddddd04", Email(Logo) with
        {
            InlineImages = [new RenderedInlineImage { ContentId = Logo.ContentId, MediaType = "image/png", FileName = "logo.png", Data = [0x89, 0x50, 0x4E, 0x47] }],
        }), Ct);

        Assert.Equal(DeliveryOutcome.Delivered, delivered.Outcome);
        var mime = MimeMessage.Load(new MemoryStream(Assert.Single(sink.Messages).Data), Ct);
        var related = Assert.IsType<MultipartRelated>(Assert.IsType<MultipartAlternative>(mime.Body)[1]);
        Assert.Equal(4, related.Count);
        Assert.All(related.OfType<MimePart>().Skip(1), p => Assert.Equal(ContentDisposition.Inline, p.ContentDisposition!.Disposition));
        Assert.Equal((DeliveryOutcome.Permanent, "smtp.invalidImage"), (invalid.Outcome, invalid.ErrorCode));
        Assert.Single(sink.Messages);
    }

    [Theory]
    [InlineData(552, "5.3.4 Message size exceeds fixed maximum message size")]
    [InlineData(552, "Requested mail action aborted: exceeded storage allocation")]
    [InlineData(554, "5.3.4 Message too big for system")]
    public void TooLarge_IsPermanent_WithItsHint(int status, string reply)
    {
        var result = SmtpErrorClassifier.ClassifyCommand(status, reply).Result;
        Assert.Equal((DeliveryOutcome.Permanent, DeliveryHints.MessageTooLarge), (result.Outcome, result.Detail));
    }
}
