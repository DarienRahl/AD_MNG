using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch.Creation;

/// <summary>A value after <see cref="CreationValues.Finish"/>.</summary>
/// <param name="Value">The finished value.</param>
/// <param name="Sanitized">Characters were changed or removed (<em>(zmieniono znaki)</em>, 2.6).</param>
/// <param name="Truncated">The value was cut to the attribute's limit (<em>(skrócono)</em>).</param>
public sealed record FinishedValue(string Value, bool Sanitized, bool Truncated)
{
    /// <summary>The flags and the length only – never the value.</summary>
    public override string ToString() => $"Finished value ({Value.Length} characters, sanitized={Sanitized}, truncated={Truncated})";
}

/// <summary>The profile options <see cref="CreationValues.Finish"/> applies (2.2).</summary>
/// <param name="SanitizeIdentifiers">Identifier rule values go through <see cref="CreationNames.SanitizeIdentifier"/>.</param>
/// <param name="TruncateNames">sAMAccountName and cn rule values are cut to their limits (never a counter value).</param>
/// <param name="Phones">The phone format of phone attributes.</param>
/// <param name="IsCounterValue">The value contains the counter: it is never truncated (too long → <c>CounterTooLong</c>).</param>
public sealed record CreationFinishOptions(bool SanitizeIdentifiers, bool TruncateNames, PhoneFormat Phones, bool IsCounterValue)
{
    public override string ToString() =>
        $"Finish options (sanitize={SanitizeIdentifiers}, truncate={TruncateNames}, phones={Phones}, counter={IsCounterValue})";
}

/// <summary>
/// Value rules of created objects (docs/dev/phase3-bulk-create.md 2.2, owner B1): finishing of rule values, validation
/// per kind and attribute, the attributes that are never a rule target nor a mapped attribute.
/// </summary>
/// <remarks>
/// The contract commit has pass-through bodies so that the planner (B2) is not blocked: <see cref="Finish"/> returns the
/// value unchanged, <see cref="Validate"/> finds nothing, <see cref="IsBlockedAttribute"/> knows only the attribute names
/// of 2.2 (B1 adds the schema-driven rules – DN syntax, read-only, binary, secret, not allowed for the class);
/// <see cref="IsPhoneAttribute"/> and <see cref="IsMultiLine"/> are the lists of 2.2.
/// </remarks>
public static class CreationValues
{
    private static readonly HashSet<string> PhoneAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "telephoneNumber", "mobile", "homePhone", "ipPhone", "pager", "facsimileTelephoneNumber", "otherTelephone", "otherMobile",
        "otherHomePhone",
    };

    private static readonly HashSet<string> MultiLineAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "streetAddress", "postalAddress", "info",
    };

    /// <summary>The attributes 2.2 names as blocked (the schema-driven rules come on top, B1).</summary>
    private static readonly HashSet<string> NamedBlockedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "objectClass", "objectCategory", "name", "distinguishedName", "unicodePwd", "userPassword", "pwdLastSet",
        "userAccountControl", "accountExpires", "groupType", "lockoutTime", "msDS-SupportedEncryptionTypes", "sAMAccountType",
        "primaryGroupID", "objectSid", "sIDHistory", "objectGUID", "member", "memberOf", "nTSecurityDescriptor", "instanceType",
        "adminCount", "isCriticalSystemObject", "servicePrincipalName", "msDS-AllowedToDelegateTo",
        "msDS-AllowedToActOnBehalfOfOtherIdentity", "altSecurityIdentities", "msDS-KeyCredentialLink", "proxyAddresses", "c", "co",
        "countryCode", "mailNickname", "targetAddress", "legacyExchangeDN", "homeMDB", "manager", "managedBy",
    };

    /// <summary>
    /// Finishes a value that comes from a rule (character-wise sanitizing, then the length rules); values from a column or
    /// an edited cell are only normalized and never pass through here (contract: pass-through).
    /// </summary>
    public static FinishedValue Finish(ObjectKind kind, string attribute, string raw, CreationFinishOptions options)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(options);
        return new FinishedValue(raw, Sanitized: false, Truncated: false);
    }

    /// <summary>
    /// The findings of the values of one attribute of a created object (codes of <see cref="IssueCodes"/> and
    /// <see cref="BulkCreateIssueCodes"/>, the attribute set on each; contract: none).
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(ObjectKind kind, string attribute, IReadOnlyList<string> values, ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(schema);
        return [];
    }

    /// <summary>
    /// Never a rule target nor a mapped attribute (2.2): naming, class, password, account-control and security attributes,
    /// <c>proxyAddresses</c> (only through <c>AddPrimarySmtp</c>), <c>c</c> / <c>co</c> / <c>countryCode</c> (only through the
    /// <c>Country</c> target), the Exchange and Skype recipient attributes, DN-syntax attributes (<c>manager</c> /
    /// <c>managedBy</c> only through the <c>Manager</c> target), read-only, binary and secret attributes and attributes the
    /// schema does not allow for the class. <c>cn</c> is allowed (it becomes the RDN).
    /// </summary>
    public static bool IsBlockedAttribute(string attribute, ObjectKind kind, ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(schema);
        return IsNamedBlockedAttribute(attribute);
    }

    /// <summary>
    /// The schema-independent part of <see cref="IsBlockedAttribute"/>: the attribute names of 2.2 and the <c>msExch*</c> /
    /// <c>msRTCSIP-*</c> prefixes (complete in the contract). The settings normalization of the naming profiles uses it,
    /// where no schema is at hand.
    /// </summary>
    public static bool IsNamedBlockedAttribute(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return NamedBlockedAttributes.Contains(attribute)
            || attribute.StartsWith("msExch", StringComparison.OrdinalIgnoreCase)
            || attribute.StartsWith("msRTCSIP-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The phone attributes (formatted by <see cref="PhoneFormat.International"/>).</summary>
    public static bool IsPhoneAttribute(string attribute) => PhoneAttributes.Contains(attribute ?? throw new ArgumentNullException(nameof(attribute)));

    /// <summary><c>streetAddress</c>, <c>postalAddress</c> and <c>info</c>; every other attribute is single-line.</summary>
    public static bool IsMultiLine(string attribute) => MultiLineAttributes.Contains(attribute ?? throw new ArgumentNullException(nameof(attribute)));
}
