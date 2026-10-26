using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>Canonical setting values and keys (stage B design 8.5).</summary>
public static class GpoValues
{
    /// <summary>Longest stored text value (and list element); longer values become <c>sha256:&lt;hex&gt;</c>.</summary>
    public const int MaxValueLength = 200;

    /// <summary>Most elements of a stored list value; longer lists become one hash.</summary>
    public const int MaxListElements = 500;

    /// <summary>Longest setting key; longer keys keep a prefix and a hash suffix (keys are never dropped).</summary>
    public const int MaxKeyLength = 512;

    /// <summary>Prefix of hashed values.</summary>
    public const string HashPrefix = "sha256:";

    /// <summary>
    /// Text from a GPO file as a canonical value: CR, LF and TAB become spaces, other control characters, lone surrogates
    /// and noncharacters U+FFFD (the file content is untrusted), surrounding white space trimmed.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            char? replacement = c switch
            {
                '\r' or '\n' or '\t' => ' ',
                _ when char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) => null,
                _ when char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]) => null,
                _ when char.IsSurrogate(c) || char.IsControl(c) || c is '￾' or '￿' => '�',
                _ => null,
            };
            if (replacement is { } r)
            {
                builder ??= new StringBuilder(value, 0, i, value.Length);
                builder.Append(r);
            }
            else
            {
                builder?.Append(c);
            }
        }

        return (builder?.ToString() ?? value).Trim();
    }

    /// <summary>A sanitized value capped at <see cref="MaxValueLength"/>: longer values become their hash.</summary>
    public static (string Value, GpoSettingKind Kind) Cap(string? value)
    {
        var text = Sanitize(value);
        return text.Length <= MaxValueLength ? (text, GpoSettingKind.Text) : (Hash(text), GpoSettingKind.Hash);
    }

    /// <summary>A sanitized value cut to <paramref name="maxLength"/> characters (policy comments, 8.4) – never inside a
    /// surrogate pair.</summary>
    public static string Truncate(string? value, int maxLength = MaxValueLength)
    {
        var text = Sanitize(value);
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..length].TrimEnd();
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the UTF-8 bytes of <paramref name="value"/>.</summary>
    public static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    /// <summary><c>sha256:&lt;hex&gt;</c> of <paramref name="content"/>.</summary>
    public static string Hash(ReadOnlySpan<byte> content) =>
        HashPrefix + Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>A setting key: sanitized; a key longer than <see cref="MaxKeyLength"/> keeps its start and gets a hash suffix
    /// so that it stays unique.</summary>
    public static string Key(string key)
    {
        var text = Sanitize(key);
        if (text.Length <= MaxKeyLength)
        {
            return text;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return text[..(MaxKeyLength - 18)] + "…#" + hash;
    }

    /// <summary>A quoted list item (<c>"a\"b"</c>) for REG_MULTI_SZ values.</summary>
    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Invariant decimal.</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
