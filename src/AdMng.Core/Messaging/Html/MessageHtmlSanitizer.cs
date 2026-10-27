using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Core.Messaging.Html;

/// <summary>What the sanitizer works on: an HTML fragment (a template's body) or a whole message document.</summary>
public enum HtmlSanitizeMode
{
    Fragment,
    Document,
}

/// <summary>The importer's hooks into the sanitizer's URL pass (images, Safe Links, relative URLs).</summary>
public interface IHtmlImportHooks
{
    /// <summary>The content id a pasted image source (<c>file:</c>, <c>data:</c>, a single remote image backed by the
    /// clipboard bitmap) became, or null when it cannot be imported (the hook reports why).</summary>
    string? ResolveImage(string source);

    /// <summary>A link as the importer rewrites it (Safe Links unwrapped, relative URLs resolved), or the input.</summary>
    string RewriteLink(string href);
}

/// <summary>Options of <see cref="MessageHtmlSanitizer"/>.</summary>
public sealed record HtmlSanitizeOptions
{
    public HtmlSanitizeMode Mode { get; init; }

    /// <summary>Content ids of images that may be referenced as <c>cid:</c> (others → finding, the image removed).</summary>
    public IReadOnlySet<string> InlineImageIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Template mode: <c>href</c> may start with a field (<c>{…}</c>); it is checked again after rendering. A field
    /// in an image <c>src</c> is always a finding.</summary>
    public bool AllowFieldUrls { get; init; }

    /// <summary>Only the importer: images, Safe Links, relative URLs.</summary>
    public IHtmlImportHooks? Import { get; init; }
}

/// <summary>Kinds of changes the sanitizer makes.</summary>
public enum HtmlChangeKind
{
    /// <summary>An element removed with its content (detail: the name).</summary>
    RemovedElement,

    /// <summary>An unknown element replaced by its content (detail: the name).</summary>
    UnwrappedElement,

    /// <summary>An attribute removed (detail: the name).</summary>
    RemovedAttribute,

    /// <summary>A CSS declaration removed (detail: the property).</summary>
    RemovedStyle,

    /// <summary>Hidden content (<c>display:none</c>, <c>mso-hide:all</c>) removed (detail: the element).</summary>
    HiddenContent,

    /// <summary>A link with a scheme that is not allowed, unwrapped (detail: the scheme, <c>#</c> or <c>relative</c>).</summary>
    LinkRemoved,

    /// <summary>An image on a web server removed (detail: the host).</summary>
    RemoteImage,

    /// <summary>A <c>cid:</c> reference without its image (detail: the content id).</summary>
    MissingImage,

    /// <summary>An image source that is neither a known <c>cid:</c> nor importable (detail: the scheme).</summary>
    ImageRejected,

    /// <summary>A field in an image <c>src</c> (images are static template assets).</summary>
    FieldInImageSource,

    /// <summary>The HTML is nested deeper than <see cref="MessageHtmlLimits.MaxNestingDepth"/>: nothing of it is kept
    /// (detail: the limit).</summary>
    TooDeep,
}

/// <summary>One change of <see cref="MessageHtmlSanitizer.Sanitize"/>; <see cref="IsSecurity"/>: active content, event
/// handlers, disallowed URL schemes, remote resources, <c>cid:</c> without an image, fields in an image source, CSS that
/// hides or overlays content, HTML nested too deeply.</summary>
public sealed record HtmlChange(HtmlChangeKind Kind, bool IsSecurity, string? Detail = null);

/// <summary>A security finding of <see cref="MessageHtmlSanitizer.Validate"/>.</summary>
public sealed record HtmlFinding(HtmlChangeKind Kind, string? Detail = null)
{
    public override string ToString() => Detail is null ? Kind.ToString() : $"{Kind}({Detail})";
}

/// <summary>Result of <see cref="MessageHtmlSanitizer.Sanitize"/>.</summary>
/// <param name="Html">The sanitized HTML (fragment or document).</param>
/// <param name="Changes">What was removed or changed.</param>
/// <param name="ImageIds">Content ids referenced by <c>cid:</c>, in order of first reference.</param>
/// <param name="Links">The kept <c>href</c> values, in order.</param>
public sealed record HtmlSanitizeResult(string Html, IReadOnlyList<HtmlChange> Changes, IReadOnlyList<string> ImageIds, IReadOnlyList<string> Links)
{
    public override string ToString() => $"HtmlSanitizeResult({Html.Length} characters, {Changes.Count} changes)";
}

/// <summary>
/// The HTML sanitizer of messages to users (design note section 4) – the one component AD Manager and the AdMng Watchdog
/// validator share. Parses with the HTML5 (WHATWG) algorithm (AngleSharp), so the tree it checks is the tree a browser,
/// new Outlook or OWA builds; inlines <c>&lt;style&gt;</c> rules (<see cref="CssInliner"/>); removes hidden content, active
/// and embedding elements, comments, event handlers and every attribute, CSS property, URL scheme and image source outside
/// <see cref="HtmlPolicy"/>; unwraps unknown elements; cleans up (attribute-less spans, empty inline elements, identical
/// adjacent inline elements merged); writes inherited properties only where they change; serializes deterministically.
/// Guarantees (tests): idempotent, stable under re-parsing, <see cref="UserMessageRules.FindForbiddenHtml"/> of the
/// result empty, text and template fields unchanged except by the documented transformations. Pure; never fetches
/// anything (the parser has no loaders).
/// </summary>
public static class MessageHtmlSanitizer
{
    /// <summary>Sanitizes <paramref name="html"/>: the repaired HTML and every change.</summary>
    public static HtmlSanitizeResult Sanitize(string html, HtmlSanitizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(options);
        var run = HtmlSanitizerRun.Execute(html, options);
        return run.Result();
    }

    /// <summary>The security findings only – what <see cref="Sanitize"/> would remove because it is dangerous (empty =
    /// the HTML may be sent as it is, apart from cosmetic differences).</summary>
    public static IReadOnlyList<HtmlFinding> Validate(string html, HtmlSanitizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(options);
        return [.. Sanitize(html, options with { Import = null }).Changes
            .Where(c => c.IsSecurity)
            .Select(c => new HtmlFinding(c.Kind, c.Detail))
            .Distinct()];
    }
}

/// <summary>One sanitizer run over a parsed document (internal so the importer can repair placeholders on the tree before
/// it is serialized).</summary>
internal sealed partial class HtmlSanitizerRun
{
    private static readonly HashSet<string> InlineElements = new(StringComparer.Ordinal)
    {
        "a", "abbr", "b", "bdi", "bdo", "big", "cite", "code", "del", "dfn", "em", "font", "i", "ins", "kbd", "mark", "q", "s", "samp",
        "small", "span", "strike", "strong", "sub", "sup", "tt", "u",
    };

    private readonly HtmlSanitizeOptions _options;
    private readonly List<HtmlChange> _changes = [];

    private HtmlSanitizerRun(IDocument document, IElement root, HtmlSanitizeOptions options)
    {
        Document = document;
        Root = root;
        _options = options;
    }

    public IDocument Document { get; }

    /// <summary><c>body</c> (fragment mode) or <c>html</c> (document mode).</summary>
    public IElement Root { get; }

    /// <summary>The content container (<c>body</c>).</summary>
    public IElement Body => Document.Body ?? Root;

    public IReadOnlyList<HtmlChange> Changes => _changes;

    /// <summary>The HTML was nested deeper than <see cref="MessageHtmlLimits.MaxNestingDepth"/>; the run holds an empty
    /// document instead (and the <see cref="HtmlChangeKind.TooDeep"/> change).</summary>
    public bool TooDeep { get; private init; }

    public static HtmlSanitizerRun Execute(string html, HtmlSanitizeOptions options)
    {
        var run = Parse(html, options);
        run.Sanitize();
        return run;
    }

    /// <summary>Parses without sanitizing.</summary>
    public static HtmlSanitizerRun Parse(string html, HtmlSanitizeOptions options)
    {
        // Every walk below recurses: HTML nested too deeply is replaced by an empty document before any of them runs.
        var parsed = HtmlNesting.ParseDocument(options.Mode == HtmlSanitizeMode.Fragment ? "<!DOCTYPE html><html><head></head><body>" + html + "</body></html>" : html);
        var tooDeep = parsed is null;
        var document = parsed ?? new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument("<!DOCTYPE html><html><head></head><body></body></html>");

        if (document.Body is null)
        {
            // A frameset document: no body to keep.
            var body = document.CreateElement("body");
            document.DocumentElement.AppendChild(body);
        }

        var root = options.Mode == HtmlSanitizeMode.Fragment ? document.Body! : document.DocumentElement;
        var run = new HtmlSanitizerRun(document, root, options) { TooDeep = tooDeep };
        if (tooDeep)
        {
            run._changes.Add(new HtmlChange(HtmlChangeKind.TooDeep, true, MessageHtmlLimits.MaxNestingDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return run;
    }

    public void Sanitize()
    {
        var rules = CollectStyleRules();
        if (rules.Count > 0 && rules.Count * (long)(Root.Descendants<IElement>().Count() + 1) <= MessageHtmlLimits.MaxStyleRuleMatches)
        {
            CssInliner.Inline(Root, rules);
        }

        foreach (var node in Root.Descendants().Where(n => n is IComment or IProcessingInstruction or IDocumentType).ToList())
        {
            node.Parent?.RemoveChild(node);
        }

        if (_options.Mode == HtmlSanitizeMode.Document)
        {
            ProcessDocumentElement(Root);
        }
        else
        {
            ProcessChildren(Root);
        }

        Finish();
    }

    /// <summary>Inherited dedupe, cleanup and white-space normalization (again after the importer's placeholder repair).</summary>
    public void Finish()
    {
        Dedupe(Body, new Dictionary<string, string>(StringComparer.Ordinal));
        Cleanup(Body);
        NormalizeWhiteSpace(Body);
    }

    public HtmlSanitizeResult Result()
    {
        var html = _options.Mode == HtmlSanitizeMode.Fragment ? HtmlSerializer.Children(Root) : HtmlSerializer.Document(Root);
        var images = Body.Descendants<IElement>()
            .Where(e => e.LocalName == "img" && e.GetAttribute("src") is { } src && src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetAttribute("src")![4..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var links = Body.Descendants<IElement>().Where(e => e.LocalName == "a").Select(e => e.GetAttribute("href")).OfType<string>().ToList();
        return new HtmlSanitizeResult(html, [.. _changes], images, links);
    }

    // ---- style sheets ---------------------------------------------------------------------------------------------------

    private List<CssRule> CollectStyleRules()
    {
        var rules = new List<CssRule>();
        foreach (var style in Document.Descendants<IElement>().Where(e => e.LocalName == "style").ToList())
        {
            var css = style.TextContent;
            var compact = new string([.. css.Where(c => !char.IsWhiteSpace(c))]).ToLowerInvariant();
            var dangerous = compact.Contains("url(", StringComparison.Ordinal) || compact.Contains("expression(", StringComparison.Ordinal)
                || compact.Contains("@import", StringComparison.Ordinal) || compact.Contains("behavior", StringComparison.Ordinal)
                || compact.Contains("-moz-binding", StringComparison.Ordinal) || compact.Contains("javascript:", StringComparison.Ordinal);
            _changes.Add(new HtmlChange(HtmlChangeKind.RemovedElement, dangerous, "style"));
            rules.AddRange(CssParser.ParseStyleSheet(css, rules.Count));
            style.Parent?.RemoveChild(style);
        }

        return rules;
    }

    // ---- elements -------------------------------------------------------------------------------------------------------

    private void ProcessDocumentElement(IElement html)
    {
        KeepOnly(html, ["lang"]);
        foreach (var child in html.ChildNodes.ToList())
        {
            if (child is IElement { LocalName: "head" } head)
            {
                foreach (var headChild in head.ChildNodes.ToList())
                {
                    if (headChild is IElement element)
                    {
                        Removed(element.LocalName.ToLowerInvariant());
                    }

                    head.RemoveChild(headChild);
                }

                foreach (var attribute in head.Attributes.ToList())
                {
                    head.RemoveAttribute(attribute.Name);
                }
            }
            else if (child is IElement { LocalName: "body" } body)
            {
                FilterAttributes(body, "body");
                ProcessChildren(body);
            }
            else
            {
                html.RemoveChild(child);
            }
        }
    }

    private void ProcessChildren(INode parent)
    {
        foreach (var child in parent.ChildNodes.ToList())
        {
            switch (child)
            {
                case IElement element:
                    ProcessElement(element);
                    break;
                case IText:
                    break;
                default:
                    parent.RemoveChild(child);
                    break;
            }
        }
    }

    private void ProcessElement(IElement element)
    {
        var name = element.LocalName.ToLowerInvariant();
        var declarations = CssInliner.ToDictionary(CssParser.ParseDeclarations(element.GetAttribute("style")));
        if (CssValues.Hides(declarations))
        {
            _changes.Add(new HtmlChange(HtmlChangeKind.HiddenContent, true, name));
            element.Parent?.RemoveChild(element);
            return;
        }

        if (element.NamespaceUri is { } ns && ns != AngleSharp.Dom.NamespaceNames.HtmlUri)
        {
            // <svg>, <math> and everything inside them (foreign content: scripts, xlink:href, mXSS shapes).
            Removed(name, security: true);
            element.Parent?.RemoveChild(element);
            return;
        }

        if (HtmlPolicy.RemovedElements.Contains(name) || HtmlPolicy.DocumentElements.Contains(name))
        {
            Removed(name);
            element.Parent?.RemoveChild(element);
            return;
        }

        if (!HtmlPolicy.KeptElements.Contains(name))
        {
            // The element goes, its attributes with it – an event handler or a URL is still a finding.
            foreach (var attribute in element.Attributes)
            {
                var attributeName = attribute.Name.ToLowerInvariant();
                if (attributeName.StartsWith("on", StringComparison.Ordinal)
                    || (HtmlPolicy.UrlAttributes.Contains(attributeName) && !string.IsNullOrWhiteSpace(attribute.Value)))
                {
                    _changes.Add(new HtmlChange(HtmlChangeKind.RemovedAttribute, true, attributeName));
                }
            }

            ProcessChildren(element);
            _changes.Add(new HtmlChange(HtmlChangeKind.UnwrappedElement, false, name));
            Unwrap(element);
            return;
        }

        if (!FilterAttributes(element, name))
        {
            return;   // the element was removed or unwrapped by its URL
        }

        ProcessChildren(element);
    }

    private void Removed(string name, bool security = false) =>
        _changes.Add(new HtmlChange(HtmlChangeKind.RemovedElement, security || !HtmlPolicy.CosmeticRemovals.Contains(name), name));

    // ---- attributes -----------------------------------------------------------------------------------------------------

    /// <summary>Filters the attributes; false when the element itself was removed (image) or unwrapped (link).</summary>
    private bool FilterAttributes(IElement element, string name)
    {
        var allowed = HtmlPolicy.ElementAttributes.TryGetValue(name, out var specific) ? specific : [];
        foreach (var attribute in element.Attributes.ToList())
        {
            var attributeName = attribute.Name.ToLowerInvariant();
            if (attributeName == "style")
            {
                FilterStyle(element);
                continue;
            }

            if (attributeName.StartsWith("on", StringComparison.Ordinal))
            {
                _changes.Add(new HtmlChange(HtmlChangeKind.RemovedAttribute, true, attributeName));
                element.RemoveAttribute(attribute.Name);
                continue;
            }

            if (!allowed.Contains(attributeName) && !HtmlPolicy.GlobalAttributes.Contains(attributeName))
            {
                var security = HtmlPolicy.UrlAttributes.Contains(attributeName) && !string.IsNullOrWhiteSpace(attribute.Value);
                _changes.Add(new HtmlChange(HtmlChangeKind.RemovedAttribute, security, attributeName));
                element.RemoveAttribute(attribute.Name);
                continue;
            }

            if (attributeName is "href" or "src")
            {
                continue;
            }

            var value = AttributeValue(name, attributeName, attribute.Value);
            if (value is null)
            {
                _changes.Add(new HtmlChange(HtmlChangeKind.RemovedAttribute, false, attributeName));
                element.RemoveAttribute(attribute.Name);
            }
            else if (value != attribute.Value)
            {
                element.SetAttribute(attribute.Name, value);
            }
        }

        return name switch
        {
            "a" => FilterLink(element),
            "img" => FilterImage(element),
            _ => true,
        };
    }

    private void FilterStyle(IElement element)
    {
        var resolved = CssInliner.Cascade(element, []) ?? [];
        var declarations = CssInliner.ToDictionary(resolved);
        CssValues.ApplyConversions(declarations);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (property, value) in declarations)
        {
            switch (CssValues.Filter(property, value, out var normalized))
            {
                case CssVerdict.Keep:
                    kept[property] = normalized;
                    break;
                case var verdict:
                    _changes.Add(new HtmlChange(HtmlChangeKind.RemovedStyle, verdict == CssVerdict.DropSecurity, property));
                    break;
            }
        }

        // Text too small to read hides it (font-size:0.1px, font:1px Arial); an empty spacer paragraph of 1pt stays.
        if (CssValues.FontSizePixels(kept) < CssValues.MinReadableFontPixels && HasTextOfItsSize(element))
        {
            kept.Remove("font-size");
            kept.Remove("font");
            _changes.Add(new HtmlChange(HtmlChangeKind.RemovedStyle, true, "font-size"));
        }

        SetStyle(element, kept);
    }

    /// <summary>True when the element has visible text in its own font size: text below it that no element with its own
    /// <c>font</c> or <c>font-size</c> encloses.</summary>
    private static bool HasTextOfItsSize(IElement element)
    {
        var stack = new Stack<INode>(element.ChildNodes);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case IText text when text.Data.Any(c => !char.IsWhiteSpace(c)):
                    return true;
                case IElement child when !CssParser.ParseDeclarations(child.GetAttribute("style")).Any(d => d.Property is "font" or "font-size"):
                    foreach (var node in child.ChildNodes)
                    {
                        stack.Push(node);
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>Writes declarations in the fixed order of <see cref="HtmlPolicy.CssProperties"/> (or removes the attribute).</summary>
    internal static void SetStyle(IElement element, IReadOnlyDictionary<string, string> declarations)
    {
        if (declarations.Count == 0)
        {
            element.RemoveAttribute("style");
            return;
        }

        var ordered = HtmlPolicy.CssProperties.Where(declarations.ContainsKey).Select(p => p + ":" + declarations[p]);
        element.SetAttribute("style", string.Join(";", ordered));
    }

    /// <summary>The ordered declarations of an already filtered <c>style</c> attribute.</summary>
    internal static Dictionary<string, string> StyleOf(IElement element) =>
        CssInliner.ToDictionary(CssParser.ParseDeclarations(element.GetAttribute("style")));

    private bool FilterLink(IElement anchor)
    {
        var raw = anchor.GetAttribute("href");
        if (raw is null)
        {
            return true;
        }

        var href = CleanUrl(raw);
        if (_options.AllowFieldUrls && href.StartsWith('{'))
        {
            SetIfChanged(anchor, "href", raw, href);
            return true;
        }

        if (_options.Import is { } hooks)
        {
            href = hooks.RewriteLink(href);
        }

        var scheme = SchemeOf(href);
        var valid = scheme switch
        {
            "http" or "https" => Uri.TryCreate(href, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host),
            "mailto" or "tel" => href.Length > scheme.Length + 1,
            _ => false,
        };
        if (valid)
        {
            SetIfChanged(anchor, "href", raw, href);
            return true;
        }

        var detail = scheme ?? (href.StartsWith('#') ? "#" : href.Length == 0 ? "empty" : "relative");
        _changes.Add(new HtmlChange(HtmlChangeKind.LinkRemoved, scheme is not null && HtmlPolicy.DangerousSchemes.Contains(scheme), detail));
        anchor.RemoveAttribute("href");
        ProcessChildren(anchor);
        Unwrap(anchor);
        return false;
    }

    private bool FilterImage(IElement image)
    {
        var raw = image.GetAttribute("src");
        var source = raw is null ? string.Empty : CleanUrl(raw);
        if (source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = source[4..];
            var known = _options.InlineImageIds.FirstOrDefault(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                SetIfChanged(image, "src", raw!, "cid:" + known);
                return true;
            }

            return RemoveImage(image, HtmlChangeKind.MissingImage, true, id);
        }

        if (source.StartsWith('{'))
        {
            return RemoveImage(image, HtmlChangeKind.FieldInImageSource, true, null);
        }

        if (source.Length > 0 && _options.Import?.ResolveImage(source) is { } contentId)
        {
            image.SetAttribute("src", "cid:" + contentId);
            return true;
        }

        var scheme = SchemeOf(source);
        if (scheme is "http" or "https")
        {
            var host = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : scheme;
            return RemoveImage(image, HtmlChangeKind.RemoteImage, true, host);
        }

        return RemoveImage(image, HtmlChangeKind.ImageRejected, source.Length > 0, scheme ?? (source.Length == 0 ? "empty" : "relative"));
    }

    private bool RemoveImage(IElement image, HtmlChangeKind kind, bool security, string? detail)
    {
        _changes.Add(new HtmlChange(kind, security, detail));
        image.Parent?.RemoveChild(image);
        return false;
    }

    private static void SetIfChanged(IElement element, string attribute, string raw, string value)
    {
        if (!string.Equals(raw, value, StringComparison.Ordinal))
        {
            element.SetAttribute(attribute, value);
        }
    }

    /// <summary>A URL as browsers read it: tab, CR and LF removed anywhere, leading and trailing C0 controls and spaces
    /// trimmed (<c>java&amp;#9;script:</c> is <c>javascript:</c>).</summary>
    internal static string CleanUrl(string value) =>
        new string([.. value.Where(c => c is not ('\t' or '\n' or '\r'))]).Trim(Enumerable.Range(0, 33).Select(i => (char)i).ToArray());

    /// <summary>The lower-case scheme of a URL, or null for a relative one.</summary>
    internal static string? SchemeOf(string url)
    {
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        var scheme = url[..colon];
        return char.IsAsciiLetter(scheme[0]) && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            ? scheme.ToLowerInvariant()
            : null;
    }

    private static void KeepOnly(IElement element, string[] names)
    {
        foreach (var attribute in element.Attributes.ToList())
        {
            if (!names.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase) || (attribute.Name == "lang" && AttributeValue("html", "lang", attribute.Value) is null))
            {
                element.RemoveAttribute(attribute.Name);
            }
        }
    }

    /// <summary>The normalized value of an allowed attribute, or null when it is not valid.</summary>
    private static string? AttributeValue(string element, string attribute, string value)
    {
        var v = value.Trim();
        static string? OneOf(string v, bool caseSensitive, params string[] options) =>
            options.FirstOrDefault(o => string.Equals(o, v, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        static bool Digits(string v, bool percent) =>
            (percent && v.EndsWith('%') ? v[..^1] : v) is { Length: > 0 and <= 5 } d && d.All(char.IsAsciiDigit);
        return attribute switch
        {
            // A tooltip or alternative text that looks like markup, a handler or a script URL could trip the cheap first gate
            // (UserMessageRules.FindForbiddenHtml) of the AdMng Watchdog, an older one included: it is dropped.
            "title" or "alt" => new string([.. value.Where(c => !char.IsControl(c) || c is '\t')]) is { Length: <= 1000 } text
                && UserMessageRules.FindForbiddenAttributeText(text).Count == 0 ? text : null,
            "lang" => System.Text.RegularExpressions.Regex.IsMatch(v, "^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$") ? v : null,
            "dir" => OneOf(v, false, "ltr", "rtl", "auto"),
            "align" => element switch
            {
                "img" => OneOf(v, false, "left", "right", "top", "middle", "bottom", "baseline", "absmiddle", "absbottom", "texttop", "center")?.ToLowerInvariant(),
                "caption" => OneOf(v, false, "top", "bottom", "left", "right")?.ToLowerInvariant(),
                _ => OneOf(v, false, "left", "right", "center", "justify", "char")?.ToLowerInvariant(),
            },
            "valign" => OneOf(v, false, "top", "middle", "bottom", "baseline")?.ToLowerInvariant(),
            "width" or "height" => Digits(v, percent: true) ? v : null,
            "border" or "cellpadding" or "cellspacing" or "hspace" or "vspace" or "colspan" or "rowspan" or "span" or "start" or "value" =>
                Digits(v, percent: false) ? v : null,
            "bgcolor" or "color" => v.StartsWith('#') || v.All(char.IsAsciiLetter) ? CssValues.Color(v) : null,
            "face" => v.Length is > 0 and <= 200 && v.All(c => char.IsLetterOrDigit(c) || c is ' ' or ',' or '-' or '_' or '\'') ? v : null,
            "size" => System.Text.RegularExpressions.Regex.IsMatch(v, "^[+-]?[1-7]$") ? v : null,
            "nowrap" or "reversed" => string.Empty,
            "type" => element switch
            {
                "ol" => OneOf(v, true, "1", "a", "A", "i", "I"),
                "ul" => OneOf(v, false, "disc", "circle", "square")?.ToLowerInvariant(),
                _ => OneOf(v, true, "1", "a", "A", "i", "I") ?? OneOf(v, false, "disc", "circle", "square")?.ToLowerInvariant(),
            },
            "scope" => OneOf(v, false, "row", "col", "rowgroup", "colgroup")?.ToLowerInvariant(),
            _ => null,
        };
    }

    // ---- inherited dedupe -----------------------------------------------------------------------------------------------

    /// <summary>Properties the user agent sets on an element itself (they are not inherited from the parent there).</summary>
    private static IEnumerable<string> UserAgentResets(IElement element) => element.LocalName switch
    {
        "a" => ["color"],
        "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => ["font-size", "font-weight"],
        "b" or "strong" => ["font-weight"],
        "th" => ["font-weight", "text-align"],
        "i" or "em" or "cite" or "dfn" or "var" => ["font-style"],
        "small" or "big" or "sub" or "sup" => ["font-size"],
        "code" or "tt" or "kbd" or "samp" => ["font-family"],
        "pre" => ["font-family", "white-space"],
        "center" or "caption" => ["text-align"],
        "mark" => ["color"],
        "font" => ["color", "font-family", "font-size"],
        // Quirks mode (pasted Office HTML has no doctype): tables do not inherit font properties.
        "table" => HtmlPolicy.InheritedProperties,
        _ => [],
    };

    private static void Dedupe(IElement element, Dictionary<string, string> inherited)
    {
        var here = new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        foreach (var reset in UserAgentResets(element))
        {
            here.Remove(reset);
        }

        if (element.HasAttribute("style"))
        {
            var declarations = StyleOf(element);
            var changed = false;
            foreach (var (property, value) in declarations.ToList())
            {
                if (HtmlPolicy.InheritedProperties.Contains(property) && here.TryGetValue(property, out var parent) && parent == value)
                {
                    declarations.Remove(property);
                    changed = true;
                }
            }

            if (declarations.ContainsKey("font"))
            {
                foreach (var property in new[] { "font-style", "font-variant", "font-weight", "font-size", "line-height", "font-family" })
                {
                    here.Remove(property);
                }
            }

            foreach (var (property, value) in declarations)
            {
                if (HtmlPolicy.InheritedProperties.Contains(property))
                {
                    here[property] = value;
                }
            }

            if (changed)
            {
                SetStyle(element, declarations);
            }
        }

        foreach (var child in element.Children.ToList())
        {
            Dedupe(child, here);
        }
    }

    // ---- cleanup --------------------------------------------------------------------------------------------------------

    private static void Cleanup(IElement root)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var changed = false;
            foreach (var element in root.Descendants<IElement>().Reverse().ToList())
            {
                if (element.Parent is null)
                {
                    continue;
                }

                var name = element.LocalName;
                if ((name is "span" or "font" && element.Attributes.Length == 0) || (name == "a" && !element.HasAttribute("href")))
                {
                    Unwrap(element);
                    changed = true;
                }
                else if ((InlineElements.Contains(name) || name == "div") && element.ChildNodes.All(n => n is IText { Data.Length: 0 }))
                {
                    element.Parent.RemoveChild(element);
                    changed = true;
                }
            }

            root.Normalize();
            changed |= MergeSiblings(root);
            root.Normalize();
            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>Merges adjacent sibling inline elements with the same name and identical attributes.</summary>
    private static bool MergeSiblings(IElement root)
    {
        var changed = false;
        foreach (var element in new[] { root }.Concat(root.Descendants<IElement>()).ToList())
        {
            var child = element.FirstChild;
            while (child is not null)
            {
                var next = child.NextSibling;
                if (child is IElement first && next is IElement second && InlineElements.Contains(first.LocalName)
                    && first.LocalName == second.LocalName && SameAttributes(first, second))
                {
                    foreach (var moved in second.ChildNodes.ToList())
                    {
                        first.AppendChild(moved);
                    }

                    element.RemoveChild(second);
                    changed = true;
                    continue;
                }

                child = next;
            }
        }

        return changed;
    }

    private static bool SameAttributes(IElement a, IElement b) =>
        a.Attributes.Length == b.Attributes.Length
        && a.Attributes.All(attribute => string.Equals(b.GetAttribute(attribute.Name), attribute.Value, StringComparison.Ordinal));

    /// <summary>Removes white-space-only text nodes where they cannot be seen: next to a block element, at the start or
    /// end of a block and between the rows and items of tables and lists (outside <c>pre</c>).</summary>
    private static void NormalizeWhiteSpace(IElement root)
    {
        foreach (var text in root.Descendants<IText>().ToList())
        {
            if (text.Data.Any(c => !IsCollapsible(c)) || text.Parent is not IElement parent || IsPreformatted(parent))
            {
                continue;
            }

            var previous = text.PreviousSibling;
            var next = text.NextSibling;
            var parentIsBlock = IsBlock(parent) || parent == root;
            if ((previous is IElement p && IsBlock(p)) || (next is IElement n && IsBlock(n))
                || (previous is null && parentIsBlock) || (next is null && parentIsBlock)
                || parent.LocalName is "table" or "thead" or "tbody" or "tfoot" or "tr" or "ul" or "ol" or "dl" or "colgroup")
            {
                parent.RemoveChild(text);
            }
            else if (text.Data.Length > 1)
            {
                // Collapsible white space between inline content renders as one space: keep one character.
                text.Data = text.Data.Contains('\n', StringComparison.Ordinal) ? "\n" : " ";
            }
        }

        // Runs of white space with a line break inside text render as one space too (left behind by removed elements);
        // runs of spaces stay (they may be part of a field's format).
        foreach (var text in root.Descendants<IText>().ToList())
        {
            if (text.Parent is IElement parent && !IsPreformatted(parent) && MultiLineWhiteSpace().IsMatch(text.Data))
            {
                text.Data = MultiLineWhiteSpace().Replace(text.Data, "\n");
            }
        }
    }

    private static bool IsCollapsible(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    internal static bool IsBlock(IElement element) => HtmlPolicy.BlockElements.Contains(element.LocalName);

    private static bool IsPreformatted(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (e.LocalName == "pre" || (e.GetAttribute("style") is { } style && style.Contains("white-space:pre", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces an element by its children.</summary>
    internal static void Unwrap(IElement element)
    {
        var parent = element.Parent;
        if (parent is null)
        {
            return;
        }

        foreach (var child in element.ChildNodes.ToList())
        {
            parent.InsertBefore(child, element);
        }

        parent.RemoveChild(element);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[ \t\f]*[\r\n][ \t\r\n\f]*", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MultiLineWhiteSpace();
}
