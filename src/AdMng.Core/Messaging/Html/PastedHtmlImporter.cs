using System.Text.RegularExpressions;
using AdMng.Core.Messaging.Images;
using AdMng.Core.Templates;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Core.Messaging.Html;

/// <summary>Why a pasted image file could not be read.</summary>
public enum PastedImageFailure
{
    /// <summary>Not a local file in the temp folder (a network path, another folder, a link) – never opened.</summary>
    NotAllowed,

    /// <summary>The file does not exist (any more).</summary>
    NotFound,

    /// <summary>Larger than <see cref="MessageImageLimits.MaxImageBytes"/>.</summary>
    TooLarge,

    /// <summary>Reading failed.</summary>
    ReadError,
}

/// <summary>The bytes of a pasted image file (or why there are none); <see cref="FileName"/> is the base name only.</summary>
public sealed record PastedImageRead(byte[]? Data, string FileName, PastedImageFailure? Failure = null)
{
    public override string ToString() => Failure is { } failure ? $"PastedImageRead({failure})" : $"PastedImageRead({Data?.Length ?? 0} B)";
}

/// <summary>Reads the <c>file:</c> images of a pasted message (Outlook's <c>msohtmlclip</c> files) under the rule of
/// <see cref="ClipboardImagePolicy"/>; implemented with file IO in Presentation, faked in tests.</summary>
public interface IPastedImageSource
{
    /// <summary>Reads at most <paramref name="maxBytes"/> + 1 bytes of a <c>file:</c> URI, or reports why not.</summary>
    Task<PastedImageRead> ReadAsync(string fileUri, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>Input of <see cref="PastedHtmlImporter.ImportAsync"/>.</summary>
public sealed record HtmlImportRequest
{
    /// <summary>The clipboard HTML (<see cref="CfHtmlParser"/>).</summary>
    public required CfHtmlDocument Document { get; init; }

    /// <summary>Reads <c>file:</c> images; null = such images are unreadable.</summary>
    public IPastedImageSource? Files { get; init; }

    /// <summary>The clipboard's bitmap (PNG) – used for a single remote image ("Copy image" in a browser).</summary>
    public byte[]? ClipboardBitmap { get; init; }

    /// <summary>Allowed link domains (settings + AD DNS domain + sender's domain).</summary>
    public IReadOnlyList<string> AllowedLinkDomains { get; init; } = [];

    /// <summary>The template engine of messages (placeholder repair); null = a field is <c>{name}</c> / <c>{$name…}</c>.</summary>
    public ITemplateEngine? Engine { get; init; }

    /// <summary>Images the template already has (the other language): counted against the template's limits.</summary>
    public IReadOnlyList<MessageImage> ExistingImages { get; init; } = [];
}

/// <summary>Result of an import: sanitized HTML (a fragment), its images (referenced by <c>cid:</c>), the problems and a
/// text body generated from the HTML.</summary>
public sealed record HtmlImportResult(string Html, IReadOnlyList<MessageImage> Images, IReadOnlyList<HtmlImportProblem> Problems, string SuggestedText)
{
    public override string ToString() => $"HtmlImportResult({Html.Length} characters, {Images.Count} images, {Problems.Count} problems)";
}

/// <summary>
/// Turns a formatted message pasted from Outlook, Word or a browser into a template's HTML body (design note section 3):
/// the full clipboard HTML is parsed; its style sheets are cascaded into <c>style</c> attributes; Office's conditional
/// comments, VML and <c>o:p</c> elements and Word's list paragraphs are resolved; the fragment between the clipboard
/// markers is taken with the inherited font of its context; images are read once into memory (<c>file:</c> under
/// <see cref="ClipboardImagePolicy"/>, <c>data:</c> decoded, remote ones never fetched) and referenced as <c>cid:</c>;
/// then <see cref="MessageHtmlSanitizer"/> (fragment mode), Safe Links unwrapping, relative links resolved against
/// <c>SourceURL</c>, placeholder repair, link checks and the text suggestion. Never throws on content – everything it
/// cannot keep is an <see cref="HtmlImportProblem"/>.
/// </summary>
public static partial class PastedHtmlImporter
{
    /// <summary>Elements removed with their content during the Office pre-processing (each has an image fallback).</summary>
    private static readonly string[] OfficeRemoved = ["v:", "m:"];

    /// <summary>Attributes whose removal is expected and not worth a problem line.</summary>
    private static readonly HashSet<string> QuietAttributes = new(StringComparer.Ordinal)
    {
        "class", "id", "name", "lang", "width", "height", "border", "align", "valign", "nowrap", "cellspacing", "cellpadding", "target",
        "rel", "dir", "title", "start", "type", "value", "colspan", "rowspan", "bgcolor", "color", "face", "size", "alt", "role",
        "tabindex", "aria-hidden", "aria-label", "spellcheck", "contenteditable", "draggable", "translate", "hidden",
    };

    /// <summary>CSS properties whose removal changes what the reader sees (other removals are Office or layout noise).</summary>
    private static readonly HashSet<string> NoticeableStyles = new(StringComparer.Ordinal)
    {
        "float", "position", "display", "background", "background-image", "max-height", "overflow", "overflow-x", "overflow-y",
    };

    /// <summary>Imports a pasted message.</summary>
    public static async Task<HtmlImportResult> ImportAsync(HtmlImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<HtmlImportProblem>(request.Document.Problems);

        // 1–5: parse, cascade, comments, Office elements, Word lists, fragment with its inherited context.
        var fragment = Prepare(request.Document.Html);
        if (fragment is null)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.TooDeep, MessageIssueSeverity.Error,
                MessageHtmlLimits.MaxNestingDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return new HtmlImportResult(string.Empty, [], problems, string.Empty);
        }

        // Images: read once, before the (synchronous) sanitizer runs.
        var images = await ResolveImagesAsync(fragment, request, problems, cancellationToken).ConfigureAwait(false);

        // 6–8: sanitize with the import hooks, cleanup.
        var hooks = new ImportHooks(images, request.Document.SourceUrl, problems);
        var options = new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Fragment, Import = hooks };
        var run = HtmlSanitizerRun.Parse(fragment, options);
        run.Sanitize();
        problems.AddRange(Problems(run.Changes));

        // 9: placeholder repair, then the cleanup again (pieces left empty).
        var engine = request.Engine;
        var repaired = PlaceholderRepair.Repair(run.Body, candidate => IsField(candidate, engine));
        problems.AddRange(repaired.Select(f => new HtmlImportProblem(HtmlImportProblemKind.PlaceholderRepaired, MessageIssueSeverity.Info, f)));
        run.Finish();
        var result = run.Result();
        var html = result.Html;
        if (html.Length > MessageHtmlLimits.MaxHtmlLength)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ContentTruncated, MessageIssueSeverity.Warning, "html"));
        }

        problems.AddRange(LinkProblems(html, request.AllowedLinkDomains));
        var referenced = result.ImageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = images.Values.OfType<MessageImage>().Where(i => referenced.Contains(i.ContentId)).DistinctBy(i => i.ContentId).ToList();
        return new HtmlImportResult(html, kept, problems, HtmlToText.Convert(html));
    }

    /// <summary>True when <paramref name="candidate"/> (with braces) is a valid field of the message engine.</summary>
    public static bool IsField(string candidate, ITemplateEngine? engine)
    {
        if (candidate.Length < 3 || candidate[0] != '{' || candidate[^1] != '}' || candidate.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        if (engine is null)
        {
            return FieldPattern().IsMatch(candidate);
        }

        var parsed = engine.Parse(candidate);
        return parsed.IsValid && (parsed.ReferencedAttributes.Count > 0 || parsed.SpecialTokens.Count > 0);
    }

    /// <summary>Links outside the allowed domains and links whose text names another host (warnings at paste).</summary>
    public static IReadOnlyList<HtmlImportProblem> LinkProblems(string html, IReadOnlyList<string> allowedDomains)
    {
        ArgumentNullException.ThrowIfNull(allowedDomains);
        var problems = new List<HtmlImportProblem>();
        foreach (var link in HtmlLinkInspector.Links(html))
        {
            if (link.Href.StartsWith('{'))
            {
                continue;
            }

            if (HtmlLinkInspector.HostOf(link.Href) is not null && !UserMessageRules.IsLinkAllowed(link.Href, allowedDomains))
            {
                problems.Add(new HtmlImportProblem(HtmlImportProblemKind.LinkOutsideAllowedDomains, MessageIssueSeverity.Warning, UserMessageRules.LinkHost(link.Href)));
            }

            if (HtmlLinkInspector.IsTextMismatch(link))
            {
                problems.Add(new HtmlImportProblem(HtmlImportProblemKind.LinkTextMismatch, MessageIssueSeverity.Warning, UserMessageRules.LinkHost(link.Href)));
            }
        }

        return problems;
    }

    // ---- preparation ----------------------------------------------------------------------------------------------------

    /// <summary>Steps 1–5: the fragment's HTML with every style inlined, Office constructs resolved and the inherited
    /// properties of its context on a wrapper <c>div</c>; null when the HTML is nested deeper than
    /// <see cref="MessageHtmlLimits.MaxNestingDepth"/> (the range copy recurses).</summary>
    internal static string? Prepare(string html)
    {
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html);
        if (HtmlNesting.IsTooDeep(document))
        {
            return null;
        }
        var rules = new List<CssRule>();
        foreach (var style in document.Descendants<IElement>().Where(e => e.LocalName == "style").ToList())
        {
            rules.AddRange(CssParser.ParseStyleSheet(style.TextContent, rules.Count));
            style.Parent?.RemoveChild(style);
        }

        CssInliner.Inline(document.DocumentElement, rules);
        ResolveComments(document);
        ResolveOfficeElements(document.DocumentElement);
        WordListConverter.Convert(document.Body ?? document.DocumentElement);
        var (container, context) = ExtractFragment(document);
        if (InheritedContext(context) is { Count: > 0 } inherited)
        {
            var wrapper = document.CreateElement("div");
            wrapper.SetAttribute("style", string.Join(";", inherited.Select(p => p.Key + ":" + p.Value)));
            foreach (var node in container.ChildNodes.ToList())
            {
                wrapper.AppendChild(node);
            }

            container.AppendChild(wrapper);
        }

        return container.InnerHtml;
    }

    /// <summary>Conditional comments: downlevel-revealed markers (<c>&lt;![if …]&gt;</c>, <c>&lt;![endif]&gt;</c> – bogus comments
    /// to the HTML5 parser) are dropped and their content kept, except the <c>[if !supportLists]</c> list markers, whose text
    /// goes to <see cref="WordListConverter"/>; every other comment is removed, the fragment markers stay.</summary>
    private static void ResolveComments(IDocument document)
    {
        foreach (var comment in document.Descendants<IComment>().ToList())
        {
            if (comment.Parent is null)
            {
                continue;
            }

            var data = comment.Data.Trim();
            if (IsFragmentMarker(data))
            {
                continue;
            }

            var revealed = RevealedStartPattern().Match(data);
            if (revealed.Success && data.Contains("supportLists", StringComparison.OrdinalIgnoreCase))
            {
                var region = new List<INode>();
                for (var next = comment.NextSibling; next is not null && !(next is IComment end && end.Data.Trim().Equals("[endif]", StringComparison.OrdinalIgnoreCase)); next = next.NextSibling)
                {
                    region.Add(next);
                }

                var marker = string.Concat(region.Select(n => n.TextContent));
                if (comment.Parent is IElement owner && Ancestors(owner).FirstOrDefault(e => WordListConverter.ListLevel(e) is not null) is { } paragraph)
                {
                    paragraph.SetAttribute(WordListConverter.MarkerAttribute, marker);
                }

                foreach (var node in region)
                {
                    node.Parent?.RemoveChild(node);
                }
            }

            comment.Parent?.RemoveChild(comment);
        }
    }

    private static IEnumerable<IElement> Ancestors(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            yield return e;
        }
    }

    private static bool IsFragmentMarker(string data) =>
        data.Equals("StartFragment", StringComparison.OrdinalIgnoreCase) || data.Equals("EndFragment", StringComparison.OrdinalIgnoreCase)
        || data == CfHtmlParser.SentinelStart || data == CfHtmlParser.SentinelEnd;

    /// <summary><c>v:*</c>, <c>m:*</c> and <c>xml</c> removed with their content (VML shapes have an <c>&lt;img&gt;</c>
    /// fallback); <c>o:p</c> and every other prefixed element (smart tags, content controls) unwrapped; Word's <c>lang</c>
    /// attributes (spell-check language) dropped.</summary>
    private static void ResolveOfficeElements(IElement root)
    {
        foreach (var element in root.Descendants<IElement>().ToList())
        {
            if (element.Parent is null)
            {
                continue;
            }

            var name = element.LocalName.ToLowerInvariant();
            if (name == "xml" || OfficeRemoved.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                element.Parent.RemoveChild(element);
                continue;
            }

            if (name.Contains(':', StringComparison.Ordinal))
            {
                HtmlSanitizerRun.Unwrap(element);
                continue;
            }

            element.RemoveAttribute("lang");
        }
    }

    /// <summary>The nodes between the fragment markers (cloned with their partial ancestors, like a DOM range) and the
    /// elements from the body down to the markers' common ancestor (their inherited properties).</summary>
    private static (IElement Container, IReadOnlyList<IElement> Context) ExtractFragment(IDocument document)
    {
        var body = document.Body ?? document.DocumentElement;
        var container = document.CreateElement("div");
        var comments = body.Descendants<IComment>().ToList();
        var start = comments.FirstOrDefault(c => c.Data.Trim() is var d && (d.Equals("StartFragment", StringComparison.OrdinalIgnoreCase) || d == CfHtmlParser.SentinelStart));
        var end = comments.LastOrDefault(c => c.Data.Trim() is var d && (d.Equals("EndFragment", StringComparison.OrdinalIgnoreCase) || d == CfHtmlParser.SentinelEnd));
        if (start is null || end is null)
        {
            foreach (var node in body.ChildNodes.ToList())
            {
                container.AppendChild(node.Clone(deep: true));
            }

            return (container, [body]);
        }

        var order = new Dictionary<INode, int>();
        var index = 0;
        foreach (var node in new INode[] { body }.Concat(body.Descendants()))
        {
            order[node] = index++;
        }

        var startIndex = order[start];
        var endIndex = order[end];
        if (endIndex < startIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        var common = CommonAncestor(start, end) ?? body;
        CloneRange(common, container, order, startIndex, endIndex);
        foreach (var marker in container.Descendants<IComment>().ToList())
        {
            marker.Parent?.RemoveChild(marker);
        }

        var context = new List<IElement>();
        for (var e = common as IElement ?? body; e is not null; e = e.ParentElement)
        {
            context.Insert(0, e);
            if (e == body)
            {
                break;
            }
        }

        return (container, context);
    }

    private static void CloneRange(INode source, INode target, Dictionary<INode, int> order, int start, int end)
    {
        foreach (var child in source.ChildNodes)
        {
            var first = order[child];
            var last = child.Descendants().Select(d => order[d]).DefaultIfEmpty(first).Max();
            if (last <= start || first >= end)
            {
                continue;
            }

            if (first > start && last < end)
            {
                target.AppendChild(child.Clone(deep: true));
                continue;
            }

            var shallow = child.Clone(deep: false);
            CloneRange(child, shallow, order, start, end);
            target.AppendChild(shallow);
        }
    }

    private static INode? CommonAncestor(INode a, INode b)
    {
        var ancestors = new HashSet<INode>();
        for (var n = a.Parent; n is not null; n = n.Parent)
        {
            ancestors.Add(n);
        }

        for (var n = b.Parent; n is not null; n = n.Parent)
        {
            if (ancestors.Contains(n))
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>The allowed inherited properties (<c>font-*</c>, <c>color</c>, <c>line-height</c>, <c>text-align</c>) the
    /// context elements set, the innermost winning.</summary>
    private static Dictionary<string, string> InheritedContext(IReadOnlyList<IElement> context)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in context)
        {
            foreach (var declaration in CssParser.ParseDeclarations(element.GetAttribute("style")))
            {
                if (declaration.Property is "color" or "line-height" or "text-align" or "font" || declaration.Property.StartsWith("font-", StringComparison.Ordinal))
                {
                    result[declaration.Property] = declaration.Value;
                }
            }
        }

        return result;
    }

    // ---- images ---------------------------------------------------------------------------------------------------------

    /// <summary>Reads every image source of the fragment once: source → image (null when it is not importable; the problem
    /// is recorded). Limits: the message's (count, bytes) and the template's (with its existing images).</summary>
    private static async Task<Dictionary<string, MessageImage?>> ResolveImagesAsync(
        string fragmentHtml,
        HtmlImportRequest request,
        List<HtmlImportProblem> problems,
        CancellationToken cancellationToken)
    {
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument("<!DOCTYPE html><body>" + fragmentHtml);
        var sources = document.Descendants<IElement>()
            .Where(e => e.LocalName == "img")
            .Select(e => HtmlSanitizerRun.CleanUrl(e.GetAttribute("src") ?? string.Empty))
            .ToList();
        var result = new Dictionary<string, MessageImage?>(StringComparer.Ordinal);
        // "Copy image" in a browser: the HTML has exactly one web image and the clipboard carries its bitmap.
        var remote = sources.Where(s => s.Length > 0 && IsRemote(s)).ToList();
        var useBitmap = remote.Count == 1 && HtmlSanitizerRun.SchemeOf(remote[0]) is "http" or "https" or "blob" && request.ClipboardBitmap is { Length: > 0 };
        var count = request.ExistingImages.Count;
        var templateBytes = request.ExistingImages.Sum(i => (long)i.Data.Length);
        var messageBytes = 0L;
        var messageCount = 0;
        var number = request.ExistingImages.Count;
        foreach (var source in sources.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length == 0 || source.StartsWith('{'))
            {
                problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, source.Length == 0 ? "empty" : "field"));
                result[source] = null;
                continue;
            }

            var image = await ReadAsync(source, request, useBitmap, ++number, problems, cancellationToken).ConfigureAwait(false);
            if (image is not null && result.Values.All(i => i?.ContentId != image.ContentId) && request.ExistingImages.All(i => i.ContentId != image.ContentId))
            {
                if (messageCount + 1 > MessageImageLimits.MaxImagesPerMessage || messageBytes + image.Data.Length > MessageImageLimits.MaxImagesBytesPerMessage
                    || count + 1 > MessageImageLimits.MaxImagesPerTemplate || templateBytes + image.Data.Length > MessageImageLimits.MaxImagesBytesPerTemplate)
                {
                    problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, "limit"));
                    image = null;
                }
                else
                {
                    messageCount++;
                    count++;
                    messageBytes += image.Data.Length;
                    templateBytes += image.Data.Length;
                }
            }
            else if (image is not null && request.ExistingImages.FirstOrDefault(i => i.ContentId == image.ContentId) is { } existing)
            {
                image = existing;
            }

            result[source] = image;
        }

        return result;
    }

    private static bool IsRemote(string source)
    {
        var scheme = HtmlSanitizerRun.SchemeOf(source);
        return scheme is null or "http" or "https" or "blob" or "cid";
    }

    private static async Task<MessageImage?> ReadAsync(
        string source,
        HtmlImportRequest request,
        bool useBitmap,
        int number,
        List<HtmlImportProblem> problems,
        CancellationToken cancellationToken)
    {
        var scheme = HtmlSanitizerRun.SchemeOf(source);
        if (IsRemote(source))
        {
            if (useBitmap)
            {
                return Create(request.ClipboardBitmap!, "image.png", "image/png", number, problems);
            }

            problems.Add(scheme is "http" or "https"
                ? new HtmlImportProblem(HtmlImportProblemKind.RemoteImageRemoved, MessageIssueSeverity.Warning, UserMessageRules.LinkHost(source))
                : new HtmlImportProblem(HtmlImportProblemKind.ImageUnreadable, MessageIssueSeverity.Warning, scheme ?? "relative"));
            return null;
        }

        if (scheme == "data")
        {
            return DataUri(source, number, problems);
        }

        if (scheme == "file")
        {
            // Defense in depth: a network path is never handed to the reader (it would authenticate to a foreign server).
            if (request.Files is null || ClipboardImagePolicy.LocalPath(source, OperatingSystem.IsWindows()) is null)
            {
                problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageUnreadable, MessageIssueSeverity.Warning, request.Files is null ? "file" : "network"));
                return null;
            }

            var read = await request.Files.ReadAsync(source, MessageImageLimits.MaxImageBytes, cancellationToken).ConfigureAwait(false);
            if (read.Failure is { } failure || read.Data is null)
            {
                problems.Add(read.Failure == PastedImageFailure.TooLarge
                    ? new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, nameof(MessageImageError.TooLarge))
                    : new HtmlImportProblem(HtmlImportProblemKind.ImageUnreadable, MessageIssueSeverity.Warning, read.FileName.Length > 0 ? read.FileName : "file"));
                return null;
            }

            return Create(read.Data, read.FileName, null, number, problems);
        }

        problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageUnreadable, MessageIssueSeverity.Warning, scheme));
        return null;
    }

    /// <summary>A <c>data:image/png|jpeg|gif;base64,…</c> URI; base64 longer than the limit allows is refused before decoding.</summary>
    private static MessageImage? DataUri(string source, int number, List<HtmlImportProblem> problems)
    {
        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, "data"));
            return null;
        }

        var header = source[5..comma];
        var parts = header.Split(';');
        var mediaType = parts[0].Trim();
        if (ImageFormats.FromMediaType(mediaType) is null || !parts.Skip(1).Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, mediaType.Length == 0 ? "data" : mediaType.ToLowerInvariant()));
            return null;
        }

        var payload = source[(comma + 1)..];
        if (payload.Length > ((MessageImageLimits.MaxImageBytes + 2) / 3 * 4) + 1024)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, nameof(MessageImageError.TooLarge)));
            return null;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(Uri.UnescapeDataString(payload).Trim());
        }
        catch (FormatException)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, nameof(MessageImageError.Malformed)));
            return null;
        }

        return Create(data, null, mediaType, number, problems);
    }

    private static MessageImage? Create(byte[] data, string? fileName, string? mediaType, int number, List<HtmlImportProblem> problems)
    {
        try
        {
            return MessageImage.Create(data, fileName, mediaType, number);
        }
        catch (MessageImageException ex)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ImageRejected, MessageIssueSeverity.Warning, ex.Error.ToString()));
            return null;
        }
    }

    // ---- problems -------------------------------------------------------------------------------------------------------

    private static IEnumerable<HtmlImportProblem> Problems(IReadOnlyList<HtmlChange> changes)
    {
        foreach (var change in changes)
        {
            var severity = change.IsSecurity ? MessageIssueSeverity.Warning : MessageIssueSeverity.Info;
            switch (change.Kind)
            {
                case HtmlChangeKind.RemovedElement when change.Detail is not ("style" or "title"):
                    yield return new HtmlImportProblem(HtmlImportProblemKind.RemovedElement, severity, change.Detail);
                    break;
                case HtmlChangeKind.UnwrappedElement when change.Detail is { } name && !name.Contains(':', StringComparison.Ordinal):
                    yield return new HtmlImportProblem(HtmlImportProblemKind.RemovedElement, MessageIssueSeverity.Info, name);
                    break;
                case HtmlChangeKind.RemovedAttribute when change.IsSecurity || (change.Detail is { } attribute && !QuietAttributes.Contains(attribute)
                    && !attribute.Contains(':', StringComparison.Ordinal) && !attribute.StartsWith("data-", StringComparison.Ordinal)
                    && !attribute.StartsWith("xmlns", StringComparison.Ordinal) && !attribute.StartsWith("aria-", StringComparison.Ordinal)):
                    yield return new HtmlImportProblem(HtmlImportProblemKind.RemovedAttribute, severity, change.Detail);
                    break;
                case HtmlChangeKind.RemovedStyle when change.IsSecurity || (change.Detail is { } property && NoticeableStyles.Contains(property)):
                    yield return new HtmlImportProblem(HtmlImportProblemKind.RemovedStyle, severity, change.Detail);
                    break;
                case HtmlChangeKind.HiddenContent:
                    yield return new HtmlImportProblem(HtmlImportProblemKind.RemovedElement, MessageIssueSeverity.Warning, "hidden");
                    break;
                case HtmlChangeKind.LinkRemoved:
                    yield return new HtmlImportProblem(HtmlImportProblemKind.LinkRemoved, MessageIssueSeverity.Warning, change.Detail);
                    break;
            }

            // Image changes were reported by the image pass (every source went through it).
        }
    }

    /// <summary>The sanitizer's hooks: images from the pre-read map, Safe Links unwrapped, relative links resolved.</summary>
    private sealed class ImportHooks(Dictionary<string, MessageImage?> images, Uri? sourceUrl, List<HtmlImportProblem> problems) : IHtmlImportHooks
    {
        public string? ResolveImage(string source) => images.TryGetValue(source, out var image) ? image?.ContentId : null;

        public string RewriteLink(string href)
        {
            if (SafeLinks.TryUnwrap(href, out var original))
            {
                problems.Add(new HtmlImportProblem(HtmlImportProblemKind.LinkUnwrapped, MessageIssueSeverity.Info, UserMessageRules.LinkHost(original)));
                return original;
            }

            if (sourceUrl is not null && HtmlSanitizerRun.SchemeOf(href) is null && href.Length > 0 && !href.StartsWith('#') && !href.StartsWith('{')
                && Uri.TryCreate(sourceUrl, href, out var absolute))
            {
                return absolute.AbsoluteUri;
            }

            return href;
        }
    }

    [GeneratedRegex(@"^\[if\s[^\]]*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevealedStartPattern();

    [GeneratedRegex(@"^\{\s*\$?[A-Za-z][A-Za-z0-9-]*(:[^{}]*)?(\|[^{}]*)?\s*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();
}
