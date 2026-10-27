using System.Net;

namespace AdMng.Core.Computers.Remote;

/// <summary>DNS host names of computers (dNSHostName) as targets of remote actions and tools.</summary>
public static class HostNames
{
    /// <summary>
    /// A DNS host name: 1–253 characters, labels of 1–63 letters, digits, hyphens and underscores (neither at a label's
    /// ends; Windows allows underscores in computer names and Microsoft DNS registers them), a trailing dot allowed. IP
    /// addresses are refused – Kerberos needs the host's name (its SPN). Nothing else passes, so a name is safe on the
    /// tools' command lines.
    /// </summary>
    public static bool IsValid(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.EndsWith('.') ? host[..^1] : host;
        if (name.Length is 0 or > 253 || IPAddress.TryParse(name, out _))
        {
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] is '-' or '_' || label[^1] is '-' or '_' || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        // A name of digits and dots only would be read as an address by some tools.
        return !name.All(c => char.IsAsciiDigit(c) || c == '.');
    }

    /// <summary>The name without a trailing dot, in lower case (the form shown and passed to tools).</summary>
    public static string Normalize(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return (host.EndsWith('.') ? host[..^1] : host).ToLowerInvariant();
    }
}
