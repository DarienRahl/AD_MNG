namespace AdMng.Watchdog.Service.Identity;

/// <summary>
/// Minimal reader of the service's krb5.conf (design 4.8) for one check: the realm's <c>primary_kdc</c> (or
/// <c>master_kdc</c>), compared with the PDC emulator (<c>host.primaryKdcNotPdc</c>). Only the <c>[realms]</c> section is
/// looked at; the first value wins like in MIT's profile library.
/// </summary>
public static class Krb5ConfigFile
{
    /// <summary>The primary KDC host of <paramref name="realm"/> (port removed), null when not configured.</summary>
    public static string? PrimaryKdc(IEnumerable<string> lines, string realm)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        string? section = null;
        var inRealm = false;
        string? master = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']', '*', ' ');
                inRealm = false;
                continue;
            }

            if (!string.Equals(section, "realms", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('}'))
            {
                inRealm = false;
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var tag = line[..equals].Trim().TrimEnd('*').Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.StartsWith('{'))
            {
                inRealm = string.Equals(tag, realm, StringComparison.Ordinal);
                continue;
            }

            if (!inRealm || value.Length == 0)
            {
                continue;
            }

            if (string.Equals(tag, "primary_kdc", StringComparison.Ordinal))
            {
                return HostOf(value);
            }

            if (string.Equals(tag, "master_kdc", StringComparison.Ordinal))
            {
                master ??= HostOf(value);
            }
        }

        return master;
    }

    private static string HostOf(string value)
    {
        var host = value.Trim();
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? host[1..close] : host;
        }

        var colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(':', StringComparison.Ordinal) == colon ? host[..colon] : host;
    }
}
