using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdMng.Core.Messaging.Html;

/// <summary>The HTML of a clipboard "HTML Format" (CF_HTML) entry with the fragment markers guaranteed to be present.</summary>
/// <param name="Html">The full HTML (context included) with <c>&lt;!--StartFragment--&gt;</c> / <c>&lt;!--EndFragment--&gt;</c>
/// or the sentinel comments of <see cref="CfHtmlParser"/>.</param>
/// <param name="SourceUrl">The <c>SourceURL</c> header (only for resolving relative links – never fetched, never stored).</param>
/// <param name="Problems">Invalid offsets, invalid UTF-8.</param>
public sealed record CfHtmlDocument(string Html, Uri? SourceUrl, IReadOnlyList<HtmlImportProblem> Problems)
{
    public override string ToString() => $"CfHtmlDocument({Html.Length} characters)";
}

/// <summary>
/// Parses the Windows clipboard format "HTML Format" (CF_HTML, design note section 2): <c>Key:Value</c> header lines
/// (<c>Version</c>, <c>StartHTML</c>, <c>EndHTML</c>, <c>StartFragment</c>, <c>EndFragment</c>, <c>SourceURL</c>), decimal
/// UTF-8 byte offsets from the start of the buffer, then the HTML. The fragment is marked by Outlook/Word/Chromium's
/// <c>&lt;!--StartFragment--&gt;</c> comments, else by sentinel comments inserted at the fragment offsets when they lie on a
/// tag boundary, else the whole <c>&lt;body&gt;</c> is the fragment. Pure.
/// </summary>
public static partial class CfHtmlParser
{
    /// <summary>Largest CF_HTML entry accepted (8 MiB).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    public const string StartFragmentComment = "<!--StartFragment-->";
    public const string EndFragmentComment = "<!--EndFragment-->";

    /// <summary>Data of the sentinel comment inserted at the fragment start when the source wrote no markers.</summary>
    public const string SentinelStart = "admng-fragment-start";

    /// <summary>Data of the sentinel comment inserted at the fragment end.</summary>
    public const string SentinelEnd = "admng-fragment-end";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Parses the raw bytes of a CF_HTML entry (the offsets are byte offsets).</summary>
    public static CfHtmlDocument Parse(ReadOnlySpan<byte> data)
    {
        var problems = new List<HtmlImportProblem>();
        var header = ReadHeader(data, out var headerEnd);
        var sourceUrl = SourceUrlOf(header);
        var length = data.Length;
        var offsets = Offsets(header, length);
        string html;
        if (offsets is { } o)
        {
            var hasMarkers = Contains(data[o.StartHtml..o.EndHtml], "<!--StartFragment"u8);
            if (!hasMarkers && OnTagBoundary(data, o.StartFragment, o.StartHtml, o.EndHtml) && OnTagBoundary(data, o.EndFragment, o.StartHtml, o.EndHtml))
            {
                html = Decode(data[o.StartHtml..o.StartFragment], problems)
                    + "<!--" + SentinelStart + "-->"
                    + Decode(data[o.StartFragment..o.EndFragment], problems)
                    + "<!--" + SentinelEnd + "-->"
                    + Decode(data[o.EndFragment..o.EndHtml], problems);
            }
            else
            {
                html = Decode(data[o.StartHtml..o.EndHtml], problems);
            }
        }
        else
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ClipboardOffsets, MessageIssueSeverity.Info));
            html = Decode(data[headerEnd..], problems).TrimEnd('\0');
        }

        return new CfHtmlDocument(EnsureMarkers(StripBom(html)), sourceUrl, problems.Distinct().ToList());
    }

    /// <summary>Parses CF_HTML that arrived as a string (WPF's conversion): the header is skipped, the offsets ignored and
    /// the fragment markers used.</summary>
    public static CfHtmlDocument FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        var headerText = string.Empty;
        if (HeaderLinePattern().IsMatch(text))
        {
            var firstTag = text.IndexOf('<', StringComparison.Ordinal);
            start = firstTag < 0 ? text.Length : firstTag;
            headerText = text[..start];
        }

        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            AddHeaderLine(header, line);
        }

        return new CfHtmlDocument(EnsureMarkers(StripBom(text[start..].TrimEnd('\0'))), SourceUrlOf(header), []);
    }

    /// <summary>The header lines up to the first <c>&lt;</c> (or a line that is not <c>Key:Value</c>).</summary>
    private static Dictionary<string, string> ReadHeader(ReadOnlySpan<byte> data, out int headerEnd)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        var limit = Math.Min(data.Length, 4096);
        while (position < limit && data[position] != (byte)'<')
        {
            var end = position;
            while (end < limit && data[end] is not ((byte)'\r' or (byte)'\n') && data[end] != (byte)'<')
            {
                end++;
            }

            var line = Encoding.ASCII.GetString(data[position..end]);
            if (!AddHeaderLine(header, line) && line.Trim().Length > 0)
            {
                break;
            }

            position = end;
            while (position < limit && data[position] is (byte)'\r' or (byte)'\n')
            {
                position++;
            }
        }

        headerEnd = position;
        return header;
    }

    private static bool AddHeaderLine(Dictionary<string, string> header, string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var key = line[..colon].Trim();
        if (key.Length == 0 || !key.All(char.IsAsciiLetterOrDigit))
        {
            return false;
        }

        header[key] = line[(colon + 1)..].Trim();
        return true;
    }

    private readonly record struct CfOffsets(int StartHtml, int EndHtml, int StartFragment, int EndFragment);

    /// <summary>Valid offsets: 0 ≤ StartHTML ≤ StartFragment ≤ EndFragment ≤ EndHTML ≤ length (StartHTML / EndHTML −1 =
    /// the fragment only); null when anything is missing or inconsistent.</summary>
    private static CfOffsets? Offsets(Dictionary<string, string> header, int length)
    {
        if (!header.TryGetValue("Version", out var version) || version is not ("0.9" or "1.0"))
        {
            return null;
        }

        if (Number(header, "StartFragment") is not { } startFragment || Number(header, "EndFragment") is not { } endFragment)
        {
            return null;
        }

        var startHtml = Number(header, "StartHTML") ?? -1;
        var endHtml = Number(header, "EndHTML") ?? -1;
        if (startHtml == -1 || endHtml == -1)
        {
            (startHtml, endHtml) = (startFragment, endFragment);
        }

        return startHtml >= 0 && startHtml <= startFragment && startFragment <= endFragment && endFragment <= endHtml && endHtml <= length
            ? new CfOffsets(startHtml, endHtml, startFragment, endFragment)
            : null;
    }

    private static int? Number(Dictionary<string, string> header, string key) =>
        header.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= -1
            ? value
            : null;

    private static Uri? SourceUrlOf(Dictionary<string, string> header) =>
        header.TryGetValue("SourceURL", out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : null;

    /// <summary>The offset lies between a tag's <c>&gt;</c> and the next <c>&lt;</c> (or at an end of the HTML).</summary>
    private static bool OnTagBoundary(ReadOnlySpan<byte> data, int offset, int start, int end) =>
        offset == start || offset == end || data[offset - 1] == (byte)'>' || data[offset] == (byte)'<';

    private static bool Contains(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value) >= 0;

    private static string Decode(ReadOnlySpan<byte> bytes, List<HtmlImportProblem> problems)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            problems.Add(new HtmlImportProblem(HtmlImportProblemKind.ClipboardEncoding, MessageIssueSeverity.Warning));
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static string StripBom(string html) => html.Length > 0 && html[0] == '\uFEFF' ? html[1..] : html;

    /// <summary>Adds sentinel comments around the body content when the HTML has neither the Office/Chromium markers nor
    /// the sentinels.</summary>
    private static string EnsureMarkers(string html)
    {
        if ((html.Contains(StartFragmentComment, StringComparison.OrdinalIgnoreCase) && html.Contains(EndFragmentComment, StringComparison.OrdinalIgnoreCase))
            || (html.Contains("<!--" + SentinelStart + "-->", StringComparison.Ordinal) && html.Contains("<!--" + SentinelEnd + "-->", StringComparison.Ordinal)))
        {
            return html;
        }

        var body = BodyStartPattern().Match(html);
        var start = body.Success ? body.Index + body.Length : 0;
        var bodyEnd = html.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
        var end = bodyEnd >= start ? bodyEnd : html.Length;
        return html[..start] + "<!--" + SentinelStart + "-->" + html[start..end] + "<!--" + SentinelEnd + "-->" + html[end..];
    }

    [GeneratedRegex(@"\A\s*Version\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLinePattern();

    [GeneratedRegex(@"<body\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyStartPattern();
}
