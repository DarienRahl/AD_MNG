using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "3 · Podgląd" (design note 6.4, 7.3): MailerWPiA's card for the chosen recipient (widths, header), the info line, the
/// warnings (unknown and empty fields, compatibility, size), BCC and sample previews, "Podgląd z Outlooka" through the fake
/// session, "Otwórz w przeglądarce" and "Zapisz jako .html".
/// </summary>
public sealed class PreviewPageTests
{
    private const string Subject = "Zaproszenie na konferencję – {{Miejscowość}}";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refresh_ShowsTheCardOfTheChosenRecipient_InEachWidth()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.Outlook.SelectedAccount = window.Outlook.Accounts[0];
        var preview = window.Preview;

        await preview.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Urząd Gminy Andrespol <sekretariat@andrespol.pl>", "Urząd Gminy Brójce <ug@brojce.pl>", "Starostwo Powiatowe w Zgierzu <kancelaria@zgierz.pl>"],
            preview.PreviewRecipients.Select(r => r.Text));
        Assert.Same(preview.PreviewRecipients[0], preview.SelectedRecipient);
        var html = preview.Preview.Html;
        Assert.StartsWith("<!DOCTYPE html><html><head><meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains("background:#EDEFF2", html, StringComparison.Ordinal);
        Assert.Contains("max-width:700px", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Od:</b> Jan Kowalski &lt;jan.kowalski@contoso.test&gt;</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Do:</b> sekretariat@andrespol.pl</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Temat:</b> Zaproszenie na konferencję – Andrespol</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Załączniki:</b> –</div>", html, StringComparison.Ordinal);
        Assert.Contains("zapraszamy Urząd Gminy Andrespol z miejscowości Andrespol na konferencję w Łodzi.", html, StringComparison.Ordinal);
        Assert.Contains("font-family:" + CorrespondenceDocument.BodyFont, html, StringComparison.Ordinal);
        Assert.Contains("font-size:11pt", html, StringComparison.Ordinal);
        Assert.Equal("pola: Miejscowość, zwrot, nazwa", preview.InfoText);
        Assert.Empty(preview.Warnings);
        Assert.False(preview.HasWarnings);
        Assert.Equal("Wygląda dobrze – treść nie używa niczego, co psuje się w skrzynkach przeglądarkowych.", preview.OkText);
        Assert.False(preview.IsFromOutlook);

        preview.SelectedRecipient = preview.PreviewRecipients[1];
        await preview.Refreshing!;
        Assert.Contains("<div><b>Do:</b> ug@brojce.pl</div>", preview.Preview.Html, StringComparison.Ordinal);
        Assert.Contains("Zaproszenie na konferencję – Brójce", preview.Preview.Html, StringComparison.Ordinal);
        Assert.Same(preview.PreviewRecipients[1], preview.SelectedRecipient);   // kept by address

        preview.Width = preview.WidthChoices.Single(w => w.Value == CorrespondencePreviewWidth.Phone);
        await preview.Refreshing!;
        Assert.Contains("max-width:380px", preview.Preview.Html, StringComparison.Ordinal);
        Assert.Equal("Telefon (380 px)", preview.Width.Text);

        preview.Width = preview.WidthChoices.Single(w => w.Value == CorrespondencePreviewWidth.Full);
        await preview.Refreshing!;
        Assert.DoesNotContain("max-width:", preview.Preview.Html.Replace("img{max-width:100%", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("margin:0;background:#fff", preview.Preview.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warnings_NameUnknownAndEmptyFields_CompatibilityAndSize()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        var message = window.Message;
        var image = message.AddImage(MessageImageSamples.Png(30, 10, 0x61), "baner.png")!;
        message.Subject = Subject;
        message.BodyHtml = $"<p>{{{{zwrot}}}}, tel. {{{{Telefon}}}}, kod {{{{Kod pocztowy}}}}, do {{{{Termin}}}} {{{{Termin|wkrótce}}}}.</p>"
            + $"<p><img src=\"cid:{image.ContentId}\" width=\"800\" height=\"10\"></p>";
        message.CustomFields.Add(new CustomFieldViewModel("Termin", ""));
        var folder = Directory.CreateDirectory(Path.Combine(scenario.Paths.SettingsDirectory, "pliki")).FullName;
        var video = Path.Combine(folder, "nagranie z konferencji.mp4");
        await using (var stream = File.Create(video))
        {
            stream.SetLength(21L * 1024 * 1024);
        }

        await message.AddAttachmentFilesAsync([video]);
        await window.Preview.RefreshCommand.ExecuteAsync(null);

        var warnings = window.Preview.Warnings;
        Assert.Equal(
            "Nieznane pola (zostaną puste): {{Telefon}}. Dostępne: {{zwrot}}, {{nazwa}}, {{email}}, {{Termin}}, {{Miejscowość}}, {{Kod pocztowy}}.",
            warnings[0]);
        Assert.Equal("Pole {{Kod pocztowy}} jest puste u 1 z 3 odbiorców – u nich zostanie pusta luka.", warnings[1]);
        Assert.Equal("Pole {{Termin}} jest puste u 3 z 3 odbiorców – u nich zostanie pusta luka.", warnings[2]);
        Assert.Contains("Element szerszy niż 700 px (800 px) – na telefonie wymusi przewijanie w bok.", warnings);
        Assert.Contains(warnings, w => w.StartsWith("Wiadomość waży 21,0 MB z obrazami i załącznikami", StringComparison.Ordinal));
        Assert.Contains("Załączniki ważą razem 21,0 MB – wiele serwerów odrzuca wiadomości powyżej 10–20 MB.", warnings);
        Assert.True(window.Preview.HasWarnings);
        Assert.Contains("<div><b>Załączniki:</b> nagranie z konferencji.mp4</div>", window.Preview.Preview.Html, StringComparison.Ordinal);
        Assert.Equal("obrazków osadzonych: 1  •  pola: Miejscowość, zwrot, Telefon, Kod pocztowy, Termin", window.Preview.InfoText);
        Assert.Equal([image.ContentId], window.Preview.Preview.Images.Keys);
    }

    [Fact]
    public async Task Grouped_TheListItemFields_AreCountedPerRow()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        CorrespondencePagesScenario.AddRows(window,
            CorrespondencePagesScenario.Row("jan.kowalski@contoso.test", "Jan Kowalski", ("Skrzynka", "Sekretariat IT")),
            CorrespondencePagesScenario.Row("jan.kowalski@contoso.test", "Jan Kowalski", ("Skrzynka", "")),
            CorrespondencePagesScenario.Row("anna.nowak@contoso.test", "Anna Nowak", ("Skrzynka", "Biuro projektu")));
        var message = window.Message;
        message.Subject = "Skrzynki: {{liczba}}";
        message.BodyHtml = "<p>{{zwrot}},</p><p>{{lista}}</p>";
        message.ListItemTemplate = "{{Skrzynka}}";
        message.GroupByAddress = true;

        await window.Preview.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["Jan Kowalski <jan.kowalski@contoso.test>", "Anna Nowak <anna.nowak@contoso.test>"], window.Preview.PreviewRecipients.Select(r => r.Text));
        Assert.Contains("<li>Sekretariat IT</li>", window.Preview.Preview.Html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Temat:</b> Skrzynki: 2</div>", window.Preview.Preview.Html, StringComparison.Ordinal);
        Assert.Equal(["Pole {{Skrzynka}} w elemencie listy jest puste w 1 z 3 wierszy."], window.Preview.Warnings);
    }

    [Fact]
    public async Task Bcc_IsPreviewedAsSent_WithoutTheFieldsOfARow()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.Message.SendMode = window.Message.SendModeChoices.Single(m => m.Value == CorrespondenceSendMode.Bcc);

        await window.Preview.RefreshCommand.ExecuteAsync(null);

        var html = window.Preview.Preview.Html;
        Assert.Empty(window.Preview.PreviewRecipients);
        Assert.Contains("<div><b>Do:</b> jan.kowalski@contoso.test</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>UDW:</b> 3 odbiorców</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Temat:</b> Zaproszenie na konferencję –</div>", html, StringComparison.Ordinal);
        Assert.Contains("zapraszamy Szanowni Państwo z miejscowości  na konferencję", html, StringComparison.Ordinal);
        Assert.Empty(window.Preview.Warnings);
    }

    [Fact]
    public async Task WithoutRecipients_ASampleAddressIsShown_AndTheSignatureIsIncluded()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        window.Outlook.OnBehalfOf = " sekretariat@contoso.test ";
        window.Message.SelectedSignature = window.Message.Signatures[1];

        await window.Preview.RefreshCommand.ExecuteAsync(null);

        var html = window.Preview.Preview.Html;
        Assert.Contains("<div><b>Od:</b> sekretariat@contoso.test (w imieniu)</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Do:</b> adres@odbiorcy.pl</div>", html, StringComparison.Ordinal);
        Assert.Contains("zapraszamy Szanowni Państwo z miejscowości", html, StringComparison.Ordinal);
        Assert.Contains("Urząd Gminy Andrespol", html, StringComparison.Ordinal);   // the signature
        Assert.Equal([scenario.Signatures.Logo.ContentId], window.Preview.Preview.Images.Keys);
        Assert.StartsWith("obrazków osadzonych: 1  •  pola: ", window.Preview.InfoText, StringComparison.Ordinal);
        Assert.Equal(["Nieznane pola (zostaną puste): {{Miejscowość}}. Dostępne: {{zwrot}}, {{nazwa}}, {{email}}."], window.Preview.Warnings);
    }

    [Fact]
    public async Task PreviewRecipients_AreTheFirst500Messages()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, [.. Enumerable.Range(1, 600)
            .Select(i => CorrespondencePagesScenario.Row($"urzad{i}@gmina.pl", $"Urząd Gminy nr {i}", ("Miejscowość", "Łódź")))]);

        await window.Preview.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(PreviewPageViewModel.MaxRecipients, window.Preview.PreviewRecipients.Count);
        Assert.Empty(window.Preview.Warnings);
    }

    [Fact]
    public async Task EnteringTheTab_RefreshesThePreview()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        Assert.True(window.Preview.Preview.IsEmpty);

        window.SelectedTab = CorrespondenceTab.Preview;
        await window.Preview.Refreshing!;

        Assert.Contains("sekretariat@andrespol.pl", window.Preview.Preview.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewViaOutlook_RendersTheMessageInTheSession()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.Message.SelectedSignature = window.Message.Signatures[1];
        await window.Preview.RefreshCommand.ExecuteAsync(null);

        await window.Preview.PreviewViaOutlookCommand.ExecuteAsync(null);

        var rendered = Assert.Single(scenario.Outlook.Rendered);
        Assert.Equal(["sekretariat@andrespol.pl"], rendered.To);
        Assert.Equal("Zaproszenie na konferencję – Andrespol", rendered.Subject);
        Assert.Equal([scenario.Signatures.Logo.ContentId], rendered.InlineImages.Select(i => i.ContentId));
        var preview = window.Preview;
        Assert.True(preview.IsFromOutlook);
        Assert.Contains("Treść pobrana z Outlooka – dokładnie to wyjdzie do odbiorcy.", preview.Preview.Html, StringComparison.Ordinal);
        Assert.Contains("<div><b>Do:</b> sekretariat@andrespol.pl</div>", preview.Preview.Html, StringComparison.Ordinal);
        Assert.Equal([scenario.Signatures.Logo.ContentId], preview.Preview.Images.Keys);
        Assert.Equal("Podgląd z Outlooka · obrazków osadzonych: 1 · załączników w wiadomości: 1", preview.InfoText);
        Assert.Empty(preview.Warnings);
        Assert.StartsWith("To jest treść odczytana z gotowej wiadomości.", preview.OkText, StringComparison.Ordinal);

        await preview.RefreshCommand.ExecuteAsync(null);
        Assert.False(preview.IsFromOutlook);
    }

    [Fact]
    public async Task PreviewViaOutlook_AFailureIsShown_AndALostOutlookReported()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        scenario.Outlook.RenderFailure = FakeOutlookClient.Failure(OutlookFailureKind.Disconnected, "render");

        await window.Preview.PreviewViaOutlookCommand.ExecuteAsync(null);

        var warning = Assert.Single(window.Preview.Warnings);
        Assert.StartsWith("Nie udało się pobrać podglądu z Outlooka: Połączenie z Outlookiem", warning, StringComparison.Ordinal);
        Assert.False(window.Preview.IsFromOutlook);
        Assert.Equal(OutlookState.Failed, window.Outlook.State);
    }

    [Fact]
    public async Task WithoutOutlookOrBrowser_TheCommandsAreDisabled()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync(withOutlook: false);
        using var window = await scenario.OpenWithMessageAsync();

        Assert.False(window.Preview.PreviewViaOutlookCommand.CanExecute(null));
        Assert.True(window.Preview.OpenInBrowserCommand.CanExecute(null));
        Assert.True(window.Preview.SaveHtmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenInBrowser_HandsTheCardToTheLoopbackPreview()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());

        await window.Preview.OpenInBrowserCommand.ExecuteAsync(null);

        var (content, title) = Assert.Single(scenario.Browser.Opened);
        Assert.Equal("Podgląd wiadomości", title);
        Assert.Same(window.Preview.Preview, content);
        Assert.Contains("sekretariat@andrespol.pl", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveHtml_WritesTheCardAndItsImagesIntoTheChosenFolder()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var image = message.AddImage(MessageImageSamples.Png(16, 8, 0x71), "herb-gminy.png")!;
        message.Subject = "Herb";
        message.BodyHtml = $"<p>Nasz herb:</p><p><img src=\"cid:{image.ContentId}\" alt=\"herb\"></p>";
        message.SelectedSignature = message.Signatures[1];
        var folder = Directory.CreateDirectory(Path.Combine(scenario.Paths.SettingsDirectory, "eksport")).FullName;
        var foreign = Path.Combine(folder, "logo.png");
        await File.WriteAllTextAsync(foreign, "nie obraz – plik operatora", Token);
        await window.Preview.RefreshCommand.ExecuteAsync(null);
        scenario.Dialogs.Folders.Enqueue(folder);

        await window.Preview.SaveHtmlCommand.ExecuteAsync(null);

        var html = await File.ReadAllTextAsync(Path.Combine(folder, PreviewCard.FileName), Token);
        Assert.Contains("src=\"herb-gminy.png\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"logo-2.png\"", html, StringComparison.Ordinal);   // logo.png of the folder is not replaced
        Assert.DoesNotContain("cid:", html, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Equal(image.Data, await File.ReadAllBytesAsync(Path.Combine(folder, "herb-gminy.png"), Token));
        Assert.Equal(scenario.Signatures.Logo.Data, await File.ReadAllBytesAsync(Path.Combine(folder, "logo-2.png"), Token));
        Assert.Equal("nie obraz – plik operatora", await File.ReadAllTextAsync(foreign, Token));

        // A second save into the same folder asks first, and then keeps the names of the images it wrote.
        scenario.Dialogs.Folders.Enqueue(folder);
        scenario.Ui.ConfirmAnswers.Enqueue(false);
        await window.Preview.SaveHtmlCommand.ExecuteAsync(null);
        Assert.Equal("confirm:Plik podglad_wiadomosci.html już jest w tym folderze. Zastąpić go?", scenario.Ui.Shown[^1]);
        scenario.Dialogs.Folders.Enqueue(folder);
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Preview.SaveHtmlCommand.ExecuteAsync(null);
        Assert.Contains("src=\"logo-2.png\"", await File.ReadAllTextAsync(Path.Combine(folder, PreviewCard.FileName), Token), StringComparison.Ordinal);
        Assert.Equal(4, Directory.GetFiles(folder).Length);

        // Cancelled folder choice: nothing happens.
        await window.Preview.SaveHtmlCommand.ExecuteAsync(null);
        Assert.Equal(2, scenario.Ui.Shown.Count);
    }

    [Fact]
    public void PreviewCard_ReplacesOnlyTheImagesItHasFilesFor()
    {
        var html = "<p><img src=\"cid:img-1@admng\" alt=\"a\"><img src='CID:img-2@admng'><img src=\"cid:img-3@admng\"></p>";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["img-1@admng"] = "herb gminy.png", ["img-2@admng"] = "logo.png" };

        Assert.Equal(
            "<p><img src=\"herb%20gminy.png\" alt=\"a\"><img src='logo.png'><img src=\"cid:img-3@admng\"></p>",
            PreviewCard.WithImageFiles(html, files));
        Assert.Equal(700, PreviewCard.MaxWidth(CorrespondencePreviewWidth.Desktop));
        Assert.Equal(380, PreviewCard.MaxWidth(CorrespondencePreviewWidth.Phone));
        Assert.Null(PreviewCard.MaxWidth(CorrespondencePreviewWidth.Full));
    }
}
