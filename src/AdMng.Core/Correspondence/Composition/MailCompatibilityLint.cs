using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.Messaging.Html;
using AngleSharp.Dom;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>What may break in mail clients (MailerWPiA's compatibility warnings of the preview).</summary>
public enum MailLintKind
{
    Script,
    RemoteImage,
    DataImage,
    AbsolutePosition,
    BackgroundImage,
    TooWide,
    StyleBlock,
    LinkTextMismatch,
    LargeMessage,
}

/// <summary>One finding of <see cref="MailCompatibilityLint"/>.</summary>
/// <param name="Kind">The finding.</param>
/// <param name="Detail">Pixels for <see cref="MailLintKind.TooWide"/>, the host for a link mismatch, bytes for
/// <see cref="MailLintKind.LargeMessage"/>; else null.</param>
public sealed record MailLintFinding(MailLintKind Kind, string? Detail);

/// <summary>Mail client compatibility checks of a message document (the preview's warnings).</summary>
/// <remarks>Port of MailerWPiA's <c>Lint</c> on the parsed document instead of the raw text (text that only mentions
/// "position:absolute" is no finding): <c>script</c> elements; images from web servers and <c>data:</c> images; CSS
/// <c>position:absolute|fixed</c>; background images (CSS or the <c>background</c> attribute); a <c>width</c> (attribute or
/// CSS, not <c>max-width</c>) above <see cref="MaxWidthPixels"/> px; <c>&lt;style&gt;</c> blocks; links whose text names
/// another host (one finding per host); a message above <see cref="CorrespondenceLimits.MessageWarningBytes"/>. One
/// finding per kind otherwise, in the order of <see cref="MailLintKind"/>.</remarks>
public static partial class MailCompatibilityLint
{
    /// <summary>Wider elements force sideways scrolling on a phone (MailerWPiA's 700 px).</summary>
    public const int MaxWidthPixels = 700;

    /// <summary>The findings of <paramref name="html"/>; <paramref name="messageBytes"/> (images + attachments) adds
    /// <see cref="MailLintKind.LargeMessage"/> above <see cref="CorrespondenceLimits.MessageWarningBytes"/>.</summary>
    public static IReadOnlyList<MailLintFinding> Check(string? html, long messageBytes = 0)
    {
        var findings = new List<MailLintFinding>();
        if (!string.IsNullOrWhiteSpace(html) && HtmlNesting.ParseDocument(html) is { } document)
        {
            var elements = document.Descendants<IElement>().ToList();
            if (elements.Any(e => e.LocalName == "script"))
            {
                findings.Add(new MailLintFinding(MailLintKind.Script, null));
            }

            var sources = elements.Where(e => e.LocalName == "img").Select(e => (e.GetAttribute("src") ?? string.Empty).Trim()).ToList();
            if (sources.Any(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("//", StringComparison.Ordinal)))
            {
                findings.Add(new MailLintFinding(MailLintKind.RemoteImage, null));
            }

            if (sources.Any(s => s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new MailLintFinding(MailLintKind.DataImage, null));
            }

            var styles = elements.Select(e => (Element: e, Style: e.GetAttribute("style") ?? string.Empty)).Where(s => s.Style.Length > 0).ToList();
            if (styles.Any(s => PositionPattern().IsMatch(s.Style)))
            {
                findings.Add(new MailLintFinding(MailLintKind.AbsolutePosition, null));
            }

            if (styles.Any(s => BackgroundImagePattern().IsMatch(s.Style)) || elements.Any(e => !string.IsNullOrWhiteSpace(e.GetAttribute("background"))))
            {
                findings.Add(new MailLintFinding(MailLintKind.BackgroundImage, null));
            }

            if (Widest(elements) is { } wide)
            {
                findings.Add(new MailLintFinding(MailLintKind.TooWide, wide.ToString(CultureInfo.InvariantCulture)));
            }

            if (elements.Any(e => e.LocalName == "style"))
            {
                findings.Add(new MailLintFinding(MailLintKind.StyleBlock, null));
            }

            var hosts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in HtmlLinkInspector.Links(html))
            {
                if (HtmlLinkInspector.IsTextMismatch(link) && HtmlLinkInspector.HostOf(link.Href) is { } host && hosts.Add(host))
                {
                    findings.Add(new MailLintFinding(MailLintKind.LinkTextMismatch, host));
                }
            }
        }

        if (messageBytes > CorrespondenceLimits.MessageWarningBytes)
        {
            findings.Add(new MailLintFinding(MailLintKind.LargeMessage, messageBytes.ToString(CultureInfo.InvariantCulture)));
        }

        return findings;
    }

    /// <summary>The first width in pixels above the limit (attribute <c>width</c> or CSS <c>width</c>/<c>min-width</c>),
    /// or null.</summary>
    private static int? Widest(List<IElement> elements)
    {
        foreach (var element in elements)
        {
            if (element.GetAttribute("width") is { } attribute && Pixels(attribute.Trim(), attributeValue: true) is > MaxWidthPixels and var fromAttribute)
            {
                return fromAttribute;
            }

            if (element.GetAttribute("style") is { Length: > 0 } style)
            {
                foreach (Match match in WidthPattern().Matches(style))
                {
                    if (Pixels(match.Groups[1].Value.Trim(), attributeValue: false) is > MaxWidthPixels and var fromStyle)
                    {
                        return fromStyle;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>A length in pixels: "800", "800px" (attribute: also plain numbers), "600pt" (× 4/3); percentages and other
    /// units → null.</summary>
    private static int? Pixels(string value, bool attributeValue)
    {
        var match = LengthPattern().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = match.Groups[2].Value.ToLowerInvariant();
        double? pixels = unit switch
        {
            "px" => number,
            "" when attributeValue || number == 0 => number,
            "pt" => number * 4 / 3,
            _ => null,
        };
        return pixels is { } p ? (int)Math.Round(Math.Min(p, int.MaxValue)) : null;
    }

    [GeneratedRegex(@"(?<![\w-])position\s*:\s*(absolute|fixed)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PositionPattern();

    [GeneratedRegex(@"(?<![\w-])background(-image)?\s*:[^;]*url\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackgroundImagePattern();

    [GeneratedRegex(@"(?<![\w-])(?:min-)?width\s*:\s*([^;!]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WidthPattern();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([a-zA-Z%]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();
}
