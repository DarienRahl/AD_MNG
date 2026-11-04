using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Correspondence;

/// <summary>One header line of the preview card ("Od:", "Do:", "Temat:", "Załączniki:").</summary>
internal sealed record PreviewCardLine(string Label, string Value)
{
    public override string ToString() => $"{nameof(PreviewCardLine)} {Label}";
}

/// <summary>
/// MailerWPiA's preview card (design note 7.3): a grey page, the message as a card of 700 or 380 px (or the full width) with
/// a header (Od/Do/Temat/Załączniki, optionally a green note) above the body of the message document. The body is the
/// inner content of the document's <c>&lt;body&gt;</c> (<see cref="CorrespondenceDocument.BodyOf"/>); its <c>cid:</c> images
/// stay references (the preview controls answer them from memory). The card has its own charset. Pure, in memory.
/// </summary>
internal static partial class PreviewCard
{
    /// <summary>The file "Zapisz jako .html" writes.</summary>
    public const string FileName = "podglad_wiadomosci.html";

    /// <summary>The card's width in pixels (null: the full width).</summary>
    public static int? MaxWidth(CorrespondencePreviewWidth width) => width switch
    {
        CorrespondencePreviewWidth.Phone => 380,
        CorrespondencePreviewWidth.Full => null,
        _ => 700,
    };

    /// <summary>The card document.</summary>
    /// <param name="header">The header lines (labels and values are encoded).</param>
    /// <param name="note">A green note under the header (the preview from Outlook), or null.</param>
    /// <param name="document">The message document (its body is shown).</param>
    /// <param name="maxWidth">The card's width (null: the full width).</param>
    public static string Build(IReadOnlyList<PreviewCardLine> header, string? note, string document, int? maxWidth)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(document);
        var card = maxWidth is { } pixels
            ? "max-width:" + pixels.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + "px;margin:18px auto;box-shadow:0 2px 10px rgba(15,23,42,.10);border:1px solid #E1E5EA;border-radius:8px;overflow:hidden;background:#fff"
            : "margin:0;background:#fff";
        var builder = new StringBuilder(document.Length + 1024);
        builder.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>img{max-width:100%;height:auto}</style></head>")
            .Append("<body style=\"margin:0;background:#EDEFF2;font-family:'Segoe UI',Arial,sans-serif\">")
            .Append("<div style=\"").Append(card).Append("\">")
            .Append("<div style=\"border-bottom:1px solid #E1E5EA;padding:14px 18px;font-size:13px;color:#1D2433;background:#FAFBFC\">");
        foreach (var line in header)
        {
            builder.Append("<div><b>").Append(Encode(line.Label)).Append("</b> ").Append(Encode(line.Value)).Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.Append("<div style=\"color:#0F7647\"><b>").Append(Encode(note)).Append("</b></div>");
        }

        builder.Append("</div><div style=\"padding:18px\">")
            .Append(CorrespondenceDocument.BodyOf(document))
            .Append("</div></div></body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// The card for a file next to its images: every <c>src="cid:…"</c> whose content id has a file name in
    /// <paramref name="fileNames"/> points to that file (relative, escaped); other references stay.
    /// </summary>
    public static string WithImageFiles(string html, IReadOnlyDictionary<string, string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(fileNames);
        return CidSource().Replace(html, match =>
        {
            var id = WebUtility.HtmlDecode(match.Groups["id"].Value);
            return fileNames.TryGetValue(id, out var file)
                ? match.Groups["start"].Value + Uri.EscapeDataString(file) + match.Groups["end"].Value
                : match.Value;
        });
    }

    /// <summary>The markup characters encoded; letters (Polish ones included) stay literal.</summary>
    private static string Encode(string? text) => FieldTemplate.Encode(text ?? string.Empty, FieldEncoding.Html);

    [GeneratedRegex("(?<start>\\bsrc\\s*=\\s*[\"'])cid:(?<id>[^\"']+)(?<end>[\"'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CidSource();
}
