using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Ldap;

/// <summary>A value returned with the Extended DN control: &lt;GUID=x&gt;;&lt;SID=y&gt;;dn (GUID/SID optional).</summary>
/// <remarks>
/// Components are <c>&lt;GUID=…&gt;</c> ("D" format, or 32 hex digits of the binary GUID as AD returns them with
/// flag 0) and <c>&lt;SID=…&gt;</c> (S-1-… text, or the hex of the binary SID), each followed by <c>;</c>. The
/// remainder is the DN (a <c>;</c> inside a DN value is RFC 4514-escaped). DN-Binary values
/// (<c>B:&lt;len&gt;:&lt;hex&gt;:&lt;dn&gt;</c>) are not handled here.
/// </remarks>
public readonly record struct ExtendedDistinguishedName(Guid? Guid, string? Sid, string Dn)
{
    /// <summary>Parses the string form (flag 1). A plain DN parses with Guid/Sid = null. Never throws.</summary>
    /// <returns>False for null, malformed components (unknown, duplicate, unterminated, invalid GUID/SID) or a DN
    /// part that is not a valid RFC 4514 DN.</returns>
    public static bool TryParse(string? value, out ExtendedDistinguishedName result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        System.Guid? guid = null;
        string? sid = null;
        var i = 0;
        while (i < value.Length && value[i] == '<')
        {
            var close = value.IndexOf('>', i + 1);
            if (close < 0)
            {
                return false;
            }

            var component = value.AsSpan(i + 1, close - i - 1);
            var separator = component.IndexOf('=');
            if (separator <= 0)
            {
                return false;
            }

            var name = component[..separator];
            var data = component[(separator + 1)..];
            if (name.Equals("GUID", StringComparison.OrdinalIgnoreCase))
            {
                if (guid is not null || !TryParseGuid(data, out var parsedGuid))
                {
                    return false;
                }

                guid = parsedGuid;
            }
            else if (name.Equals("SID", StringComparison.OrdinalIgnoreCase))
            {
                if (sid is not null || !TryParseSid(data, out var parsedSid))
                {
                    return false;
                }

                sid = parsedSid;
            }
            else
            {
                return false;
            }

            i = close + 1;
            if (i < value.Length)
            {
                if (value[i] != ';')
                {
                    return false;
                }

                i++;
            }
        }

        var dn = value[i..];
        if (!DistinguishedName.TryParse(dn, out _))
        {
            return false;
        }

        result = new ExtendedDistinguishedName(guid, sid, dn);
        return true;
    }

    /// <summary>Formats the string form (used by FakeDirectorySession).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Guid is { } guid)
        {
            sb.Append("<GUID=").Append(guid.ToString("D", CultureInfo.InvariantCulture)).Append(">;");
        }

        if (Sid is not null)
        {
            sb.Append("<SID=").Append(Sid).Append(">;");
        }

        return sb.Append(Dn).ToString();
    }

    private static bool TryParseGuid(ReadOnlySpan<char> data, out System.Guid guid)
    {
        if (data.Length == 32 && IsHex(data))
        {
            // Flag 0 form: the binary objectGUID (Microsoft little-endian layout) as hex.
            guid = new System.Guid(Convert.FromHexString(data));
            return true;
        }

        return System.Guid.TryParseExact(data, "D", out guid);
    }

    private static bool TryParseSid(ReadOnlySpan<char> data, out string sid)
    {
        sid = string.Empty;
        try
        {
            if (data.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
            {
                // Round trip through the binary form validates and canonicalizes the text.
                sid = AdConvert.SidToString(AdConvert.SidToBytes(data.ToString()));
                return true;
            }

            if (data.Length >= 16 && data.Length % 2 == 0 && IsHex(data))
            {
                sid = AdConvert.SidToString(Convert.FromHexString(data));
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }

        return false;
    }

    private static bool IsHex(ReadOnlySpan<char> data)
    {
        foreach (var c in data)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
