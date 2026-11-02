using System.Text.Json;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using ManualTimeProvider = AdMng.Testing.ManualTimeProvider;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The templates of Korespondencja (design note 6.1, 6.2): the built-in mailbox query, saved templates with their images
/// in the image store, export / import, the last draft, and garbage collection that keeps the images of both template
/// kinds.
/// </summary>
public sealed class CorrespondenceTemplateLibraryTests : IDisposable
{
    private readonly TempAppPaths _paths = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly MessageImageStore _store;
    private readonly CorrespondenceTemplateLibrary _library;

    public CorrespondenceTemplateLibraryTests()
    {
        _store = new MessageImageStore(_paths, _time);
        _library = new CorrespondenceTemplateLibrary(_settings, _store, _time);
    }

    public void Dispose() => _paths.Dispose();

    private static MessageImage Logo => CorrespondenceShellScenario.Image(8, 4, 0x21, "herb-gminy.png");

    private static MessageImage Stamp => CorrespondenceShellScenario.Image(6, 6, 0x63, "pieczątka.png");

    private static CorrespondenceTemplate Template(string name, params MessageImage[] images) => new()
    {
        Name = name,
        Subject = "Zaproszenie – {{Miejscowość}}",
        BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy do Łodzi.</p>"
            + string.Concat(images.Select(i => $"<p><img src=\"cid:{i.ContentId}\" alt=\"{i.FileName}\"></p>")),
        Images = [.. images.Select(MessageTemplateImage.From)],
        CustomFields = [new CustomFieldDefinition("Termin", "15 listopada")],
        ListItemTemplate = "{{Skrzynka}}",
        Salutation = "Szanowni Państwo",
        VotingEnabled = false,
    };

    [Fact]
    public void BuiltIns_TheMailboxQueryPerMailbox_ThenGroupedByOwner()
    {
        var builtIns = _library.BuiltIns;

        Assert.Equal([CorrespondenceTemplateLibrary.MailboxStillNeededId, CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId], builtIns.Select(t => t.Id));
        Assert.All(builtIns, t => Assert.True(CorrespondenceTemplateLibrary.IsBuiltIn(t.Id)));
        var single = builtIns[0];
        Assert.Equal("Weryfikacja skrzynki pocztowej (wiadomość na każdą skrzynkę)", single.Name);
        Assert.Equal("Weryfikacja skrzynki pocztowej: {{Skrzynka}}", single.Subject);
        Assert.False(single.GroupByAddress);
        Assert.Contains("wskazana w temacie tej wiadomości", single.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(["Termin", "podpis"], FieldTemplate.UsedFields(single.BodyHtml));
        var grouped = builtIns[1];
        Assert.Equal("Weryfikacja skrzynek pocztowych (jedna wiadomość na osobę)", grouped.Name);
        Assert.Equal("Weryfikacja skrzynek pocztowych", grouped.Subject);
        Assert.True(grouped.GroupByAddress);
        Assert.Contains("<p style=\"margin:0\">{{lista}}</p>", grouped.BodyHtml, StringComparison.Ordinal);   // a paragraph of its own
        Assert.Equal(["lista", "Termin", "podpis"], FieldTemplate.UsedFields(grouped.BodyHtml));
        Assert.All(builtIns, t =>
        {
            // The deadline is a date field: today (2026-10-13 on the test clock) + 14 days, "15 listopada 2026r." style.
            Assert.Equal([new CustomFieldDefinition("Termin", "2026-10-27", CustomFieldKind.Date, CustomFieldFormats.DefaultDate)], t.CustomFields);
            Assert.Equal("27 października 2026r.", CustomFieldFormats.DateText(t.CustomFields[0]));
            Assert.Equal("{{Skrzynka}} ({{Adres skrzynki}})", t.ListItemTemplate);
            Assert.Null(t.Salutation);
            Assert.False(t.VotingEnabled);
            Assert.Equal(("Aptos,sans-serif", 12d, "normal"), (t.FontFamily, t.FontSize!.Value, t.LineHeight));
            Assert.Equal(new MessageBaseStyle("Aptos,sans-serif", 12, "normal"), CorrespondenceTemplateLibrary.StyleOf(t));
            Assert.Empty(t.Images);
            Assert.DoesNotContain('\r', t.BodyHtml);
            Assert.False(t.BodyHtml.EndsWith('\n'));
        });

        // New instances: changing one changes nothing else; the deadline follows the clock.
        single.Subject = "zmieniony";
        Assert.Equal("Weryfikacja skrzynki pocztowej: {{Skrzynka}}", _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!.Subject);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, _library.All[0].Id);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId, _library.All[1].Id);
        _time.Advance(TimeSpan.FromDays(20));
        Assert.Equal("2026-11-16", _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId)!.CustomFields[0].Value);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId, CorrespondenceTemplateLibrary.MailboxTemplateId(groupByOwner: true));
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, CorrespondenceTemplateLibrary.MailboxTemplateId(groupByOwner: false));
    }

    [Fact]
    public void BuiltIn_Grouped_RendersItsListWithoutAnEmptyParagraph()
    {
        var builtIn = _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId)!;
        var draft = new CorrespondenceDraft
        {
            Subject = builtIn.Subject,
            BodyHtml = builtIn.BodyHtml,
            CustomFields = builtIn.CustomFields,
            ListItemTemplate = builtIn.ListItemTemplate,
            GroupByAddress = true,
        };
        var unit = new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski",
        [
            new Dictionary<string, string> { ["Skrzynka"] = "Sekretariat IT", ["Adres skrzynki"] = "sekretariat.it@contoso.test" },
            new Dictionary<string, string> { ["Skrzynka"] = "Rekrutacja", ["Adres skrzynki"] = "rekrutacja@contoso.test" },
        ]);

        var rendered = CorrespondenceRenderer.Render(draft, unit, ["Skrzynka", "Adres skrzynki"]);

        Assert.Contains("<li>Sekretariat IT (sekretariat.it@contoso.test)</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("<li>Rekrutacja (rekrutacja@contoso.test)</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("<b>27 października 2026r.</b>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><ul>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<p style=\"margin:0\"></p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<p></p>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Empty(rendered.UnknownFields);
        Assert.Empty(rendered.EmptyFields);
    }

    [Fact]
    public void BuiltInBodies_AreTheOperatorsExampleMessage_Unchanged()
    {
        // The embedded files are the golden HTML byte for byte (LF line ends, pinned by .gitattributes): any edit of their
        // text or styles breaks the 1:1 look of the message.
        Assert.Equal("c03bfcb9ad637c4a933f6e23484c85a9118ef924c00aeefe7ef44f48f355b887", ResourceSha256("mailbox-still-needed.html"));
        Assert.Equal("0cb9a68857a150f4b254d715e9369c16593dd825bb7615ed60a1fc090baa4442", ResourceSha256("mailbox-still-needed-grouped.html"));
        Assert.Equal(ResourceText("mailbox-still-needed.html").TrimEnd('\n'), _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!.BodyHtml);
        Assert.Equal(ResourceText("mailbox-still-needed-grouped.html").TrimEnd('\n'), _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId)!.BodyHtml);
    }

    private static byte[] ResourceBytes(string fileName)
    {
        using var stream = typeof(CorrespondenceTemplateLibrary).Assembly.GetManifestResourceStream("AdMng.Presentation.Correspondence.Templates." + fileName)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string ResourceSha256(string fileName) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ResourceBytes(fileName)));

    private static string ResourceText(string fileName) => System.Text.Encoding.UTF8.GetString(ResourceBytes(fileName));

    [Fact]
    public async Task Save_NewReplaceDelete_AndBuiltInsAreNeverStored()
    {
        var changes = 0;
        _library.Changed += (_, _) => changes++;

        var saved = await _library.SaveAsync(Template("  Zaproszenie na szkolenie  "));
        Assert.Equal("Zaproszenie na szkolenie", saved.Name);
        Assert.Equal(32, saved.Id.Length);
        var copy = _library.Find(saved.Id)!;
        Assert.Equal("Zaproszenie – {{Miejscowość}}", copy.Subject);
        Assert.Equal([new CustomFieldDefinition("Termin", "15 listopada")], copy.CustomFields);

        copy.Subject = "Nowy temat";
        Assert.Equal("Zaproszenie – {{Miejscowość}}", _library.Find(saved.Id)!.Subject);   // copies only
        var replaced = await _library.SaveAsync(copy);
        Assert.Equal(saved.Id, replaced.Id);
        Assert.Equal("Nowy temat", Assert.Single(_library.Saved).Subject);

        var fromBuiltIn = _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!;
        fromBuiltIn.Name = "Skrzynki – moja wersja";
        var own = await _library.SaveAsync(fromBuiltIn);
        Assert.NotEqual(CorrespondenceTemplateLibrary.MailboxStillNeededId, own.Id);
        Assert.Equal(2, _library.Saved.Count);
        Assert.Equal(4, _library.All.Count);   // the two built-ins and the two saved
        Assert.DoesNotContain(_settings.Current.Correspondence.Templates, t => CorrespondenceTemplateLibrary.IsBuiltIn(t.Id));

        await _library.DeleteAsync(CorrespondenceTemplateLibrary.MailboxStillNeededId);
        await _library.DeleteAsync("nieznany");
        Assert.Equal(2, _library.Saved.Count);
        await _library.DeleteAsync(saved.Id);
        Assert.Equal(own.Id, Assert.Single(_library.Saved).Id);
        Assert.Null(_library.Find(saved.Id));
        Assert.Null(_library.Find(null));
        Assert.Equal(4, changes);
        await Assert.ThrowsAsync<ArgumentException>(() => _library.SaveAsync(Template("  ")));
    }

    [Fact]
    public async Task Save_RefusesTemplatesBeyondTheLimit()
    {
        await _settings.UpdateAsync(s => s.Correspondence.Templates.AddRange(
            Enumerable.Range(0, CorrespondenceSettings.MaxTemplates).Select(i => new CorrespondenceTemplate { Name = $"Szablon {i}" })), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.SaveAsync(Template("Jeden za dużo")));

        var existing = _library.Find(_library.Saved[0].Id)!;
        existing.Subject = "Zmiana istniejącego";
        await _library.SaveAsync(existing);
        Assert.Equal("Zmiana istniejącego", _library.Saved[0].Subject);
    }

    [Fact]
    public async Task Save_KeepsTheReferencedImages_InTheStore()
    {
        var template = Template("Z herbem", Logo);
        template.BodyHtml += "<p>bez obrazka</p>";
        template.Images.Add(MessageTemplateImage.From(Stamp));     // listed but not referenced: dropped

        var saved = await _library.SaveAsync(template, [Logo, Stamp]);

        Assert.Equal([Logo.ContentId], saved.Images.Select(i => i.ContentId));
        Assert.True(File.Exists(_store.PathOf(Logo.Sha256, Logo.MediaType)));
        Assert.False(File.Exists(_store.PathOf(Stamp.Sha256, Stamp.MediaType)));
        var images = await _library.LoadImagesAsync(_library.Find(saved.Id)!, TestContext.Current.CancellationToken);
        Assert.Equal(Logo.Data, images[Logo.ContentId].Data);

        // Saved again without image bytes: the metadata of the referenced image stays.
        var again = await _library.SaveAsync(_library.Find(saved.Id)!);
        Assert.Equal([Logo.ContentId], again.Images.Select(i => i.ContentId));
    }

    [Fact]
    public async Task ImagesLargerThanTheLimitOfMessagesToUsers_AreKept()
    {
        var large = CorrespondenceShellScenario.Image(640, 600, 0x10, "zrzut-ekranu.png");
        Assert.True(large.Data.Length > MessageImageLimits.MaxImageBytes);

        var saved = await _library.SaveAsync(Template("Instrukcja ze zrzutem", large), [large]);

        Assert.Single(_settings.Current.Correspondence.Templates.Single(t => t.Id == saved.Id).Images);   // normalization keeps it
        var images = await _library.LoadImagesAsync(_library.Find(saved.Id)!, TestContext.Current.CancellationToken);
        Assert.Equal(large.Sha256, images[large.ContentId].Sha256);
        Assert.Null(await _store.LoadAsync(MessageTemplateImage.From(large), TestContext.Current.CancellationToken));  // the U1 limit refuses it
    }

    [Fact]
    public async Task ExportImport_RoundTrip_WithImages_AndNewIds()
    {
        var first = await _library.SaveAsync(Template("Zaproszenie", Logo), [Logo]);
        var second = await _library.SaveAsync(Template("Przypomnienie – Łódź", Logo, Stamp), [Logo, Stamp]);

        var json = await _library.ExportJsonAsync();

        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal("AdMng.CorrespondenceTemplates", document.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("templates").GetArrayLength());
            Assert.Equal(2, document.RootElement.GetProperty("images").GetArrayLength());   // each image once
            Assert.Equal(Convert.ToBase64String(Logo.Data), document.RootElement.GetProperty("images")[0].GetProperty("data").GetString());
        }

        // Another computer: empty settings and image folder.
        using var otherPaths = new TempAppPaths();
        var otherSettings = new InMemorySettingsStore();
        var otherStore = new MessageImageStore(otherPaths, _time);
        var other = new CorrespondenceTemplateLibrary(otherSettings, otherStore);
        var result = await other.ImportAsync(json);

        Assert.Equal(new TemplateImportResult(2, 0), result);
        Assert.Equal(["Zaproszenie", "Przypomnienie – Łódź"], other.Saved.Select(t => t.Name));
        Assert.DoesNotContain(other.Saved, t => t.Id == first.Id || t.Id == second.Id);
        var imported = other.Find(other.Saved[1].Id)!;
        Assert.Equal("Zaproszenie – {{Miejscowość}}", imported.Subject);
        Assert.Contains("{{zwrot}}", imported.BodyHtml, StringComparison.Ordinal);
        Assert.Equal([new CustomFieldDefinition("Termin", "15 listopada")], imported.CustomFields);
        var images = await other.LoadImagesAsync(imported, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { Logo.Sha256, Stamp.Sha256 }.Order(), images.Values.Select(i => i.Sha256).Order());

        // Importing again adds copies (never overwrites).
        await other.ImportAsync(json);
        Assert.Equal(4, other.Saved.Count);
        Assert.Equal(4, other.Saved.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task DateFieldsAndTheBaseStyle_SurviveSavingExportAndImport()
    {
        var template = Template("Weryfikacja – kopia");
        template.CustomFields = [new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date, "dd.MM.yyyy"), new CustomFieldDefinition("Wydział", "Łączności")];
        template.FontFamily = "Aptos,sans-serif";
        template.FontSize = 12;
        template.LineHeight = "normal";

        var saved = await _library.SaveAsync(template);
        var json = await _library.ExportJsonAsync();
        var other = new CorrespondenceTemplateLibrary(new InMemorySettingsStore(), new MessageImageStore(_paths, _time));
        await other.ImportAsync(json);

        foreach (var copy in new[] { _library.Find(saved.Id)!, Assert.Single(other.Saved) })
        {
            Assert.Equal(
                [new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date, "dd.MM.yyyy"), new CustomFieldDefinition("Wydział", "Łączności")],
                copy.CustomFields);
            Assert.Equal(new MessageBaseStyle("Aptos,sans-serif", 12, "normal"), CorrespondenceTemplateLibrary.StyleOf(copy));
        }

        using var document = JsonDocument.Parse(json);
        var field = document.RootElement.GetProperty("templates")[0].GetProperty("customFields")[0];
        Assert.Equal("Date", field.GetProperty("kind").GetString());
        Assert.Equal("dd.MM.yyyy", field.GetProperty("format").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());   // additive: still version 1
    }

    [Fact]
    public async Task OlderFilesWithoutKindsOrFonts_ImportAsTextFields_KeepingTheWindowsFont()
    {
        const string json = """
            {"format":"AdMng.CorrespondenceTemplates","version":1,"templates":[{"id":"x","name":"Stary szablon","subject":"Temat",
             "bodyHtml":"<p>Treść {{Termin}}</p>","customFields":[{"name":"Termin","value":"15 listopada"}]}]}
            """;

        await _library.ImportAsync(json);

        var imported = Assert.Single(_library.Saved);
        Assert.Equal([new CustomFieldDefinition("Termin", "15 listopada")], imported.CustomFields);
        Assert.Null(CorrespondenceTemplateLibrary.StyleOf(imported));
    }

    [Fact]
    public void SettingsNormalization_KeepsKindAndFormat_ADateThatDoesNotParseIsEmpty()
    {
        var settings = new CorrespondenceSettings
        {
            LastDraft = new CorrespondenceTemplate
            {
                Name = "Szkic",
                CustomFields =
                [
                    new CustomFieldDefinition(" Termin ", " 2026-11-15 ", CustomFieldKind.Date, "  d MMMM yyyy  "),
                    new CustomFieldDefinition("Spotkanie", "jutro", CustomFieldKind.Date, " "),
                    new CustomFieldDefinition("Długi", "2026-11-15", CustomFieldKind.Date, new string('d', CustomFieldFormats.MaxFormatLength + 1)),
                    new CustomFieldDefinition("Opis", "tekst", CustomFieldKind.Text, "dd.MM.yyyy"),
                    new CustomFieldDefinition("Dziwny", "wartość", (CustomFieldKind)7, "x"),
                ],
                FontFamily = "Arial\" onclick=\"x",
                FontSize = 400,
                LineHeight = " 1.15 ",
            },
        };

        settings.Normalize();

        Assert.Equal(
        [
            new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date, "d MMMM yyyy"),
            new CustomFieldDefinition("Spotkanie", "", CustomFieldKind.Date),
            new CustomFieldDefinition("Długi", "2026-11-15", CustomFieldKind.Date),
            new CustomFieldDefinition("Opis", "tekst"),
            new CustomFieldDefinition("Dziwny", "wartość"),
        ], settings.LastDraft.CustomFields);
        Assert.Null(settings.LastDraft.FontFamily);
        Assert.Null(settings.LastDraft.FontSize);
        Assert.Equal("1.15", settings.LastDraft.LineHeight);
        Assert.False(new MailboxOwnerSettings().GroupByOwner);   // one message per mailbox unless the operator groups
    }

    [Fact]
    public async Task Import_ParsesValidatesAndSanitizesOffTheCallersThread()
    {
        // Up to 20 MiB of JSON with base64 images and 100 templates to sanitize: never on the UI thread (rule 6). On a
        // thread with a synchronization context the import gives the thread back before the work is done.
        await _library.SaveAsync(Template("Zaproszenie"));
        var json = await _library.ExportJsonAsync();
        var other = new CorrespondenceTemplateLibrary(new InMemorySettingsStore(), _store);
        using var ui = new Watchdog.TestUiThread();

        var completedOnTheCallersThread = true;
        TemplateImportResult? result = null;
        await ui.RunAsync(async () =>
        {
            var import = other.ImportAsync(json);
            completedOnTheCallersThread = import.IsCompleted;
            result = await import;
        });

        Assert.False(completedOnTheCallersThread);
        Assert.Equal(new TemplateImportResult(1, 0), result);
        Assert.Equal(["Zaproszenie"], other.Saved.Select(t => t.Name));
        Assert.Empty(ui.Errors);
    }

    [Fact]
    public async Task Import_ChecksEveryImage_AndSanitizesTheHtml()
    {
        await _library.SaveAsync(Template("Z obrazkami", Logo, Stamp), [Logo, Stamp]);
        var json = await _library.ExportJsonAsync();

        // Tampered: a wrong hash for the logo, a script and an event handler in the body.
        using var document = JsonDocument.Parse(json);
        var images = document.RootElement.GetProperty("images").EnumerateArray()
            .Select(i => new
            {
                sha256 = i.GetProperty("sha256").GetString() == Logo.Sha256 ? new string('0', 64) : i.GetProperty("sha256").GetString(),
                mediaType = i.GetProperty("mediaType").GetString(),
                fileName = i.GetProperty("fileName").GetString(),
                data = i.GetProperty("data").GetString(),
            })
            .ToList();
        var templates = document.RootElement.GetProperty("templates").EnumerateArray()
            .Select(t => JsonSerializer.Deserialize<CorrespondenceTemplate>(t.GetRawText(), JsonSettingsStore.SerializerOptions)!)
            .ToList();
        templates[0].BodyHtml += "<script>alert('x')</script><p onclick=\"zło()\">Klik</p>";
        var tampered = JsonSerializer.Serialize(new { format = CorrespondenceTemplateLibrary.ExportFormat, version = 1, templates, images }, JsonSettingsStore.SerializerOptions);

        using var otherPaths = new TempAppPaths();
        var other = new CorrespondenceTemplateLibrary(new InMemorySettingsStore(), new MessageImageStore(otherPaths, _time));
        var result = await other.ImportAsync(tampered);

        Assert.Equal(new TemplateImportResult(1, 1), result);
        var imported = Assert.Single(other.Saved);
        Assert.Equal([Stamp.ContentId], imported.Images.Select(i => i.ContentId));
        Assert.DoesNotContain(Logo.ContentId, imported.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Stamp.ContentId, imported.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", imported.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", imported.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Klik", imported.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportImport_KeepsThePlainTextListItemTemplateAsTyped()
    {
        var template = Template("Skrzynki");
        template.ListItemTemplate = "{{Skrzynka}} <{{Adres skrzynki}}> & „więcej” <b>";
        await _library.SaveAsync(template, []);
        var json = await _library.ExportJsonAsync();

        using var otherPaths = new TempAppPaths();
        var other = new CorrespondenceTemplateLibrary(new InMemorySettingsStore(), new MessageImageStore(otherPaths, _time));
        await other.ImportAsync(json);

        Assert.Equal("{{Skrzynka}} <{{Adres skrzynki}}> & „więcej” <b>", Assert.Single(other.Saved).ListItemTemplate);
    }

    [Fact]
    public async Task Import_RefusesWhatIsNotATemplatesFile()
    {
        await Assert.ThrowsAsync<FormatException>(() => _library.ImportAsync("{ to nie jest JSON"));
        await Assert.ThrowsAsync<FormatException>(() => _library.ImportAsync("{\"format\":\"AdMng.MessageTemplates\",\"version\":2,\"templates\":[]}"));
        await Assert.ThrowsAsync<FormatException>(() => _library.ImportAsync("{\"format\":\"AdMng.CorrespondenceTemplates\",\"version\":2,\"templates\":[]}"));
        await Assert.ThrowsAsync<FormatException>(() => _library.ImportAsync(new string(' ', CorrespondenceTemplateLibrary.MaxFileLength + 1)));
        Assert.Equal(new TemplateImportResult(0, 0), await _library.ImportAsync("{\"format\":\"AdMng.CorrespondenceTemplates\",\"version\":1,\"templates\":[{\"name\":\"  \"}]}"));
        Assert.Empty(_library.Saved);
    }

    [Fact]
    public async Task Export_RefusesFilesOverTheLimit()
    {
        var large = Enumerable.Range(0, 4).Select(i => CorrespondenceShellScenario.Image(1400, 1200, (byte)(0x10 + i), $"zrzut{i}.png")).ToArray();
        var template = Template("Wielki", large);
        await _library.SaveAsync(template, large);

        await Assert.ThrowsAsync<IOException>(() => _library.ExportJsonAsync());
    }

    [Fact]
    public async Task LastDraft_SavedWithItsImages_AndLoadedAsACopy()
    {
        Assert.Null(_library.LoadLastDraft());
        var draft = CorrespondenceTemplateLibrary.FromDraft(new CorrespondenceDraft
        {
            Subject = "Szkic – zebranie",
            BodyHtml = $"<p>{{{{nazwa}}}}</p><p><img src=\"cid:{Logo.ContentId}\"></p>",
            Images = new Dictionary<string, MessageImage> { [Logo.ContentId] = Logo, [Stamp.ContentId] = Stamp },
            CustomFields = [new CustomFieldDefinition("Termin", "jutro")],
            ListItemTemplate = "{{Skrzynka}}",
            Salutation = "Szanowna Pani",
        });
        draft.VotingEnabled = true;
        draft.GroupByAddress = false;

        await _library.SaveLastDraftAsync(draft, [Logo, Stamp]);

        var loaded = _library.LoadLastDraft()!;
        Assert.Equal("Szkic – zebranie", loaded.Subject);
        Assert.Equal("Szanowna Pani", loaded.Salutation);
        Assert.Equal([Logo.ContentId], loaded.Images.Select(i => i.ContentId));
        Assert.True(loaded.VotingEnabled);
        Assert.False(loaded.GroupByAddress);
        Assert.Equal(Logo.Data, (await _library.LoadImagesAsync(loaded, TestContext.Current.CancellationToken))[Logo.ContentId].Data);
        loaded.Subject = "zmiana kopii";
        Assert.Equal("Szkic – zebranie", _library.LoadLastDraft()!.Subject);
        Assert.Empty(_library.Saved);    // the draft is no template
    }

    [Fact]
    public async Task GarbageCollection_KeepsTheImagesOfEveryTemplateKind()
    {
        // A U1 template, a Korespondencja template and the last draft each hold one image.
        var userMessages = new UserMessageTemplateLibrary(_settings, new Presentation.Localization.ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("pl-PL")), _store);
        var u1Image = MessageImage.Create(MessageImageSamples.Png(5, 5, 0x77), "u1.png");
        await userMessages.SaveAsync(new MessageTemplate
        {
            Name = "Hasło wygasa – wersja działu",
            Polish = new MessageTemplateText { Subject = "Hasło", Html = $"<p>Treść</p><p><img src=\"cid:{u1Image.ContentId}\"></p>" },
            Images = [MessageTemplateImage.From(u1Image)],
        }, [u1Image]);
        var saved = await _library.SaveAsync(Template("Z herbem", Logo), [Logo]);
        await _library.SaveLastDraftAsync(Template("szkic", Stamp), [Stamp]);
        var orphan = CorrespondenceShellScenario.Image(3, 3, 0x05, "sierota.png");
        await _store.SaveAsync([orphan], TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, _store.CollectGarbage(_settings.Current));      // only the orphan
        Assert.False(File.Exists(_store.PathOf(orphan.Sha256, orphan.MediaType)));

        // Saving and deleting in either library never deletes the other's images.
        await userMessages.SaveAsync(userMessages.Find(userMessages.Custom[0].Id)! with { Name = "Zmieniony" });
        await _library.DeleteAsync(saved.Id);
        _time.Advance(TimeSpan.FromHours(1));
        await _library.SaveLastDraftAsync(_library.LoadLastDraft()!);
        Assert.True(File.Exists(_store.PathOf(u1Image.Sha256, u1Image.MediaType)));
        Assert.True(File.Exists(_store.PathOf(Stamp.Sha256, Stamp.MediaType)));
        Assert.False(File.Exists(_store.PathOf(Logo.Sha256, Logo.MediaType)));      // its template was deleted
        Assert.Equal(
            [u1Image.Sha256, Stamp.Sha256],
            MessageImageStore.ReferencedImages(_settings.Current).Select(i => i.Sha256));
    }
}
