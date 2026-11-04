using AdMng.Core.Correspondence.Import;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>An Outlook signature ready to append: sanitized HTML fragment with <c>cid:</c> images.</summary>
public sealed record CorrespondenceSignature(string Name, string BodyHtml, IReadOnlyList<MessageImage> Images)
{
    public override string ToString() => $"{nameof(CorrespondenceSignature)} {Name}";
}

/// <summary>A parsed signature and what was dropped while parsing it.</summary>
/// <param name="Signature">The signature (possibly empty).</param>
/// <param name="Problems">Each problem as <c>code</c> or <c>code: detail</c> with a code of
/// <see cref="SignatureProblemCodes"/> (see <see cref="SignatureProblemCodes.Split"/>).</param>
public sealed record SignatureParseResult(CorrespondenceSignature Signature, IReadOnlyList<string> Problems)
{
    public override string ToString() => $"{nameof(SignatureParseResult)} ({Problems.Count} problems)";
}

/// <summary>The problems of <see cref="OutlookSignatureParser"/> (stable codes for the UI's texts).</summary>
public static class SignatureProblemCodes
{
    /// <summary>The HTML is nested too deeply: nothing of it is kept.</summary>
    public const string TooDeep = "signature.tooDeep";

    /// <summary>An image file could not be read (detail: its relative path).</summary>
    public const string ImageUnreadable = "signature.imageUnreadable";

    /// <summary>An image was refused (detail: the <see cref="MessageImageError"/> or <c>limit</c>).</summary>
    public const string ImageRejected = "signature.imageRejected";

    /// <summary>An image path leaves the signature folder (rooted, <c>..</c>, a drive or <c>file:</c>) – never read
    /// (detail: the path as written).</summary>
    public const string ImagePathRefused = "signature.imagePathRefused";

    /// <summary>An image on a web server was removed (detail: the host).</summary>
    public const string RemoteImage = "signature.remoteImage";

    /// <summary>Active or dangerous content was removed (detail: the element, attribute or property).</summary>
    public const string ContentRemoved = "signature.contentRemoved";

    /// <summary>A link with a scheme that is not allowed was removed (detail: the scheme).</summary>
    public const string LinkRemoved = "signature.linkRemoved";

    public static IReadOnlyList<string> All { get; } = [TooDeep, ImageUnreadable, ImageRejected, ImagePathRefused, RemoteImage, ContentRemoved, LinkRemoved];

    /// <summary>"code" or "code: detail".</summary>
    public static string Format(string code, string? detail) => string.IsNullOrEmpty(detail) ? code : code + ": " + detail;

    /// <summary>The code and detail of a problem written by <see cref="Format"/>.</summary>
    public static (string Code, string? Detail) Split(string problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var colon = problem.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? (problem, null) : (problem[..colon], problem[(colon + 2)..]);
    }
}

/// <summary>Outlook signatures (<c>.htm</c> files of <c>%APPDATA%\Microsoft\Signatures</c>) as message fragments.</summary>
public static class OutlookSignatureParser
{
    /// <summary>
    /// Parses an Outlook signature (.htm): charset from &lt;meta&gt; else ANSI, CSS of &lt;style&gt; inlined, conditional
    /// comments, VML, o:p and &lt;link&gt; removed, sanitized as a fragment (U1c sanitizer); relative images
    /// ("Name_files/image001.png") read through <paramref name="readRelative"/> and embedded by content id. Unreadable /
    /// invalid images are dropped with a problem.
    /// </summary>
    /// <remarks>
    /// Decoding: a BOM, else the <c>&lt;meta&gt;</c> charset, else strict UTF-8, else <paramref name="ansiCodePage"/>.
    /// <paramref name="readRelative"/> gets the path relative to the signature folder with <c>/</c> separators (percent
    /// escapes decoded) and returns the bytes or null; a rooted path, a drive, a scheme or a <c>..</c> segment is never
    /// passed to it. Images: PNG/JPEG/GIF up to <see cref="CorrespondenceLimits.MaxImageBytes"/> each (metadata stripped),
    /// at most <see cref="CorrespondenceLimits.MaxImagesPerMessage"/> and
    /// <see cref="CorrespondenceLimits.MaxImagesBytesPerMessage"/> together; <c>data:</c> images are decoded; images on web
    /// servers are removed (AD Manager never fetches anything).
    /// </remarks>
    public static SignatureParseResult Parse(string name, ReadOnlySpan<byte> html, Func<string, byte[]?> readRelative, int ansiCodePage)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(readRelative);
        var problems = new List<string>();
        var text = TextDecoding.DecodeHtml(html, ansiCodePage);
        var prepared = string.IsNullOrWhiteSpace(text) ? string.Empty : PastedHtmlImporter.Prepare(text);
        if (prepared is null)
        {
            problems.Add(SignatureProblemCodes.TooDeep);
            return new SignatureParseResult(new CorrespondenceSignature(name, string.Empty, []), problems);
        }

        var images = ReadImages(prepared, readRelative, problems);
        var result = MessageHtmlSanitizer.Sanitize(prepared, new HtmlSanitizeOptions
        {
            Mode = HtmlSanitizeMode.Fragment,
            Import = new SignatureHooks(images),
        });
        foreach (var change in result.Changes.Where(c => c.IsSecurity && !(c.Kind == HtmlChangeKind.RemovedElement && c.Detail is "link" or "meta")))
        {
            var problem = change.Kind switch
            {
                HtmlChangeKind.RemoteImage => SignatureProblemCodes.Format(SignatureProblemCodes.RemoteImage, change.Detail),
                HtmlChangeKind.LinkRemoved => SignatureProblemCodes.Format(SignatureProblemCodes.LinkRemoved, change.Detail),
                HtmlChangeKind.TooDeep => SignatureProblemCodes.TooDeep,
                HtmlChangeKind.MissingImage or HtmlChangeKind.ImageRejected or HtmlChangeKind.FieldInImageSource => null,   // reported by the image pass
                _ => SignatureProblemCodes.Format(SignatureProblemCodes.ContentRemoved, change.Detail),
            };
            if (problem is not null && !problems.Contains(problem, StringComparer.Ordinal))
            {
                problems.Add(problem);
            }
        }

        var byId = images.Values.OfType<MessageImage>().DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase);
        var kept = result.ImageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        return new SignatureParseResult(new CorrespondenceSignature(name, result.Html, kept), problems);
    }

    /// <summary>Reads every image source of the prepared fragment once: source (as the sanitizer sees it) → image, or null
    /// when it is not embedded (the problem recorded; web images are left to the sanitizer, which reports them).</summary>
    private static Dictionary<string, MessageImage?> ReadImages(string fragment, Func<string, byte[]?> readRelative, List<string> problems)
    {
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument("<!DOCTYPE html><body>" + fragment);
        var sources = document.Descendants<IElement>()
            .Where(e => e.LocalName == "img")
            .Select(e => HtmlSanitizerRun.CleanUrl(e.GetAttribute("src") ?? string.Empty))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var result = new Dictionary<string, MessageImage?>(StringComparer.Ordinal);
        var count = 0;
        var bytes = 0L;
        var number = 0;
        foreach (var source in sources)
        {
            if (source.Length == 0)
            {
                continue;
            }

            var scheme = HtmlSanitizerRun.SchemeOf(source);
            if (scheme is "http" or "https" or "cid")
            {
                continue;   // the sanitizer removes them (web images reported as remote)
            }

            number++;
            MessageImage? image = null;
            if (scheme == "data")
            {
                image = DataImage(source, number, problems);
            }
            else if (RelativePath(source) is not { } path)
            {
                problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImagePathRefused, Shorten(source)));
            }
            else
            {
                image = FileImage(path, readRelative, number, problems);
            }

            if (image is not null && !result.Values.Any(i => i?.ContentId == image.ContentId))
            {
                if (count + 1 > CorrespondenceLimits.MaxImagesPerMessage || bytes + image.Data.Length > CorrespondenceLimits.MaxImagesBytesPerMessage)
                {
                    problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, "limit"));
                    image = null;
                }
                else
                {
                    count++;
                    bytes += image.Data.Length;
                }
            }

            result[source] = image;
        }

        return result;
    }

    private static MessageImage? FileImage(string path, Func<string, byte[]?> readRelative, int number, List<string> problems)
    {
        byte[]? data;
        try
        {
            data = readRelative(path);
        }
        catch (IOException)
        {
            data = null;
        }
        catch (UnauthorizedAccessException)
        {
            data = null;
        }

        if (data is null)
        {
            problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImageUnreadable, path));
            return null;
        }

        return Create(data, path, null, number, problems);
    }

    /// <summary>A <c>data:image/png|jpeg|gif;base64,…</c> image.</summary>
    private static MessageImage? DataImage(string source, int number, List<string> problems)
    {
        var comma = source.IndexOf(',', StringComparison.Ordinal);
        var header = comma > 5 ? source[5..comma].Split(';') : [];
        if (comma < 0 || header.Length == 0 || ImageFormats.FromMediaType(header[0].Trim()) is null
            || !header.Skip(1).Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, "data"));
            return null;
        }

        var payload = source[(comma + 1)..];
        if (payload.Length > ((CorrespondenceLimits.MaxImageBytes + 2) / 3 * 4) + 1024)
        {
            problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, nameof(MessageImageError.TooLarge)));
            return null;
        }

        try
        {
            return Create(Convert.FromBase64String(Uri.UnescapeDataString(payload).Trim()), null, header[0].Trim(), number, problems);
        }
        catch (FormatException)
        {
            problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, nameof(MessageImageError.Malformed)));
            return null;
        }
    }

    private static MessageImage? Create(byte[] data, string? fileName, string? mediaType, int number, List<string> problems)
    {
        try
        {
            return MessageImage.Create(data, fileName, mediaType, number, CorrespondenceLimits.MaxImageBytes);
        }
        catch (MessageImageException ex)
        {
            problems.Add(SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, ex.Error.ToString()));
            return null;
        }
    }

    /// <summary>The path relative to the signature folder ("Jan_files/image001.png"), or null when it is rooted, has a
    /// drive or scheme, or a <c>..</c> segment.</summary>
    internal static string? RelativePath(string source)
    {
        string path;
        try
        {
            path = Uri.UnescapeDataString(source);
        }
        catch (UriFormatException)
        {
            return null;
        }

        path = path.Replace('\\', '/');
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = path.Split('/').Where(s => s is not ("" or ".")).ToList();
        if (segments.Count == 0 || segments.Any(s => s == ".." || s.Trim().Trim('.').Length == 0))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    private static string Shorten(string value) => value.Length <= 120 ? value : value[..120] + "…";

    private sealed class SignatureHooks(Dictionary<string, MessageImage?> images) : IHtmlImportHooks
    {
        public string? ResolveImage(string source) => images.TryGetValue(source, out var image) ? image?.ContentId : null;

        public string RewriteLink(string href) => href;
    }
}
