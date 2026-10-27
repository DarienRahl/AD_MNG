using System.Text;
using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.UserMessages;
using AdMng.Testing.RichHtml;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// "Wklej sformatowaną treść" (U1c, design note sections 2, 3, 7.1 and 12): the workflow over a fake clipboard with the
/// Core fixtures and a temp-folder image source (only local files in the temp folder are read), a bitmap or text alone;
/// in the window: replace only after confirmation, images pooled and deduplicated, the text body filled, the paste results
/// with "Zezwól na domenę …" (the domain lands in the settings) and "Usuń łącze", removing an image, the text from the HTML
/// and a saved template keeping its images.
/// </summary>
public sealed class HtmlPasteWorkflowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "admng-paste-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "admng-outside-" + Guid.NewGuid().ToString("N"));

    public HtmlPasteWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "msohtmlclip1", "01"));
        Directory.CreateDirectory(_outside);
        File.WriteAllBytes(Clip("clip_image001.png"), MessageImageSamples.Png(9, 3, seed: 0x01, withMetadata: true));
        File.WriteAllBytes(Clip("clip_image002.png"), MessageImageSamples.Png(12, 4, seed: 0x02, withMetadata: true));
        File.WriteAllBytes(Clip("clip_image003.jpg"), MessageImageSamples.Jpeg(20, 10, withMetadata: true));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var folder in new[] { _temp, _outside })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private string Clip(string name) => Path.Combine(_temp, "msohtmlclip1", "01", name);

    private PastedImageFileSource Files() => new(_temp, windows: false);

    /// <summary>The Outlook fixture with its images in this test's temp folder.</summary>
    private string OutlookHtml() =>
        CfHtmlFixture.Html("outlook-classic").Replace("file:///C:/Users/JKOWAL~1/AppData/Local/Temp/", "file://" + _temp + "/", StringComparison.Ordinal);

    private static ClipboardContent Formatted(string html) => new(CfHtmlFixture.Build(html), null, null, null);

    // ---- The workflow -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOutlookFixture_IsImported_WithItsImagesReadFromTheTempFolder()
    {
        var clipboard = new FakeClipboard { Formatted = Formatted(OutlookHtml()) };
        var workflow = new HtmlPasteWorkflow(clipboard, Files());

        var result = await workflow.ImportAsync(workflow.ReadClipboard(), ["contoso.pl"], [], Ct);

        Assert.Equal(HtmlPasteKind.Html, result.Kind);
        Assert.NotEmpty(result.Images);
        var stripped = new[] { "clip_image001.png", "clip_image002.png", "clip_image003.jpg" }
            .ToDictionary(n => MessageImage.Create(File.ReadAllBytes(Clip(n)), n).Sha256, n => n);
        Assert.All(result.Images, image =>
        {
            // Every image is a file of the temp folder, stripped of its metadata, under its base name.
            Assert.Equal(stripped[image.Sha256], image.FileName);
            Assert.Contains($"cid:{image.ContentId}", result.Html, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("file:", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(_temp, result.Html, StringComparison.Ordinal);
        Assert.Contains("Szanowni Państwo", result.SuggestedText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Problems, p => p.Kind == HtmlImportProblemKind.ImageUnreadable);
    }

    [Fact]
    public async Task TheFileSource_ReadsOnlyLocalFilesInsideTheTempFolder()
    {
        var files = Files();
        var inside = await files.ReadAsync("file://" + Clip("clip_image001.png"), MessageImageLimits.MaxImageBytes, Ct);
        Assert.Null(inside.Failure);
        Assert.Equal("clip_image001.png", inside.FileName);
        Assert.Equal(await File.ReadAllBytesAsync(Clip("clip_image001.png"), Ct), inside.Data);

        var elsewhere = Path.Combine(_outside, "clip_image001.png");
        await File.WriteAllBytesAsync(elsewhere, MessageImageSamples.Png(), Ct);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + elsewhere, 1000, Ct)).Failure);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + _temp + "/../" + Path.GetFileName(_outside) + "/clip_image001.png", 1000, Ct)).Failure);

        // Another program's picture in the temp folder (HTML copied from a web page may name it) is never read.
        var other = Path.Combine(_temp, "zrzut-ekranu-innej-aplikacji.png");
        await File.WriteAllBytesAsync(other, MessageImageSamples.Png(), Ct);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + other, 1000, Ct)).Failure);
        var renamed = Clip("logo-działu.png");
        await File.WriteAllBytesAsync(renamed, MessageImageSamples.Png(), Ct);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + renamed, 1000, Ct)).Failure);
        var nested = Path.Combine(_temp, "inna", "msohtmlclip1", "01");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "clip_image001.png"), MessageImageSamples.Png(), Ct);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + Path.Combine(nested, "clip_image001.png"), 1000, Ct)).Failure);

        // A link inside the temp folder to a file elsewhere is refused (no reparse point on the way).
        var link = Path.Combine(_temp, "msohtmlclip1", "01", "clip_image077.png");
        File.CreateSymbolicLink(link, elsewhere);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + link, 1000, Ct)).Failure);
        var folderLink = Path.Combine(_temp, "msohtmlclip2");
        Directory.CreateSymbolicLink(folderLink, _outside);
        Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync("file://" + folderLink + "/clip_image001.png", 1000, Ct)).Failure);

        // Network paths and hosts are never opened; missing and large files are reported.
        foreach (var uri in new[] { "file://serwer/udział/clip_image001.png", "file:////serwer/udział/clip_image001.png", "https://contoso.pl/logo.png", "C:\\Temp\\a.png" })
        {
            Assert.Equal(PastedImageFailure.NotAllowed, (await files.ReadAsync(uri, 1000, Ct)).Failure);
        }

        Assert.Equal(PastedImageFailure.NotFound, (await files.ReadAsync("file://" + Clip("clip_image009.png"), 1000, Ct)).Failure);
        Assert.Equal(PastedImageFailure.TooLarge, (await files.ReadAsync("file://" + Clip("clip_image003.jpg"), 10, Ct)).Failure);
        Assert.Equal(PastedImageFailure.NotAllowed, (await new PastedImageFileSource(_temp, windows: true).ReadAsync("file://serwer/udział/a.png", 1000, Ct)).Failure);
    }

    [Fact]
    public async Task ABitmapAlone_IsOneImage_TextAlone_IsParagraphs_NothingIsNothing()
    {
        var png = MessageImageSamples.Png(8, 8, seed: 0x31, withMetadata: true);
        var workflow = new HtmlPasteWorkflow(new FakeClipboard(), Files());

        var image = await workflow.ImportAsync(new ClipboardContent(null, null, png, null), [], [], Ct);
        var text = await workflow.ImportAsync(new ClipboardContent(null, null, null, "Dzień dobry,\n\nzażółć gęślą jaźń"), [], [], Ct);
        var svg = await workflow.ImportAsync(new ClipboardContent(null, null, MessageImageSamples.Svg, null), [], [], Ct);

        Assert.Equal(HtmlPasteKind.Image, image.Kind);
        var single = Assert.Single(image.Images);
        Assert.Equal($"<p><img src=\"cid:{single.ContentId}\" alt=\"obraz1\" width=\"8\" height=\"8\"></p>\n", image.Html);
        Assert.Equal((HtmlPasteKind.Text, "<p>Dzień dobry,</p>\n<p>zażółć gęślą jaźń</p>\n"), (text.Kind, text.Html));
        Assert.Equal(HtmlPasteKind.Image, svg.Kind);
        Assert.Empty(svg.Images);
        Assert.Equal(HtmlImportProblemKind.ImageRejected, Assert.Single(svg.Problems).Kind);
        Assert.Same(HtmlPasteResult.Nothing, await workflow.ImportAsync(null, [], [], Ct));
        Assert.Same(HtmlPasteResult.Nothing, await workflow.ImportAsync(new ClipboardContent(null, "", null, ""), [], [], Ct));
        Assert.Same(HtmlPasteResult.TooLarge, await workflow.ImportAsync(new ClipboardContent(new byte[CfHtmlParser.MaxBytes + 1], null, null, null), [], [], Ct));
    }

    [Fact]
    public async Task TheStringOfThePlatform_IsParsedInTextMode()
    {
        var workflow = new HtmlPasteWorkflow(new FakeClipboard(), null);
        var html = Encoding.UTF8.GetString(CfHtmlFixture.Build("<html><body><!--StartFragment--><p>Łódź <b>pogrubiona</b></p><!--EndFragment--></body></html>"));

        var result = await workflow.ImportAsync(new ClipboardContent(null, html, null, null), [], [], Ct);

        Assert.Equal("<p>Łódź <b>pogrubiona</b></p>\n", result.Html);
    }

    // ---- In the window --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Window_PasteAsksBeforeReplacing_FillsAnEmptyTextBody_AndListsTheResults()
    {
        using var rich = await RichScenario.CreateAsync(Files());
        var composer = await rich.OpenAsync();
        composer.Html = "<p>Stara treść</p>";
        composer.Body = string.Empty;
        rich.Clipboard.Formatted = Formatted(OutlookHtml());

        rich.Dialogs.ConfirmAnswers.Enqueue(false);
        await composer.PasteFormattedCommand.ExecuteAsync(null);
        Assert.Equal("<p>Stara treść</p>", composer.Html);

        rich.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.PasteFormattedCommand.ExecuteAsync(null);

        Assert.Contains("Szanowni Państwo", composer.Html, StringComparison.Ordinal);
        Assert.Contains("Szanowni Państwo", composer.Body, StringComparison.Ordinal);
        Assert.NotEmpty(composer.TemplateImages);
        Assert.True(composer.HasPasteProblems);
        Assert.Contains(composer.PasteProblems, p => p.Kind == HtmlImportProblemKind.LinkUnwrapped);
        Assert.False(composer.IsPasting);
        Assert.StartsWith("Pasted:", composer.RichStatusText, StringComparison.Ordinal);
        Assert.Null(composer.TemplateErrors is { } errors && errors.Contains("references an image", StringComparison.Ordinal) ? errors : null);
    }

    [Fact]
    public async Task Window_ImagesArePooled_AndDeduplicated_AcrossLanguages()
    {
        using var rich = await RichScenario.CreateAsync(Files());
        var composer = await rich.OpenAsync();
        rich.Clipboard.Formatted = Formatted(OutlookHtml());
        await PasteAsync(rich, composer);
        var count = composer.TemplateImages.Count;

        composer.EditLanguage = composer.Languages.Single(l => l.Value == MessageLanguage.English);
        await PasteAsync(rich, composer);

        Assert.Equal(count, composer.TemplateImages.Count);
        Assert.Equal(count, composer.TemplateImages.Select(i => i.ContentId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(composer.TemplateImages, i => Assert.Contains($"cid:{i.ContentId}", composer.Html, StringComparison.Ordinal));
        Assert.Equal(composer.TemplateImages.Count, composer.ComposePreview.Images.Count);
    }

    [Fact]
    public async Task Window_AllowDomain_LandsInTheSettings_RemoveLink_UnwrapsIt()
    {
        using var rich = await RichScenario.CreateAsync(Files());
        var composer = await rich.OpenAsync();
        rich.Clipboard.Formatted = Formatted(
            "<html><body><!--StartFragment--><p>Zmień hasło na <a href=\"https://haslo.contoso.pl/\">haslo.contoso.pl</a> albo <a href=\"https://phish.example/\">portal.contoso.test</a>.</p><!--EndFragment--></body></html>");
        await PasteAsync(rich, composer);

        var outside = Assert.Single(composer.PasteProblems, p => p.Kind == HtmlImportProblemKind.LinkOutsideAllowedDomains && p.Detail == "haslo.contoso.pl");
        Assert.Equal(["haslo.contoso.pl", "contoso.pl"], outside.DomainChoices.Select(c => c.Domain));
        Assert.Equal("Allow domain contoso.pl", outside.DomainChoices[1].Text);
        await composer.AllowLinkDomainCommand.ExecuteAsync(outside.DomainChoices[1]);

        Assert.Contains("contoso.pl", rich.Messages.Harness.Settings.Current.UserMessages.AllowedLinkDomains);
        Assert.DoesNotContain(composer.PasteProblems, p => p.Kind == HtmlImportProblemKind.LinkOutsideAllowedDomains && p.Detail == "haslo.contoso.pl");

        var mismatch = Assert.Single(composer.PasteProblems, p => p.Kind == HtmlImportProblemKind.LinkTextMismatch);
        Assert.True(mismatch.CanRemoveLink);
        composer.RemoveLinkCommand.Execute(mismatch);

        Assert.DoesNotContain("phish.example", composer.Html, StringComparison.Ordinal);
        Assert.Contains("portal.contoso.test", composer.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(composer.PasteProblems, p => p.Detail == "phish.example");
    }

    [Fact]
    public async Task Window_RemoveImage_GenerateText_AndPasteImageAtTheCaret()
    {
        using var rich = await RichScenario.CreateAsync(Files());
        var composer = await rich.OpenAsync();
        rich.Clipboard.Formatted = Formatted(OutlookHtml());
        await PasteAsync(rich, composer);
        var first = composer.TemplateImages[0];

        composer.RemoveImageCommand.Execute(first);

        Assert.DoesNotContain(first.ContentId, composer.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(composer.TemplateImages, i => i.ContentId == first.ContentId);

        rich.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.GenerateTextCommand.ExecuteAsync(null);
        Assert.Equal(HtmlToText.Convert(composer.Html), composer.Body);

        composer.Html = "<p>Przed</p><p>Po</p>";
        composer.HtmlCaretIndex = "<p>Przed</p>".Length;
        rich.Clipboard.Formatted = new ClipboardContent(null, null, MessageImageSamples.Png(6, 6, seed: 0x41), null);
        composer.PasteImageCommand.Execute(null);
        var pasted = Assert.Single(composer.TemplateImages, i => composer.Html.Contains("<p>Przed</p><img src=\"cid:" + i.ContentId, StringComparison.Ordinal));
        Assert.Equal("6 × 6 px, 1 KB", pasted.SizeText);

        rich.Clipboard.Formatted = new ClipboardContent(null, null, null, "tylko tekst");
        composer.PasteImageCommand.Execute(null);
        Assert.Equal("The clipboard holds no image.", composer.RichStatusText);
    }

    [Fact]
    public async Task Window_ASavedTemplate_KeepsItsImages_AndLoadsThemWhenSelected()
    {
        using var rich = await RichScenario.CreateAsync(Files());
        var composer = await rich.OpenAsync();
        rich.Clipboard.Formatted = Formatted(OutlookHtml());
        await PasteAsync(rich, composer);
        var ids = composer.TemplateImages.Select(i => i.ContentId).Order(StringComparer.Ordinal).ToList();
        composer.TemplateName = "Instrukcja z Outlooka";

        await composer.SaveTemplateCommand.ExecuteAsync(null);

        var saved = Assert.Single(rich.Messages.Harness.Settings.Current.UserMessages.Templates);
        Assert.Equal(ids, saved.Images.Select(i => i.ContentId).Order(StringComparer.Ordinal));

        var other = await rich.OpenAsync(saved.Id);
        await WaitForImagesAsync(other, ids.Count);
        Assert.Equal(ids, other.TemplateImages.Select(i => i.ContentId).Order(StringComparer.Ordinal));
        Assert.Null(other.RichStatusText);
        Assert.False(other.HasMissingImages);

        // The files disappear (another window collected them): the window says so and can remove the references.
        foreach (var file in Directory.GetFiles(rich.Store.Folder))
        {
            File.Delete(file);
        }

        var third = await rich.OpenAsync(saved.Id);
        await WaitForImagesAsync(third, 0);
        Assert.StartsWith($"{ids.Count} image(s) of the template could not be read", third.RichStatusText, StringComparison.Ordinal);
        Assert.True(third.HasMissingImages);
        third.RemoveMissingImagesCommand.Execute(null);
        Assert.DoesNotContain("cid:", third.Html, StringComparison.Ordinal);
        Assert.False(third.HasMissingImages);
    }

    private static async Task PasteAsync(RichScenario rich, UserMessageComposerViewModel composer)
    {
        if (!string.IsNullOrWhiteSpace(composer.Html))
        {
            rich.Dialogs.ConfirmAnswers.Enqueue(true);
        }

        await composer.PasteFormattedCommand.ExecuteAsync(null);
    }

    private static async Task WaitForImagesAsync(UserMessageComposerViewModel composer, int count)
    {
        for (var i = 0; i < 200 && (composer.TemplateImages.Count != count || composer.RichStatusText == "Loading the template's images…"); i++)
        {
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>The message scenario with an image store and a file image source (like the application's DI).</summary>
    private sealed class RichScenario : IDisposable
    {
        private RichScenario(MessageScenario messages, IPastedImageSource files)
        {
            Messages = messages;
            var harness = messages.Harness;
            Store = new MessageImageStore(Paths, harness.Time);
            Services = new UserMessageServices(
                new UserMessageTemplateLibrary(harness.Settings, harness.Localizer, Store),
                messages.Services.Transports,
                new CsvCodec(),
                harness.AuditLog,
                harness.Time,
                (delay, token) =>
                {
                    harness.Time.Advance(delay);
                    return Task.CompletedTask;
                },
                pastedImages: files);
            Factory = new DialogViewModelFactory(
                harness.Services, harness.Engines, harness.Views, harness.AuditLog, new AuditLogExporter(new CsvCodec()), harness.PowerShell,
                harness.Theme, messages: messages.Launcher, userMessages: Services);
        }

        public MessageScenario Messages { get; }

        public TempAppPaths Paths { get; } = new();

        public MessageImageStore Store { get; }

        public UserMessageServices Services { get; }

        public DialogViewModelFactory Factory { get; }

        public FakeClipboard Clipboard => Messages.Harness.Clipboard;

        public ScriptedDialogService Dialogs => Messages.Harness.Dialogs;

        public static async Task<RichScenario> CreateAsync(IPastedImageSource files) => new(await MessageScenario.CreateAsync(), files);

        public async Task<UserMessageComposerViewModel> OpenAsync(string? templateId = null)
        {
            var composer = Factory.CreateUserMessageComposer(new UserMessageRequest([Messages.Company.JanKowalski], templateId));
            await composer.InitializeAsync();
            return composer;
        }

        public void Dispose() => Paths.Dispose();
    }
}
