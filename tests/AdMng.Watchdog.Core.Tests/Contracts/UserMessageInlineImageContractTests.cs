using System.Text.Json;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Contracts;

/// <summary>Contract v2 of messages to users (U1c, design note u1c-u1d section 9): inline images in the request, the new
/// status fields (and an older service's status without them), the limits equal to AD Manager's, the configuration
/// switches with their validation and audit rating, the outbox payload model and the fake client's capability switch.</summary>
public sealed class UserMessageInlineImageContractTests
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(16, 8, seed: 0x52), "logo-działu.png");

    private static UserMessageRequest Request() => new()
    {
        Id = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-aabbccddeeff"),
        From = new UserMessageAddressDto { Address = "helpdesk@contoso.test", DisplayName = "Dział IT – Pomoc" },
        To = [new UserMessageAddressDto { Address = "zofia.zak@contoso.test", DisplayName = "Zofia Żak" }],
        Subject = "Zmiana hasła – instrukcja ze zrzutem ekranu",
        TextBody = "Dzień dobry, Zofio!\n[Logo]",
        HtmlBody = $"<p>Dzień dobry, Zofio!</p><p><img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p>",
        InlineImages = [new UserMessageImageDto { ContentId = Logo.ContentId, MediaType = Logo.MediaType, Data = Logo.Data.ToArray(), FileName = Logo.FileName }],
    };

    [Fact]
    public void InlineImages_RoundTrip_AsBase64_AndNeverPrintTheirBytes()
    {
        var json = JsonSerializer.Serialize(Request(), WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<UserMessageRequest>(json, WatchdogJson.Options)!;

        Assert.Contains("\"inlineImages\":[{\"contentId\":\"" + Logo.ContentId + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"data\":\"" + Convert.ToBase64String(Logo.Data) + "\"", json, StringComparison.Ordinal);
        var image = Assert.Single(back.InlineImages);
        Assert.Equal(Logo.Data.ToArray(), image.Data);
        Assert.Equal((Logo.ContentId, "image/png", Logo.FileName), (image.ContentId, image.MediaType, image.FileName));
        Assert.Equal($"UserMessageImageDto({Logo.ContentId})", image.ToString());
        Assert.Equal(JsonSerializer.Serialize(back, WatchdogJson.Options), json);
    }

    [Fact]
    public void ARequestWithoutImages_StaysAsInU1b_SoAnOlderServiceAcceptsIt()
    {
        var back = JsonSerializer.Deserialize<UserMessageRequest>("{\"id\":\"0192f3a4-5b6c-7d8e-9f00-112233445566\",\"htmlBody\":\"<p>ż</p>\"}", WatchdogJson.Options)!;
        Assert.Empty(back.InlineImages);

        // The member is not written while it is empty (an older service rejects unknown members).
        var json = JsonSerializer.Serialize(Request() with { InlineImages = [] }, WatchdogJson.Options);
        Assert.DoesNotContain("inlineImages", json, StringComparison.Ordinal);
        Assert.DoesNotContain("inlineImages", JsonSerializer.Serialize(new UserMessageRequest { Id = Guid.NewGuid(), InlineImages = new List<UserMessageImageDto>() }, WatchdogJson.Options), StringComparison.Ordinal);
        Assert.Empty(JsonSerializer.Deserialize<UserMessageRequest>(json, WatchdogJson.Options)!.InlineImages);
    }

    [Fact]
    public void AttachmentsAreStillUnknownMembers_EvenNextToImages()
    {
        var json = JsonSerializer.Serialize(Request(), WatchdogJson.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserMessageRequest>(json[..^1] + ",\"attachments\":[]}", WatchdogJson.Options));
        // An image carries no unknown members either (no path, no disposition).
        var tampered = json.Replace("\"fileName\":", "\"path\":\"C:\\\\Temp\\\\x.png\",\"fileName\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserMessageRequest>(tampered, WatchdogJson.Options));
    }

    [Fact]
    public void TheNewStatusFields_RoundTrip()
    {
        var status = new UserMessagingStatusDto
        {
            Enabled = true,
            SmtpConfigured = true,
            Capabilities = [UserMessagingCapabilities.InlineImages],
            MaxInlineImages = UserMessageLimits.MaxInlineImages,
            MaxInlineImageBytes = UserMessageLimits.MaxInlineImageBytes,
            MaxInlineImagesTotalBytes = UserMessageLimits.MaxInlineImagesTotalBytes,
            MaxHtmlLength = UserMessageLimits.MaxHtmlLength,
            StoredImageBytes = 123_456,
            MaxStoredImageBytes = 256L * 1024 * 1024,
        };
        var json = JsonSerializer.Serialize(status, WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<UserMessagingStatusDto>(json, WatchdogJson.Options)!;

        Assert.Contains("\"capabilities\":[\"inlineImages\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"maxStoredImageBytes\":268435456", json, StringComparison.Ordinal);
        Assert.Equal(status.Capabilities, back.Capabilities);
        Assert.Equal(status with { Capabilities = back.Capabilities, AllowedSenders = back.AllowedSenders }, back);
    }

    [Fact]
    public void TheStatusOfAnOlderService_MeansNoImagesAndU1bHtml()
    {
        const string olderService = "{\"enabled\":true,\"smtpConfigured\":true,\"channel\":\"smtp.contoso.test:587\",\"maxMessagesPerHour\":300,\"maxMessagesPerDay\":2000,\"maxRecipients\":20}";
        var status = JsonSerializer.Deserialize<UserMessagingStatusDto>(olderService, WatchdogJson.Options)!;

        Assert.Empty(status.Capabilities);
        Assert.Equal((0, 0L, 0L), (status.MaxInlineImages, status.MaxInlineImageBytes, status.MaxInlineImagesTotalBytes));
        Assert.Equal(100_000, status.MaxHtmlLength);
        Assert.Equal((0L, 0L), (status.StoredImageBytes, status.MaxStoredImageBytes));
        Assert.True(status.Available);
    }

    [Fact]
    public void TheLimits_EqualAdManagers()
    {
        Assert.Equal(MessageHtmlLimits.MaxHtmlLength, UserMessageLimits.MaxHtmlLength);
        Assert.Equal(MessageImageLimits.MaxImagesPerMessage, UserMessageLimits.MaxInlineImages);
        Assert.Equal(MessageImageLimits.MaxImageBytes, UserMessageLimits.MaxInlineImageBytes);
        Assert.Equal(MessageImageLimits.MaxImagesBytesPerMessage, UserMessageLimits.MaxInlineImagesTotalBytes);
        Assert.Equal(MessageHtmlLimits.LegacyMaxHtmlLength, new UserMessagingStatusDto().MaxHtmlLength);
    }

    [Fact]
    public void TheIssueCodes_AreTheDocumentedOnes()
    {
        Assert.Equal(
            ["invalidImage", "missingImage", "unreferencedImage", "imageIdMismatch", "tooLarge", "imagesDisabled", "imageStorageFull"],
            [UserMessageIssueCodes.InvalidImage, UserMessageIssueCodes.MissingImage, UserMessageIssueCodes.UnreferencedImage, UserMessageIssueCodes.ImageIdMismatch,
             UserMessageIssueCodes.TooLarge, UserMessageIssueCodes.ImagesDisabled, UserMessageIssueCodes.ImageStorageFull]);
        Assert.Equal("smtp.messageTooLarge", DeliveryHints.MessageTooLarge);
    }

    [Fact]
    public void Configuration_AllowsImagesByDefault_WithA256MbCap_AndOlderFilesGetTheDefaults()
    {
        var defaults = new UserMessagingSettings();
        var old = JsonSerializer.Deserialize<WatchdogConfiguration>("{\"schemaVersion\":2,\"userMessages\":{\"enabled\":true}}", WatchdogJson.Options)!;
        var json = JsonSerializer.Serialize(new WatchdogConfiguration() with { UserMessages = new UserMessagingSettings { AllowInlineImages = false, MaxStoredImageMb = 64 } }, WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!;

        Assert.Equal((true, 256), (defaults.AllowInlineImages, defaults.MaxStoredImageMb));
        Assert.Equal((true, 256), (old.UserMessages.AllowInlineImages, old.UserMessages.MaxStoredImageMb));
        Assert.Equal((false, 64), (back.UserMessages.AllowInlineImages, back.UserMessages.MaxStoredImageMb));
        Assert.Contains("\"allowInlineImages\":false", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(16, false)]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public void Validation_ChecksTheStoredImageCap(int megabytes, bool rejected)
    {
        var validator = new ConfigurationValidator(TimeProvider.System);
        var configuration = SampleConfiguration.Create() with { UserMessages = new UserMessagingSettings { MaxStoredImageMb = megabytes } };

        var issues = validator.Validate(configuration, new AdMng.Watchdog.Core.Abstractions.ConfigValidationContext()).Issues;

        Assert.Equal(rejected, issues.Any(i => i.Path == "userMessages.maxStoredImageMb" && i.Code == ConfigValidationCodes.Range));
    }

    [Fact]
    public void SwitchingImagesOff_IsAMediumChange()
    {
        var auditor = new ConfigChangeAuditor(new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero)));
        var before = new DefaultConfigurationFactory().Create();

        var audit = auditor.Compare(before, before with { UserMessages = before.UserMessages with { AllowInlineImages = false, MaxStoredImageMb = 1024 } });

        Assert.Equal(Severity.Medium, audit.Severity);
        Assert.Equal(["userMessages.allowInlineImages", "userMessages.maxStoredImageMb"], audit.Items.Select(i => i.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheOutboxPayload_CarriesImagesWithoutPrintingThem()
    {
        var image = new RenderedInlineImage { ContentId = Logo.ContentId, MediaType = Logo.MediaType, FileName = Logo.FileName, Data = Logo.Data.ToArray() };
        Assert.Equal($"RenderedInlineImage({Logo.ContentId}, {Logo.Data.Length} B)", image.ToString());
        Assert.Empty(new RenderedEmail { Subject = "x", HtmlBody = "<p>y</p>", TextBody = "y" }.InlineImages);
    }

    [Fact]
    public async Task TheFakeClient_SupportsImages_AndAnswersLikeAnOlderServiceWithoutTheCapability()
    {
        var token = TestContext.Current.CancellationToken;
        using var client = new FakeWatchdogClient();
        client.UserMessagingStatus = client.UserMessagingStatus with { Enabled = true };
        Assert.Contains(UserMessagingCapabilities.InlineImages, client.UserMessagingStatus.Capabilities);
        Assert.Equal(UserMessageState.Delivered, (await client.SendUserMessageAsync(Request(), token)).State);

        client.UserMessagingStatus = FakeWatchdogClient.WithoutInlineImages(client.UserMessagingStatus);
        var older = await Assert.ThrowsAsync<WatchdogClientException>(() => client.SendUserMessageAsync(Request() with { Id = Guid.NewGuid() }, token));
        Assert.Equal((400, "invalidJson"), (older.StatusCode, Assert.Single(older.Issues).Code));

        client.RejectImagesAsOlderService = false;
        var disabled = await Assert.ThrowsAsync<WatchdogClientException>(() => client.SendUserMessageAsync(Request() with { Id = Guid.NewGuid() }, token));
        Assert.Equal(UserMessageIssueCodes.ImagesDisabled, Assert.Single(disabled.Issues).Code);

        // Without images an older service still delivers.
        Assert.Equal(UserMessageState.Delivered, (await client.SendUserMessageAsync(Request() with { Id = Guid.NewGuid(), InlineImages = [] }, token)).State);
    }
}
