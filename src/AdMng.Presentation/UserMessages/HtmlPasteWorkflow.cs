using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Core.Templates;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.UserMessages;

/// <summary>What a paste produced.</summary>
public enum HtmlPasteKind
{
    /// <summary>The clipboard held nothing usable.</summary>
    Nothing,

    /// <summary>Formatted content (CF_HTML) imported.</summary>
    Html,

    /// <summary>Only a bitmap: one image.</summary>
    Image,

    /// <summary>Only text: wrapped as paragraphs.</summary>
    Text,

    /// <summary>The clipboard's HTML is larger than <see cref="CfHtmlParser.MaxBytes"/>.</summary>
    TooLarge,
}

/// <summary>The result of a paste: the HTML for the edited language, its images, the problems and the text suggestion.</summary>
public sealed record HtmlPasteResult(HtmlPasteKind Kind, string Html, IReadOnlyList<MessageImage> Images, IReadOnlyList<HtmlImportProblem> Problems, string SuggestedText)
{
    public static HtmlPasteResult Nothing { get; } = new(HtmlPasteKind.Nothing, string.Empty, [], [], string.Empty);

    public static HtmlPasteResult TooLarge { get; } = new(HtmlPasteKind.TooLarge, string.Empty, [], [], string.Empty);

    public override string ToString() => $"HtmlPasteResult({Kind}, {Html.Length} characters, {Images.Count} images, {Problems.Count} problems)";
}

/// <summary>
/// "Wklej sformatowaną treść" (U1c, design note u1c-u1d sections 2, 3 and 12): the clipboard is read on the calling (UI)
/// thread – the clipboard is STA – then everything else runs off it: CF_HTML parsing (raw bytes, or the platform's string in
/// text mode), the import (styles inlined, Office markup resolved, images read once under the clipboard file rule, never
/// fetched from the network, sanitized, placeholders repaired, links checked) and the text suggestion. A bitmap alone
/// becomes one image, text alone becomes paragraphs. Never throws on content.
/// </summary>
public sealed class HtmlPasteWorkflow(IClipboardService clipboard, IPastedImageSource? files, ITemplateEngine? engine = null)
{
    /// <summary>The formatted content of the clipboard (call on the UI thread).</summary>
    public ClipboardContent? ReadClipboard() => clipboard.ReadFormatted();

    /// <summary>Imports clipboard content (off the UI thread).</summary>
    /// <param name="content">What <see cref="ReadClipboard"/> returned.</param>
    /// <param name="allowedLinkDomains">Settings domains, the AD DNS domain and the sender's domain.</param>
    /// <param name="existingImages">Images the template already has (the other language) – counted against its limits.</param>
    /// <param name="cancellationToken">Stops the import (the operator cancelled or closed the window).</param>
    public Task<HtmlPasteResult> ImportAsync(
        ClipboardContent? content,
        IReadOnlyList<string> allowedLinkDomains,
        IReadOnlyList<MessageImage> existingImages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedLinkDomains);
        ArgumentNullException.ThrowIfNull(existingImages);
        if (content is null || content.IsEmpty)
        {
            return Task.FromResult(HtmlPasteResult.Nothing);
        }

        return Task.Run(() => ImportCoreAsync(content, allowedLinkDomains, existingImages, cancellationToken), cancellationToken);
    }

    /// <summary>The clipboard's bitmap as an image for "Wklej obraz", or why not (null + the error).</summary>
    public static (MessageImage? Image, MessageImageError? Error) ImageOf(ClipboardContent? content, int number = 1)
    {
        if (content?.Png is not { Length: > 0 } png)
        {
            return (null, null);
        }

        return png.Length > MessageImageLimits.MaxImageBytes * 4L
            ? (null, MessageImageError.TooLarge)
            : MessageImage.TryCreate(png, $"obraz{number}.png", "image/png", out var image, out var error) ? (image, null) : (null, error);
    }

    /// <summary>The HTML of an inserted image: <c>&lt;img src="cid:…" alt="…"&gt;</c>.</summary>
    public static string ImageHtml(MessageImage image, string? alt = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var text = string.IsNullOrWhiteSpace(alt) ? Path.GetFileNameWithoutExtension(image.FileName) : alt.Trim();
        return $"<img src=\"cid:{image.ContentId}\" alt=\"{System.Net.WebUtility.HtmlEncode(text)}\" width=\"{image.Width}\" height=\"{image.Height}\">";
    }

    private async Task<HtmlPasteResult> ImportCoreAsync(
        ClipboardContent content,
        IReadOnlyList<string> allowedLinkDomains,
        IReadOnlyList<MessageImage> existingImages,
        CancellationToken cancellationToken)
    {
        CfHtmlDocument? document = null;
        if (content.CfHtml is { Length: > 0 } raw)
        {
            if (raw.Length > CfHtmlParser.MaxBytes)
            {
                return HtmlPasteResult.TooLarge;
            }

            document = CfHtmlParser.Parse(raw);
        }
        else if (!string.IsNullOrEmpty(content.CfHtmlText))
        {
            if (content.CfHtmlText.Length > CfHtmlParser.MaxBytes)
            {
                return HtmlPasteResult.TooLarge;
            }

            document = CfHtmlParser.FromText(content.CfHtmlText);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (document is not null)
        {
            var result = await PastedHtmlImporter.ImportAsync(
                new HtmlImportRequest
                {
                    Document = document,
                    Files = files,
                    ClipboardBitmap = content.Png,
                    AllowedLinkDomains = allowedLinkDomains,
                    Engine = engine,
                    ExistingImages = existingImages,
                },
                cancellationToken).ConfigureAwait(false);
            return new HtmlPasteResult(HtmlPasteKind.Html, result.Html, result.Images, result.Problems, result.SuggestedText);
        }

        if (content.Png is { Length: > 0 })
        {
            var (image, error) = ImageOf(content, existingImages.Count + 1);
            return image is null
                ? new HtmlPasteResult(HtmlPasteKind.Image, string.Empty, [], [new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, error?.ToString())], string.Empty)
                : new HtmlPasteResult(HtmlPasteKind.Image, "<p>" + ImageHtml(image) + "</p>\n", [image], [], string.Empty);
        }

        var text = content.Text ?? string.Empty;
        return text.Length == 0
            ? HtmlPasteResult.Nothing
            : new HtmlPasteResult(
                HtmlPasteKind.Text,
                MessageHtmlSanitizer.Sanitize(UserMessageRules.TextToHtml(text), new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Fragment }).Html,
                [],
                [],
                text.ReplaceLineEndings("\n").Trim());
    }

    public override string ToString() => nameof(HtmlPasteWorkflow);
}
