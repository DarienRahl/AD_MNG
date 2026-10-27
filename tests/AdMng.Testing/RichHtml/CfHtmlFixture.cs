using System.Globalization;
using System.Reflection;
using System.Text;

namespace AdMng.Testing.RichHtml;

/// <summary>
/// Clipboard "HTML Format" (CF_HTML) entries for the U1c tests: <see cref="Build"/> writes the header with correct UTF-8
/// byte offsets (as Outlook, Word and Chromium do), <see cref="Html"/> reads the HTML fixtures transcribed from real
/// captures (structure verbatim, text replaced by Polish sample text with diacritics; embedded resources).
/// </summary>
public static class CfHtmlFixture
{
    /// <summary>Fixture names (embedded resources <c>RichHtml/Fixtures/{name}.html</c>).</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "outlook-classic", "word-365", "outlook-new", "chrome-edge", "firefox",
        "malicious-script", "malicious-css", "malicious-mxss", "malicious-unc-image",
    ];

    /// <summary>The fixtures transcribed from Office and browsers (not the malicious samples).</summary>
    public static IReadOnlyList<string> RealCaptures { get; } = ["outlook-classic", "word-365", "outlook-new", "chrome-edge", "firefox"];

    /// <summary>The HTML of a fixture.</summary>
    public static string Html(string name)
    {
        var assembly = typeof(CfHtmlFixture).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".RichHtml.Fixtures.{name}.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// A CF_HTML entry for <paramref name="html"/>: Version 0.9 header with 10-digit offsets (StartHTML, EndHTML,
    /// StartFragment, EndFragment, optional SourceURL). The fragment is the range between the
    /// <c>&lt;!--StartFragment--&gt;</c> and <c>&lt;!--EndFragment--&gt;</c> comments (after the first, before the second),
    /// or the whole HTML when it has none. <paramref name="trailingNul"/> appends the NUL and padding of <c>GlobalSize</c>.
    /// </summary>
    public static byte[] Build(string html, string? sourceUrl = null, bool trailingNul = true)
    {
        ArgumentNullException.ThrowIfNull(html);
        var body = Encoding.UTF8.GetBytes(html);
        var start = IndexOf(body, "<!--StartFragment-->"u8);
        var end = IndexOf(body, "<!--EndFragment-->"u8);
        var fragmentStart = start >= 0 ? start + "<!--StartFragment-->".Length : 0;
        var fragmentEnd = end >= 0 ? end : body.Length;
        string Header(int offset) =>
            "Version:0.9\r\n"
            + "StartHTML:" + Offset(offset) + "\r\n"
            + "EndHTML:" + Offset(offset + body.Length) + "\r\n"
            + "StartFragment:" + Offset(offset + fragmentStart) + "\r\n"
            + "EndFragment:" + Offset(offset + fragmentEnd) + "\r\n"
            + (sourceUrl is null ? string.Empty : "SourceURL:" + sourceUrl + "\r\n");
        var headerLength = Encoding.ASCII.GetByteCount(Header(0));
        var header = Encoding.ASCII.GetBytes(Header(headerLength));
        var result = header.Concat(body);
        if (trailingNul)
        {
            result = result.Concat(new byte[] { 0, 0, 0, 0 });
        }

        return [.. result];
    }

    /// <summary>A CF_HTML entry with explicit header values (wrong offsets, -1, missing keys).</summary>
    public static byte[] Raw(string header, string html) => [.. Encoding.ASCII.GetBytes(header), .. Encoding.UTF8.GetBytes(html)];

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> value) => data.AsSpan().IndexOf(value);

    /// <summary>Formats an offset like the Office header (10 digits).</summary>
    public static string Offset(int value) => value.ToString("D10", CultureInfo.InvariantCulture);

    internal static Assembly Assembly => typeof(CfHtmlFixture).Assembly;
}
