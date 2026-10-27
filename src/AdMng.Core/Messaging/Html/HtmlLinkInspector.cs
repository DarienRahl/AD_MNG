using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Core.Messaging.Html;

/// <summary>A link of an HTML body: its target and visible text.</summary>
public sealed record HtmlLink(string Href, string Text);

/// <summary>
/// The links of an HTML body (parsed like a browser) and the phishing shape "the visible text is an address with another
/// host than the link" (design note section 6, <c>LinkTextMismatch</c>).
/// </summary>
public static partial class HtmlLinkInspector
{
    /// <summary>Every <c>a</c> with an <c>href</c>, in document order.</summary>
    public static IReadOnlyList<HtmlLink> Links(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html);
        return [.. document.Descendants<IElement>()
            .Where(e => e.LocalName == "a" && e.HasAttribute("href"))
            .Select(e => new HtmlLink(e.GetAttribute("href")!.Trim(), e.TextContent.Trim()))];
    }

    /// <summary>The host of an http(s) link, lower case without a leading "www.", or null.</summary>
    public static string? HostOf(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var value = href.Trim();
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? StripWww(uri.IdnHost.ToLowerInvariant())
            : null;
    }

    /// <summary>True when the text of <paramref name="link"/> looks like an address or a host and names another host than
    /// the link's target (<c>&lt;a href="https://evil.example/"&gt;haslo.contoso.pl&lt;/a&gt;</c>).</summary>
    public static bool IsTextMismatch(HtmlLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var target = HostOf(link.Href);
        if (target is null)
        {
            return false;
        }

        var text = link.Text.Trim().TrimEnd('.', ',', ';', ':', '!', '?');
        if (!AddressPattern().IsMatch(text))
        {
            return false;
        }

        var shown = HostOf(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text);
        return shown is not null && !string.Equals(shown, target, StringComparison.Ordinal);
    }

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

    [GeneratedRegex(@"^(https?://)?[\p{L}\d-]+(\.[\p{L}\d-]+)*\.\p{L}{2,}(:\d+)?(/\S*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
