using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Testing.RichHtml;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The editor's view model in the UI tests: images by content id (validated as the message page does), the formatted
/// paste scripted (by default the U1c importer over a CF_HTML fixture), every error line recorded.
/// </summary>
internal sealed class FakeEditorHost : IRichEditorHost
{
    private readonly Dictionary<string, MessageImage> _images = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, MessageImage> Images => _images;

    /// <summary>The answer of <see cref="PasteFormattedAsync"/> (null: nothing pasted).</summary>
    public Func<Task<RichPaste?>> Paste { get; set; } = () => Task.FromResult<RichPaste?>(null);

    /// <summary>Refuses every image (as the page does over its limits).</summary>
    public bool RefuseImages { get; set; }

    public int PasteCalls { get; private set; }

    public List<string?> AddedFileNames { get; } = [];

    public List<string> Errors { get; } = [];

    /// <summary>A paste of the U1c importer over <paramref name="fixture"/> (images from the fake clipboard folder).</summary>
    public static Func<Task<RichPaste?>> Imported(string fixture) => async () =>
    {
        var result = await PastedHtmlImporter.ImportAsync(
            new HtmlImportRequest
            {
                Document = CfHtmlParser.Parse(CfHtmlFixture.Build(CfHtmlFixture.Html(fixture))),
                Files = new FakePastedImageSource(),
            },
            CancellationToken.None).ConfigureAwait(true);
        return new RichPaste(result.Html, result.Images);
    };

    public Task<RichPaste?> PasteFormattedAsync()
    {
        PasteCalls++;
        return Paste();
    }

    public MessageImage? AddImage(byte[] data, string? fileName)
    {
        AddedFileNames.Add(fileName);
        if (RefuseImages)
        {
            ReportEditorError("Obraz odrzucony.");
            return null;
        }

        if (!MessageImage.TryCreate(data, fileName, null, out var image, out var error))
        {
            ReportEditorError("Obraz odrzucony: " + error);
            return null;
        }

        _images.TryAdd(image!.ContentId, image);
        return _images[image.ContentId];
    }

    /// <summary>Registers an image as the page would after loading a template.</summary>
    public FakeEditorHost With(MessageImage image)
    {
        _images[image.ContentId] = image;
        return this;
    }

    public void ReportEditorError(string text) => Errors.Add(text);
}
