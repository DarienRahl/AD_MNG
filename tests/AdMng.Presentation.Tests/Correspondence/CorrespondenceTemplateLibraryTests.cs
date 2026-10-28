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
        _library = new CorrespondenceTemplateLibrary(_settings, _store);
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
    public void BuiltIn_MailboxStillNeeded_AsInTheDesignNote()
    {
        var builtIn = Assert.Single(CorrespondenceTemplateLibrary.BuiltIns);

        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, builtIn.Id);
        Assert.True(CorrespondenceTemplateLibrary.IsBuiltIn(builtIn.Id));
        Assert.Equal("Czy skrzynka pocztowa jest nadal potrzebna?", builtIn.Subject);
        Assert.Contains("<p>{{lista}}</p>", builtIn.BodyHtml, StringComparison.Ordinal);       // a paragraph of its own
        Assert.StartsWith("<p>{{Zwrot osobowy}},</p>", builtIn.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("{{Pana/Panią}}", builtIn.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Jeżeli do {{Termin}} nie otrzymamy odpowiedzi", builtIn.BodyHtml, StringComparison.Ordinal);
        Assert.Equal([new CustomFieldDefinition("Termin", "")], builtIn.CustomFields);
        Assert.Equal("{{Skrzynka}} ({{Adres skrzynki}}) – ostatnie logowanie: {{Ostatnie logowanie|brak danych}}", builtIn.ListItemTemplate);
        Assert.True(builtIn.VotingEnabled);
        Assert.Equal("Tak, nadal potrzebna;Nie, można usunąć", builtIn.VotingOptions);
        Assert.True(builtIn.GroupByAddress);
        Assert.Empty(builtIn.Images);
        Assert.Equal(["Zwrot osobowy", "Pana/Panią", "lista", "Termin"], FieldTemplate.UsedFields(builtIn.BodyHtml));

        // New instances: changing one changes nothing else.
        builtIn.Subject = "zmieniony";
        Assert.Equal("Czy skrzynka pocztowa jest nadal potrzebna?", _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!.Subject);
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStillNeededId, _library.All[0].Id);
    }

    [Fact]
    public void BuiltIn_RendersItsListWithoutAnEmptyParagraph()
    {
        var builtIn = _library.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!;
        var draft = new CorrespondenceDraft
        {
            Subject = builtIn.Subject,
            BodyHtml = builtIn.BodyHtml,
            CustomFields = builtIn.CustomFields,
            ListItemTemplate = builtIn.ListItemTemplate,
        };
        var unit = new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski",
        [
            new Dictionary<string, string> { ["Skrzynka"] = "Sekretariat IT", ["Adres skrzynki"] = "sekretariat.it@contoso.test", ["Ostatnie logowanie"] = "" },
            new Dictionary<string, string> { ["Skrzynka"] = "Rekrutacja", ["Adres skrzynki"] = "rekrutacja@contoso.test", ["Ostatnie logowanie"] = "3 marca 2026" },
        ]);

        var rendered = CorrespondenceRenderer.Render(draft, unit, ["Skrzynka", "Adres skrzynki", "Ostatnie logowanie", "Zwrot osobowy", "Pana/Panią"]);

        Assert.Contains("<li>Sekretariat IT (sekretariat.it@contoso.test) – ostatnie logowanie: brak danych</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.Contains("<li>Rekrutacja (rekrutacja@contoso.test) – ostatnie logowanie: 3 marca 2026</li>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><ul>", rendered.HtmlDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<p></p>", rendered.HtmlDocument, StringComparison.Ordinal);
    }

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
        Assert.Equal(3, _library.All.Count);
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
