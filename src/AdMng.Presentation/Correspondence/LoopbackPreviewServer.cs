using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// "Otwórz w przeglądarce" without a file (design note docs/dev/u5-outlook-correspondence.md 9.3, D5, rule 1): a minimal
/// HTTP/1.1 server on <c>127.0.0.1</c> (a random port) that serves each published preview from memory under a random
/// 128-bit token path – <c>/{token}/</c> the document (UTF-8, the CSP of the in-app preview with images from the same origin,
/// <c>no-store</c>) and <c>/{token}/cid/{content id}</c> its images. Only <c>GET</c> with the server's own <c>Host</c>
/// (DNS rebinding) is answered; any other method, host or path is 404; a request whose header is over
/// <see cref="MaxHeaderBytes"/> or that does not arrive within the request timeout is dropped without an answer. A
/// document lives <see cref="DocumentLifetime"/> (at most <see cref="MaxDocuments"/> are kept, the oldest go first) or
/// until <see cref="Dispose"/>. Nothing is written to disk or logged; the constructor opens no socket (the listener starts
/// with the first <see cref="Publish"/>). Thread-safe.
/// </summary>
public sealed partial class LoopbackPreviewServer : IDisposable
{
    /// <summary>The largest request header (request line and fields) accepted; longer requests are dropped.</summary>
    public const int MaxHeaderBytes = 8 * 1024;

    /// <summary>Bytes of the random token in the path of a document (128 bits).</summary>
    public const int TokenBytes = 16;

    /// <summary>Documents kept at the same time; publishing one more forgets the oldest.</summary>
    public const int MaxDocuments = 20;

    /// <summary>Connections served at the same time; more are closed at once (other local processes cannot exhaust the app).</summary>
    internal const int MaxConnections = 32;

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PrunePeriod = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _requestTimeout;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PreviewDocument> _documents = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;
    private ITimer? _pruneTimer;
    private int _port;
    private int _connections;
    private bool _disposed;

    /// <summary>A server whose documents live <see cref="DocumentLifetime"/>.</summary>
    /// <param name="time">The clock of the document lifetime (null: the system clock).</param>
    public LoopbackPreviewServer(TimeProvider? time = null)
        : this(time, DocumentLifetime, DefaultRequestTimeout)
    {
    }

    /// <summary>Tests: another lifetime and request timeout.</summary>
    internal LoopbackPreviewServer(TimeProvider? time, TimeSpan lifetime, TimeSpan requestTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime;
        _requestTimeout = requestTimeout;
    }

    /// <summary>How long a published document is served (15 minutes).</summary>
    public static TimeSpan DocumentLifetime { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The port of the listener, or null before the first <see cref="Publish"/>.</summary>
    public int? Port
    {
        get
        {
            lock (_gate)
            {
                return _listener is null ? null : _port;
            }
        }
    }

    /// <summary>The documents currently served (expired ones are forgotten first).</summary>
    public int Count
    {
        get
        {
            Prune();
            lock (_gate)
            {
                return _documents.Count;
            }
        }
    }

    /// <summary>
    /// Serves <paramref name="content"/> (its <c>cid:</c> images rewritten to the server's own image paths, the title in
    /// <c>&lt;title&gt;</c>) and returns its address <c>http://127.0.0.1:{port}/{token}/</c>. Starts the listener on first use.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The server was disposed.</exception>
    /// <exception cref="SocketException">No loopback port could be opened.</exception>
    public Uri Publish(HtmlPreviewContent content, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));
        var images = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, image) in content.Images ?? new Dictionary<string, MessageImage>())
        {
            if (!string.IsNullOrEmpty(id) && image is not null)
            {
                images.TryAdd(id, image);
            }
        }

        var html = Encoding.UTF8.GetBytes(WithTitle(RewriteImages(content.Html ?? string.Empty, token), title));
        int port;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            port = EnsureStarted();
            RemoveExpired(_time.GetUtcNow());
            while (_documents.Count >= MaxDocuments)
            {
                _documents.Remove(_documents.MinBy(d => d.Value.Expires).Key);
            }

            _documents[token] = new PreviewDocument(html, images, _time.GetUtcNow() + _lifetime);
        }

        return new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/{token}/"));
    }

    /// <summary>Stops the listener and forgets every document; later requests cannot connect.</summary>
    public void Dispose()
    {
        TcpListener? listener;
        CancellationTokenSource? stopping;
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _documents.Clear();
            listener = _listener;
            stopping = _stopping;
            timer = _pruneTimer;
            _listener = null;
            _stopping = null;
            _pruneTimer = null;
        }

        timer?.Dispose();
        stopping?.Cancel();
        listener?.Stop();
        stopping?.Dispose();
    }

    public override string ToString() => nameof(LoopbackPreviewServer);

    /// <summary>Forgets the expired documents (also done on every request and publish, and by a timer each minute).</summary>
    internal void Prune()
    {
        lock (_gate)
        {
            RemoveExpired(_time.GetUtcNow());
        }
    }

    /// <summary>The CSP of a served document: nothing but inline styles and images of this server.</summary>
    internal static string ContentSecurityPolicy(int port) => string.Create(
        CultureInfo.InvariantCulture,
        $"default-src 'none'; img-src http://127.0.0.1:{port}; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");

    /// <summary><c>src="cid:X"</c> → <c>src="/{token}/cid/X"</c> (the id URL-escaped); only the served copy is changed.</summary>
    internal static string RewriteImages(string html, string token) => CidSource().Replace(
        html,
        m => $"src={m.Groups[1].Value}/{token}/cid/{Uri.EscapeDataString(WebUtility.HtmlDecode(m.Groups[2].Value))}{m.Groups[1].Value}");

    /// <summary>The document with <c>&lt;title&gt;</c> (encoded); a fragment is wrapped in a UTF-8 document first.</summary>
    internal static string WithTitle(string html, string? title)
    {
        var element = string.IsNullOrWhiteSpace(title) ? string.Empty : "<title>" + Encode(title.Trim()) + "</title>";
        if (element.Length > 0 && TitleElement().IsMatch(html))
        {
            element = string.Empty;
        }

        if (HeadStart().Match(html) is { Success: true } head)
        {
            return html.Insert(head.Index + head.Length, element);
        }

        if (HtmlStart().Match(html) is { Success: true } root)
        {
            return html.Insert(root.Index + root.Length, "<head><meta charset=\"utf-8\">" + element + "</head>");
        }

        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">" + element + "</head><body>" + html + "</body></html>";
    }

    /// <summary>Text for an element: the markup characters as entities, everything else literal (the document is UTF-8).</summary>
    private static string Encode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private int EnsureStarted()
    {
        if (_listener is not null)
        {
            return _port;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start(backlog: 64);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        var stopping = new CancellationTokenSource();
        _listener = listener;
        _stopping = stopping;
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _pruneTimer = _time.CreateTimer(_ => Prune(), null, PrunePeriod, PrunePeriod);
        // The token is taken now: a Dispose before the loop starts disposes the source, and its Token would throw.
        var token = stopping.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token));
        return _port;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var token in _documents.Where(d => d.Value.Expires <= now).Select(d => d.Key).ToList())
        {
            _documents.Remove(token);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            catch (SocketException)
            {
                if (stopping.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client, stopping);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                timeout.CancelAfter(_requestTimeout);
                client.NoDelay = true;
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (head is null)
                {
                    return;   // over the limit, closed early or too slow: dropped without an answer
                }

                var response = Answer(head);
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The browser went away, the request was too slow or the server stopped: nothing to report (and nothing logged).
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    /// <summary>The request header up to the empty line, or null when it is over <see cref="MaxHeaderBytes"/> or the
    /// connection closed first.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var searchFrom = Math.Max(0, length - 3);
            length += read;
            var end = buffer.AsSpan(searchFrom, length - searchFrom).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return Encoding.Latin1.GetString(buffer, 0, searchFrom + end);
            }
        }

        return null;
    }

    /// <summary>The whole response to a request header.</summary>
    private byte[] Answer(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return NotFound();
        }

        int port;
        lock (_gate)
        {
            port = _port;
        }

        var host = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim())
            .ToList();
        if (host.Count != 1 || !string.Equals(host[0], string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}"), StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var target = request[1];
        var query = target.IndexOfAny(['?', '#']);
        var path = query >= 0 ? target[..query] : target;
        var segments = path.Split('/');
        if (segments.Length < 3 || segments[0].Length != 0 || !IsToken(segments[1]))
        {
            return NotFound();
        }

        PreviewDocument? document;
        lock (_gate)
        {
            RemoveExpired(_time.GetUtcNow());
            _documents.TryGetValue(segments[1], out document);
        }

        if (document is null)
        {
            return NotFound();
        }

        if (segments is [_, _, ""])
        {
            return Ok("text/html; charset=utf-8", document.Html, ContentSecurityPolicy(port));
        }

        if (segments is [_, _, "cid", { Length: > 0 } escaped] && Unescape(escaped) is { } id && document.Images.TryGetValue(id, out var image))
        {
            return Ok(image.MediaType, image.Data, "default-src 'none'");
        }

        return NotFound();
    }

    private static bool IsToken(string segment) =>
        segment.Length == TokenBytes * 2 && segment.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static string? Unescape(string segment)
    {
        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static byte[] Ok(string contentType, byte[] body, string contentSecurityPolicy) =>
        Response("200 OK", contentType, body, contentSecurityPolicy);

    private static byte[] NotFound() => Response("404 Not Found", "text/plain; charset=utf-8", "Not found"u8.ToArray(), "default-src 'none'");

    private static byte[] Response(string status, string contentType, byte[] body, string contentSecurityPolicy)
    {
        var header = "HTTP/1.1 " + status + "\r\nContent-Type: " + contentType
            + "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture)
            + "\r\nCache-Control: no-store\r\nPragma: no-cache\r\nContent-Security-Policy: " + contentSecurityPolicy
            + "\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCross-Origin-Resource-Policy: same-origin"
            + "\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(header);
        var response = new byte[bytes.Length + body.Length];
        bytes.CopyTo(response, 0);
        body.CopyTo(response, bytes.Length);
        return response;
    }

    [GeneratedRegex("""src\s*=\s*(["'])cid:([^"']+)\1""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CidSource();

    [GeneratedRegex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadStart();

    [GeneratedRegex(@"<html(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlStart();

    [GeneratedRegex(@"<title[\s>]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleElement();

    /// <summary>One published preview: the document bytes, its images by content id and when it expires.</summary>
    private sealed record PreviewDocument(byte[] Html, IReadOnlyDictionary<string, MessageImage> Images, DateTimeOffset Expires)
    {
        public override string ToString() => nameof(PreviewDocument);
    }
}
