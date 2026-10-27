using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AdMng.Core.Messaging.Images;

/// <summary>
/// An inline image of a message to users (U1c, design note section 7.3): validated (PNG / JPEG / GIF by magic bytes,
/// structure, dimensions, size), stripped of its metadata and identified by its content: the <see cref="ContentId"/> is
/// derived from the SHA-256 of <see cref="Data"/>, so the same image always has the same id, a template references it as
/// <c>&lt;img src="cid:img-3f2a9c0d1e4b5a67@admng"&gt;</c> and the AdMng Watchdog can verify the id against the bytes.
/// Images are static template assets (operator settings, identical for every recipient) – never directory data.
/// <see cref="Create"/> is the only factory. <see cref="ToString"/> never shows bytes or a path.
/// </summary>
public sealed partial record MessageImage
{
    /// <summary>The suffix of every content id.</summary>
    public const string ContentIdDomain = "admng";

    /// <summary>"img-" + the first 16 hex digits of the SHA-256 + "@admng".</summary>
    public required string ContentId { get; init; }

    /// <summary>image/png | image/jpeg | image/gif.</summary>
    public required string MediaType { get; init; }

    /// <summary>The validated and stripped bytes.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Data"/>.</summary>
    public required string Sha256 { get; init; }

    /// <summary>File name of the MIME part: <c>[A-Za-z0-9._-]{1,64}</c> (else <c>image{n}.{ext}</c>); never a path.</summary>
    public string FileName { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The format.</summary>
    public ImageKind Kind => ImageFormats.FromMediaType(MediaType) ?? ImageKind.Png;

    /// <summary>The content id of an image whose stripped bytes have the SHA-256 <paramref name="sha256Hex"/>.</summary>
    public static string ContentIdFor(string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Hex);
        return "img-" + sha256Hex[..Math.Min(16, sha256Hex.Length)].ToLowerInvariant() + "@" + ContentIdDomain;
    }

    /// <summary>A content id of the form <c>img-&lt;16 hex&gt;@admng</c>.</summary>
    public static bool IsContentId(string? value) => value is not null && ContentIdPattern().IsMatch(value);

    /// <summary>
    /// Validates, strips and hashes an image. <paramref name="fileName"/> (only its base name is used) and
    /// <paramref name="declaredMediaType"/> must agree with the content when given.
    /// </summary>
    /// <param name="data">The bytes as read (at most <see cref="MessageImageLimits.MaxImageBytes"/> after stripping).</param>
    /// <param name="fileName">Original file name ("clip_image001.png"), or null.</param>
    /// <param name="declaredMediaType">Media type of a data: URI or a request, or null.</param>
    /// <param name="number">Number of a generated file name (<c>image{n}.png</c>).</param>
    /// <exception cref="MessageImageException">The image is refused.</exception>
    public static MessageImage Create(ReadOnlySpan<byte> data, string? fileName = null, string? declaredMediaType = null, int number = 1)
    {
        if (data.IsEmpty)
        {
            throw new MessageImageException(MessageImageError.Empty);
        }

        var kind = ImageFormats.Detect(data) ?? throw new MessageImageException(MessageImageError.UnknownFormat);
        if (declaredMediaType is not null && ImageFormats.FromMediaType(declaredMediaType) != kind)
        {
            throw new MessageImageException(MessageImageError.TypeMismatch);
        }

        var baseName = BaseName(fileName);
        if (baseName.Contains('.', StringComparison.Ordinal) && ImageFormats.FromExtension(baseName) != kind)
        {
            throw new MessageImageException(MessageImageError.TypeMismatch);
        }

        var structure = ImageFormats.Inspect(data);
        var stripped = ImageMetadataStripper.Strip(data, structure);
        if (stripped.Length > MessageImageLimits.MaxImageBytes)
        {
            throw new MessageImageException(MessageImageError.TooLarge);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(stripped));
        return new MessageImage
        {
            ContentId = ContentIdFor(sha),
            MediaType = ImageFormats.MediaType(kind),
            Data = stripped,
            Sha256 = sha,
            FileName = SafeFileName(baseName, kind, number),
            Width = structure.Width,
            Height = structure.Height,
        };
    }

    /// <summary>Like <see cref="Create"/>, without an exception.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> data, string? fileName, string? declaredMediaType, out MessageImage? image, out MessageImageError error)
    {
        try
        {
            image = Create(data, fileName, declaredMediaType);
            error = default;
            return true;
        }
        catch (MessageImageException ex)
        {
            image = null;
            error = ex.Error;
            return false;
        }
    }

    /// <summary>A valid MIME file name: the base name when it is <c>[A-Za-z0-9._-]{1,64}</c> (the extension added when
    /// missing), else <c>image{number}.{ext}</c>.</summary>
    public static string SafeFileName(string? fileName, ImageKind kind, int number = 1)
    {
        var name = BaseName(fileName);
        if (name.Length > 0 && !name.Contains('.', StringComparison.Ordinal))
        {
            name += "." + ImageFormats.Extension(kind);
        }

        return FileNamePattern().IsMatch(name) && !name.StartsWith('.')
            ? name
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"image{Math.Max(1, number)}.{ImageFormats.Extension(kind)}");
    }

    public override string ToString() => $"MessageImage({ContentId}, {MediaType}, {Data.Length} B)";

    /// <summary>The last segment of a path or URI ("C:\…\clip_image001.png" → "clip_image001.png").</summary>
    private static string BaseName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var value = fileName.Trim();
        var cut = value.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? value[(cut + 1)..] : value;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("^img-[0-9a-f]{16}@admng$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdPattern();
}
