using System.ComponentModel;
using System.Net;
using System.Net.Http;
using AdMng.App.Services;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// "Otwórz w przeglądarce" of the App (U5, design note 9.3): the preview is published on the loopback server and its
/// address handed to the browser – the document and its images are served from memory, every other path is 404; an empty
/// preview is refused; a browser that cannot start is reported; the server stops with the service. No browser starts in
/// these tests.
/// </summary>
public sealed class LoopbackBrowserPreviewTests : IDisposable
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75), "logo.png");

    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task OpenAsync_ServesThePreview_AndOpensItsAddress()
    {
        SkipUnlessWindows();
        var opened = new List<Uri>();
        using var preview = new LoopbackBrowserPreview(new LoopbackPreviewServer(), opened.Add);
        var content = new HtmlPreviewContent(
            $"<!DOCTYPE html><html><head></head><body><p>Szanowni Państwo,</p><img src=\"cid:{Logo.ContentId}\"></body></html>",
            new Dictionary<string, MessageImage> { [Logo.ContentId] = Logo });

        await preview.OpenAsync(content, "Podgląd – Jan Kowalski");

        var address = Assert.Single(opened);
        Assert.Equal("127.0.0.1", address.Host);
        Assert.Matches("^/[0-9a-f]{32}/$", address.AbsolutePath);
        var page = await _http.GetStringAsync(address, Ct);
        Assert.Contains("<title>Podgląd – Jan Kowalski</title>", page, StringComparison.Ordinal);
        Assert.Contains("Szanowni Państwo,", page, StringComparison.Ordinal);
        var image = await _http.GetByteArrayAsync(new Uri(address, "cid/" + Uri.EscapeDataString(Logo.ContentId)), Ct);
        Assert.Equal(Logo.Data, image);

        using var other = await _http.GetAsync(new Uri(address, "/favicon.ico"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using var unknown = await _http.GetAsync(new Uri(address, "cid/img-ffffffffffffffff%40admng"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(nameof(LoopbackBrowserPreview), preview.ToString());
    }

    [Fact]
    public async Task AnEmptyPreview_IsRefused_WithoutABrowser()
    {
        SkipUnlessWindows();
        var opened = new List<Uri>();
        using var preview = new LoopbackBrowserPreview(new LoopbackPreviewServer(), opened.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.OpenAsync(HtmlPreviewContent.Empty, "Pusty"));

        Assert.Empty(opened);
        Assert.Null(preview.Server.Port);   // nothing listened
    }

    [Fact]
    public async Task ABrowserThatCannotStart_IsReported()
    {
        SkipUnlessWindows();
        using var preview = new LoopbackBrowserPreview(new LoopbackPreviewServer(), _ => throw new Win32Exception(2));

        await Assert.ThrowsAsync<Win32Exception>(() => preview.OpenAsync(new HtmlPreviewContent("<p>Treść</p>", new Dictionary<string, MessageImage>()), "Tytuł"));
    }

    [Fact]
    public async Task Dispose_StopsTheServer()
    {
        SkipUnlessWindows();
        Uri? address = null;
        var preview = new LoopbackBrowserPreview(new LoopbackPreviewServer(), uri => address = uri);
        await preview.OpenAsync(new HtmlPreviewContent("<p>Treść</p>", new Dictionary<string, MessageImage>()), "Tytuł");
        Assert.NotNull(address);

        preview.Dispose();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync(address, Ct));
    }

    [Fact]
    public void TheApplicationService_OpensNoSocketWhenCreated()
    {
        SkipUnlessWindows();
        using var preview = new LoopbackBrowserPreview();

        Assert.Null(preview.Server.Port);
    }

    private static void SkipUnlessWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "The App runs on Windows.");
}
