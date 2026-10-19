using System.Net;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>DNS host names as attribution compares them: trimmed, lower-case, without a trailing dot.</summary>
public static class HostNames
{
    /// <summary>The normalized form of <paramref name="host"/>.</summary>
    public static string Normalize(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>True when both names denote the same host (case-insensitive, trailing dot ignored).</summary>
    public static bool AreEqual(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="host"/> can name a DC or a WEF collector the service identity authenticates to: at
    /// most 253 letters, digits, <c>.</c>, <c>-</c> or <c>_</c> with at least one letter, and not an IP literal in any
    /// form <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> accepts (<c>10.0.0.5</c>, <c>0x7f000001</c>,
    /// <c>2130706433</c>, IPv6). Kerberos needs a host name (SPN); an IP literal would let the session fall back to NTLM
    /// towards an arbitrary host (section 11.1).
    /// </summary>
    public static bool IsHostName(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253
            || !host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            || !host.Any(char.IsLetter))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out _);
    }
}
