using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.PowerShell.Reading;

/// <summary>
/// Turns the raw values of one attribute into what a script works with, by the rules the program's attribute view uses
/// (<c>AttributeFormatter</c>: the same decisions about what is a FILETIME, a SID, a GUID, a flag set), but typed:
/// a number stays a number, a date is a <see cref="DateTime"/> (local time, the way the program shows it), a boolean is a
/// <see cref="bool"/>, a multi-valued attribute is an array. FILETIME 0 and the "never" sentinels are <c>$null</c> (the
/// program shows "never"). docs/dev/r1-powershell-window.md 12.2 has the table.
/// </summary>
internal static class AdmValueConverter
{
    private static readonly HashSet<string> SidAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.ObjectSid, Attr.SidHistory, Attr.TokenGroups, "tokenGroupsGlobalAndUniversal", "tokenGroupsNoGCAcceptable",
        "securityIdentifier", "mS-DS-CreatorSID",
    };

    private static readonly HashSet<string> GeneralizedTimeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.WhenCreated, Attr.WhenChanged, "dSCorePropagationData", "msDS-LastSuccessfulInteractiveLogonTime",
    };

    /// <summary>
    /// The value of the attribute: a scalar for a single-valued attribute, an array for a multi-valued one (also with one
    /// value), <c>$null</c> when a single-valued attribute has no value (an empty array when a multi-valued one has none).
    /// A secret that is never shown (<see cref="SecretAttributes.IsNeverShown"/>) is the mask, as in the Attribute Editor.
    /// </summary>
    public static object? Convert(string attribute, IReadOnlyList<string>? ranged, AttributeValues? values, ISchemaCatalog schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(schema);
        var name = BaseName(attribute);
        var definition = schema.FindAttribute(name);
        var multi = definition is null ? (values?.Count ?? ranged?.Count ?? 0) > 1 : !definition.IsSingleValued;
        if (SecretAttributes.IsNeverShown(name, schema))
        {
            return values is { Count: > 0 } ? SecretAttributes.Mask : multi ? Array.Empty<string>() : null;
        }

        IEnumerable<object> raw = ranged is not null ? ranged.Cast<object>() : values ?? Enumerable.Empty<object>();
        var converted = raw.Select(value => ConvertValue(name, definition, value)).ToList();
        if (!multi)
        {
            return converted.Count == 0 ? null : converted[0];
        }

        return ToArray(converted);
    }

    /// <summary>One value, without the array: what the formatter would show for it, typed.</summary>
    public static object? ConvertValue(string attribute, AttributeDefinition? definition, object value)
    {
        var name = BaseName(attribute);
        return value switch
        {
            byte[] bytes => ConvertBytes(name, definition, bytes),
            string text => ConvertText(name, definition, text),
            _ => value,
        };
    }

    private static object? ConvertBytes(string name, AttributeDefinition? definition, byte[] bytes)
    {
        if (bytes.Length == 16 && name.EndsWith("guid", StringComparison.OrdinalIgnoreCase))
        {
            return AdConvert.GuidFromBytes(bytes);
        }

        if (SidAttributes.Contains(name) || definition?.Syntax == AttributeSyntax.Sid)
        {
            try
            {
                return AdConvert.SidToString(bytes);
            }
            catch (FormatException)
            {
                return bytes;
            }
        }

        if (definition is { IsBinary: false } && TryDecodeUtf8(bytes, out var text))
        {
            return ConvertText(name, definition, text);
        }

        if (definition is null)
        {
            // Nothing says what these bytes are: text when they are text, otherwise base64 (a string a script can carry).
            return TryDecodeUtf8(bytes, out var unknownText) ? unknownText : System.Convert.ToBase64String(bytes);
        }

        return bytes;
    }

    private static object? ConvertText(string name, AttributeDefinition? definition, string text)
    {
        if (Attr.FileTimeAttributes.Contains(name))
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime))
            {
                return text;
            }

            if (fileTime is 0 or AdConvert.FileTimeNever)
            {
                return null;
            }

            return AdConvert.FromFileTime(fileTime) is { } fileTimeValue ? fileTimeValue.LocalDateTime : text;
        }

        if (string.Equals(name, Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Attr.UserAccountControlComputed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Attr.GroupType, StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) ? flags : text;
        }

        var syntax = definition?.Syntax ?? (GeneralizedTimeAttributes.Contains(name) ? AttributeSyntax.GeneralizedTime : AttributeSyntax.Unknown);
        switch (syntax)
        {
            case AttributeSyntax.GeneralizedTime:
                if (AdConvert.FromGeneralizedTime(text) is not { } when)
                {
                    return text;
                }

                return when.Year <= 1601 ? null : when.LocalDateTime;
            case AttributeSyntax.UtcTime:
                return DateTimeOffset.TryParseExact(text, "yyMMddHHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)
                    ? utc.LocalDateTime
                    : text;
            case AttributeSyntax.Boolean:
                return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? true
                    : text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? false
                    : text;
            case AttributeSyntax.Integer or AttributeSyntax.Enumeration:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : text;
            case AttributeSyntax.LargeInteger:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var large) ? large : text;
            default:
                return text;
        }
    }

    /// <summary>A typed array when every value has the same type, otherwise <c>object[]</c> (a date list with "never" entries).</summary>
    private static Array ToArray(List<object?> values)
    {
        var first = values.FirstOrDefault(v => v is not null)?.GetType();
        if (first is null || values.Any(v => v is null || v.GetType() != first))
        {
            return values.Count == 0 ? Array.Empty<string>() : values.ToArray();
        }

        var array = Array.CreateInstance(first, values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            array.SetValue(values[i], i);
        }

        return array;
    }

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string BaseName(string attribute)
    {
        var semicolon = attribute.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0 ? attribute : attribute[..semicolon];
    }
}
