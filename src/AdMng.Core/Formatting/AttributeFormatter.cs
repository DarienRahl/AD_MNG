using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Formatting;

/// <summary>
/// Default <see cref="IAttributeFormatter"/> driven by the session schema.
/// </summary>
/// <remarks>
/// Dates are shown in <see cref="TimeZoneInfo.Local"/> using the culture's general ("G") pattern. FILETIME values 0
/// and <see cref="long.MaxValue"/> and GeneralizedTime values in year 1601 are shown as <see cref="NeverText"/>
/// (English; the UI maps it to a localized word). Attributes missing from the schema are formatted as plain text and
/// parsed without validation (the change planner reports them as unknown).
/// </remarks>
public sealed class AttributeFormatter : IAttributeFormatter
{
    /// <summary>Text used for the FILETIME / GeneralizedTime "never / not set" sentinels. The UI localizes it.</summary>
    public const string NeverText = "never";

    /// <summary>Separator between multiple values in <see cref="Format"/>.</summary>
    public const string ValueSeparator = "; ";

    /// <summary>Binary values longer than this are truncated in the display (followed by "...").</summary>
    public const int MaxDisplayedBytes = 256;

    private static readonly HashSet<string> SidAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.ObjectSid, Attr.SidHistory, Attr.TokenGroups, "tokenGroupsGlobalAndUniversal", "tokenGroupsNoGCAcceptable",
        "securityIdentifier", "mS-DS-CreatorSID",
    };

    private static readonly HashSet<string> UserAccountControlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UserAccountControl, Attr.UserAccountControlComputed,
    };

    /// <summary>Well-known GeneralizedTime attributes, used when the schema does not describe them.</summary>
    private static readonly HashSet<string> GeneralizedTimeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.WhenCreated, Attr.WhenChanged, "dSCorePropagationData", "msDS-LastSuccessfulInteractiveLogonTime",
    };

    private static readonly (int Bit, string Name)[] UserAccountControlNames =
    [
        (0x0001, "SCRIPT"), (0x0002, "ACCOUNTDISABLE"), (0x0008, "HOMEDIR_REQUIRED"), (0x0010, "LOCKOUT"),
        (0x0020, "PASSWD_NOTREQD"), (0x0040, "PASSWD_CANT_CHANGE"), (0x0080, "ENCRYPTED_TEXT_PWD_ALLOWED"),
        (0x0100, "TEMP_DUPLICATE_ACCOUNT"), (0x0200, "NORMAL_ACCOUNT"), (0x0800, "INTERDOMAIN_TRUST_ACCOUNT"),
        (0x1000, "WORKSTATION_TRUST_ACCOUNT"), (0x2000, "SERVER_TRUST_ACCOUNT"), (0x10000, "DONT_EXPIRE_PASSWORD"),
        (0x20000, "MNS_LOGON_ACCOUNT"), (0x40000, "SMARTCARD_REQUIRED"), (0x80000, "TRUSTED_FOR_DELEGATION"),
        (0x100000, "NOT_DELEGATED"), (0x200000, "USE_DES_KEY_ONLY"), (0x400000, "DONT_REQ_PREAUTH"),
        (0x800000, "PASSWORD_EXPIRED"), (0x1000000, "TRUSTED_TO_AUTH_FOR_DELEGATION"), (0x4000000, "PARTIAL_SECRETS_ACCOUNT"),
    ];

    private static readonly (int Bit, string Name)[] GroupTypeNames =
    [
        (0x01, "BUILTIN_LOCAL_GROUP"), (0x02, "ACCOUNT_GROUP"), (0x04, "RESOURCE_GROUP"), (0x08, "UNIVERSAL_GROUP"),
        (0x10, "APP_BASIC_GROUP"), (0x20, "APP_QUERY_GROUP"), (unchecked((int)0x80000000), "SECURITY_ENABLED"),
    ];

    private static readonly string[] IsoDateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss",
    ];

    private readonly ISchemaCatalog _schema;

    public AttributeFormatter(ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
    }

    /// <summary>ADSI flag names (e.g. NORMAL_ACCOUNT) of a userAccountControl value; unknown bits as hex.</summary>
    public static IReadOnlyList<string> GetUserAccountControlFlagNames(int value) => DecodeFlags(value, UserAccountControlNames);

    /// <summary>ADSI flag names (e.g. ACCOUNT_GROUP, SECURITY_ENABLED) of a groupType value; unknown bits as hex.</summary>
    public static IReadOnlyList<string> GetGroupTypeFlagNames(int value) => DecodeFlags(value, GroupTypeNames);

    /// <inheritdoc />
    public string Format(string attribute, AttributeValues? values, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(culture);
        if (values is null || values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(ValueSeparator, values.Select(v => FormatValue(attribute, v, culture)));
    }

    /// <inheritdoc />
    public string FormatValue(string attribute, object value, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(culture);
        var name = BaseName(attribute);
        var definition = _schema.FindAttribute(name);
        return value switch
        {
            byte[] bytes => FormatBinary(name, definition, bytes, culture),
            string text => FormatText(name, definition, text, culture),
            IFormattable formattable => formattable.ToString(null, culture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <inheritdoc />
    public bool TryParse(string attribute, string input, CultureInfo culture, out object? value, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(culture);
        value = null;
        error = null;
        if (input.Length == 0)
        {
            error = "A value is required; clear the attribute instead of entering an empty value.";
            return false;
        }

        var name = BaseName(attribute);
        if (Attr.FileTimeAttributes.Contains(name))
        {
            return TryParseFileTime(name, input, culture, out value, out error);
        }

        var definition = _schema.FindAttribute(name);
        if (definition is null)
        {
            value = input;
            return true;
        }

        var trimmed = input.Trim();
        switch (definition.Syntax)
        {
            case AttributeSyntax.Boolean:
                return TryParseBoolean(trimmed, out value, out error);
            case AttributeSyntax.Integer or AttributeSyntax.Enumeration:
                return TryParseInteger(trimmed, definition, int.MinValue, int.MaxValue, out value, out error);
            case AttributeSyntax.LargeInteger:
                return TryParseInteger(trimmed, definition, long.MinValue, long.MaxValue, out value, out error);
            case AttributeSyntax.DistinguishedName:
                return TryParseDn(trimmed, out value, out error);
            case AttributeSyntax.DnWithBinary or AttributeSyntax.DnWithString:
                return TryParseDnWith(trimmed, definition.Syntax, out value, out error);
            case AttributeSyntax.GeneralizedTime:
                return TryParseGeneralizedTime(trimmed, culture, utc: false, out value, out error);
            case AttributeSyntax.UtcTime:
                return TryParseGeneralizedTime(trimmed, culture, utc: true, out value, out error);
            case AttributeSyntax.Sid:
                return TryParseSid(trimmed, out value, out error);
            case AttributeSyntax.OctetString or AttributeSyntax.ReplicaLink or AttributeSyntax.SecurityDescriptor:
                return TryParseBinary(name, trimmed, definition, out value, out error);
            default:
                return TryParseString(input, definition, out value, out error);
        }
    }

    private static string FormatBinary(string name, AttributeDefinition? definition, byte[] bytes, CultureInfo culture)
    {
        if (bytes.Length == 16 && name.EndsWith("guid", StringComparison.OrdinalIgnoreCase))
        {
            return AdConvert.GuidFromBytes(bytes).ToString("D");
        }

        if (SidAttributes.Contains(name) || definition?.Syntax == AttributeSyntax.Sid)
        {
            try
            {
                return AdConvert.SidToString(bytes);
            }
            catch (FormatException)
            {
                return ToHex(bytes);
            }
        }

        if (definition is { IsBinary: false } && TryDecodeUtf8(bytes, out var text))
        {
            return FormatText(name, definition, text, culture);
        }

        return ToHex(bytes);
    }

    private static string FormatText(string name, AttributeDefinition? definition, string text, CultureInfo culture)
    {
        if (Attr.FileTimeAttributes.Contains(name))
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime))
            {
                return text;
            }

            if (fileTime is 0 or AdConvert.FileTimeNever)
            {
                return NeverText;
            }

            return AdConvert.FromFileTime(fileTime) is { } when ? ToLocalText(when, culture) : text;
        }

        if (UserAccountControlAttributes.Contains(name))
        {
            return FormatFlags(text, UserAccountControlNames);
        }

        if (string.Equals(name, Attr.GroupType, StringComparison.OrdinalIgnoreCase))
        {
            return FormatFlags(text, GroupTypeNames);
        }

        var syntax = definition?.Syntax ?? (GeneralizedTimeAttributes.Contains(name) ? AttributeSyntax.GeneralizedTime : AttributeSyntax.Unknown);
        switch (syntax)
        {
            case AttributeSyntax.GeneralizedTime:
                return FormatGeneralizedTime(text, culture);
            case AttributeSyntax.UtcTime:
                return DateTimeOffset.TryParseExact(text, "yyMMddHHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var utcTime)
                    ? ToLocalText(utcTime, culture)
                    : text;
            case AttributeSyntax.Boolean:
                return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "TRUE"
                    : text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? "FALSE"
                    : text;
            default:
                return text;
        }
    }

    private static string FormatGeneralizedTime(string text, CultureInfo culture)
    {
        if (AdConvert.FromGeneralizedTime(text) is not { } when)
        {
            return text;
        }

        return when.Year <= 1601 ? NeverText : ToLocalText(when, culture);
    }

    private static string ToLocalText(DateTimeOffset when, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(when, TimeZoneInfo.Local).ToString("G", culture);

    private static string FormatFlags(string text, (int Bit, string Name)[] table)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return text;
        }

        var names = DecodeFlags(value, table);
        return names.Count == 0 ? text : $"{text} ({string.Join(", ", names)})";
    }

    private static List<string> DecodeFlags(int value, (int Bit, string Name)[] table)
    {
        var names = new List<string>();
        var remaining = value;
        foreach (var (bit, name) in table)
        {
            if ((value & bit) == bit)
            {
                names.Add(name);
                remaining &= ~bit;
            }
        }

        if (remaining != 0)
        {
            names.Add("0x" + remaining.ToString("X", CultureInfo.InvariantCulture));
        }

        return names;
    }

    private static string ToHex(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, MaxDisplayedBytes);
        var sb = new StringBuilder(shown * 3 + 4);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > MaxDisplayedBytes)
        {
            sb.Append(" ...");
        }

        return sb.ToString();
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

    private static bool TryParseFileTime(string name, string input, CultureInfo culture, out object? value, out string? error)
    {
        value = null;
        error = null;
        var trimmed = input.Trim();
        if (trimmed.Equals(NeverText, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Equals(name, Attr.AccountExpires, StringComparison.OrdinalIgnoreCase)
                ? AdConvert.FileTimeNever.ToString(CultureInfo.InvariantCulture)
                : "0";
            return true;
        }

        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var raw))
        {
            value = raw.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (TryParseDate(trimmed, culture, out var when))
        {
            value = AdConvert.ToFileTime(when).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        error = $"'{input}' is not a date, a FILETIME number or '{NeverText}'.";
        return false;
    }

    private static bool TryParseBoolean(string input, out object? value, out string? error)
    {
        error = null;
        value = input.ToUpperInvariant() switch
        {
            "TRUE" or "1" => "TRUE",
            "FALSE" or "0" => "FALSE",
            _ => null,
        };
        if (value is null)
        {
            error = $"'{input}' is not a boolean; use TRUE or FALSE.";
            return false;
        }

        return true;
    }

    private static bool TryParseInteger(string input, AttributeDefinition definition, long min, long max, out object? value, out string? error)
    {
        value = null;
        error = null;
        long number;
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(input.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            {
                error = $"'{input}' is not a valid hexadecimal number.";
                return false;
            }

            // Hex input is a bit pattern: 0x80000002 for a 32-bit attribute means -2147483646.
            if (max == int.MaxValue && hex <= uint.MaxValue)
            {
                number = unchecked((int)(uint)hex);
            }
            else if (max == long.MaxValue)
            {
                number = unchecked((long)hex);
            }
            else
            {
                error = $"'{input}' is out of range.";
                return false;
            }
        }
        else if (!long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
        {
            error = $"'{input}' is not a valid integer.";
            return false;
        }

        if (number < min || number > max)
        {
            error = $"{number.ToString(CultureInfo.InvariantCulture)} is out of range ({min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}).";
            return false;
        }

        if ((definition.RangeLower is { } lower && number < lower) || (definition.RangeUpper is { } upper && number > upper))
        {
            error = $"{number.ToString(CultureInfo.InvariantCulture)} is out of the allowed range ({Describe(definition.RangeLower)} to {Describe(definition.RangeUpper)}).";
            return false;
        }

        value = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string Describe(long? bound) => bound?.ToString(CultureInfo.InvariantCulture) ?? "unbounded";

    private static bool TryParseDn(string input, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (!DistinguishedName.TryParse(input, out var dn) || dn.IsRoot)
        {
            error = $"'{input}' is not a valid distinguished name.";
            return false;
        }

        value = input;
        return true;
    }

    /// <summary>DN-Binary <c>B:&lt;hex length&gt;:&lt;hex&gt;:&lt;dn&gt;</c> / DN-String <c>S:&lt;length&gt;:&lt;text&gt;:&lt;dn&gt;</c>.</summary>
    private static bool TryParseDnWith(string input, AttributeSyntax syntax, out object? value, out string? error)
    {
        value = null;
        error = null;
        var prefix = syntax == AttributeSyntax.DnWithBinary ? "B:" : "S:";
        var parts = input.Split(':', 3);
        var valid = input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && parts.Length == 3
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            && parts[2].Length > length && parts[2][length] == ':'
            && (syntax != AttributeSyntax.DnWithBinary || parts[2][..length].All(char.IsAsciiHexDigit))
            && DistinguishedName.TryParse(parts[2][(length + 1)..], out var dn) && !dn.IsRoot;
        if (!valid)
        {
            error = $"'{input}' is not a valid value; expected '{prefix}<length>:<value>:<distinguished name>'.";
            return false;
        }

        value = input;
        return true;
    }

    private static bool TryParseGeneralizedTime(string input, CultureInfo culture, bool utc, out object? value, out string? error)
    {
        value = null;
        error = null;
        DateTimeOffset when;
        if (input.Length >= 14 && input.Take(14).All(char.IsAsciiDigit) && AdConvert.FromGeneralizedTime(input) is { } parsed)
        {
            when = parsed;
        }
        else if (!TryParseDate(input, culture, out when))
        {
            error = $"'{input}' is not a valid date and time.";
            return false;
        }

        value = utc
            ? when.ToUniversalTime().ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + "Z"
            : AdConvert.ToGeneralizedTime(when);
        return true;
    }

    /// <summary>Parses a date typed in the culture's format (or ISO 8601) as local time unless it has an offset.</summary>
    private static bool TryParseDate(string input, CultureInfo culture, out DateTimeOffset value)
    {
        value = default;
        DateTime local;
        if (!DateTime.TryParse(input, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out local)
            && !DateTime.TryParseExact(input, IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out local))
        {
            return false;
        }

        if (local.Kind == DateTimeKind.Utc)
        {
            value = new DateTimeOffset(local, TimeSpan.Zero);
            return true;
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        value = new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
        return true;
    }

    private static bool TryParseSid(string input, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (input.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                value = AdConvert.SidToBytes(input);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                error = $"'{input}' is not a valid SID.";
                return false;
            }
        }

        if (TryParseHex(input, out var bytes) && bytes.Length >= 8)
        {
            try
            {
                _ = AdConvert.SidToString(bytes);
                value = bytes;
                return true;
            }
            catch (FormatException)
            {
                // fall through to the error below
            }
        }

        error = $"'{input}' is not a valid SID (expected S-1-5-21-... or hexadecimal bytes).";
        return false;
    }

    private static bool TryParseBinary(string name, string input, AttributeDefinition definition, out object? value, out string? error)
    {
        value = null;
        error = null;
        byte[] bytes;
        if (name.EndsWith("guid", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(input, out var guid))
        {
            bytes = guid.ToByteArray();
        }
        else if (!TryParseHex(input, out bytes))
        {
            error = $"'{input}' is not valid hexadecimal data (e.g. '01 A2 FF').";
            return false;
        }

        if (!CheckLength(bytes.Length, definition, "bytes", out error))
        {
            return false;
        }

        value = bytes;
        return true;
    }

    /// <summary>Accepts "01 A2 FF", "01A2FF", "0x01 0xA2", "01-A2-FF" and "01:A2:FF".</summary>
    private static bool TryParseHex(string input, out byte[] bytes)
    {
        bytes = [];
        var digits = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (c == '0' && i + 1 < input.Length && input[i + 1] is 'x' or 'X')
            {
                i += 2;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '-' or ':' or ',')
            {
                i++;
                continue;
            }

            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }

            digits.Append(c);
            i++;
        }

        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            return false;
        }

        bytes = Convert.FromHexString(digits.ToString());
        return true;
    }

    private static bool TryParseString(string input, AttributeDefinition definition, out object? value, out string? error)
    {
        value = null;
        error = null;
        switch (definition.Syntax)
        {
            case AttributeSyntax.NumericString when !input.All(c => char.IsAsciiDigit(c) || c == ' '):
                error = "Only digits and spaces are allowed.";
                return false;
            case AttributeSyntax.PrintableString when !input.All(IsPrintableStringCharacter):
                error = "Only letters A-Z, digits, spaces and ' ( ) + , - . / : = ? are allowed.";
                return false;
            case AttributeSyntax.IA5String when !Ascii.IsValid(input):
                error = "Only ASCII characters are allowed.";
                return false;
            case AttributeSyntax.ObjectIdentifier when input.Any(char.IsWhiteSpace):
                error = "An object identifier cannot contain spaces.";
                return false;
        }

        if (!CheckLength(input.Length, definition, "characters", out error))
        {
            return false;
        }

        value = input;
        return true;
    }

    private static bool IsPrintableStringCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is ' ' or '\'' or '(' or ')' or '+' or ',' or '-' or '.' or '/' or ':' or '=' or '?';

    private static bool CheckLength(int length, AttributeDefinition definition, string unit, out string? error)
    {
        error = null;
        if (definition.RangeUpper is { } upper && length > upper)
        {
            error = $"The value is too long ({length} {unit}, maximum {upper}).";
            return false;
        }

        if (definition.RangeLower is { } lower && length < lower)
        {
            error = $"The value is too short ({length} {unit}, minimum {lower}).";
            return false;
        }

        return true;
    }

    private static string BaseName(string attribute)
    {
        var semicolon = attribute.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0 ? attribute : attribute[..semicolon];
    }
}
