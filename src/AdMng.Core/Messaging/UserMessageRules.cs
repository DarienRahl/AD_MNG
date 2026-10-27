using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Schema;

namespace AdMng.Core.Messaging;

/// <summary>
/// The security rules of messages to users (README U1, "Bezpieczeństwo i RODO"): no password or secret field, links only to
/// allowed domains, restricted HTML, header-safe text, strict addresses.
/// </summary>
public static partial class UserMessageRules
{
    /// <summary>Longest subject accepted (characters, after rendering).</summary>
    public const int MaxSubjectLength = 250;

    private static readonly string[] PasswordLikeNames = ["password", "pwd", "hasło", "haslo", "pass", "secret", "sekret"];

    private static readonly string[] ForbiddenHtmlElements = ["script", "iframe", "frame", "object", "embed", "form", "input", "link", "meta", "base", "applet"];

    private static readonly string[] ForbiddenSchemes = ["javascript:", "vbscript:", "data:", "file:"];

    /// <summary>
    /// An attribute that must never appear in a message: a secret (<see cref="SecretAttributes"/>, incl. confidential ones
    /// of the schema) or anything named like a password (<c>password</c>, <c>pwd</c>).
    /// </summary>
    public static bool IsForbiddenAttribute(string attribute, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return SecretAttributes.IsSecret(attribute, schema)
            || attribute.Contains("password", StringComparison.OrdinalIgnoreCase)
            || attribute.Contains("pwd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An operator input named like a password (<c>{$input:hasło}</c>) – rejected, a message never carries one.</summary>
    public static bool IsForbiddenInputName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return PasswordLikeNames.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes CR, LF and every other control character (header injection) and trims; runs of spaces stay.</summary>
    public static string HeaderSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Parses one address strictly: a single mailbox with a local part and a domain, no display name, no list separators
    /// and no control or white-space characters.
    /// </summary>
    public static bool TryParseAddress(string? text, out string address)
    {
        address = string.Empty;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 320 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || value.IndexOfAny([',', ';', '<', '>', '"', '(', ')', '[', ']', '\\']) >= 0)
        {
            return false;
        }

        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1 || value.IndexOf('@') != at)
        {
            return false;
        }

        var domain = value[(at + 1)..];
        if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..", StringComparison.Ordinal)
            || !domain.All(c => char.IsLetterOrDigit(c) || c is '.' or '-'))
        {
            return false;
        }

        address = value;
        return true;
    }

    /// <summary>The primary SMTP address of <c>proxyAddresses</c> (the one with the upper-case <c>SMTP:</c> prefix), or null.</summary>
    public static string? PrimarySmtpAddress(IEnumerable<string> proxyAddresses)
    {
        ArgumentNullException.ThrowIfNull(proxyAddresses);
        var primary = proxyAddresses.FirstOrDefault(p => p.StartsWith("SMTP:", StringComparison.Ordinal));
        return primary is null ? null : primary[5..].Trim();
    }

    /// <summary>The domain part of an address (lower case), or null.</summary>
    public static string? DomainOf(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at > 0 && at < address!.Length - 1 ? address[(at + 1)..].ToLowerInvariant() : null;
    }

    /// <summary>
    /// Links in a text or HTML: <c>http(s)://</c>, <c>ftp://</c>, <c>www.</c> and every <c>href</c>/<c>src</c> value except
    /// inline images (<c>cid:</c>).
    /// </summary>
    public static IReadOnlyList<string> FindLinks(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var links = new List<string>();
        foreach (Match match in UrlPattern().Matches(content))
        {
            links.Add(WebUtility.HtmlDecode(match.Value).TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"'));
        }

        foreach (Match match in AttributeUrlPattern().Matches(content))
        {
            // An inline image (cid:, U1c) is part of the message, not a link.
            var value = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (!value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(value);
            }
        }

        return [.. links.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// True when <paramref name="link"/> may appear in a message: <c>mailto:</c>, <c>tel:</c> and in-page anchors, or an
    /// http(s)/ftp link whose host is one of <paramref name="allowedDomains"/> or a subdomain of one.
    /// </summary>
    public static bool IsLinkAllowed(string link, IEnumerable<string> allowedDomains)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        var value = link.Trim();
        if (value.Length == 0 || value.StartsWith('#')
            || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp") || string.IsNullOrEmpty(uri.IdnHost))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return allowedDomains
            .Select(NormalizeDomain)
            .Any(d => d.Length > 0 && (host == d || host.EndsWith("." + d, StringComparison.Ordinal)));
    }

    /// <summary>The host of a link for messages ("evil.example"), or the link itself.</summary>
    public static string LinkHost(string link)
    {
        var value = link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + link : link;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : link;
    }

    /// <summary>A domain of the allow-list in its comparable form ("Contoso.com." → "contoso.com"; "https://x.pl/" → "x.pl").</summary>
    public static string NormalizeDomain(string? domain)
    {
        var value = domain?.Trim() ?? string.Empty;
        if (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.Host;
        }

        value = value.TrimStart('*', '.').TrimEnd('.', '/').ToLowerInvariant();
        try
        {
            return value.Length == 0 ? value : new IdnMapping().GetAscii(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>
    /// What an operator's HTML body must not contain: active elements (<c>script</c>, <c>iframe</c>, <c>object</c>,
    /// <c>embed</c>, <c>form</c>, <c>input</c>, <c>link</c>, <c>meta</c>, <c>base</c>), <c>on…=</c> event handlers and
    /// <c>javascript:</c>/<c>vbscript:</c>/<c>data:</c>/<c>file:</c> URLs. Returns the offending constructs (empty = fine).
    /// Handlers and schemes are looked for in the markup only – tags (comments and conditional comments included) and the
    /// CSS of <c>&lt;style&gt;</c> – never in the text: "Termin (data: 5.11.2026)", "Kliknij "Plik: Zapisz"" or
    /// "Status online = tak" are words of a message. Within the markup a scheme counts where a URL can start (see
    /// <see cref="StartsUrl"/>) and a handler is an attribute name (quoted values are not names).
    /// </summary>
    public static IReadOnlyList<string> FindForbiddenHtml(string? html) =>
        string.IsNullOrWhiteSpace(html) ? [] : Find(html, MarkupRegions(html), quotedValuesAreText: true);

    /// <summary>The constructs of <see cref="FindForbiddenHtml"/> anywhere in a text that becomes an attribute value (a
    /// tooltip, an image's alternative text): stricter than the markup check – a value that only looks like a handler, an
    /// element or a script URL is refused too (the sanitizer drops it).</summary>
    internal static IReadOnlyList<string> FindForbiddenAttributeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Find(text, [text, "=" + text], quotedValuesAreText: false);
    }

    private static List<string> Find(string html, IReadOnlyList<string> markup, bool quotedValuesAreText)
    {
        var found = new List<string>();
        foreach (Match match in ElementPattern().Matches(html))
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            if (ForbiddenHtmlElements.Contains(name))
            {
                found.Add("<" + name + ">");
            }
        }

        foreach (var region in markup)
        {
            foreach (Match match in EventHandlerPattern().Matches(quotedValuesAreText ? QuotedValuePattern().Replace(region, "=\"\"") : region))
            {
                found.Add(match.Groups["name"].Value.ToLowerInvariant() + "=");
            }
        }

        var compact = markup.Select(r => string.Concat(WebUtility.HtmlDecode(r).Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)))).ToList();
        found.AddRange(ForbiddenSchemes.Where(s => compact.Any(r => StartsUrl(r, s))));
        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The markup of <paramref name="html"/> as the HTML tokenizer sees it, conservatively: every tag from <c>&lt;</c> + a
    /// letter, <c>/</c>, <c>!</c> or <c>?</c> to its <c>&gt;</c> outside a quoted attribute value (a comment ends at its first
    /// <c>&gt;</c>, so markup inside a conditional comment is markup too), and the content of <c>&lt;style&gt;</c>.
    /// </summary>
    private static List<string> MarkupRegions(string html)
    {
        var regions = new List<string>();
        for (var i = html.IndexOf('<', StringComparison.Ordinal); i >= 0 && i + 1 < html.Length; i = html.IndexOf('<', i))
        {
            if (!(char.IsAsciiLetter(html[i + 1]) || html[i + 1] is '/' or '!' or '?'))
            {
                i++;
                continue;
            }

            var end = TagEnd(html, i + 1);
            var tag = html[i..end];
            regions.Add(tag);
            i = end;
            if (StyleStartPattern().IsMatch(tag))
            {
                var close = html.IndexOf("</style", i, StringComparison.OrdinalIgnoreCase);
                var stop = close < 0 ? html.Length : close;
                regions.Add(html[i..stop]);
                i = stop;
            }
        }

        return regions;
    }

    /// <summary>The index after the <c>&gt;</c> that ends the tag starting before <paramref name="start"/> (a quote opens a
    /// value only after <c>=</c>), or the end of the text.</summary>
    internal static int TagEnd(string html, int start)
    {
        char? quote = null;
        var afterEquals = false;
        for (var j = start; j < html.Length; j++)
        {
            var c = html[j];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
            }
            else if (c == '>')
            {
                return j + 1;
            }
            else if (afterEquals && c is '"' or '\'')
            {
                quote = c;
                afterEquals = false;
            }
            else if (c == '=')
            {
                afterEquals = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                afterEquals = false;
            }
        }

        return html.Length;
    }

    /// <summary>
    /// True when <paramref name="scheme"/> occurs in the decoded markup without white space and control characters
    /// (<paramref name="compact"/>) where a URL starts: at the beginning, after <c>=</c>, a quote or a backtick (attribute
    /// values, quoted or not) or after <c>(</c> (CSS <c>url(…)</c>). Browsers ignore leading white space and control
    /// characters of a URL, which are already removed.
    /// </summary>
    private static bool StartsUrl(string compact, string scheme)
    {
        for (var i = compact.IndexOf(scheme, StringComparison.OrdinalIgnoreCase); i >= 0; i = compact.IndexOf(scheme, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || compact[i - 1] is '=' or '"' or '\'' or '`' or '(')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>HTML of a plain text: encoded, paragraphs on blank lines, line breaks as &lt;br&gt;.</summary>
    public static string TextToHtml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paragraphs = text.ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => "<p>" + string.Join("<br>\n", p.Split('\n').Select(WebUtility.HtmlEncode)) + "</p>");
        return string.Join("\n", paragraphs);
    }

    /// <summary>
    /// The complete HTML document of a message: <paramref name="bodyHtml"/> (a fragment or a document) with the fixed footer
    /// before <c>&lt;/body&gt;</c>. The document itself passes <see cref="FindForbiddenHtml"/> (AdMng Watchdog checks the
    /// whole HTML body again), so it has no <c>&lt;meta charset&gt;</c> – the MIME part declares UTF-8.
    /// </summary>
    public static string HtmlDocument(string bodyHtml, string footer, MessageLanguage language)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        ArgumentNullException.ThrowIfNull(footer);
        var footerHtml = "<hr style=\"border:none;border-top:1px solid #cccccc;margin-top:24px\">\n"
            + "<p style=\"color:#666666;font-size:9pt\">" + WebUtility.HtmlEncode(footer) + "</p>";
        var end = bodyHtml.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0)
        {
            return bodyHtml[..end] + footerHtml + "\n" + bodyHtml[end..];
        }

        return string.Create(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html>
            <html lang="{MessageLanguages.Code(language)}">
            <body style="font-family:'Segoe UI',Arial,sans-serif;font-size:11pt;color:#1b1b1b">
            {bodyHtml}
            {footerHtml}
            </body>
            </html>
            """);
    }

    /// <summary>The plain text of a message with the fixed footer.</summary>
    public static string TextDocument(string body, string footer)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(footer);
        return body.ReplaceLineEndings("\n").TrimEnd() + "\n\n-- \n" + footer + "\n";
    }

    [GeneratedRegex(@"\b(?:(?:https?|ftp)://|www\.)[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b(?:href|src|action|background|formaction|poster|srcset)\s*=\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttributeUrlPattern();

    [GeneratedRegex(@"<\s*/?\s*(?<name>[a-zA-Z][a-zA-Z0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ElementPattern();

    [GeneratedRegex(@"[\s""'/](?<name>on[a-zA-Z]+)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventHandlerPattern();

    [GeneratedRegex(@"=\s*(?:""[^""]*""|'[^']*')", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValuePattern();

    [GeneratedRegex(@"^<style[\s/>]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleStartPattern();
}
