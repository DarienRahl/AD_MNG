using System.Windows.Controls;
using System.Windows.Documents;
using AdMng.Core.Messaging.Images;

namespace AdMng.App.Correspondence;

/// <summary>
/// The HTML round trip of the Korespondencja editor (design note 9.2) for the sanitized subset: paragraphs, alignment,
/// lists, tables, runs (bold, italic, underline/strike, colour, background, size, font), links, line breaks and
/// <c>cid:</c> images. The output is re-sanitized by the U1c sanitizer.
/// </summary>
public static class FlowDocumentHtml
{
    /// <summary>MailerWPiA's FromDocument: the document as an HTML fragment; <paramref name="contentIdOf"/> gives an image's content id.</summary>
    public static string ToHtml(FlowDocument document, Func<Image, string?> contentIdOf) => throw new NotImplementedException("U5 T7a app");

    /// <summary>The inverse for the sanitized subset; <c>img cid:</c> becomes an inline image tagged with its content id.</summary>
    public static FlowDocument ToDocument(string html, IReadOnlyDictionary<string, MessageImage> images) => throw new NotImplementedException("U5 T7a app");
}
