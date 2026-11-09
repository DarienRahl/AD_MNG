using AdMng.Core.Model;

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
    /// forbidden or control character, not only dots and spaces, not ending with a dot (2.2).
    /// </summary>
    public static bool IsValidSamAccountName(string value, ObjectKind kind) =>
        throw new NotImplementedException("F3 B1: CreationNames.IsValidSamAccountName (2.2).");

    /// <summary>The UPN prefix (before <c>@</c>) contains no character AD rejects (the rules of <c>AccountNames.IsValidUpnPrefix</c>).</summary>
    public static bool IsValidUpnPrefix(string value) =>
        throw new NotImplementedException("F3 B1: CreationNames.IsValidUpnPrefix (2.2).");

    /// <summary>
    /// A valid computer name: 1–15 characters, letters, digits and <c>-</c>, not only digits, not starting or ending with
    /// <c>-</c>, no <c>.</c> (an underscore is the warning <c>ComputerNameNotDnsSafe</c>, not an error).
    /// </summary>
    public static bool IsValidComputerName(string value) =>
        throw new NotImplementedException("F3 B1: CreationNames.IsValidComputerName (2.2).");

    /// <summary>
    /// The character filter of the template function <c>sam</c> without its 20-character cut: diacritics transliterated,
    /// white space, non-ASCII and <c>" / \ [ ] : ; | = , + * ? &lt; &gt; @</c> removed, trailing dots trimmed; never cuts.
    /// </summary>
    public static string SanitizeIdentifier(string value, ObjectKind kind, string attribute) =>
        throw new NotImplementedException("F3 B1: CreationNames.SanitizeIdentifier (2.2).");

    /// <summary>
    /// <paramref name="value"/> with the disambiguator <paramref name="number"/>: identifiers get the digits (before the first
    /// <c>@</c>), <c>cn</c> / <c>displayName</c> of users, contacts and groups get <c>" (2)"</c> – the base cut so the result
    /// fits <paramref name="maxLength"/>; computers never (2.4).
    /// </summary>
    public static string AppendDisambiguator(string attribute, string value, int number, int maxLength) =>
        throw new NotImplementedException("F3 B1: CreationNames.AppendDisambiguator (2.2).");
}
