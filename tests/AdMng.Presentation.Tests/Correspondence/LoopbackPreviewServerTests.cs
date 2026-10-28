using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "Otwórz w przeglądarce" (U5, design note 9.3): the loopback server over real sockets on 127.0.0.1 – the document and its
/// images from memory with the CSP and <c>no-store</c>, a 128-bit token per document, 404 for every other path, method and
/// host, requests over 8 KiB of headers or too slow dropped without an answer, the 15-minute lifetime, the cap on documents,
/// title encoding and the end of everything on dispose.
/// </summary>
public sealed class LoopbackPreviewServerTests : IDisposable
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75), "logo.png");
    private static readonly MessageImage Photo = MessageImage.Create(MessageImageSamples.Jpeg(20, 10), "zdjęcie.jpg");

    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Publish_ServesTheDocumentFromMemory_WithItsHeaders()
    {
        using var server = new LoopbackPreviewServer();
        var html = $"<!DOCTYPE html>\n<html><head></head><body><p>Szanowni Państwo, zażółć gęślą jaźń</p><img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></body></html>";
        var url = server.Publish(Content(html, Logo), "Podgląd – Łódź");

        using var response = await _http.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(
            $"default-src 'none'; img-src http://127.0.0.1:{server.Port}; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("Szanowni Państwo, zażółć gęślą jaźń", body, StringComparison.Ordinal);
        Assert.Contains("<head><title>Podgląd – Łódź</title></head>", body, StringComparison.Ordinal);

        // The cid: reference points to the server's own image path (the served copy only).
        var token = url.Segments[1].TrimEnd('/');
        Assert.Contains($"src=\"/{token}/cid/{Uri.EscapeDataString(Logo.ContentId)}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_AreServedFromMemory_WithTheirMediaType()
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content($"<p><img src=\"cid:{Logo.ContentId}\"><img src='cid:{Photo.ContentId}'></p>", Logo, Photo));

        foreach (var (image, path) in new[] { (Logo, Uri.EscapeDataString(Logo.ContentId)), (Photo, Photo.ContentId) })
        {
            using var response = await _http.GetAsync(new Uri(url, "cid/" + path), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(image.MediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(image.Data, await response.Content.ReadAsByteArrayAsync(Ct));
        }
    }

    [Fact]
    public async Task ContentIds_AreMatchedIgnoringCase_AndHtmlEntitiesAreDecoded()
    {
        using var server = new LoopbackPreviewServer();
        var shouted = Logo.ContentId.ToUpperInvariant().Replace("@", "&#64;", StringComparison.Ordinal);
        var url = server.Publish(Content($"<p><img src=\"cid:{shouted}\"></p>", Logo));
        var body = await _http.GetStringAsync(url, Ct);

        var path = body.Split('"').Single(part => part.Contains("/cid/", StringComparison.Ordinal));
        Assert.EndsWith("/cid/" + Uri.EscapeDataString(Logo.ContentId.ToUpperInvariant()), path, StringComparison.Ordinal);
        using var image = await _http.GetAsync(new Uri(url, path), Ct);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(Logo.Data, await image.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public void Url_IsOnTheLoopbackAddress_WithA128BitTokenPerDocument()
    {
        using var server = new LoopbackPreviewServer();
        Assert.Null(server.Port);   // the constructor opens no socket

        var first = server.Publish(Content("<p>Pierwszy</p>"));
        var second = server.Publish(Content("<p>Drugi</p>"));

        Assert.Equal("http", first.Scheme);
        Assert.Equal("127.0.0.1", first.Host);
        Assert.Equal(server.Port, first.Port);
        Assert.Equal(first.Port, second.Port);
        foreach (var url in new[] { first, second })
        {
            Assert.Matches("^/[0-9a-f]{32}/$", url.AbsolutePath);
        }

        Assert.NotEqual(first.AbsolutePath, second.AbsolutePath);
        Assert.Equal(2, server.Count);
        Assert.Equal(nameof(LoopbackPreviewServer), server.ToString());
    }

    [Fact]
    public async Task EveryOtherPath_Is404()
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content($"<p><img src=\"cid:{Logo.ContentId}\"></p>", Logo));
        var token = url.Segments[1].TrimEnd('/');
        var other = new string('0', 32);
        string[] paths =
        [
            "/", "/favicon.ico", $"/{token}", $"/{token}/x", $"/{token}/cid/", $"/{token}/cid/{Uri.EscapeDataString(Photo.ContentId)}",
            $"/{token}/cid/{Uri.EscapeDataString(Logo.ContentId)}/x", $"/{other}/", $"/{token.ToUpperInvariant()}/", $"/{token[..31]}/",
            $"/{token}/../{token}/", $"/{token}//", $"/x/{token}/", $"/{token}/cid/%ZZ",
        ];

        foreach (var path in paths)
        {
            var response = await RawAsync(server.Port!.Value, $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n");
            Assert.True(response.Status == 404, $"{path}: {response.Status}");
            Assert.Equal("no-store", response.Headers["cache-control"]);
        }

        // The document itself, with a query string, is still served.
        var ok = await RawAsync(server.Port!.Value, $"GET /{token}/?v=2 HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n");
        Assert.Equal(200, ok.Status);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("get")]
    public async Task OnlyGet_IsAnswered(string method)
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content("<p>Treść</p>"));

        var response = await RawAsync(url.Port, $"{method} {url.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1:{url.Port}\r\nContent-Length: 0\r\n\r\n");

        Assert.Equal(404, response.Status);
        Assert.DoesNotContain("Treść", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host: evil.example:{0}\r\n")]
    [InlineData("Host: localhost:{0}\r\n")]
    [InlineData("Host: 127.0.0.1\r\n")]
    [InlineData("")]
    [InlineData("Host: 127.0.0.1:{0}\r\nHost: evil.example\r\n")]
    public async Task AnotherHost_Is404_AgainstDnsRebinding(string hostLine)
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content("<p>Treść</p>"));

        var host = string.Format(CultureInfo.InvariantCulture, hostLine, url.Port);
        var response = await RawAsync(url.Port, $"GET {url.AbsolutePath} HTTP/1.1\r\n{host}\r\n");

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task RequestsUpTo8KiB_AreServed_LongerOnesAreDroppedWithoutAnAnswer()
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content("<p>Treść</p>"));
        var start = $"GET {url.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1:{url.Port}\r\nX-Pad: ";

        var fits = start + new string('a', LoopbackPreviewServer.MaxHeaderBytes - start.Length - 4) + "\r\n\r\n";
        Assert.Equal(LoopbackPreviewServer.MaxHeaderBytes, fits.Length);
        Assert.Equal(200, (await RawAsync(url.Port, fits)).Status);

        var tooLong = start + new string('a', LoopbackPreviewServer.MaxHeaderBytes) + "\r\n\r\n";
        var dropped = await RawAsync(url.Port, tooLong);
        Assert.Equal(0, dropped.Status);
        Assert.Empty(dropped.Body);
    }

    [Fact]
    public async Task ARequestInSeveralPieces_IsServed()
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content("<p>Kawałki</p>"));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, url.Port, Ct);
        var stream = client.GetStream();
        foreach (var piece in new[] { "GET ", url.AbsolutePath, " HTTP/1.1\r", "\nHost: 127.0.0.1:" + url.Port + "\r\n\r", "\n" })
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(piece), Ct);
            await stream.FlushAsync(Ct);
            await Task.Delay(30, Ct);
        }

        var response = Parse(await ReadToEndAsync(stream));
        Assert.Equal(200, response.Status);
        Assert.Contains("Kawałki", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowRequest_IsDroppedAfterTheTimeout()
    {
        using var server = new LoopbackPreviewServer(TimeProvider.System, LoopbackPreviewServer.DocumentLifetime, TimeSpan.FromMilliseconds(300));
        var url = server.Publish(Content("<p>Treść</p>"));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, url.Port, Ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {url.AbsolutePath} HTTP/1.1\r\n"), Ct);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        var answer = await ReadToEndAsync(stream, wait.Token);

        Assert.Empty(answer);   // closed without a response, well before the test's own 10 s
    }

    [Fact]
    public async Task Documents_Expire_After15Minutes()
    {
        var clock = new ManualTimeProvider();
        using var server = new LoopbackPreviewServer(clock);
        var url = server.Publish(Content("<p>Krótko żyjący podgląd</p>"));

        clock.Advance(LoopbackPreviewServer.DocumentLifetime - TimeSpan.FromSeconds(1));
        using (var stillThere = await _http.GetAsync(url, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        using var gone = await _http.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(0, server.Count);

        // A document published later lives its own 15 minutes on the same listener.
        var next = server.Publish(Content("<p>Nowy</p>"));
        Assert.Equal(url.Port, next.Port);
        Assert.Equal("<p>Nowy</p>", ExtractBody(await _http.GetStringAsync(next, Ct)));
    }

    [Fact]
    public async Task OnlyTheNewestDocuments_AreKept()
    {
        var clock = new ManualTimeProvider();
        using var server = new LoopbackPreviewServer(clock);
        var urls = new List<Uri>();
        for (var i = 0; i < LoopbackPreviewServer.MaxDocuments + 3; i++)
        {
            urls.Add(server.Publish(Content($"<p>Dokument {i}</p>")));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(LoopbackPreviewServer.MaxDocuments, server.Count);
        for (var i = 0; i < urls.Count; i++)
        {
            using var response = await _http.GetAsync(urls[i], Ct);
            Assert.Equal(i < 3 ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ParallelRequests_AreServed()
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content($"<p><img src=\"cid:{Logo.ContentId}\"></p>", Logo));
        var image = new Uri(url, "cid/" + Uri.EscapeDataString(Logo.ContentId));

        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            using var response = await _http.GetAsync(i % 2 == 0 ? url : image, Ct);
            return response.StatusCode;
        }));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    [Fact]
    public async Task Dispose_StopsTheListener_AndForgetsTheDocuments()
    {
        var server = new LoopbackPreviewServer();
        var url = server.Publish(Content("<p>Treść</p>"));
        Assert.Equal(1, server.Count);

        server.Dispose();
        server.Dispose();   // twice is harmless

        Assert.Equal(0, server.Count);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync(url, Ct));
        Assert.Throws<ObjectDisposedException>(() => server.Publish(Content("<p>Po zamknięciu</p>")));
    }

    [Fact]
    public async Task TheTitle_IsEncoded_AndAFragmentBecomesADocument()
    {
        using var server = new LoopbackPreviewServer();
        var url = server.Publish(Content("<p>Treść <b>pogrubiona</b></p>"), "<script>alert(1)</script> & „Żółć”");

        var body = await _http.GetStringAsync(url, Ct);

        Assert.StartsWith("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>&lt;script&gt;alert(1)&lt;/script&gt; &amp; „Żółć”</title></head><body>", body, StringComparison.Ordinal);
        Assert.EndsWith("<p>Treść <b>pogrubiona</b></p></body></html>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html lang=\"pl\"><body><p>A</p></body></html>", null, "<html lang=\"pl\"><head><meta charset=\"utf-8\"></head><body><p>A</p></body></html>")]
    [InlineData("<html><head><title>Własny</title></head><body>B</body></html>", "Inny", "<html><head><title>Własny</title></head><body>B</body></html>")]
    [InlineData("<HTML><HEAD class=\"x\"></HEAD>C</HTML>", "T", "<HTML><HEAD class=\"x\"><title>T</title></HEAD>C</HTML>")]
    [InlineData("<p>D</p>", "  ", "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"></head><body><p>D</p></body></html>")]
    public void WithTitle_KeepsTheDocument(string html, string? title, string expected) =>
        Assert.Equal(expected, LoopbackPreviewServer.WithTitle(html, title));

    [Fact]
    public void RewriteImages_ChangesOnlyCidSources()
    {
        var html = "<img src=\"cid:a@b\"><img src='cid:c d'><img src=\"https://x.pl/cid:e\"><a href=\"cid:f\">cid:g</a>";

        var rewritten = LoopbackPreviewServer.RewriteImages(html, "0123");

        Assert.Equal("<img src=\"/0123/cid/a%40b\"><img src='/0123/cid/c%20d'><img src=\"https://x.pl/cid:e\"><a href=\"cid:f\">cid:g</a>", rewritten);
    }

    private static HtmlPreviewContent Content(string html, params MessageImage[] images) =>
        new(html, images.ToDictionary(i => i.ContentId, StringComparer.Ordinal));

    private static string ExtractBody(string document)
    {
        var start = document.IndexOf("<body>", StringComparison.Ordinal) + "<body>".Length;
        return document[start..document.LastIndexOf("</body>", StringComparison.Ordinal)];
    }

    private static async Task<RawResponse> RawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        var stream = client.GetStream();
        try
        {
            await stream.WriteAsync(Encoding.Latin1.GetBytes(request), Ct);
            await stream.FlushAsync(Ct);
        }
        catch (IOException)
        {
            // The server may close a too long request before it is fully written.
        }

        return Parse(await ReadToEndAsync(stream));
    }

    private static async Task<byte[]> ReadToEndAsync(NetworkStream stream, CancellationToken? cancellationToken = null)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken ?? Ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }
        }
        catch (IOException)
        {
            // A reset after a dropped request: what arrived before it counts.
        }

        return buffer.ToArray();
    }

    private static RawResponse Parse(byte[] data)
    {
        var end = data.AsSpan().IndexOf("\r\n\r\n"u8);
        if (end < 0)
        {
            return new RawResponse(0, new Dictionary<string, string>(), data);
        }

        var lines = Encoding.ASCII.GetString(data, 0, end).Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
        var headers = lines.Skip(1).Select(l => l.Split(": ", 2)).ToDictionary(p => p[0].ToLowerInvariant(), p => p[1], StringComparer.Ordinal);
        return new RawResponse(status, headers, data[(end + 4)..]);
    }

    private sealed record RawResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);
}
