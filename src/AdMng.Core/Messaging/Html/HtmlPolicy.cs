namespace AdMng.Core.Messaging.Html;

/// <summary>Limits of the HTML of messages to users (U1c).</summary>
public static class MessageHtmlLimits
{
    /// <summary>Characters of the HTML of one language after sanitizing (Outlook HTML is verbose once its styles are
    /// inlined; U1b's 100 000 was too small). The AdMng Watchdog reports its own value.</summary>
    public const int MaxHtmlLength = 400_000;

    /// <summary>The HTML length an AdMng Watchdog without the U1c contract accepts.</summary>
    public const int LegacyMaxHtmlLength = 100_000;
}

/// <summary>
/// The allow-lists of the message HTML sanitizer (design note section 4.3, policy version 1): elements kept, removed with
/// their content or unwrapped; attributes per element; CSS properties (values are checked by <c>CssValues</c>); URL
/// schemes. Shared by AD Manager and the AdMng Watchdog validator, so both apply the same rules.
/// </summary>
public static class HtmlPolicy
{
    /// <summary>Version of the policy (bumped when an allow-list changes).</summary>
    public const int Version = 1;

    /// <summary>Elements kept (with filtered attributes).</summary>
    public static IReadOnlySet<string> KeptElements { get; } = Set(
        "a", "abbr", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center", "cite", "code", "col", "colgroup", "dd", "del",
        "dfn", "div", "dl", "dt", "em", "font", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "mark", "ol",
        "p", "pre", "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "tt", "u", "ul", "wbr");

    /// <summary>Elements kept only in document mode (<c>head</c> is emptied).</summary>
    public static IReadOnlySet<string> DocumentElements { get; } = Set("html", "head", "body");

    /// <summary>Elements removed together with their content.</summary>
    public static IReadOnlySet<string> RemovedElements { get; } = Set(
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input", "button", "select", "textarea",
        "option", "link", "meta", "base", "title", "svg", "math", "template", "noscript", "audio", "video", "source", "track", "canvas",
        "picture", "xml", "param", "dialog", "portal", "slot", "datalist", "keygen", "output", "progress", "meter", "map", "area",
        "head", "noembed", "noframes", "xmp", "plaintext", "listing");

    /// <summary>Removed elements that are not a security finding by themselves (<c>style</c> is inlined first and its
    /// rules checked; a <c>title</c> is invisible text).</summary>
    public static IReadOnlySet<string> CosmeticRemovals { get; } = Set("style", "title", "head", "noscript", "xml", "xmp", "plaintext", "listing", "noframes", "noembed");

    /// <summary>Block-level elements (serialization line breaks, text conversion, placeholder repair boundaries).</summary>
    public static IReadOnlySet<string> BlockElements { get; } = Set(
        "html", "head", "body", "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col", "hr", "pre", "center");

    /// <summary>Void elements (no end tag).</summary>
    public static IReadOnlySet<string> VoidElements { get; } = Set("br", "hr", "img", "col", "wbr");

    /// <summary>Attributes every kept element may carry.</summary>
    public static IReadOnlyList<string> GlobalAttributes { get; } = ["title", "lang", "dir"];

    /// <summary>Attributes per element (besides <see cref="GlobalAttributes"/> and <c>style</c>), in serialization order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ElementAttributes { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["p"] = ["align"],
        ["div"] = ["align"],
        ["h1"] = ["align"],
        ["h2"] = ["align"],
        ["h3"] = ["align"],
        ["h4"] = ["align"],
        ["h5"] = ["align"],
        ["h6"] = ["align"],
        ["blockquote"] = ["align"],
        ["caption"] = ["align"],
        ["a"] = ["href"],
        ["img"] = ["src", "alt", "width", "height", "border", "align", "hspace", "vspace"],
        ["table"] = ["border", "cellpadding", "cellspacing", "width", "align", "bgcolor"],
        ["td"] = ["colspan", "rowspan", "width", "height", "align", "valign", "bgcolor", "nowrap"],
        ["th"] = ["colspan", "rowspan", "width", "height", "align", "valign", "bgcolor", "nowrap", "scope"],
        ["tr"] = ["align", "valign", "bgcolor"],
        ["tbody"] = ["align", "valign", "bgcolor"],
        ["thead"] = ["align", "valign", "bgcolor"],
        ["tfoot"] = ["align", "valign", "bgcolor"],
        ["col"] = ["span", "width"],
        ["colgroup"] = ["span", "width"],
        ["ol"] = ["start", "type", "reversed"],
        ["ul"] = ["type"],
        ["li"] = ["value", "type"],
        ["font"] = ["face", "size", "color"],
        ["html"] = [],
    };

    /// <summary>Attributes that carry a URL (dropping one with a value is a security finding: a remote resource or active
    /// content).</summary>
    public static IReadOnlySet<string> UrlAttributes { get; } = Set(
        "href", "src", "srcset", "background", "action", "formaction", "poster", "dynsrc", "lowsrc", "data", "codebase", "classid",
        "profile", "manifest", "icon", "ping", "xlink:href", "xml:base", "srcdoc", "archive");

    /// <summary>Allowed CSS properties in the fixed serialization order of the declarations.</summary>
    public static IReadOnlyList<string> CssProperties { get; } =
    [
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-width", "border-style", "border-color",
        "border-top-width", "border-top-style", "border-top-color",
        "border-right-width", "border-right-style", "border-right-color",
        "border-bottom-width", "border-bottom-style", "border-bottom-color",
        "border-left-width", "border-left-style", "border-left-color",
        "border-collapse", "border-spacing", "border-radius",
        "width", "min-width", "max-width", "height", "min-height",
        "font", "font-style", "font-variant", "font-weight", "font-size", "line-height", "font-family",
        "color", "background-color",
        "text-align", "text-indent", "text-transform", "text-decoration", "text-decoration-line", "text-decoration-style",
        "text-decoration-color", "vertical-align", "letter-spacing", "word-spacing", "white-space", "direction",
        "list-style-type", "list-style-position", "table-layout", "caption-side", "empty-cells", "word-wrap", "overflow-wrap",
        "word-break",
    ];

    /// <summary>Inherited properties: not written again when the nearest ancestor sets the same value.</summary>
    public static IReadOnlySet<string> InheritedProperties { get; } = Set(
        "color", "font-style", "font-variant", "font-weight", "font-size", "font-family", "line-height", "letter-spacing", "word-spacing",
        "text-align", "text-transform", "white-space", "direction");

    /// <summary>Link schemes kept in <c>a@href</c>.</summary>
    public static IReadOnlySet<string> LinkSchemes { get; } = Set("http", "https", "mailto", "tel");

    /// <summary>URL schemes whose presence is a security finding (active content or local resources).</summary>
    public static IReadOnlySet<string> DangerousSchemes { get; } = Set("javascript", "vbscript", "data", "file", "blob", "livescript", "mocha", "jar");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
