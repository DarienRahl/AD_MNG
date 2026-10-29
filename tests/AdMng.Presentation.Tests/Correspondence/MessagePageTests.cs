using System.Globalization;
using System.Text.Json;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.RichHtml;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "2 · Wiadomość" (design note 6.4, 7.2): signatures, "Wstaw pole", custom fields, validation before sending, deferred
/// delivery (MailerWPiA's rules), attachments, images of the editor, the formatted paste through the U1c workflow,
/// templates and the options of the settings.
/// </summary>
public sealed class MessagePageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Open_ListsTheSignatures_AndReadsTheOneOfTheSettings()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Harness.Settings.Current.Correspondence.SignatureName = CorrespondencePagesScenario.ParsingSignatures.Kowalski;

        using var window = await scenario.OpenAsync();
        var message = window.Message;
        await message.SignatureLoading!;

        Assert.Equal(["(bez podpisu)", "Jan Kowalski – podpis", "Sekretariat"], message.Signatures.Select(s => s.Text));
        Assert.Null(message.Signatures[0].Name);
        Assert.Equal(CorrespondencePagesScenario.ParsingSignatures.Kowalski, message.SelectedSignature?.Name);
        Assert.Equal(string.Empty, message.SignatureProblemsText);
        var signature = message.Draft().Signature;
        Assert.NotNull(signature);
        Assert.Equal([scenario.Signatures.Logo.ContentId], signature.Images.Select(i => i.ContentId));
    }

    [Fact]
    public async Task Signature_None_Missing_Unreadable_AndProblems_AreShown()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        Assert.Equal("(bez podpisu)", message.SelectedSignature?.Text);
        Assert.Null(message.Draft().Signature);

        scenario.Signatures.Problems[CorrespondencePagesScenario.ParsingSignatures.Secretariat] =
        [
            SignatureProblemCodes.Format(SignatureProblemCodes.RemoteImage, "cdn.contoso.pl"),
            SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, nameof(MessageImageError.UnknownFormat)),
        ];
        message.SelectedSignature = message.Signatures.Single(s => s.Name == CorrespondencePagesScenario.ParsingSignatures.Secretariat);
        await message.SignatureLoading!;
        Assert.Equal(
            "Podpis „Sekretariat”: usunięto obraz z serwera cdn.contoso.pl – obrazy z sieci nie są pobierane; odrzucono obraz (to nie PNG, JPEG ani GIF)",
            message.SignatureProblemsText);
        Assert.NotNull(message.Draft().Signature);

        scenario.Signatures.Names.Remove(CorrespondencePagesScenario.ParsingSignatures.Kowalski);
        message.SelectedSignature = message.Signatures.Single(s => s.Name == CorrespondencePagesScenario.ParsingSignatures.Kowalski);
        await message.SignatureLoading!;
        Assert.Equal("Nie znaleziono podpisu „Jan Kowalski – podpis”.", message.SignatureProblemsText);
        Assert.Null(message.Draft().Signature);

        scenario.Signatures.Failure = new IOException("dysk niedostępny");
        message.SelectedSignature = message.Signatures.Single(s => s.Name == CorrespondencePagesScenario.ParsingSignatures.Secretariat);
        await message.SignatureLoading!;
        Assert.StartsWith("Nie udało się wczytać podpisu „Sekretariat”: ", message.SignatureProblemsText, StringComparison.Ordinal);
        Assert.Null(message.Draft().Signature);
    }

    [Fact]
    public async Task RefreshSignatures_KeepsTheChoice_AndNamesTheFolderWhenThereAreNone()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.SelectedSignature = message.Signatures.Single(s => s.Name == CorrespondencePagesScenario.ParsingSignatures.Secretariat);
        await message.SignatureLoading!;
        scenario.Signatures.Names.Add("Dział Kadr");

        await message.RefreshSignaturesCommand.ExecuteAsync(null);

        Assert.Equal(4, message.Signatures.Count);
        Assert.Equal(CorrespondencePagesScenario.ParsingSignatures.Secretariat, message.SelectedSignature?.Name);
        Assert.NotNull(message.Draft().Signature);

        scenario.Signatures.Names.Clear();
        await message.RefreshSignaturesCommand.ExecuteAsync(null);

        Assert.Equal(["(bez podpisu)"], message.Signatures.Select(s => s.Text));
        Assert.Null(message.SelectedSignature?.Name);
        Assert.Equal($"Nie znaleziono podpisów Outlooka w folderze {scenario.Signatures.Folder}.", message.SignatureProblemsText);
    }

    [Fact]
    public async Task FieldChoices_AreBuiltIns_CustomFields_AndColumnsWithoutDuplicates()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        Assert.Equal(["{{zwrot}}", "{{nazwa}}", "{{email}}"], message.FieldChoices);

        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Row("biuro@łódź.pl", "Biuro",
            ("Nazwa", "Biuro"), ("E-mail", "biuro@łódź.pl"), ("Miejscowość", "Łódź"), ("Kod pocztowy", "90-001")));
        message.CustomFields.Add(new CustomFieldViewModel("Termin", "15 listopada"));

        Assert.Equal(["{{zwrot}}", "{{nazwa}}", "{{email}}", "{{Termin}}", "{{Miejscowość}}", "{{Kod pocztowy}}"], message.FieldChoices);

        message.GroupByAddress = true;
        Assert.Equal(["{{zwrot}}", "{{nazwa}}", "{{email}}", "{{lista}}", "{{liczba}}", "{{Termin}}", "{{Miejscowość}}", "{{Kod pocztowy}}"], message.FieldChoices);

        message.CustomFields[0].Name = "Data spotkania";
        Assert.Contains("{{Data spotkania}}", message.FieldChoices);
        Assert.DoesNotContain("{{Termin}}", message.FieldChoices);

        var inserted = new List<string>();
        message.InsertFieldRequested += (_, field) => inserted.Add(field);
        message.InsertFieldCommand.Execute("{{Miejscowość}}");
        message.InsertFieldCommand.Execute(null);
        Assert.Equal(["{{Miejscowość}}"], inserted);
    }

    [Fact]
    public async Task CustomFields_AreValidated()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        message.Subject = "Zaproszenie";
        message.BodyHtml = "<p>Treść</p>";

        message.AddCustomFieldCommand.Execute(null);
        message.CustomFields.Add(new CustomFieldViewModel("Termin", "15 listopada"));
        message.CustomFields.Add(new CustomFieldViewModel("termin", "16 listopada"));
        message.CustomFields.Add(new CustomFieldViewModel("Zwrot", "Szanowni"));
        message.CustomFields.Add(new CustomFieldViewModel("Adres e-mail", "x"));
        message.CustomFields.Add(new CustomFieldViewModel("Miejscowość", "Łódź"));
        message.CustomFields.Add(new CustomFieldViewModel("{Pole}", "x"));
        message.CustomFields.Add(new CustomFieldViewModel("Zaproszenie", "na {{Termin}} w {{Miejscowość}}"));
        message.CustomFields.Add(new CustomFieldViewModel("Sala", "nr 12 ({{nazwa}})"));

        Assert.Equal(
            [
                "wpisz nazwę pola",
                null,
                "pole o tej nazwie już jest",
                "to nazwa pola wbudowanego",
                "to nazwa pola wbudowanego",
                "to nazwa kolumny listy odbiorców",
                "nazwa nie może zawierać znaków { } | i może mieć najwyżej 80 znaków",
                "wartość nie może używać innych pól własnych ({{Termin}})",
                null,
            ],
            message.CustomFields.Select(f => f.Error));
        var errors = message.ValidateForSend();
        Assert.Contains("Pole własne „Miejscowość”: to nazwa kolumny listy odbiorców", errors);
        Assert.Equal(7, errors.Count(e => e.StartsWith("Pole własne „", StringComparison.Ordinal)));

        foreach (var field in message.CustomFields.Where(f => f.Error is not null).ToList())
        {
            message.RemoveCustomFieldCommand.Execute(field);
        }

        Assert.Empty(message.ValidateForSend());
        Assert.Equal([new CustomFieldDefinition("Termin", "15 listopada"), new CustomFieldDefinition("Sala", "nr 12 ({{nazwa}})")], message.Draft().CustomFields);
    }

    [Fact]
    public async Task ValidateForSend_ListsWhatBlocksSending()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;

        Assert.Equal(["Wpisz temat wiadomości.", "Treść wiadomości jest pusta."], message.ValidateForSend());

        message.Subject = new string('a', CorrespondenceLimits.MaxSubjectLength + 1);
        message.BodyHtml = "<p> </p>";
        Assert.Equal(["Temat jest dłuższy niż 255 znaków.", "Treść wiadomości jest pusta."], message.ValidateForSend());

        message.Subject = "Zaproszenie – {{Miejscowość}}";
        message.BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy.</p>";
        message.UseVoting = true;
        message.VotingOptions = " ; ";
        message.DeliverLater = true;
        message.DeliverTime = "25:99";
        Assert.Equal(
            ["Nie rozumiem godziny „25:99” – wpisz ją w formacie 08:30.", "Wpisz odpowiedzi przycisków głosowania oddzielone średnikiem."],
            message.ValidateForSend());

        message.VotingOptions = "Tak; Nie ;";
        message.DeliverTime = "8:30";
        Assert.Empty(message.ValidateForSend());
        Assert.Equal("Tak;Nie", message.VotingOptionsText);

        // An image alone is a body.
        var image = message.AddImage(MessageImageSamples.Png(4, 4), "mapa.png")!;
        message.BodyHtml = $"<p><img src=\"cid:{image.ContentId}\"></p>";
        Assert.Empty(message.ValidateForSend());
    }

    [Fact]
    public async Task ValidateForSend_RefusesImagesOverTheLimitOfAMessage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.Subject = "Galeria";
        var images = Enumerable.Range(0, CorrespondenceLimits.MaxImagesPerMessage)
            .Select(i => message.AddImage(MessageImageSamples.Png(2 + (i % 7), 2 + (i / 7), (byte)i), $"zdjecie{i}.png")!)
            .ToList();
        message.BodyHtml = string.Concat(images.Select(i => $"<p><img src=\"cid:{i.ContentId}\"></p>"));
        Assert.Equal(CorrespondenceLimits.MaxImagesPerMessage, images.Select(i => i.ContentId).Distinct().Count());
        Assert.Empty(message.ValidateForSend());

        // The signature's logo is the 51st image of every message.
        message.SelectedSignature = message.Signatures.Single(s => s.Name == CorrespondencePagesScenario.ParsingSignatures.Kowalski);
        await message.SignatureLoading!;

        var error = Assert.Single(message.ValidateForSend());
        Assert.StartsWith("Obrazy przekraczają limit wiadomości: 51 obrazów, ", error, StringComparison.Ordinal);
        Assert.EndsWith("(najwyżej 50 obrazów i 25,0 MB).", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "08:00", true, "2026-10-14T08:00:00+00:00")]
    [InlineData(1, "8:30", true, "2026-10-14T08:30:00+00:00")]
    [InlineData(1, "8.30", true, "2026-10-14T08:30:00+00:00")]
    [InlineData(1, "8", true, "2026-10-14T08:00:00+00:00")]
    [InlineData(1, "09", true, "2026-10-14T09:00:00+00:00")]
    [InlineData(0, "12:02", true, "2026-10-13T12:02:00+00:00")]
    [InlineData(365, "11:59", true, "2027-10-13T11:59:00+00:00")]
    [InlineData(1, "25:00", false, "Nie rozumiem godziny „25:00” – wpisz ją w formacie 08:30.")]
    [InlineData(1, "ósma", false, "Nie rozumiem godziny „ósma” – wpisz ją w formacie 08:30.")]
    [InlineData(0, "12:00", false, "Termin dostarczenia musi być w przyszłości (podano 13.10.2026 12:00).")]
    [InlineData(0, "12:01", false, "Termin dostarczenia musi być w przyszłości (podano 13.10.2026 12:01).")]
    [InlineData(-1, "10:00", false, "Termin dostarczenia musi być w przyszłości (podano 12.10.2026 10:00).")]
    [InlineData(366, "08:00", false, "Termin dostarczenia jest dalej niż rok – sprawdź datę.")]
    public async Task DeferredDelivery_FollowsMailerWPiA(int days, string time, bool valid, string expected)
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        Assert.True(message.TryGetDeferredDelivery(out var none));
        Assert.Null(none);
        Assert.Equal(new DateTime(2026, 10, 14), message.DeliverDate);   // tomorrow by default

        message.DeliverLater = true;
        message.DeliverDate = new DateTime(2026, 10, 13).AddDays(days);
        message.DeliverTime = time;

        Assert.Equal(valid, message.TryGetDeferredDelivery(out var at));
        if (valid)
        {
            Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), at);
            Assert.Null(message.DeliverError);
        }
        else
        {
            Assert.Null(at);
            Assert.Equal(expected, message.DeliverError);
        }

        message.DeliverLater = false;
        Assert.Null(message.DeliverError);
    }

    [Fact]
    public async Task DeferredDelivery_WithoutADate_IsToday()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.DeliverLater = true;
        message.DeliverDate = null;
        message.DeliverTime = "18:15";

        Assert.True(message.TryGetDeferredDelivery(out var at));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 18, 15, 0, TimeSpan.Zero), at);
    }

    [Fact]
    public async Task Attachments_AreAdded_Counted_Removed_AndCheckedBeforeSending()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var folder = Directory.CreateDirectory(Path.Combine(scenario.Paths.SettingsDirectory, "załączniki")).FullName;
        var program = Path.Combine(folder, "program-konferencji.pdf");
        var map = Path.Combine(folder, "mapa dojazdu.png");
        await File.WriteAllBytesAsync(program, new byte[1500], Token);
        await File.WriteAllBytesAsync(map, new byte[2 * 1024 * 1024], Token);
        Assert.Equal("Załączniki", message.AttachmentsHeaderText);
        Assert.False(message.HasAttachments);
        Assert.False(message.RemoveAttachmentsCommand.CanExecute(null));

        await message.AddAttachmentFilesAsync([program, map, program, Path.Combine(folder, "brak.docx"), " "]);

        Assert.Equal(["program-konferencji.pdf", "mapa dojazdu.png"], message.Attachments.Select(a => a.Name));
        Assert.Equal(["2 KB", "2,0 MB"], message.Attachments.Select(a => a.SizeText));
        Assert.True(message.HasAttachments);
        Assert.Equal("Załączniki (2, 2,0 MB)", message.AttachmentsHeaderText);
        Assert.Empty(scenario.Ui.Shown);   // below 15 MB: no warning

        // "Dodaj załączniki": the dialog's files, the same file once.
        scenario.Dialogs.Files.Enqueue([map]);
        await message.AddAttachmentsCommand.ExecuteAsync(null);
        Assert.Equal(2, message.Attachments.Count);

        // A file gone before sending blocks it.
        message.Subject = "Program";
        message.BodyHtml = "<p>W załączniku program.</p>";
        File.Delete(map);
        await message.RefreshAttachmentsAsync();
        Assert.False(message.Attachments[1].Exists);
        Assert.Equal("brak pliku", message.Attachments[1].SizeText);
        Assert.Equal(["Brakuje plików załączników: mapa dojazdu.png. Usuń je z listy albo przywróć pliki."], message.ValidateForSend());

        message.SelectedAttachments = new List<object> { message.Attachments[1] };
        message.RemoveAttachmentsCommand.Execute(null);
        Assert.Equal(["program-konferencji.pdf"], message.Attachments.Select(a => a.Name));
        Assert.Empty(message.ValidateForSend());

        message.ClearAttachmentsCommand.Execute(null);
        Assert.Empty(message.Attachments);
        Assert.Equal("Załączniki", message.AttachmentsHeaderText);
    }

    [Fact]
    public async Task Attachments_AboveFifteenMegabytes_AreWarnedAbout()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var folder = Directory.CreateDirectory(Path.Combine(scenario.Paths.SettingsDirectory, "duże")).FullName;
        var video = Path.Combine(folder, "nagranie.mp4");
        await using (var stream = File.Create(video))
        {
            stream.SetLength(CorrespondenceLimits.AttachmentWarningBytes + 1);
        }

        await window.Message.AddAttachmentFilesAsync([video]);

        var shown = Assert.Single(scenario.Ui.Shown);
        Assert.StartsWith("info:Załączniki ważą razem 15,0 MB. Wiele serwerów urzędowych odrzuca wiadomości powyżej 10–20 MB", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddImage_ValidatesWithTheCorrespondenceLimits_AndIsIdempotent()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var png = MessageImageSamples.Png(20, 10, 0x33, withMetadata: true);

        var image = message.AddImage(png, "schemat.png");
        Assert.NotNull(image);
        Assert.Same(image, message.AddImage(png, "inna-nazwa.png"));
        Assert.Same(image, message.AddImage(image.Data, image.FileName));    // the editor registers what it shows
        Assert.Equal([image.ContentId], message.BodyImages.Keys);
        Assert.Same(message.BodyImages, ((IRichEditorHost)message).Images);

        Assert.Null(message.AddImage("to nie jest obraz"u8.ToArray(), "notatka.png"));
        Assert.Equal("Obraz odrzucony: to nie PNG, JPEG ani GIF", message.PasteProblemsText);

        message.ReportEditorError("Schowek jest zajęty.");
        Assert.Equal("Schowek jest zajęty.", message.PasteProblemsText);
    }

    [Fact]
    public async Task AddImage_AtTheLimit_MakesRoomFromImagesTheBodyNoLongerUses()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var images = Enumerable.Range(0, CorrespondenceLimits.MaxImagesPerMessage)
            .Select(i => message.AddImage(MessageImageSamples.Png(2 + (i % 7), 2 + (i / 7), (byte)i), null)!)
            .ToList();
        message.BodyHtml = string.Concat(images.Select(i => $"<p><img src=\"cid:{i.ContentId}\"></p>"));
        var extra = MessageImageSamples.Png(30, 30, 0x99);

        Assert.Null(message.AddImage(extra, "nadmiarowy.png"));
        Assert.Equal("W treści może być najwyżej 50 obrazów.", message.PasteProblemsText);

        message.BodyHtml = string.Concat(images.Skip(1).Select(i => $"<p><img src=\"cid:{i.ContentId}\"></p>"));
        var added = message.AddImage(extra, "nadmiarowy.png");

        Assert.NotNull(added);
        Assert.Equal(CorrespondenceLimits.MaxImagesPerMessage, message.BodyImages.Count);
        Assert.DoesNotContain(images[0].ContentId, message.BodyImages.Keys);
    }

    [Fact]
    public async Task PasteFormatted_GoesThroughTheU1cWorkflow_RepairsFields_AndRegistersImages()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        const string html =
            "<html><body><!--StartFragment-->"
            + "<p class=MsoNormal style='font-family:Calibri'>Szanowni Państwo z <b><span style='color:#C00000'>{{</span></b><b><span style='color:#C00000'>Miejscowość</span></b><b><span style='color:#C00000'>}}</span></b>,<o:p></o:p></p>"
            + "<p class=MsoNormal>zapraszamy na <a href=\"https://konferencja.uni.lodz.pl/program\">program konferencji</a>.</p>"
            + "<p class=MsoNormal><img width=9 height=3 src=\"file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/clip_image001.png\" alt=\"Herb\"></p>"
            + "<p class=MsoNormal><img src=\"https://cdn.contoso.pl/baner.png\" alt=\"Baner\"></p>"
            + "<!--EndFragment--></body></html>";
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(CfHtmlFixture.Build(html), null, null, "Szanowni Państwo");

        var paste = await message.PasteFormattedAsync();

        Assert.NotNull(paste);
        Assert.Contains("Miejscowość", FieldTemplate.UsedFields(paste.Html));
        Assert.DoesNotContain("file:", paste.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.contoso.pl", paste.Html, StringComparison.OrdinalIgnoreCase);
        var image = Assert.Single(paste.Images);
        Assert.Contains($"cid:{image.ContentId}", paste.Html, StringComparison.Ordinal);
        Assert.Same(image, message.BodyImages[image.ContentId]);
        Assert.Contains(scenario.PastedImages.Requests, uri => uri.EndsWith("clip_image001.png", StringComparison.Ordinal));
        Assert.StartsWith("Wklejono z uwagami: ", message.PasteProblemsText, StringComparison.Ordinal);
        Assert.Contains("Usunięto obraz z sieci (cdn.contoso.pl)", message.PasteProblemsText, StringComparison.Ordinal);
        Assert.DoesNotContain("konferencja.uni.lodz.pl", message.PasteProblemsText, StringComparison.Ordinal);   // links may go anywhere

        scenario.Harness.Clipboard.Formatted = null;
        Assert.Null(await message.PasteFormattedAsync());
    }

    [Fact]
    public async Task PasteFormatted_ATextOnlyClipboard_GivesParagraphs()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(null, null, null, "Dzień dobry {{nazwa}},\nzapraszamy.");

        var paste = await window.Message.PasteFormattedAsync();

        Assert.NotNull(paste);
        Assert.Empty(paste.Images);
        Assert.Contains("{{nazwa}}", paste.Html, StringComparison.Ordinal);
        Assert.Equal(string.Empty, window.Message.PasteProblemsText);
    }

    [Fact]
    public async Task Templates_BuiltInFirst_LoadedWithoutAskingIntoAnEmptyMessage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var builtIn = Assert.Single(message.Templates);
        Assert.Equal("Czy skrzynka pocztowa jest nadal potrzebna? (wbudowany)", builtIn.Text);
        Assert.True(builtIn.IsBuiltIn);
        Assert.False(message.DeleteTemplateCommand.CanExecute(null));
        Assert.False(message.ExportTemplatesCommand.CanExecute(null));

        message.SelectedTemplate = builtIn;
        await message.TemplateLoading!;

        Assert.Empty(scenario.Ui.Shown);
        Assert.Equal("Czy skrzynka pocztowa jest nadal potrzebna?", message.Subject);
        Assert.Contains("{{lista}}", message.BodyHtml, StringComparison.Ordinal);
        Assert.Equal([("Termin", "")], message.CustomFields.Select(f => (f.Name, f.Value)));
        Assert.StartsWith("{{Skrzynka}} ({{Adres skrzynki}})", message.ListItemTemplate, StringComparison.Ordinal);
        Assert.True(message.UseVoting);
        Assert.Equal(CorrespondenceSettings.DefaultVotingOptions, message.VotingOptions);
        Assert.True(message.GroupByAddress);
        Assert.False(message.DeleteTemplateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Templates_OverAnotherMessage_AskFirst()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.Subject = "Zaproszenie do Łodzi";
        message.BodyHtml = "<p>Treść zaproszenia</p>";

        scenario.Ui.ConfirmAnswers.Enqueue(false);
        message.SelectedTemplate = message.Templates[0];
        await message.TemplateLoading!;

        Assert.Equal(
            "confirm:Zastąpić bieżącą wiadomość szablonem „Czy skrzynka pocztowa jest nadal potrzebna?”? Temat, treść i pola własne zostaną zastąpione.",
            Assert.Single(scenario.Ui.Shown));
        Assert.Null(message.SelectedTemplate);
        Assert.Equal("Zaproszenie do Łodzi", message.Subject);

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        Assert.True(await message.LoadTemplateAsync(CorrespondenceTemplateLibrary.MailboxStillNeededId));
        Assert.Equal("Czy skrzynka pocztowa jest nadal potrzebna?", message.Subject);
        Assert.Equal(2, scenario.Ui.Shown.Count);

        // The same message again: nothing to lose, no question.
        Assert.True(await message.LoadTemplateAsync(CorrespondenceTemplateLibrary.MailboxStillNeededId));
        Assert.Equal(2, scenario.Ui.Shown.Count);
        Assert.False(await message.LoadTemplateAsync("nie-ma-takiego"));
    }

    [Fact]
    public async Task SaveTemplate_NewOverwriteAndRename_KeepTheImages()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var image = message.AddImage(MessageImageSamples.Png(12, 6, 0x51), "herb.png")!;
        message.Subject = "Zaproszenie – {{Miejscowość}}";
        message.BodyHtml = $"<p>{{{{zwrot}}}},</p><p><img src=\"cid:{image.ContentId}\" alt=\"herb\"></p>";
        message.UseVoting = true;
        message.VotingOptions = "Będę;Nie będę";

        await message.SaveTemplateCommand.ExecuteAsync(null);
        Assert.Equal([("Szablony korespondencji", "Wpisz nazwę szablonu.")], scenario.Ui.Errors);
        Assert.Empty(scenario.Templates.Saved);

        message.TemplateName = "Zaproszenie na konferencję";
        await message.SaveTemplateCommand.ExecuteAsync(null);

        var saved = Assert.Single(scenario.Templates.Saved);
        Assert.Equal("Zaproszenie na konferencję", saved.Name);
        Assert.Equal((true, "Będę;Nie będę", false), (saved.VotingEnabled, saved.VotingOptions, saved.GroupByAddress));
        Assert.Equal([image.ContentId], saved.Images.Select(i => i.ContentId));
        Assert.NotNull(await scenario.Images.LoadAsync(saved.Images[0], Token, CorrespondenceLimits.MaxImageBytes));
        Assert.Equal(saved.Id, message.SelectedTemplate?.Id);
        Assert.Equal(2, message.Templates.Count);
        Assert.Contains((NotificationSeverity.Success, "Zapisano szablon „Zaproszenie na konferencję”."), scenario.Harness.Notifications.Items);
        Assert.True(message.DeleteTemplateCommand.CanExecute(null));

        message.Subject = "Zaproszenie – nowy termin";
        await message.SaveTemplateCommand.ExecuteAsync(null);
        Assert.Equal("Zaproszenie – nowy termin", Assert.Single(scenario.Templates.Saved).Subject);

        message.TemplateName = "Przypomnienie";
        await message.SaveTemplateCommand.ExecuteAsync(null);
        Assert.Equal(["Zaproszenie na konferencję", "Przypomnienie"], scenario.Templates.Saved.Select(t => t.Name));
        Assert.Equal(3, message.Templates.Count);
    }

    [Fact]
    public async Task DeleteTemplate_AsksFirst()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.Subject = "Przypomnienie";
        message.BodyHtml = "<p>Przypominamy o terminie.</p>";
        message.TemplateName = "Przypomnienie o terminie";
        await message.SaveTemplateCommand.ExecuteAsync(null);

        scenario.Ui.ConfirmAnswers.Enqueue(false);
        await message.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Single(scenario.Templates.Saved);
        Assert.Equal("confirm:Usunąć szablon „Przypomnienie o terminie”?", scenario.Ui.Shown[^1]);

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await message.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Empty(scenario.Templates.Saved);
        Assert.Null(message.SelectedTemplate);
        Assert.Single(message.Templates);
    }

    [Fact]
    public async Task ExportAndImportTemplates_RoundTrip_AndRefuseOtherFiles()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.Subject = "Zaproszenie";
        message.BodyHtml = "<p>Zapraszamy do Łodzi.</p>";
        message.TemplateName = "Zaproszenie – Łódź";
        await message.SaveTemplateCommand.ExecuteAsync(null);
        Directory.CreateDirectory(scenario.Paths.SettingsDirectory);
        var file = Path.Combine(scenario.Paths.SettingsDirectory, "szablony.json");
        scenario.Harness.Files.NextSavePath = file;

        await message.ExportTemplatesCommand.ExecuteAsync(null);

        Assert.Equal(MessagePageViewModel.TemplatesFileName, scenario.Harness.Files.LastDefaultFileName);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, Token)))
        {
            Assert.Equal(CorrespondenceTemplateLibrary.ExportFormat, document.RootElement.GetProperty("format").GetString());
        }

        scenario.Harness.Files.NextOpenPath = file;
        await message.ImportTemplatesCommand.ExecuteAsync(null);
        Assert.Equal(["Zaproszenie – Łódź", "Zaproszenie – Łódź"], scenario.Templates.Saved.Select(t => t.Name));
        Assert.Equal(3, message.Templates.Count);
        Assert.Contains((NotificationSeverity.Success, "Zaimportowano szablony: 1."), scenario.Harness.Notifications.Items);

        var other = Path.Combine(scenario.Paths.SettingsDirectory, "inny.json");
        await File.WriteAllTextAsync(other, "{\"format\":\"AdMng.MessageTemplates\",\"version\":2}", Token);
        scenario.Harness.Files.NextOpenPath = other;
        await message.ImportTemplatesCommand.ExecuteAsync(null);
        Assert.Equal("To nie jest plik szablonów korespondencji AD Managera.", scenario.Ui.Errors[^1].Message);
    }

    [Fact]
    public async Task NewMessage_AsksAndClearsTheMessage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.Subject = "Zaproszenie";
        message.BodyHtml = "<p>Treść</p>";
        message.CustomFields.Add(new CustomFieldViewModel("Termin", "jutro"));
        message.ListItemTemplate = "{{Skrzynka}}";
        message.Salutation = "Szanowna Pani";

        scenario.Ui.ConfirmAnswers.Enqueue(false);
        await message.NewMessageCommand.ExecuteAsync(null);
        Assert.Equal("Zaproszenie", message.Subject);

        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await message.NewMessageCommand.ExecuteAsync(null);
        Assert.Equal(("", true, 0, ""), (message.Subject, message.IsBodyEmpty, message.CustomFields.Count, message.ListItemTemplate));
        Assert.Equal("Szanowna Pani", message.Salutation);   // an option, not the message
        Assert.Equal(2, scenario.Ui.Shown.Count);

        await message.NewMessageCommand.ExecuteAsync(null);   // nothing to lose: no question
        Assert.Equal(2, scenario.Ui.Shown.Count);
    }

    [Fact]
    public async Task Options_RoundTripThroughTheSettings()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var settings = scenario.Harness.Settings.Current.Correspondence;
        settings.Salutation = "Szanowni Państwo Radni";
        settings.DelaySeconds = 5.5;
        settings.SendMode = CorrespondenceSendMode.Bcc;
        settings.BccChunkSize = 120;
        settings.ReadReceipt = true;
        settings.GroupByAddress = true;
        settings.VotingEnabled = true;
        settings.VotingOptions = "Tak;Nie";
        settings.DeferTime = "7:45";

        using var window = await scenario.OpenAsync();
        var message = window.Message;

        Assert.Equal(("Szanowni Państwo Radni", 5.5, CorrespondenceSendMode.Bcc, 120), (message.Salutation, message.DelaySeconds, message.SendMode.Value, message.BccChunkSize));
        Assert.Equal((true, true, true, "Tak;Nie", "7:45"), (message.ReadReceipt, message.GroupByAddress, message.UseVoting, message.VotingOptions, message.DeliverTime));
        Assert.True(message.IsBcc);
        Assert.Equal("Jedna wiadomość, odbiorcy w UDW", message.SendMode.Text);

        message.BccChunkSize = 900;
        message.DelaySeconds = -3;
        Assert.Equal((500, 0d), (message.BccChunkSize, message.DelaySeconds));
        message.BccChunkSize = 0;
        message.DelaySeconds = double.NaN;
        Assert.Equal((1, 3d), (message.BccChunkSize, message.DelaySeconds));

        message.SendMode = message.SendModeChoices[0];
        message.Subject = "Temat";
        var saved = new CorrespondenceSettings();
        message.SaveTo(saved);
        Assert.Equal((CorrespondenceSendMode.Individual, 1, 3d, "Temat"), (saved.SendMode, saved.BccChunkSize, saved.DelaySeconds, saved.Subject));
        Assert.Equal(("Szanowni Państwo Radni", true, "Tak;Nie", "7:45", ""), (saved.Salutation, saved.VotingEnabled, saved.VotingOptions, saved.DeferTime, saved.SignatureName));
    }

    [Fact]
    public async Task DraftAsync_WaitsForTheChosenSignature()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        message.Subject = "Temat";
        message.BodyHtml = "<p>Treść</p>";
        message.SelectedSignature = message.Signatures[2];

        var draft = await message.DraftAsync();

        Assert.Equal(CorrespondencePagesScenario.ParsingSignatures.Secretariat, draft.Signature?.Name);
        var template = message.ToTemplate("Szablon");
        Assert.Equal(("Szablon", "Temat", "<p>Treść</p>"), (template.Name, template.Subject, template.BodyHtml));
    }
}
