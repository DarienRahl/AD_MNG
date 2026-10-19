using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Core.Batch;

/// <summary>
/// How attribute values are represented in the change pipeline (<see cref="AttributeChange"/>, audit, rollback, CSV
/// report): as strings. Binary attributes cannot be edited in a batch (<see cref="IssueCodes.BinaryNotSupported"/>),
/// with one exception: the attributes in <see cref="HexEncodedAttributes"/> (<c>logonHours</c>) travel as lowercase
/// hexadecimal text and are converted to bytes only when they are written (<see cref="ToWire"/>).
/// </summary>
public static class PipelineValues
{
    /// <summary>Binary attributes whose pipeline values are hex strings.</summary>
    public static IReadOnlySet<string> HexEncodedAttributes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Attr.LogonHours,
    };

    public static bool IsHexEncoded(string attribute) => attribute is not null && HexEncodedAttributes.Contains(attribute);

    /// <summary>
    /// The values of <paramref name="attribute"/> as pipeline strings: hex for <see cref="HexEncodedAttributes"/>,
    /// otherwise as <see cref="DirectoryObject.GetStrings"/> returns them. Empty when the attribute is absent.
    /// </summary>
    public static IReadOnlyList<string> Read(DirectoryObject obj, string attribute)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (!IsHexEncoded(attribute))
        {
            return obj.GetStrings(attribute);
        }

        return obj.TryGet(attribute, out var values)
            ? [.. values.Select(v => v is byte[] bytes ? Convert.ToHexStringLower(bytes) : Normalize(attribute, (string)v))]
            : [];
    }

    /// <summary>The canonical pipeline form of a value: hex digits in lower case without whitespace for hex attributes.</summary>
    public static string Normalize(string attribute, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsHexEncoded(attribute)
            ? string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant()
            : value;
    }

    /// <summary>Whether <paramref name="value"/> is a valid pipeline value of a hex-encoded attribute.</summary>
    public static bool IsValidHex(string attribute, string value) =>
        string.Equals(attribute, Attr.LogonHours, StringComparison.OrdinalIgnoreCase)
            ? LogonHours.TryParseHex(value, out _)
            : value.Length > 0 && value.Length % 2 == 0 && value.All(char.IsAsciiHexDigit);

    /// <summary>Whether <see cref="ToWire"/> can convert the value (any string, or hexadecimal text for hex attributes).</summary>
    public static bool IsWireValue(string attribute, string value)
    {
        if (!IsHexEncoded(attribute))
        {
            return true;
        }

        var normalized = Normalize(attribute, value);
        return normalized.Length % 2 == 0 && normalized.All(char.IsAsciiHexDigit);
    }

    /// <summary>The value sent to the directory: bytes for hex attributes, the string itself otherwise.</summary>
    /// <exception cref="FormatException">A hex attribute's value is not hexadecimal.</exception>
    public static object ToWire(string attribute, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsHexEncoded(attribute) ? Convert.FromHexString(Normalize(attribute, value)) : value;
    }
}
