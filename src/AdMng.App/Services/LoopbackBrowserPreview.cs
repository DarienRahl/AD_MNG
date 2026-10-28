using System.Diagnostics;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.UserMessages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.App.Services;

/// <summary>
/// "Otwórz w przeglądarce" without a file (design note 9.3, D5): publishes the preview on the application's
/// <see cref="LoopbackPreviewServer"/> (127.0.0.1, a random 128-bit token path, the document and its <c>cid</c> images
/// from memory, CSP of the preview, no-store, 15 minutes or until the app exits) and opens its address in the default
/// browser. Application singleton; nothing is written to disk and the constructor opens no socket.
/// </summary>
public sealed class LoopbackBrowserPreview : IBrowserPreview, IDisposable
{
    private readonly LoopbackPreviewServer _server;
    private readonly Action<Uri> _openBrowser;
    private readonly ILogger<LoopbackBrowserPreview> _logger;

    public LoopbackBrowserPreview(TimeProvider? time = null, ILogger<LoopbackBrowserPreview>? logger = null)
        : this(new LoopbackPreviewServer(time), OpenInDefaultBrowser, logger)
    {
    }

    /// <summary>Tests: another server and another way to open the address (no browser starts).</summary>
    internal LoopbackBrowserPreview(LoopbackPreviewServer server, Action<Uri> openBrowser, ILogger<LoopbackBrowserPreview>? logger = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _openBrowser = openBrowser ?? throw new ArgumentNullException(nameof(openBrowser));
        _logger = logger ?? NullLogger<LoopbackBrowserPreview>.Instance;
    }

    /// <summary>The server (tests).</summary>
    internal LoopbackPreviewServer Server => _server;

    /// <summary>Serves <paramref name="content"/> on the loopback server and opens it in the default browser.</summary>
    /// <exception cref="InvalidOperationException">The preview is empty.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">No loopback port could be opened.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">No browser could be started.</exception>
    public Task OpenAsync(HtmlPreviewContent content, string title)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsEmpty)
        {
            throw new InvalidOperationException("There is no preview to open.");
        }

        var address = _server.Publish(content, title);
        try
        {
            _openBrowser(address);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Never the address (it carries the token) or the content – only that no browser could be started.
            _logger.LogWarning("The browser could not be started ({Type}).", ex.GetType().Name);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the server; open browser tabs can no longer load the preview.</summary>
    public void Dispose() => _server.Dispose();

    public override string ToString() => nameof(LoopbackBrowserPreview);

    private static void OpenInDefaultBrowser(Uri address)
    {
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}
