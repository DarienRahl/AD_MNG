using System.Net;

namespace AdMng.Core.EventLogs;

/// <summary>
/// DNS host names as event records and their readers compare them (<c>System/Computer</c>, DCs, collectors): trimmed,
/// lower-case, without a trailing dot. Moved from the Watchdog's attribution, which uses it unchanged. Not to be confused
/// with <see cref="Computers.Remote.HostNames"/> (the strict validation of remote-action targets).
/// </summary>
public static class EventHostNames
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
    /// towards an arbitrary host (Watchdog specification 11.1).
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

    /// <summary>
    /// The computer part of a name from an event: the first label of a DNS name, without leading <c>\\</c> and a
    /// trailing <c>$</c> (computer account), in lower case (<c>WAW-PC001$</c>, <c>\\WAW-PC001</c> and
    /// <c>waw-pc001.contoso.test</c> → <c>waw-pc001</c>); null for empty values and <c>-</c>. IP addresses are returned
    /// whole (normalized like <see cref="EventCodes.NormalizeIpAddress"/>).
    /// </summary>
    public static string? ShortName(string? host)
    {
        var name = EventCodes.NormalizeWorkstation(host);
        if (name is null)
        {
            return null;
        }

        if (EventCodes.NormalizeIpAddress(name) is { } address && IPAddress.TryParse(address, out _))
        {
            return address.ToLowerInvariant();
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var label = (dot > 0 ? name[..dot] : name).TrimEnd('$');
        return label.Length == 0 ? null : label.ToLowerInvariant();
    }

    /// <summary>
    /// True when both names denote the same computer: equal DNS names (<see cref="AreEqual"/>), or a NetBIOS-style name
    /// (one label, as clients supply it in 4740/4776/4625) equal to the other name's first label
    /// (<c>WAW-PC001</c> = <c>waw-pc001.contoso.test</c>). Two different FQDNs never match by their first labels.
    /// </summary>
    public static bool SameComputer(string? a, string? b)
    {
        var left = EventCodes.NormalizeWorkstation(a);
        var right = EventCodes.NormalizeWorkstation(b);
        if (left is null || right is null)
        {
            return false;
        }

        if (AreEqual(left.TrimEnd('$'), right.TrimEnd('$')))
        {
            return true;
        }

        var leftIsFqdn = left.Contains('.', StringComparison.Ordinal);
        var rightIsFqdn = right.Contains('.', StringComparison.Ordinal);
        if (leftIsFqdn && rightIsFqdn)
        {
            return false;
        }

        var leftShort = ShortName(left);
        return leftShort is not null && !IPAddress.TryParse(left, out _) && !IPAddress.TryParse(right, out _)
            && string.Equals(leftShort, ShortName(right), StringComparison.Ordinal);
    }
}
