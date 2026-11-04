using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Testing.Client;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// Inline images through AdMng Watchdog (U1c, design note section 9.6): the status mapped to capabilities (an older
/// service: no images, HTML 100 000), images sent only to a service that takes them, a service downgraded between status
/// and send (<c>invalidJson</c> → <c>watchdog.imagesNotSupported</c>), the stored-image cap; in the window the transport bar
/// with "send directly through SMTP" (this run only, the setting unchanged) and "send without images" (re-planned).
/// </summary>
public sealed class WatchdogUserMessageTransportImagesTests
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75, withMetadata: true), "logo.png");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutgoingUserMessage Message(params MessageImage[] images) => new()
    {
        Id = Guid.NewGuid(),
        From = new MessageAddress("helpdesk@contoso.test", "Dział IT"),
        To = [new MessageAddress("zofia.zak@contoso.test", "Zofia Żak")],
        Subject = "Instrukcja ze zrzutem – zażółć gęślą jaźń",
        TextBody = "Dzień dobry, Zofio!",
        HtmlBody = images.Length == 0 ? "<p>Dzień dobry</p>" : $"<p>Dzień dobry <img src=\"cid:{images[0].ContentId}\" alt=\"Logo\"></p>",
        InlineImages = images,
    };

    private static FakeWatchdogClient Enabled(bool images = true)
    {
        var client = new FakeWatchdogClient(sampleData: false);
        client.UserMessagingStatus = client.UserMessagingStatus with { Enabled = true };
        if (!images)
        {
            client.UserMessagingStatus = FakeWatchdogClient.WithoutInlineImages(client.UserMessagingStatus);
        }

        return client;
    }

    [Fact]
    public async Task TheStatus_BecomesCapabilities_AnOlderServiceTakesNoImages()
    {
        var current = await new WatchdogUserMessageTransport(Enabled()).GetStatusAsync(Ct);
        var older = await new WatchdogUserMessageTransport(Enabled(images: false)).GetStatusAsync(Ct);

        Assert.True(current.Capabilities.InlineImages);
        Assert.Equal((10, 1024L * 1024, 2L * 1024 * 1024, 400_000), (current.Capabilities.MaxInlineImages, current.Capabilities.MaxInlineImageBytes, current.Capabilities.MaxInlineImagesTotalBytes, current.Capabilities.MaxHtmlLength));
        Assert.Equal(256L * 1024 * 1024, current.Capabilities.ImageStorageAvailableBytes);
        Assert.False(older.Capabilities.InlineImages);
        Assert.Equal(100_000, older.Capabilities.MaxHtmlLength);
        Assert.Null(older.Capabilities.ImageStorageAvailableBytes);
        Assert.Equal(UserMessageTransportCapabilities.Legacy, WatchdogUserMessageTransport.CapabilitiesOf(new UserMessagingStatusDto()));
        Assert.Equal(
            1000L,
            WatchdogUserMessageTransport.CapabilitiesOf(Enabled().UserMessagingStatus with { StoredImageBytes = 256L * 1024 * 1024 - 1000 }).ImageStorageAvailableBytes);
    }

    [Fact]
    public async Task Images_GoOnlyToAServiceThatTakesThem()
    {
        var client = Enabled();
        await using (var connection = await new WatchdogUserMessageTransport(client).ConnectAsync(Ct))
        {
            Assert.True((await connection.SendAsync(Message(Logo), Ct)).IsAccepted);
            Assert.True((await connection.SendAsync(Message(), Ct)).IsAccepted);
        }

        var image = Assert.Single(client.UserMessages[0].InlineImages);
        Assert.Equal((Logo.ContentId, "image/png", Logo.Data), (image.ContentId, image.MediaType, image.Data));
        Assert.Empty(client.UserMessages[1].InlineImages);
        Assert.Empty(WatchdogUserMessageTransport.ToRequest(Message(Logo), withImages: false).InlineImages);

        var older = Enabled(images: false);
        await using var oldConnection = await new WatchdogUserMessageTransport(older).ConnectAsync(Ct);
        var refused = await oldConnection.SendAsync(Message(Logo), Ct);
        Assert.Equal((UserMessageDeliveryOutcome.TransportFailed, WatchdogUserMessageTransport.ImagesNotSupportedCode), (refused.Outcome, refused.Code));
        Assert.True(refused.StopsRun);
        Assert.Empty(older.UserMessages);
        Assert.True((await oldConnection.SendAsync(Message(), Ct)).IsAccepted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AServiceDowngradedBetweenStatusAndSend_StopsTheRun_NeverRetriesWithoutImages(bool olderService)
    {
        var client = Enabled();
        await using var connection = await new WatchdogUserMessageTransport(client).ConnectAsync(Ct);
        client.UserMessagingStatus = FakeWatchdogClient.WithoutInlineImages(client.UserMessagingStatus);
        client.RejectImagesAsOlderService = olderService;

        var delivery = await connection.SendAsync(Message(Logo), Ct);

        Assert.Equal((UserMessageDeliveryOutcome.TransportFailed, WatchdogUserMessageTransport.ImagesNotSupportedCode), (delivery.Outcome, delivery.Code));
        Assert.Single(client.UserMessages);
        Assert.Equal(
            (UserMessageDeliveryOutcome.Rejected, "watchdog.invalid"),
            (WatchdogUserMessageTransport.Classify(Invalid("invalidJson")).Outcome, WatchdogUserMessageTransport.Classify(Invalid("invalidJson")).Code));
    }

    [Fact]
    public void TheStoredImageCap_StopsTheRun()
    {
        var full = new WatchdogClientException(WatchdogClientErrorKind.Validation, "full")
        {
            StatusCode = 429,
            ProblemType = ApiProblem.RateLimited,
            Issues = [new ConfigIssue { Path = "userMessages.maxStoredImageMb", Code = UserMessageIssueCodes.ImageStorageFull, Severity = IssueSeverity.Error, Message = "full" }],
        };

        var delivery = WatchdogUserMessageTransport.Classify(full, hadImages: true);

        Assert.Equal((UserMessageDeliveryOutcome.TransportFailed, WatchdogUserMessageTransport.ImageStorageCode), (delivery.Outcome, delivery.Code));
        Assert.Contains(WatchdogUserMessageTransport.ImageStorageCode, UserMessageText.KnownCodes);
        Assert.Contains(WatchdogUserMessageTransport.ImagesNotSupportedCode, UserMessageText.KnownCodes);
    }

    [Fact]
    public async Task Window_WithAnOlderService_ShowsTheBar_AndSendsDirectlyForThisRunOnly()
    {
        var (scenario, composer) = await OpenWithLogoAsync(watchdogImages: false);

        await composer.SendCommand.ExecuteAsync(null);

        Assert.True(composer.IsTransportFitVisible);
        Assert.StartsWith("AdMng Watchdog does not take images", composer.TransportFitText, StringComparison.Ordinal);
        Assert.True(composer.CanSendDirectForRun);
        Assert.True(composer.CanSendWithoutImages);
        Assert.Empty(scenario.Watchdog.UserMessages);
        Assert.Empty(scenario.Transport.Attempts);

        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendDirectForRunCommand.ExecuteAsync(null);

        var sent = Assert.Single(scenario.Transport.Accepted);
        Assert.Equal([Logo.ContentId], sent.InlineImages.Select(i => i.ContentId));
        Assert.Empty(scenario.Watchdog.UserMessages);
        Assert.Equal(UserMessageTransportChoice.Watchdog, scenario.Harness.Settings.Current.UserMessages.Transport);
        Assert.False(composer.IsTransportFitVisible);
        var entry = Assert.Single(scenario.Harness.AuditLog.Entries, e => e.Action == UserMessageComposerViewModel.SessionLogAction);
        Assert.EndsWith("(directly through SMTP)", entry.OldValue, StringComparison.Ordinal);
        Assert.Equal("smtp.contoso.test:587", entry.Server);

        // A new plan ends the choice: the Watchdog is the transport again.
        await composer.RetryFailedCommand.ExecuteAsync(null);
        Assert.StartsWith("Sending through AdMng Watchdog", composer.TransportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Window_SendWithoutImages_RePlans_AndTheServiceGetsNoImages()
    {
        var (scenario, composer) = await OpenWithLogoAsync(watchdogImages: false);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.True(composer.IsTransportFitVisible);

        await composer.SendWithoutImagesCommand.ExecuteAsync(null);

        Assert.False(composer.IsTransportFitVisible);
        Assert.True(composer.IsWithoutImages);
        var item = Item(composer, "Jan Kowalski");
        Assert.Empty(item.InlineImages);
        Assert.Contains("<p>[Logo – Dział IT]</p>", item.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(item.Planned.Issues, i => i.Code == HtmlIssueCodes.ImagesOmitted);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(scenario.Watchdog.UserMessages);
        Assert.Empty(sent.InlineImages);
        Assert.DoesNotContain("cid:", sent.HtmlBody, StringComparison.Ordinal);
        Assert.EndsWith("(without images)", Assert.Single(scenario.Harness.AuditLog.Entries, e => e.Action == UserMessageComposerViewModel.SessionLogAction).OldValue, StringComparison.Ordinal);

        // Back in the editor the choice is gone.
        composer.BackCommand.Execute(null);
        Assert.False(composer.IsWithoutImages);
    }

    [Fact]
    public async Task Window_WithACurrentService_SendsTheImagesThroughIt()
    {
        var (scenario, composer) = await OpenWithLogoAsync(watchdogImages: true);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        Assert.False(composer.IsTransportFitVisible);
        var sent = Assert.Single(scenario.Watchdog.UserMessages);
        Assert.Equal([Logo.ContentId], sent.InlineImages.Select(i => i.ContentId));
        Assert.Contains($"cid:{Logo.ContentId}", sent.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(UserMessageReportStatus.Accepted, Item(composer, "Jan Kowalski").ReportStatus);
    }

    [Fact]
    public async Task Window_TheTestMessage_IsCheckedToo()
    {
        var (scenario, composer) = await OpenWithLogoAsync(watchdogImages: false);
        composer.SelectedItem = Item(composer, "Jan Kowalski");

        await composer.SendTestCommand.ExecuteAsync(null);

        Assert.True(composer.IsTransportFitVisible);
        Assert.Empty(scenario.Watchdog.UserMessages);
        await composer.SendDirectForRunCommand.ExecuteAsync(null);
        var test = Assert.Single(scenario.Transport.Accepted);
        Assert.StartsWith("[TEST]", test.Subject, StringComparison.Ordinal);
        Assert.Single(test.InlineImages);
    }

    /// <summary>A window for Jan Kowalski with the Watchdog as transport and a pasted HTML body with one image (a data: URI).</summary>
    private static async Task<(MessageScenario Scenario, UserMessageComposerViewModel Composer)> OpenWithLogoAsync(bool watchdogImages)
    {
        var scenario = await CreateAsync();
        scenario.Watchdog.UserMessagingStatus = scenario.Watchdog.UserMessagingStatus with { Enabled = true };
        if (!watchdogImages)
        {
            scenario.Watchdog.UserMessagingStatus = FakeWatchdogClient.WithoutInlineImages(scenario.Watchdog.UserMessagingStatus);
        }

        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.Transport = UserMessageTransportChoice.Watchdog, Ct);
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(
            CfHtmlFixture.Build($"<html><body><!--StartFragment--><p>Dzień dobry {{givenName}},</p><p><img src=\"{MessageImageSamples.DataUri(Logo.Data, "image/png")}\" alt=\"Logo – Dział IT\"></p><!--EndFragment--></body></html>"),
            null,
            null,
            null);
        if (!string.IsNullOrWhiteSpace(composer.Html))
        {
            scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        }

        await composer.PasteFormattedCommand.ExecuteAsync(null);
        Assert.Single(composer.TemplateImages);
        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.True(Item(composer, "Jan Kowalski").HasInlineImages, composer.Error);
        return (scenario, composer);
    }

    private static WatchdogClientException Invalid(string code) => new(WatchdogClientErrorKind.Validation, "invalid")
    {
        StatusCode = 400,
        ProblemType = ApiProblem.Validation,
        Issues = [new ConfigIssue { Path = "$", Code = code, Severity = IssueSeverity.Error, Message = code }],
    };
}
