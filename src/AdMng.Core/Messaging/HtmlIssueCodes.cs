namespace AdMng.Core.Messaging;

/// <summary>
/// Issue codes of formatted messages with inline images (U1c, design note section 7.4; localization keys
/// <c>UserMessages.Issue.{code}</c>). <see cref="MessageIssueCodes"/> stays unchanged.
/// </summary>
public static class HtmlIssueCodes
{
    /// <summary>A <c>cid:</c> reference without its image (error; argument: the content id or file name).</summary>
    public const string ImageMissing = "ImageMissing";

    /// <summary>The images are over the limits of a message or a template (error; argument: what is too large).</summary>
    public const string ImagesTooLarge = "ImagesTooLarge";

    /// <summary>The message is sent without its images (info: the operator chose "send without images").</summary>
    public const string ImagesOmitted = "ImagesOmitted";

    /// <summary>The HTML of a message or a template is longer than the limit (error; argument: the length).</summary>
    public const string HtmlTooLong = "HtmlTooLong";

    /// <summary>The HTML is nested deeper than <c>MessageHtmlLimits.MaxNestingDepth</c> (error; argument: the limit).</summary>
    public const string HtmlTooDeep = "HtmlTooDeep";

    /// <summary>A link's text names another host than its target (warning; argument: the target host).</summary>
    public const string LinkTextMismatch = "LinkTextMismatch";

    /// <summary>The template's HTML will be normalized when it is saved (info, template level).</summary>
    public const string HtmlNormalized = "HtmlNormalized";

    /// <summary>Every code (localization test).</summary>
    public static IReadOnlyList<string> All { get; } = [ImageMissing, ImagesTooLarge, ImagesOmitted, HtmlTooLong, HtmlTooDeep, LinkTextMismatch, HtmlNormalized];
}
