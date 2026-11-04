using AdMng.Core.Messaging.Images;
using MimeKit;

namespace AdMng.Mail;

/// <summary>
/// The MIME body of a message to a user (U1c, design note section 8) – the one builder used by AD Manager's direct SMTP
/// transport (<see cref="UserMimeMessageBuilder"/>) and the AdMng Watchdog (<c>MimeMessageFactory.CreateUserMessage</c>):
/// without images <c>multipart/alternative</c> (text/plain + text/html, UTF-8) as in U1; with images
/// <c>multipart/alternative</c> of the text and a <c>multipart/related</c> of the HTML and its inline images – each with its
/// <c>Content-ID</c>, <c>Content-Disposition: inline</c> with the file name and base64, no other headers – in order of first
/// reference. Only images the HTML references are added (an unreferenced related part shows as an attachment in some
/// clients). Never an attachment.
/// </summary>
public static class UserMessageMimeBody
{
    /// <summary>The body of a message.</summary>
    /// <param name="text">The plain-text part.</param>
    /// <param name="html">The HTML part; null or empty = text only.</param>
    /// <param name="images">Inline images referenced by <c>cid:</c> from <paramref name="html"/>.</param>
    public static MimeEntity Create(string text, string? html, IReadOnlyList<MessageImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new BodyBuilder { TextBody = text, HtmlBody = string.IsNullOrEmpty(html) ? null : html };
        if (!string.IsNullOrEmpty(html) && images is { Count: > 0 })
        {
            foreach (var image in OrderOfReference(html, images))
            {
                var part = new MimePart(ContentType.Parse(image.MediaType))
                {
                    Content = new MimeContent(new MemoryStream(image.Data, writable: false)),
                    ContentId = image.ContentId,
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = image.FileName },
                    ContentTransferEncoding = ContentEncoding.Base64,
                };
                part.ContentType.Name = image.FileName;
                builder.LinkedResources.Add(part);
            }
        }

        return builder.ToMessageBody();
    }

    /// <summary>The images <paramref name="html"/> references, in order of their first <c>cid:</c> reference, each once.</summary>
    public static IReadOnlyList<MessageImage> OrderOfReference(string html, IReadOnlyList<MessageImage> images)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(images);
        return [.. images
            .DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)
            .Select(i => (Image: i, At: html.IndexOf("cid:" + i.ContentId, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.At >= 0)
            .OrderBy(p => p.At)
            .Select(p => p.Image)];
    }
}
