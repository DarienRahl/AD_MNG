using System.Globalization;
using System.Net;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>One listener parsed from <c>Hosting.Urls</c>: <c>https://admng-wd.contoso.com:8443</c> (any address, the
/// name is the certificate's), <c>https://+:8443</c> / <c>*</c> / <c>0.0.0.0</c> (any), <c>http://127.0.0.1:0</c>
/// (loopback, ephemeral port), <c>https://10.0.0.5:8443</c> (that address).</summary>
public sealed record ListenerEndpoint(string Url, bool Https, IPAddress? Address, int Port, bool IsLoopback)
{
    /// <summary>Parses <c>;</c>-separated URLs (spaces and empty parts ignored).</summary>
    /// <exception cref="FormatException">A URL is not http(s) with an explicit or default port.</exception>
    public static IReadOnlyList<ListenerEndpoint> ParseAll(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return [];
        }

        return [.. urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse)];
    }

    /// <summary>Parses one URL.</summary>
    public static ListenerEndpoint Parse(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new FormatException("A listener URL must start with http:// or https://.");
        }

        var scheme = url[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            throw new FormatException("A listener URL must use http or https.");
        }

        var https = scheme == "https";
        var authority = url[(schemeEnd + 3)..];
        var slash = authority.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        string host;
        var port = https ? 443 : 80;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new FormatException("A listener URL has an invalid IPv6 address.");
            }

            host = authority[1..close];
            if (close + 1 < authority.Length)
            {
                port = ParsePort(authority[(close + 1)..]);
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon >= 0 ? authority[..colon] : authority;
            if (colon >= 0)
            {
                port = ParsePort(authority[colon..]);
            }
        }

        if (host is "+" or "*" or "" || host == "0.0.0.0" || host == "::")
        {
            return new ListenerEndpoint(url, https, null, port, IsLoopback: false);
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new ListenerEndpoint(url, https, IPAddress.Loopback, port, IsLoopback: true);
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return new ListenerEndpoint(url, https, address, port, IPAddress.IsLoopback(address));
        }

        return new ListenerEndpoint(url, https, null, port, IsLoopback: false);   // a DNS name: listen on every address
    }

    private static int ParsePort(string text)
    {
        if (text.Length < 2 || text[0] != ':' || !int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port > 65535)
        {
            throw new FormatException("A listener URL has an invalid port.");
        }

        return port;
    }
}
