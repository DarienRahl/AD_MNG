namespace AdMng.Core.Messaging.Html;

/// <summary>What the import of a pasted message could not keep as it was (localized as
/// <c>UserMessages.Rich.Problem.{Kind}</c>).</summary>
public enum HtmlImportProblemKind
{
    /// <summary>An element that is not allowed was removed (detail: the element name).</summary>
    RemovedElement,

    /// <summary>An attribute that is not allowed was removed (detail: the attribute name).</summary>
    RemovedAttribute,

    /// <summary>A CSS declaration that is not allowed was removed (detail: the property).</summary>
    RemovedStyle,

    /// <summary>An image on a web server was removed – remote content is never fetched (detail: the host).</summary>
    RemoteImageRemoved,

    /// <summary>An image was refused: not PNG/JPEG/GIF, malformed, too large or over the limits (detail: the reason).</summary>
    ImageRejected,

    /// <summary>An image could not be read: not in the temp folder, a network path, missing (detail: the file name or scheme).</summary>
    ImageUnreadable,

    /// <summary>A link with a scheme that is not allowed was removed, its text kept (detail: the scheme).</summary>
    LinkRemoved,

    /// <summary>A link points outside the allowed domains (detail: the host).</summary>
    LinkOutsideAllowedDomains,

    /// <summary>A Microsoft Defender Safe Links address was replaced by the original one (detail: the host).</summary>
    LinkUnwrapped,

    /// <summary>The visible text of a link is an address with another host than the link itself (detail: the link's host).</summary>
    LinkTextMismatch,

    /// <summary>A field split by Outlook's formatting was joined again (detail: the field).</summary>
    PlaceholderRepaired,

    /// <summary>The clipboard's CF_HTML offsets were invalid; the fragment markers were used.</summary>
    ClipboardOffsets,

    /// <summary>The clipboard's HTML was not valid UTF-8; invalid bytes were replaced.</summary>
    ClipboardEncoding,

    /// <summary>The content is longer than the limit and was cut, or the template's image limits were reached.</summary>
    ContentTruncated,
}

/// <summary>One finding of a paste. <see cref="Detail"/> is a tag, property, host, scheme, reason or field name – never text
/// of the message.</summary>
public sealed record HtmlImportProblem(HtmlImportProblemKind Kind, MessageIssueSeverity Severity, string? Detail = null)
{
    public override string ToString() => $"{Kind}({Detail})";
}
