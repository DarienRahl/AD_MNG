using System.Globalization;
using System.Text;
using AdMng.Core.Model;
using AdMng.Core.Templates;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// Identifier names and limits of created objects (docs/dev/phase3-bulk-create.md 2.2). The limits, the class chains and
/// the forbidden sAMAccountName characters are copies of <c>AttributeOption.ClassesOf</c> and <c>AccountNames</c> of
/// AdMng.Presentation (Core cannot reference it), pinned equal by the Presentation test <c>CreationNamesParityTests</c>;
/// the validation, sanitizing and disambiguation members are B1's.
/// </summary>
public static class CreationNames
{
    /// <summary>sAMAccountName of users (the SAM limit).</summary>
    public const int MaxUserSamAccountName = 20;

    /// <summary>The NetBIOS name of a computer; its sAMAccountName <c>NAME$</c> is at most 16 characters.</summary>
    public const int MaxComputerName = 15;

    /// <summary>sAMAccountName of groups (the schema's rangeUpper; Windows allows group names this long).</summary>
    public const int MaxGroupSamAccountName = 256;

    /// <summary><c>cn</c> (the RDN value) of every kind.</summary>
    public const int MaxCn = 64;

    /// <summary><c>userPrincipalName</c> (the schema's rangeUpper).</summary>
    public const int MaxUpn = 1024;

    /// <summary>The local part of a <c>mail</c> address (RFC 5321).</summary>
    public const int MaxMailLocalPart = 64;

    /// <summary>
    /// The longest sAMAccountName of <paramref name="kind"/>: 20 for users, 16 for computers (<c>NAME$</c>), 256 for groups,
    /// 0 for kinds without a sAMAccountName (contacts).
    /// </summary>
    public static int MaxSamAccountName(ObjectKind kind) => kind switch
    {
        ObjectKind.User or ObjectKind.InetOrgPerson => MaxUserSamAccountName,
        ObjectKind.Computer => MaxComputerName + 1,
        ObjectKind.Group => MaxGroupSamAccountName,
        _ => 0,
    };

    /// <summary>Characters AD rejects in sAMAccountName: <c>" / \ [ ] : ; | = , + * ? &lt; &gt; @</c> and control characters.</summary>
    public static bool IsInvalidSamCharacter(char c) => c is '"' or '/' or '\\' or '[' or ']' or ':' or ';' or '|'
        or '=' or ',' or '+' or '*' or '?' or '<' or '>' or '@' || char.IsControl(c);

    private const string SamAccountNameAttribute = "sAMAccountName";
    private const string UpnAttribute = "userPrincipalName";
    private const string MailAttribute = "mail";
    private const char BackslashChar = '\\';

    /// <summary>The objectClass chain of an object kind (structural classes; auxiliaries are added by the schema).</summary>
    public static IReadOnlyList<string> ClassChainOf(ObjectKind kind) => kind switch
    {
        ObjectKind.User => ["top", "person", "organizationalPerson", "user"],
        ObjectKind.InetOrgPerson => ["top", "person", "organizationalPerson", "user", "inetOrgPerson"],
        ObjectKind.Computer => ["top", "person", "organizationalPerson", "user", "computer"],
        ObjectKind.Group => ["top", "group"],
        ObjectKind.Contact => ["top", "person", "organizationalPerson", "contact"],
        ObjectKind.OrganizationalUnit => ["top", "organizationalUnit"],
        ObjectKind.Container => ["top", "container"],
        _ => ["top"],
    };

    /// <summary>The most specific structural class of <paramref name="kind"/> (<c>user</c>, <c>contact</c>, <c>group</c>, <c>computer</c> …).</summary>
    public static string StructuralClassOf(ObjectKind kind) => ClassChainOf(kind)[^1];

    /// <summary>
    /// A syntactically valid sAMAccountName of <paramref name="kind"/>: non-empty, within <see cref="MaxSamAccountName"/>, no
    /// forbidden or control character, not only dots and spaces, not ending with a dot (2.2). The <c>$</c> rules of users and
    /// computers are <see cref="BulkCreateIssueCodes.TrailingDollar"/> of <c>CreationValues.Validate</c>; contacts have no
    /// sAMAccountName (always false).
    /// </summary>
    public static bool IsValidSamAccountName(string value, ObjectKind kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        var max = MaxSamAccountName(kind);
        return max > 0
            && !string.IsNullOrWhiteSpace(value)
            && value.Length <= max
            && !value.Any(IsInvalidSamCharacter)
            && !value.All(c => c is '.' or ' ')
            && !value.EndsWith('.');
    }

    /// <summary>The UPN prefix (before <c>@</c>) contains no character AD rejects (the rules of <c>AccountNames.IsValidUpnPrefix</c>).</summary>
    public static bool IsValidUpnPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '@' or '"' or '(' or ')' or ',' or ':' or ';' or '<' or '>' or '[' or BackslashChar or ']');

    /// <summary>
    /// A valid computer name: 1–15 characters, letters, digits and <c>-</c>, not only digits, not starting or ending with
    /// <c>-</c>, no <c>.</c> (an underscore is the warning <c>ComputerNameNotDnsSafe</c>, not an error). Letters are ASCII
    /// (a NetBIOS and DNS name).
    /// </summary>
    public static bool IsValidComputerName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length is >= 1 and <= MaxComputerName
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            && !value.All(char.IsAsciiDigit)
            && value[0] != '-'
            && value[^1] != '-';
    }

    /// <summary>
    /// The character filter of the template function <c>sam</c> without its 20-character cut: diacritics transliterated,
    /// white space, non-ASCII and <c>" / \ [ ] : ; | = , + * ? &lt; &gt; @</c> removed, trailing dots trimmed; never cuts.
    /// </summary>
    /// <remarks>
    /// The <c>sAMAccountName</c> of a group keeps spaces and letters with diacritics (valid in a group name): only the
    /// forbidden and control characters are removed and the ends trimmed. An address part (<c>userPrincipalName</c>,
    /// <c>mail</c>) also loses a leading dot and runs of dots (<c>.kowalski</c>, <c>jan..kowalski</c> are no valid local
    /// parts). The value is one part: <c>@</c> is removed like any forbidden character (<c>CreationValues.Finish</c> splits
    /// an address at its last <c>@</c> first).
    /// </remarks>
    public static string SanitizeIdentifier(string value, ObjectKind kind, string attribute)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(attribute);
        var builder = new StringBuilder(value.Length);
        if (kind == ObjectKind.Group && string.Equals(attribute, SamAccountNameAttribute, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in value)
            {
                // A removed character leaves no double space ("Kadry / Płace" → "Kadry Płace").
                if (!IsInvalidSamCharacter(c) && !(c == ' ' && (builder.Length == 0 || builder[^1] == ' ')))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Trim().TrimEnd('.').TrimEnd();
        }

        var address = string.Equals(attribute, UpnAttribute, StringComparison.OrdinalIgnoreCase)
            || string.Equals(attribute, MailAttribute, StringComparison.OrdinalIgnoreCase);
        foreach (var c in Diacritics.Remove(value))
        {
            if (c > '~' || char.IsWhiteSpace(c) || char.IsControl(c) || IsInvalidSamCharacter(c))
            {
                continue;
            }

            if (address && c == '.' && (builder.Length == 0 || builder[^1] == '.'))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().TrimEnd('.');
    }

    /// <summary>
    /// <paramref name="value"/> with the disambiguator <paramref name="number"/>: identifiers get the digits (before the first
    /// <c>@</c>), <c>cn</c> / <c>displayName</c> of users, contacts and groups get <c>" (2)"</c> – the base cut so the result
    /// fits <paramref name="maxLength"/>; computers never (2.4).
    /// </summary>
    /// <remarks>
    /// Identifiers are <c>sAMAccountName</c>, <c>userPrincipalName</c> and <c>mail</c>: <c>jan.kowalski</c> →
    /// <c>jan.kowalski2</c>, <c>jan.kowalski@contoso.com</c> → <c>jan.kowalski2@contoso.com</c>; <paramref name="maxLength"/>
    /// limits the part before the first <c>@</c> (the whole value without one) – 20 for a user's login, 64 for the local part
    /// of <c>mail</c> – and the base is cut before the digits, trailing dots trimmed again. Every other attribute gets
    /// <c>" (n)"</c> (<em>Jan Kowalski (2)</em>), the base cut to fit <paramref name="maxLength"/> and trimmed. The caller never
    /// passes the names of a computer (they collide as errors, 2.4).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is negative or the disambiguator alone exceeds <paramref name="maxLength"/>.</exception>
    public static string AppendDisambiguator(string attribute, string value, int number, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        var digits = number.ToString(CultureInfo.InvariantCulture);
        if (IsIdentifier(attribute))
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, digits.Length);
            var at = value.IndexOf('@', StringComparison.Ordinal);
            var local = at < 0 ? value : value[..at];
            var rest = at < 0 ? string.Empty : value[at..];
            return Cut(local, maxLength - digits.Length).TrimEnd('.') + digits + rest;
        }

        var suffix = " (" + digits + ")";
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, suffix.Length);
        return Cut(value, maxLength - suffix.Length).TrimEnd() + suffix;
    }

    /// <summary>The identifiers that get digits from <see cref="AppendDisambiguator"/>: <c>sAMAccountName</c>, <c>userPrincipalName</c>, <c>mail</c>.</summary>
    internal static bool IsIdentifier(string attribute) =>
        string.Equals(attribute, SamAccountNameAttribute, StringComparison.OrdinalIgnoreCase)
        || string.Equals(attribute, UpnAttribute, StringComparison.OrdinalIgnoreCase)
        || string.Equals(attribute, MailAttribute, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first <paramref name="length"/> characters of <paramref name="value"/>, never splitting a surrogate pair.</summary>
    internal static string Cut(string value, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= length)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];
    }
}
