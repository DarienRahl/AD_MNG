using System.Globalization;
using System.Text;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>The HTML document of a Korespondencja message (MailerWPiA's BuildMessage without a &lt;style&gt; block).</summary>
/// <remarks>Webmail drops <c>&lt;body&gt;</c> and <c>&lt;style&gt;</c>, so the font and colour sit on an inner
/// <c>&lt;div&gt;</c> and everything else is inline (the signature is CSS-inlined when it is parsed).</remarks>
public static class CorrespondenceDocument
{
    public const string BodyFont = "Calibri,'Segoe UI',Arial,sans-serif";

    public const double BodyPoints = 11;

    /// <summary>The style of the wrapping <c>div</c>.</summary>
    public static string WrapperStyle { get; } =
        "font-family:" + BodyFont + ";font-size:" + BodyPoints.ToString(CultureInfo.InvariantCulture) + "pt;color:#000000;line-height:1.4";

    /// <summary>
    /// <c>&lt;html&gt;&lt;head&gt;&lt;meta charset=utf-8&gt;&lt;/head&gt;&lt;body&gt;&lt;div
    /// style="font-family:…;font-size:11pt;color:#000000;line-height:1.4"&gt;</c> body (+ "&lt;br&gt;" + signature)
    /// <c>&lt;/div&gt;&lt;/body&gt;&lt;/html&gt;</c>.
    /// </summary>
    public static string Build(string bodyHtml, CorrespondenceSignature? signature)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        var builder = new StringBuilder(bodyHtml.Length + (signature?.BodyHtml.Length ?? 0) + 256);
        builder.Append("<html><head><meta charset=\"utf-8\"></head><body>");
        builder.Append("<div style=\"").Append(WrapperStyle).Append("\">");
        builder.Append(bodyHtml);
        if (!string.IsNullOrWhiteSpace(signature?.BodyHtml))
        {
            builder.Append("<br>").Append(signature.BodyHtml);
        }

        builder.Append("</div></body></html>");
        return builder.ToString();
    }

    /// <summary>The inner content of &lt;body&gt; (for previews); the input when it has no body.</summary>
    public static string BodyOf(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var start = -1;
        for (var i = document.IndexOf("<body", StringComparison.OrdinalIgnoreCase); i >= 0;
             i = document.IndexOf("<body", i + 5, StringComparison.OrdinalIgnoreCase))
        {
            var next = i + 5 < document.Length ? document[i + 5] : '\0';
            if (next == '>' || char.IsWhiteSpace(next) || next == '/')
            {
                var close = document.IndexOf('>', i + 5);
                start = close < 0 ? -1 : close + 1;
                break;
            }
        }

        if (start < 0)
        {
            return document;
        }

        var end = document.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
        if (end < start)
        {
            end = document.Length;
        }

        return document[start..end].Trim();
    }
}
