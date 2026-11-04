using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AdMng.App.UiTests;

/// <summary>
/// Formatted messages in the message window (U1c, design note u1c-u1d sections 2, 10 and 12): the Outlook fixture as raw
/// CF_HTML bytes on the real clipboard with its images in an <c>msohtmlclip&lt;n&gt;\01</c> folder of the temp folder, pasted through
/// <see cref="ClipboardHtmlReader"/> – the image strip, the paste results with "Zezwól na domenę …", the formatting preview
/// (forced to the HTML source); the rendered "Widok" tab of a planned message (WebView2 where the runtime exists, else the
/// source); the transport bar. No binding errors.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class UserMessagesRichHtmlWindowTests
{
    [Fact]
    public Task PasteFromTheClipboard_FillsTheStripAndTheResults_ThePreviewFallsBackToTheSource() => WpfTestHost.RunAsync(async () =>
    {
        using var clip = new OfficeClipFolder();
        clip.Add("clip_image001.png", MessageImageSamples.Png(9, 3, seed: 0x01, withMetadata: true));
        clip.Add("clip_image002.png", MessageImageSamples.Png(12, 4, seed: 0x02, withMetadata: true));
        clip.Add("clip_image003.jpg", MessageImageSamples.Jpeg(20, 10, withMetadata: true));
        var (context, dialogs) = await TestDialogService.CreateContextAsync(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(new FakeUserMessageTransport()));
            services.Replace(ServiceDescriptor.Singleton(sp => new WebView2Host(sp.GetRequiredService<ILogger<WebView2Host>>()) { IsDisabled = true }));
        });
        await using var _ = context;
        await context.ConnectAsync();
        var window = await OpenAsync(context, BuiltInMessageTemplates.Custom);
        var composer = window.ViewModel;
        Assert.Contains(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.V && b.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                                                                      && ReferenceEquals(b.Command, composer.PasteFormattedCommand));

        ClipboardFixture.Put(CfHtmlFixture.Build(clip.OutlookHtml()));
        dialogs.ConfirmAnswer = true;
        await composer.PasteFormattedCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !composer.IsPasting, "the paste");
        await UiTestSupport.WaitForIdleAsync();
        // clip_image001.png is Outlook's VML original – only its <img> fallback (clip_image002.png) and the photo are taken.
        Assert.True(composer.TemplateImages.Count == 2, $"{composer.TemplateImages.Count} images pasted; {composer.RichStatusText}; "
                                                        + string.Join(" | ", composer.PasteProblems.Select(p => p.Text)));
        await UiWait.UntilAsync(() => window.TemplateImagesList.Items.Count == 2, "the two pasted images");

        // The images were read once from the temp folder: content ids in the HTML, thumbnails, names and sizes in the strip.
        Assert.Equal(2, composer.TemplateImages.Count);
        Assert.All(composer.TemplateImages, image => Assert.Contains($"cid:{image.ContentId}", window.HtmlBox.Text, StringComparison.Ordinal));
        Assert.DoesNotContain("file:", window.HtmlBox.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Szanowni Państwo,", window.HtmlBox.Text, StringComparison.Ordinal);
        Assert.Contains(UiWait.Descendants<Image>(window.TemplateImagesList), image => image.Source is not null);
        Assert.Contains(UiWait.Descendants<TextBlock>(window.TemplateImagesList), t => t.Text == "clip_image002.png");
        Assert.False(string.IsNullOrWhiteSpace(composer.Body));
        Assert.Equal(composer.RichStatusText, window.RichStatusTextBlock.Text);

        // The paste results: links outside the allowed domains, with "Allow domain …".
        Assert.True(window.PasteProblemsList.Items.Count > 0);
        var allow = UiWait.Descendants<Button>(window.PasteProblemsList).First(b => Equals(b.Content, "Allow domain contoso.pl"));
        allow.Command.Execute(allow.CommandParameter);
        await UiWait.UntilAsync(
            () => context.Services.GetRequiredService<ISettingsStore>().Current.UserMessages.AllowedLinkDomains.Contains("contoso.pl"),
            "the allowed domain in the settings");

        // The formatting preview, forced to the source view: the HTML exactly as edited, with the note.
        window.HtmlPreviewToggleButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(composer.ShowHtmlPreview);
        Assert.Equal(Visibility.Collapsed, window.HtmlBox.Visibility);
        Assert.Equal(Visibility.Visible, window.ComposePreviewView.Visibility);
        Assert.True(window.ComposePreviewView.IsShowingSource);
        Assert.Equal(composer.Html, window.ComposePreviewView.SourceBox.Text);
        Assert.Equal("Source", window.HtmlPreviewToggleButton.Content);

        // "Remove" in the strip takes the image and its <img> away.
        var first = composer.TemplateImages[0];
        var remove = UiWait.Descendants<Button>(window.TemplateImagesList).First(b => ReferenceEquals(b.CommandParameter, first));
        remove.Command.Execute(remove.CommandParameter);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(window.TemplateImagesList.Items);
        Assert.DoesNotContain($"cid:{first.ContentId}", composer.Html, StringComparison.Ordinal);
        context.AssertHealthy();
    });

    [Fact]
    public Task ThePlannedMessage_IsRenderedInTheViewTab_OrShownAsSourceWithoutTheRuntime() => WpfTestHost.RunAsync(async () =>
    {
        var logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75), "logo.png");
        var (context, _) = await TestDialogService.CreateContextAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(new FakeUserMessageTransport())));
        await using var _ = context;
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.UserMessages.Smtp.Host = "smtp.contoso.test", TestContext.Current.CancellationToken);
        var window = await OpenAsync(context, BuiltInMessageTemplates.Custom);
        var composer = window.ViewModel;
        composer.Subject = "Instrukcja zmiany hasła – {givenName}";
        composer.Body = "Dzień dobry {givenName},\nprosimy o zmianę hasła.";
        ClipboardFixture.Put(CfHtmlFixture.Build(
            $"<html><body><!--StartFragment--><p style=\"font-family:'Aptos',sans-serif\">Dzień dobry <b>{{givenName}}</b>,</p>"
            + $"<p><img src=\"{MessageImageSamples.DataUri(logo.Data, "image/png")}\" alt=\"Logo – Dział IT\"></p><!--EndFragment--></body></html>"));
        await composer.PasteFormattedCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => composer.TemplateImages.Count == 1, "the pasted logo");

        await composer.PlanCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => window.ItemsList.Items.Count > 0, "the planned messages");
        var item = composer.AllItems.First(i => i.HasInlineImages);
        window.ItemsList.SelectedItem = item;
        window.MessageTabs.SelectedItem = window.MessageViewTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(item.InlineImagesText, window.SelectedImagesText.Text);
        Assert.Equal(Visibility.Visible, window.SelectedImagesText.Visibility);

        // WebView2 where the runtime is installed (rendered from memory), otherwise the HTML source with the download link.
        var host = context.Services.GetRequiredService<WebView2Host>();
        var environment = await host.GetEnvironmentAsync();
        var view = window.MessagePreviewView;
        await UiWait.UntilAsync(() => view.IsRendered || view.IsShowingSource, "the rendered preview or its fallback", timeoutSeconds: 60);
        if (environment is null)
        {
            Assert.True(view.IsShowingSource);
            Assert.Equal(item.HtmlBody, view.SourceBox.Text);
        }
        else
        {
            Assert.True(view.IsRendered, "WebView2 did not render the preview.");
            Assert.False(view.IsShowingSource);
            Assert.NotNull(host.UserDataFolder);
            Assert.StartsWith(Path.GetTempPath(), host.UserDataFolder, StringComparison.OrdinalIgnoreCase);
        }

        // The text and the source tabs show the message as it is sent.
        window.MessageTabs.SelectedItem = window.MessageSourceTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(item.HtmlBody, window.SelectedHtmlBody.Text);
        Assert.Contains($"cid:{logo.ContentId}", window.SelectedHtmlBody.Text, StringComparison.Ordinal);
        window.MessageTabs.SelectedItem = window.MessageTextTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains("Dzień dobry Jan,", window.SelectedTextBody.Text, StringComparison.Ordinal);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTransportBar_StopsASendTheTransportCannotTake() => WpfTestHost.RunAsync(async () =>
    {
        // A transport that reports itself as an older AdMng Watchdog (no images).
        var transport = new FakeUserMessageTransport(UserMessageTransportKind.Watchdog, "admng-wd.contoso.com")
        {
            Status = UserMessageTransportStatus.Available with { Capabilities = UserMessageTransportCapabilities.Legacy },
        };
        var logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x76), "logo.png");
        var (context, dialogs) = await TestDialogService.CreateContextAsync(language: "pl", configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(transport)));
        await using var _ = context;
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.UserMessages.Smtp.Host = "smtp.contoso.test", TestContext.Current.CancellationToken);
        var window = await OpenAsync(context, BuiltInMessageTemplates.Custom);
        var composer = window.ViewModel;
        composer.Subject = "Instrukcja – {givenName}";
        composer.Body = "Dzień dobry {givenName},\nw załączeniu instrukcja.";
        ClipboardFixture.Put(CfHtmlFixture.Build(
            $"<html><body><!--StartFragment--><p>Dzień dobry {{givenName}},</p><p><img src=\"{MessageImageSamples.DataUri(logo.Data, "image/png")}\" alt=\"Logo – Dział IT\"></p><!--EndFragment--></body></html>"));
        dialogs.ConfirmAnswer = true;
        await composer.PasteFormattedCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => composer.TemplateImages.Count == 1, "the pasted logo");
        await composer.PlanCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => window.ItemsList.Items.Count > 0, "the planned messages");

        await composer.SendCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(Visibility.Visible, window.TransportFitBar.Visibility);
        Assert.StartsWith("AdMng Watchdog nie przyjmuje obrazów", window.TransportFitTextBlock.Text, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, window.SendDirectForRunButton.Visibility);
        Assert.Equal(Visibility.Visible, window.SendWithoutImagesButton.Visibility);
        Assert.Equal("Wyślij bez obrazów", window.SendWithoutImagesButton.Content);
        Assert.Empty(transport.Attempts);

        window.DismissTransportFitButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Collapsed, window.TransportFitBar.Visibility);
        Assert.Empty(transport.Attempts);
        context.AssertHealthy();
    });

    /// <summary>
    /// The App's image source (8.3 names expanded through <see cref="ShortPathNames"/>) reads an Office clipboard image of
    /// the real temp folder; on failure the message tells which step of the path check refused it.
    /// </summary>
    [Fact]
    public async Task TheAppsImageSource_ReadsAnOfficeClipboardImage_OfTheTempFolder()
    {
        using var clip = new OfficeClipFolder();
        var png = MessageImageSamples.Png(9, 3, seed: 0x05);
        clip.Add("clip_image001.png", png);
        var uri = new Uri(Path.Combine(clip.Folder, "clip_image001.png")).AbsoluteUri;
        var source = new PastedImageFileSource(expandShortNames: ShortPathNames.Expand);

        var read = await source.ReadAsync(uri, MessageImageLimits.MaxImageBytes, TestContext.Current.CancellationToken);

        Assert.True(read.Failure is null && read.Data is not null, $"{read.Failure}: {PathDiagnostics(uri)}");
        Assert.Equal(png, read.Data);
        Assert.Equal("clip_image001.png", read.FileName);
    }

    /// <summary>What the image source sees on the way to <paramref name="uri"/> (the CI runner's paths only).</summary>
    private static string PathDiagnostics(string uri)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var local = ClipboardImagePolicy.LocalPath(uri, windows: true);
        var lines = new List<string>
        {
            $"uri={uri}",
            $"local={local}",
            $"temp={temp}",
            $"temp expanded={ShortPathNames.Expand(temp)}",
            $"local expanded={(local is null ? null : ShortPathNames.Expand(local))}",
            $"drive={new DriveInfo(temp[..1]).DriveType}",
        };
        foreach (var path in new[] { ShortPathNames.Expand(temp), local is null ? null : ShortPathNames.Expand(local) })
        {
            if (path is null)
            {
                continue;
            }

            var current = Path.GetPathRoot(path)!;
            foreach (var segment in path[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
                lines.Add($"{current}: exists={info.Exists} attributes={(info.Exists ? info.Attributes : 0)} link={info.LinkTarget}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The message window for Jan Kowalski with <paramref name="templateId"/>, the HTML section expanded.</summary>
    private static async Task<UserMessagesWindow> OpenAsync(UiTestContext context, string templateId)
    {
        var composer = context.Services.GetRequiredService<IDialogViewModelFactory>()
            .CreateUserMessageComposer(new UserMessageRequest([context.Company.JanKowalski], templateId));
        var window = (UserMessagesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(composer);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        window.HtmlExpander.IsExpanded = true;
        await UiTestSupport.WaitForIdleAsync();
        return window;
    }
}

/// <summary>Puts raw clipboard formats (CF_HTML bytes, PNG) on the real clipboard, retrying while another process holds it.</summary>
internal static class ClipboardFixture
{
    public static void Put(byte[] cfHtml, byte[]? png = null)
    {
        var data = new DataObject();
        data.SetData(ClipboardHtmlReader.HtmlFormat, new MemoryStream(cfHtml));
        if (png is not null)
        {
            data.SetData(ClipboardHtmlReader.PngFormat, new MemoryStream(png));
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (ExternalException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }
}
