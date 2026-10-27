using System.IO;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AdMng.App.UiTests;

/// <summary>
/// Release screenshot of formatted messages (U1c): a message pasted from Outlook – the HTML section with its source, the
/// strip of the three embedded images and the paste results with "Zezwól na domenę …". The rendered WebView2 preview is not
/// in the picture (a native window is not part of a WPF render), so the source is shown. Like <see cref="ScreenshotTests"/>
/// it runs only when <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name starts with "ScreenshotTests"
/// so the CI step "Screenshots" takes it too. Number 24: 22 and 23 belong to track K, 25 to U1d (design note u1c-u1d 22.4).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsRichHtml
{
    private static readonly ScreenshotInfo RichHtml = new(
        "24-user-messages-rich-html.png",
        "Wiadomości do użytkowników – wiadomość wklejona z Outlooka: treść HTML, osadzone obrazy i wyniki wklejania z akcją „Zezwól na domenę”",
        "Messages to users – a message pasted from Outlook: the HTML content, the embedded images and the paste results with \"Allow domain\"");

    [Fact]
    public Task Screenshot24_UserMessagesRichHtml()
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "msohtmlclip1", "admng-shot-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "clip_image001.png"), MessageImageSamples.Png(120, 40, seed: 0x31));
            File.WriteAllBytes(Path.Combine(folder, "clip_image002.png"), MessageImageSamples.Png(200, 100, seed: 0x32));
            File.WriteAllBytes(Path.Combine(folder, "clip_image003.jpg"), MessageImageSamples.Jpeg(160, 90));
            var html = CfHtmlFixture.Html("outlook-classic").Replace(
                "file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/", new Uri(folder + Path.DirectorySeparatorChar).AbsoluteUri, StringComparison.Ordinal);
            var clipboard = new ContentClipboard(new ClipboardContent(CfHtmlFixture.Build(html), null, null, null));
            var (context, dialogs) = await TestDialogService.CreateContextAsync("pl", services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(new FakeUserMessageTransport()));
                services.Replace(ServiceDescriptor.Scoped<IClipboardService>(_ => clipboard));
                services.Replace(ServiceDescriptor.Singleton(sp => new WebView2Host(sp.GetRequiredService<ILogger<WebView2Host>>()) { IsDisabled = true }));
            });
            await using var _ = context;
            await context.ConnectAsync();
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(
                    s => s.UserMessages.Smtp.Host = "smtp.contoso.com", TestContext.Current.CancellationToken);
                var composer = context.Services.GetRequiredService<IDialogViewModelFactory>()
                    .CreateUserMessageComposer(new UserMessageRequest([context.Company.JanKowalski, context.Company.ZanetaSzymanska], BuiltInMessageTemplates.Custom));
                composer.TemplateName = "Zmiana hasła – instrukcja z Outlooka";
                composer.Subject = "Hasło do konta {sAMAccountName} wkrótce wygaśnie";
                var window = (UserMessagesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(composer);
                await ScreenshotWriter.ShowAsync(window);
                await UiWait.InitializedAsync(window.Initialization);
                window.HtmlExpander.IsExpanded = true;
                dialogs.ConfirmAnswer = true;
                await composer.PasteFormattedCommand.ExecuteAsync(null);
                await UiWait.UntilAsync(() => window.TemplateImagesList.Items.Count == 3, "the pasted images");
                await UiTestSupport.WaitForIdleAsync(rounds: 3);
                window.HtmlExpander.BringIntoView();
                await UiTestSupport.WaitForIdleAsync(rounds: 3);

                await ScreenshotWriter.SaveAsync(window, RichHtml, dark: false);
                context.AssertHealthy();
            }
            finally
            {
                UiTestSupport.CloseAllWindows();
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                themes.Apply(ThemePreference.System);
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                try
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                    // Cleaned by Windows with the temp folder.
                }
            }
        });
    }

    /// <summary>A clipboard that holds <paramref name="content"/> (no real clipboard in the picture's way).</summary>
    private sealed class ContentClipboard(ClipboardContent content) : IClipboardService
    {
        public ClipboardContent? ReadFormatted() => content;

        public bool SetText(string text) => true;

        public bool SetSecret(string secret) => true;
    }
}
