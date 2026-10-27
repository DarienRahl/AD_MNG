using AdMng.Core.Messaging.Html;

namespace AdMng.Testing.RichHtml;

/// <summary>
/// The clipboard image files of the U1c fixtures in memory: <c>clip_image00N.png</c> / <c>.jpg</c> by base name (PNGs and
/// JPEGs with metadata, so stripping is visible), anything else not found. Records every URI it was asked for – the
/// importer must never hand it a network path.
/// </summary>
public sealed class FakePastedImageSource : IPastedImageSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakePastedImageSource()
    {
        Add("clip_image001.png", MessageImageSamples.Png(9, 3, seed: 0x01, withMetadata: true));
        Add("clip_image002.png", MessageImageSamples.Png(12, 4, seed: 0x02, withMetadata: true));
        Add("clip_image003.jpg", MessageImageSamples.Jpeg(20, 10, withMetadata: true));
        Add("clip_image005.png", MessageImageSamples.Png(5, 5, seed: 0x05));
    }

    /// <summary>URIs asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Adds (or replaces) a file by base name.</summary>
    public FakePastedImageSource Add(string fileName, byte[] data)
    {
        _files[fileName] = data;
        return this;
    }

    public Task<PastedImageRead> ReadAsync(string fileUri, int maxBytes, CancellationToken cancellationToken)
    {
        Requests.Add(fileUri);
        var name = fileUri[(fileUri.LastIndexOfAny(['/', '\\']) + 1)..];
        if (!_files.TryGetValue(name, out var data))
        {
            return Task.FromResult(new PastedImageRead(null, name, PastedImageFailure.NotFound));
        }

        return Task.FromResult(data.Length > maxBytes
            ? new PastedImageRead(null, name, PastedImageFailure.TooLarge)
            : new PastedImageRead(data, name));
    }
}
