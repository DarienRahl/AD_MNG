namespace AdMng.Core.Messaging.Html;

/// <summary>
/// Microsoft Defender for Office 365 Safe Links (design note section 6): a message copied from Outlook carries
/// <c>https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fhaslo.contoso.pl%2F&amp;data=…</c> instead of the
/// original address. The original (the decoded <c>url</c> parameter) is restored when it is itself an allowed-scheme URL;
/// other rewriting services are not unwrapped (they show up as their own domain).
/// </summary>
public static class SafeLinks
{
    private const string HostSuffix = "safelinks.protection.outlook.com";

    /// <summary>True when <paramref name="href"/> is a Safe Links address with a usable original; <paramref name="original"/>
    /// is that address.</summary>
    public static bool TryUnwrap(string? href, out string original)
    {
        original = string.Empty;
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host != HostSuffix && !host.EndsWith("." + HostSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !pair[..equals].Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')).Trim();
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || (Uri.TryCreate(value, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" && !string.IsNullOrEmpty(target.Host)))
            {
                original = value;
                return true;
            }

            return false;
        }

        return false;
    }
}
