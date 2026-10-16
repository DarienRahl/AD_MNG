using System.Net;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>The <c>insecureUrl</c> rule (8.3): absolute <c>https://</c> URLs with a host and without user info;
/// Development may use <c>http://localhost</c> / loopback addresses.</summary>
internal static class UrlRules
{
    public static bool IsAllowedHttpsUrl(string? url, bool allowLoopbackHttp)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.IsFile
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri);
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
