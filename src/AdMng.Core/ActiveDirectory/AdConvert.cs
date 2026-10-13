using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AdMng.Core.ActiveDirectory;

/// <summary>Conversions between Active Directory wire formats and .NET types.</summary>
public static class AdConvert
{
    /// <summary>accountExpires value meaning "never" (also 0).</summary>
    public const long FileTimeNever = long.MaxValue;

    /// <summary>
    /// Converts an AD "Interval" / large integer FILETIME (100ns ticks since 1601-01-01 UTC) to a timestamp.
    /// Returns <c>null</c> for 0 and <see cref="long.MaxValue"/> which AD uses for "never / not set".
    /// </summary>
    public static DateTimeOffset? FromFileTime(long fileTime)
    {
        if (fileTime <= 0 || fileTime == FileTimeNever)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Parses the decimal string form of a FILETIME attribute.</summary>
    public static DateTimeOffset? FromFileTime(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ft) ? FromFileTime(ft) : null;

    /// <summary>Converts a timestamp to FILETIME ticks (UTC).</summary>
    public static long ToFileTime(DateTimeOffset value) => value.ToUniversalTime().ToFileTime();

    /// <summary>
    /// Parses LDAP GeneralizedTime as used by AD (<c>20261013141500.0Z</c>, also with offset or fractions).
    /// </summary>
    public static DateTimeOffset? FromGeneralizedTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14)
        {
            return null;
        }

        var core = value[..14];
        if (!DateTime.TryParseExact(core, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return null;
        }

        var rest = value[14..];
        var fraction = 0d;
        if (rest.StartsWith('.') || rest.StartsWith(','))
        {
            var end = 1;
            while (end < rest.Length && char.IsAsciiDigit(rest[end]))
            {
                end++;
            }

            if (end > 1)
            {
                fraction = double.Parse("0." + rest[1..end], CultureInfo.InvariantCulture);
            }

            rest = rest[end..];
        }

        var offset = TimeSpan.Zero;
        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-')
            && int.TryParse(rest.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            && int.TryParse(rest.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (rest[0] == '-')
            {
                offset = -offset;
            }
        }

        var utc = new DateTimeOffset(dt.AddSeconds(fraction), TimeSpan.Zero) - offset;
        return utc;
    }

    /// <summary>Formats a timestamp as GeneralizedTime (<c>yyyyMMddHHmmss.0Z</c>).</summary>
    public static string ToGeneralizedTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".0Z";

    /// <summary>Converts a binary SID to its SDDL string form (S-1-5-21-...).</summary>
    public static string SidToString(ReadOnlySpan<byte> sid)
    {
        if (sid.Length < 8)
        {
            throw new FormatException("SID is too short.");
        }

        var revision = sid[0];
        var subCount = sid[1];
        if (sid.Length < 8 + (subCount * 4))
        {
            throw new FormatException("SID length does not match sub-authority count.");
        }

        long authority = 0;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | sid[i];
        }

        var sb = new StringBuilder("S-");
        sb.Append(revision.ToString(CultureInfo.InvariantCulture)).Append('-');
        sb.Append(authority.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < subCount; i++)
        {
            var sub = BinaryPrimitives.ReadUInt32LittleEndian(sid.Slice(8 + (i * 4), 4));
            sb.Append('-').Append(sub.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Converts an SDDL SID string to its binary form.</summary>
    public static byte[] SidToBytes(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var parts = sid.Split('-');
        if (parts.Length < 3 || !string.Equals(parts[0], "S", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Invalid SID '{sid}'.");
        }

        var revision = byte.Parse(parts[1], CultureInfo.InvariantCulture);
        var authority = long.Parse(parts[2], CultureInfo.InvariantCulture);
        var subs = parts.Skip(3).Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var result = new byte[8 + (subs.Length * 4)];
        result[0] = revision;
        result[1] = (byte)subs.Length;
        for (var i = 7; i >= 2; i--)
        {
            result[i] = (byte)(authority & 0xFF);
            authority >>= 8;
        }

        for (var i = 0; i < subs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8 + (i * 4), 4), subs[i]);
        }

        return result;
    }

    /// <summary>Relative identifier (last sub-authority) of a SID string.</summary>
    public static uint? GetRid(string? sid)
    {
        if (string.IsNullOrEmpty(sid))
        {
            return null;
        }

        var last = sid.LastIndexOf('-');
        return last > 0 && uint.TryParse(sid.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var rid)
            ? rid
            : null;
    }

    /// <summary>Converts objectGUID bytes (AD stores the Microsoft little-endian GUID layout).</summary>
    public static Guid GuidFromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 16 ? new Guid(bytes) : throw new FormatException("GUID must be 16 bytes.");

    /// <summary>
    /// Encodes a password for the unicodePwd attribute: the value surrounded by double quotes, UTF-16LE.
    /// The caller must send it over an encrypted connection (Kerberos sealing or TLS).
    /// </summary>
    public static byte[] EncodeUnicodePwd(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Encoding.Unicode.GetBytes("\"" + password + "\"");
    }

    /// <summary>Parses an integer attribute value stored as decimal string.</summary>
    public static long? ParseInt64(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Converts a negative AD interval (e.g. maxPwdAge, lockoutDuration) to a TimeSpan.</summary>
    public static TimeSpan? FromInterval(long value)
    {
        if (value == long.MinValue || value == 0)
        {
            return null;
        }

        return TimeSpan.FromTicks(Math.Abs(value));
    }
}
