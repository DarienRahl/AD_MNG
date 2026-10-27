using AdMng.Core.Messaging.Images;
using AngleSharp.Dom;

namespace AdMng.Core.Messaging.Html;

/// <summary>The inline images of one planned message (empty for a text-only template or "send without images").</summary>
public sealed record MessageHtmlParts(IReadOnlyList<MessageImage> InlineImages)
{
    public static MessageHtmlParts None { get; } = new([]);
}

/// <summary>
/// The U1c checks of the planner (design note section 7.4): a template's HTML bodies (the cheap first gate, the sanitizer's
/// security findings in template mode, <c>cid:</c> references against the template's images, length, link texts, cosmetic
/// normalization) and every rendered message (images collected or omitted, the text part generated from the HTML when the
/// template has none, the whole document validated again – defense in depth).
/// </summary>
public static class MessageHtmlChecks
{
    /// <summary>A template's HTML is a fragment unless it is a whole document (<c>&lt;html&gt;</c> or <c>&lt;body&gt;</c>).</summary>
    public static HtmlSanitizeMode ModeOf(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return html.Contains("<html", StringComparison.OrdinalIgnoreCase) || html.Contains("<body", StringComparison.OrdinalIgnoreCase)
            ? HtmlSanitizeMode.Document
            : HtmlSanitizeMode.Fragment;
    }

    /// <summary>The sanitizer options of a template's HTML (fields allowed in links, the template's images known).</summary>
    public static HtmlSanitizeOptions TemplateOptions(MessageTemplate template, string html)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new HtmlSanitizeOptions
        {
            Mode = ModeOf(html),
            AllowFieldUrls = true,
            InlineImageIds = (template.Images ?? []).Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Template-level issues of the HTML bodies and images.</summary>
    public static IReadOnlyList<MessageIssue> CheckTemplate(MessageTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = new List<MessageIssue>();
        foreach (var text in template.Texts())
        {
            if (string.IsNullOrWhiteSpace(text.Html))
            {
                continue;
            }

            var html = text.Html;
            issues.AddRange(UserMessageRules.FindForbiddenHtml(html).Select(h => new MessageIssue(MessageIssueCodes.HtmlNotAllowed, MessageIssueSeverity.Error, h)));
            var result = MessageHtmlSanitizer.Sanitize(html, TemplateOptions(template, html));
            foreach (var change in result.Changes.Where(c => c.IsSecurity))
            {
                issues.Add(change.Kind == HtmlChangeKind.MissingImage
                    ? new MessageIssue(HtmlIssueCodes.ImageMissing, MessageIssueSeverity.Error, change.Detail)
                    : new MessageIssue(MessageIssueCodes.HtmlNotAllowed, MessageIssueSeverity.Error, FindingText(change.Kind, change.Detail)));
            }

            if (result.Html.Length > MessageHtmlLimits.MaxHtmlLength)
            {
                issues.Add(new MessageIssue(HtmlIssueCodes.HtmlTooLong, MessageIssueSeverity.Error, Number(result.Html.Length)));
            }

            issues.AddRange(HtmlLinkInspector.Links(html)
                .Where(HtmlLinkInspector.IsTextMismatch)
                .Select(l => new MessageIssue(HtmlIssueCodes.LinkTextMismatch, MessageIssueSeverity.Warning, UserMessageRules.LinkHost(l.Href))));
            if (!string.Equals(result.Html, html, StringComparison.Ordinal) && !result.Changes.Any(c => c.IsSecurity))
            {
                issues.Add(new MessageIssue(HtmlIssueCodes.HtmlNormalized, MessageIssueSeverity.Info));
            }
        }

        var images = template.Images ?? [];
        if (images.Count > MessageImageLimits.MaxImagesPerTemplate || images.Sum(i => i.Length) > MessageImageLimits.MaxImagesBytesPerTemplate)
        {
            issues.Add(new MessageIssue(HtmlIssueCodes.ImagesTooLarge, MessageIssueSeverity.Error, Number(images.Count)));
        }

        return [.. issues.Distinct()];
    }

    /// <summary>
    /// The template as it is saved (cosmetic changes applied, design note section 4.2): each language's HTML sanitized in
    /// template mode, images referenced by neither language dropped.
    /// </summary>
    public static MessageTemplate Normalize(MessageTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        MessageTemplateText Clean(MessageTemplateText? text)
        {
            text ??= new();
            return string.IsNullOrWhiteSpace(text.Html)
                ? text with { Html = null }
                : text with { Html = MessageHtmlSanitizer.Sanitize(text.Html, TemplateOptions(template, text.Html)).Html };
        }

        var polish = Clean(template.Polish);
        var english = Clean(template.English);
        var referenced = ReferencedImageIds(polish.Html).Concat(ReferencedImageIds(english.Html)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return template with
        {
            Polish = polish,
            English = english,
            Images = [.. (template.Images ?? []).Where(i => referenced.Contains(i.ContentId)).DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>
    /// U1c part of planning one message (after the bodies were rendered): the text part from the HTML when the template
    /// has none; with <see cref="UserMessagePlanRequest.OmitImages"/> every <c>cid:</c> image becomes its <c>[alt]</c> text
    /// (info <see cref="HtmlIssueCodes.ImagesOmitted"/>); the whole document is validated again (<c>HtmlNotAllowed</c>,
    /// <c>ImageMissing</c>), its images collected in order of first reference within the message limits
    /// (<c>ImagesTooLarge</c>) and its length checked (<c>HtmlTooLong</c>).
    /// </summary>
    public static MessageHtmlParts Message(
        UserMessagePlanRequest request,
        MessageTemplateText text,
        MessageLanguage language,
        ref string htmlBody,
        ref string body,
        List<MessageIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(issues);
        if (string.IsNullOrWhiteSpace(text.Html))
        {
            return MessageHtmlParts.None;
        }

        if (request.OmitImages && ReferencedImageIds(htmlBody).Count > 0)
        {
            htmlBody = OmitImages(htmlBody);
            issues.Add(new MessageIssue(HtmlIssueCodes.ImagesOmitted, MessageIssueSeverity.Info));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            body = HtmlToText.Convert(htmlBody);
        }

        var images = request.Images ?? new Dictionary<string, MessageImage>();
        var document = UserMessageRules.HtmlDocument(htmlBody, BuiltInMessageTemplates.Footer(language), language);
        var result = MessageHtmlSanitizer.Sanitize(document, new HtmlSanitizeOptions
        {
            Mode = HtmlSanitizeMode.Document,
            InlineImageIds = images.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
        });
        foreach (var change in result.Changes.Where(c => c.IsSecurity))
        {
            issues.Add(change.Kind == HtmlChangeKind.MissingImage
                ? new MessageIssue(HtmlIssueCodes.ImageMissing, MessageIssueSeverity.Error, change.Detail)
                : new MessageIssue(MessageIssueCodes.HtmlNotAllowed, MessageIssueSeverity.Error, FindingText(change.Kind, change.Detail)));
        }

        var inline = result.ImageIds
            .Select(id => images.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value)
            .OfType<MessageImage>()
            .ToList();
        if (inline.Count > MessageImageLimits.MaxImagesPerMessage || inline.Sum(i => (long)i.Data.Length) > MessageImageLimits.MaxImagesBytesPerMessage)
        {
            issues.Add(new MessageIssue(HtmlIssueCodes.ImagesTooLarge, MessageIssueSeverity.Error, Number(inline.Count)));
        }

        if (document.Length > MessageHtmlLimits.MaxHtmlLength)
        {
            issues.Add(new MessageIssue(HtmlIssueCodes.HtmlTooLong, MessageIssueSeverity.Error, Number(document.Length)));
        }

        return new MessageHtmlParts(inline);
    }

    /// <summary>The content ids an HTML references by <c>cid:</c> (in order of first reference).</summary>
    public static IReadOnlyList<string> ReferencedImageIds(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var run = HtmlSanitizerRun.Parse(html, new HtmlSanitizeOptions { Mode = ModeOf(html) });
        return [.. run.Document.Descendants<IElement>()
            .Where(e => e.LocalName == "img" && e.GetAttribute("src") is { } src && src.Trim().StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetAttribute("src")!.Trim()[4..])
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"Send without images": every <c>&lt;img src="cid:…"&gt;</c> becomes <c>&lt;span&gt;[alt]&lt;/span&gt;</c> (nothing
    /// without alt); everything else stays as it is.</summary>
    public static string OmitImages(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var mode = ModeOf(html);
        var run = HtmlSanitizerRun.Parse(html, new HtmlSanitizeOptions { Mode = mode });
        var images = run.Document.Descendants<IElement>()
            .Where(e => e.LocalName == "img" && e.GetAttribute("src") is { } src && src.Trim().StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (images.Count == 0)
        {
            return html;
        }

        foreach (var image in images)
        {
            if (image.GetAttribute("alt") is { } alt && !string.IsNullOrWhiteSpace(alt))
            {
                var span = run.Document.CreateElement("span");
                span.TextContent = "[" + alt.Trim() + "]";
                image.Parent?.ReplaceChild(span, image);
            }
            else
            {
                image.Parent?.RemoveChild(image);
            }
        }

        return mode == HtmlSanitizeMode.Fragment ? HtmlSerializer.Children(run.Root) : HtmlSerializer.Document(run.Root);
    }

    /// <summary>How a security finding is shown as the argument of <c>HtmlNotAllowed</c> (as the first gate shows its
    /// constructs: <c>&lt;script&gt;</c>, <c>onclick=</c>, <c>javascript:</c>).</summary>
    public static string FindingText(HtmlChangeKind kind, string? detail) => kind switch
    {
        HtmlChangeKind.RemovedElement => "<" + detail + ">",
        HtmlChangeKind.RemovedAttribute => detail + "=",
        HtmlChangeKind.RemovedStyle => detail + ":",
        HtmlChangeKind.LinkRemoved => detail + ":",
        HtmlChangeKind.HiddenContent => "display:none",
        HtmlChangeKind.RemoteImage => "<img src=" + detail + ">",
        HtmlChangeKind.FieldInImageSource => "<img src={…}>",
        _ => "<img>",
    };

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
