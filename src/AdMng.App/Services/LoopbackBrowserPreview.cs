using AdMng.Presentation.Correspondence;
using AdMng.Presentation.UserMessages;

namespace AdMng.App.Services;

/// <summary>
/// "Otwórz w przeglądarce" without a file (design note 9.3): a <c>TcpListener</c> on 127.0.0.1 serves each preview under a
/// random 128-bit token path (the document and its <c>cid</c> images, CSP of the preview, no-store) for 15 minutes or until
/// the app exits; any other path or method is 404. Application singleton; nothing is written to disk and the constructor
/// opens no socket.
/// </summary>
public sealed class LoopbackBrowserPreview : IBrowserPreview, IDisposable
{
    /// <summary>Scaffold: the App track starts the listener and the browser.</summary>
    public Task OpenAsync(HtmlPreviewContent content, string title) => Task.CompletedTask;

    /// <summary>Stops the listener and forgets every document.</summary>
    public void Dispose()
    {
    }

    public override string ToString() => nameof(LoopbackBrowserPreview);
}
