using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json;
using AdMng.Core.Messaging.Images;
using AdMng.Core.Messaging;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// Templates with images in the library (U1c, design note section 11): saving writes the images first and normalizes the
/// HTML, export format version 2 carries each image once, import accepts versions 1 and 2, checks every image again, and
/// a template whose image fails keeps its text; files above 20 MiB are refused; garbage collection after delete.
/// </summary>
public sealed class TemplateLibraryImagesTests : IDisposable
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(18, 6, seed: 0x73, withMetadata: true), "logo.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(30, 20, withMetadata: true), "zrzut.jpg");

    private readonly TempAppPaths _paths = new();
    private readonly TestDoubles.ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemorySettingsStore _settings = new();
    private readonly MessageImageStore _images;
    private readonly UserMessageTemplateLibrary _library;

    public TemplateLibraryImagesTests()
    {
        _images = new MessageImageStore(_paths, _time);
        _library = new UserMessageTemplateLibrary(_settings, new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), _images);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _paths.Dispose();

    private static MessageTemplate Template(string name, string html, params MessageImage[] images) => new()
    {
        Id = "nowy",
        Name = name,
        Polish = new MessageTemplateText { Subject = "Instrukcja – zażółć gęślą jaźń", Body = "Dzień dobry {givenName}", Html = html },
        Images = [.. images.Select(MessageTemplateImage.From)],
    };

    private static string Img(MessageImage image, string alt = "obraz") => $"<img src=\"cid:{image.ContentId}\" alt=\"{alt}\">";

    [Fact]
    public async Task Save_WritesTheImagesFirst_NormalizesTheHtml_AndDropsUnreferencedImages()
    {
        var stored = await _library.SaveAsync(
            Template("Instrukcja z logo", $"<P CLASS=MsoNormal>Logo {Img(Logo, "Logo – Dział IT")}</P>", Logo, Photo),
            [Logo, Photo]);

        Assert.Equal($"<p>Logo {Img(Logo, "Logo – Dział IT")}</p>\n", stored.Polish.Html);
        Assert.Equal([Logo.ContentId], stored.Images.Select(i => i.ContentId));
        Assert.True(File.Exists(_images.PathOf(Logo.Sha256, Logo.MediaType)));
        Assert.False(File.Exists(_images.PathOf(Photo.Sha256, Photo.MediaType)));
        var saved = Assert.Single(_settings.Current.UserMessages.Templates);
        Assert.Equal((stored.Id, stored.Polish.Html), (saved.Id, saved.Polish.Html));
        Assert.Equal([Logo.ContentId], saved.Images.Select(i => i.ContentId));
        Assert.Equal(Logo.Data, (await _library.LoadImagesAsync(saved, Ct))[Logo.ContentId].Data);
    }

    [Fact]
    public async Task HtmlWithASecurityFinding_IsSavedUnchanged()
    {
        var stored = await _library.SaveAsync(Template("Zły", "<p onclick=\"x()\">Kliknij</p>"));
        Assert.Equal("<p onclick=\"x()\">Kliknij</p>", stored.Polish.Html);
    }

    [Fact]
    public async Task ExportVersion2_CarriesEachImageOnce_AndImportsIntoAnotherLibrary()
    {
        await _library.SaveAsync(Template("Pierwszy", $"<p>{Img(Logo)}</p>", Logo), [Logo]);
        await _library.SaveAsync(Template("Drugi", $"<p>{Img(Logo)} {Img(Photo)}</p>", Logo, Photo), [Logo, Photo]);

        var json = await _library.ExportJsonAsync();
        var root = JsonNode.Parse(json)!;
        Assert.Equal(2, root["version"]!.GetValue<int>());
        var images = root["images"]!.AsArray();
        Assert.Equal(2, images.Count);
        Assert.Equal(Convert.ToBase64String(Logo.Data), images.Single(i => i!["sha256"]!.GetValue<string>() == Logo.Sha256)!["data"]!.GetValue<string>());

        using var otherPaths = new TempAppPaths();
        var otherSettings = new InMemorySettingsStore();
        var other = new UserMessageTemplateLibrary(otherSettings, new ResxLocalizer(), new MessageImageStore(otherPaths, _time));
        var result = await other.ImportTemplatesAsync(json);

        Assert.Equal(new TemplateImportResult(2, 0), result);
        var imported = otherSettings.Current.UserMessages.Templates.Single(t => t.Name == "Drugi");
        Assert.NotEqual(_settings.Current.UserMessages.Templates.Single(t => t.Name == "Drugi").Id, imported.Id);
        var loaded = await other.LoadImagesAsync(imported, Ct);
        Assert.Equal(Photo.Data, loaded[Photo.ContentId].Data);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(otherPaths.SettingsDirectory, MessageImageStore.FolderName)).Length);
    }

    [Fact]
    public async Task ImportVersion1_WithoutImages_StillWorks()
    {
        const string json = """
            {"format":"AdMng.MessageTemplates","version":1,"templates":[{"id":"x","name":"Stary szablon – żółw","polish":{"subject":"Temat","body":"Treść"}}]}
            """;

        Assert.Equal(1, await _library.ImportAsync(json));
        Assert.Empty(Assert.Single(_settings.Current.UserMessages.Templates).Images);
    }

    [Theory]
    [InlineData("wrongHash")]
    [InlineData("svg")]
    [InlineData("missing")]
    public async Task ABadImage_IsDropped_AndItsTemplateKeepsTheText(string variant)
    {
        await _library.SaveAsync(Template("Z obrazami", $"<p>Tekst zostaje {Img(Logo)} {Img(Photo)}</p>", Logo, Photo), [Logo, Photo]);
        var root = JsonNode.Parse(await _library.ExportJsonAsync())!;
        var images = root["images"]!.AsArray();
        var logo = images.Single(i => i!["sha256"]!.GetValue<string>() == Logo.Sha256)!;
        switch (variant)
        {
            case "wrongHash":
                logo["sha256"] = new string('0', 64);
                break;
            case "svg":
                logo["data"] = Convert.ToBase64String(MessageImageSamples.Svg);
                logo["mediaType"] = "image/svg+xml";
                break;
            default:
                images.Remove(logo);
                break;
        }

        await _library.DeleteAsync(_settings.Current.UserMessages.Templates.Single().Id);
        var result = await _library.ImportTemplatesAsync(root.ToJsonString());

        Assert.Equal(new TemplateImportResult(1, 1), result);
        var imported = Assert.Single(_settings.Current.UserMessages.Templates);
        Assert.Contains("Tekst zostaje", imported.Polish.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(Logo.ContentId, imported.Polish.Html, StringComparison.Ordinal);
        Assert.Contains(Photo.ContentId, imported.Polish.Html, StringComparison.Ordinal);
        Assert.Equal([Photo.ContentId], imported.Images.Select(i => i.ContentId));
    }

    [Fact]
    public async Task FilesOver20MiB_AreRefused()
    {
        var json = "{\"format\":\"AdMng.MessageTemplates\",\"version\":2,\"templates\":[],\"x\":\"" + new string('a', UserMessageTemplateLibrary.MaxFileLength) + "\"}";
        await Assert.ThrowsAsync<FormatException>(() => _library.ImportTemplatesAsync(json));
        await Assert.ThrowsAsync<FormatException>(() => _library.ImportTemplatesAsync("{\"format\":\"AdMng.MessageTemplates\",\"version\":3,\"templates\":[]}"));
        await Assert.ThrowsAnyAsync<JsonException>(() => _library.ImportTemplatesAsync("{nie json"));
    }

    [Fact]
    public async Task Delete_CollectsTheImagesNoTemplateUsesAnyMore()
    {
        var stored = await _library.SaveAsync(Template("Do usunięcia", $"<p>{Img(Logo)}</p>", Logo), [Logo]);
        _time.Advance(TimeSpan.FromMinutes(11));

        await _library.DeleteAsync(stored.Id);

        Assert.False(File.Exists(_images.PathOf(Logo.Sha256, Logo.MediaType)));
        Assert.False(_library.ImageFolderIsLarge);
    }
}
