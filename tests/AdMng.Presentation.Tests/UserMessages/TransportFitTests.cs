using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// <see cref="TransportFit"/> (U1c, design note section 9.6): images to a transport without images, over its limits, over
/// the room of the service's outbox, HTML longer than it takes – and which choices each reason offers.
/// </summary>
public sealed class TransportFitTests
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(10, 4, seed: 0x74), "logo.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(30, 20), "zdjęcie.jpg");

    private static PlannedUserMessage Planned(string html, params MessageImage[] images) => new()
    {
        Index = 0,
        DistinguishedName = "CN=Zofia Żak,OU=Użytkownicy,DC=contoso,DC=test",
        Name = "Zofia Żak",
        Status = PlannedMessageStatus.Ready,
        Message = new OutgoingUserMessage
        {
            Id = Guid.NewGuid(),
            From = new MessageAddress("helpdesk@contoso.test", "Dział IT"),
            To = [new MessageAddress("zofia.zak@contoso.test", "Zofia Żak")],
            Subject = "Instrukcja – zażółć gęślą jaźń",
            TextBody = "Dzień dobry",
            HtmlBody = html,
            InlineImages = images,
        },
    };

    private static readonly UserMessageTransportCapabilities Service = UserMessageTransportCapabilities.Direct with { ImageStorageAvailableBytes = 10_000_000 };

    [Fact]
    public void MessagesWithinTheLimits_Fit()
    {
        var result = TransportFit.Check([Planned("<p>a</p>", Logo, Photo), Planned("<p>b</p>")], Service);
        Assert.True(result.IsFit);
        Assert.True(TransportFit.Check([Planned("<p>bez obrazów</p>")], UserMessageTransportCapabilities.Legacy).IsFit);
    }

    [Fact]
    public void Images_ToAServiceWithoutImages_DoNotFit_AndOfferSendingWithoutThem()
    {
        var result = TransportFit.Check([Planned("<p>a</p>", Logo), Planned("<p>b</p>"), Planned("<p>c</p>", Photo)], UserMessageTransportCapabilities.Legacy);
        Assert.Equal(new TransportFitResult(TransportFitReason.ImagesNotSupported, 2), result);
        Assert.True(result.OffersWithoutImages);
    }

    [Fact]
    public void ImagesOverTheServicesLimits_DoNotFit()
    {
        var oneImage = Service with { MaxInlineImages = 1 };
        Assert.Equal(TransportFitReason.ImagesOverLimits, TransportFit.Check([Planned("<p>a</p>", Logo, Photo)], oneImage).Reason);
        Assert.Equal(TransportFitReason.ImagesOverLimits, TransportFit.Check([Planned("<p>a</p>", Photo)], Service with { MaxInlineImageBytes = Photo.Data.Length - 1 }).Reason);
        Assert.Equal(TransportFitReason.ImagesOverLimits, TransportFit.Check([Planned("<p>a</p>", Logo, Photo)], Service with { MaxInlineImagesTotalBytes = Photo.Data.Length }).Reason);
    }

    [Fact]
    public void ImagesThatDoNotFitTheOutbox_DoNotFit_ButEachMessageCountsAlone()
    {
        var bytes = Logo.Data.Length + Photo.Data.Length;
        var messages = new[] { Planned("<p>a</p>", Logo, Photo), Planned("<p>b</p>", Logo, Photo) };
        Assert.True(TransportFit.Check(messages, Service with { ImageStorageAvailableBytes = bytes }).IsFit);
        var full = TransportFit.Check(messages, Service with { ImageStorageAvailableBytes = bytes - 1 });
        Assert.Equal(new TransportFitResult(TransportFitReason.ImageStorage, 2), full);
        Assert.True(full.OffersWithoutImages);
    }

    [Fact]
    public void HtmlLongerThanTheServiceTakes_DoesNotFit_AndOffersNoSendingWithoutImages()
    {
        var html = "<p>" + new string('ż', 100_001) + "</p>";
        var result = TransportFit.Check([Planned(html), Planned("<p>krótki</p>")], UserMessageTransportCapabilities.Legacy);
        Assert.Equal(new TransportFitResult(TransportFitReason.HtmlTooLong, 1), result);
        Assert.False(result.OffersWithoutImages);
        Assert.True(TransportFit.Check([Planned(html)], UserMessageTransportCapabilities.Direct).IsFit);
    }

    [Fact]
    public void MessagesThatCannotBeSent_AreNotChecked() =>
        Assert.True(TransportFit.Check([new PlannedUserMessage { Index = 0, DistinguishedName = "CN=x", Name = "x", Status = PlannedMessageStatus.Error }], UserMessageTransportCapabilities.Legacy).IsFit);
}
