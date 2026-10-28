using AdMng.Core.Messaging.Images;

namespace AdMng.Presentation.Correspondence;

/// <summary>A formatted paste for the editor: a sanitized HTML fragment and its <c>cid:</c> images.</summary>
/// <param name="Html">The fragment (sanitized subset, <c>{{fields}}</c> repaired).</param>
/// <param name="Images">The images the fragment references.</param>
public sealed record RichPaste(string Html, IReadOnlyList<MessageImage> Images)
{
    public override string ToString() => $"{nameof(RichPaste)} ({Images.Count} images)";
}

/// <summary>
/// What the WYSIWYG editor of Korespondencja (App <c>RichMessageEditor</c>, design note 9.2) needs from its view model:
/// the U1c paste workflow over the clipboard, image registration and the error line.
/// </summary>
public interface IRichEditorHost
{
    /// <summary>The body images by content id (the editor shows them inline).</summary>
    IReadOnlyDictionary<string, MessageImage> Images { get; }

    /// <summary>Ctrl+V with CF_HTML: the U1c workflow over the clipboard; null when nothing could be pasted.</summary>
    Task<RichPaste?> PasteFormattedAsync();

    /// <summary>Validates and registers an image (a pasted bitmap); null when refused (the reason through <see cref="ReportEditorError"/>).</summary>
    MessageImage? AddImage(byte[] data, string? fileName);

    /// <summary>Shows an error of the editor (a refused image, a paste that failed).</summary>
    void ReportEditorError(string text);
}
