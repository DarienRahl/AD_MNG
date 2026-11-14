using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Messaging;
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
/// <param name="IsCounterValue">The value contains the counter or is a part of such a value (2.4): it is never truncated (too long → <c>CounterTooLong</c>) and is finished character by character only (see <see cref="CreationValues.Finish"/>).</param>
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
/// Messages of the issues are English fallbacks without any value (the UI shows <c>Issue.&lt;code&gt;</c> with the
/// attribute); each code is reported once per attribute. Pure and thread-safe; <see cref="IsBlockedAttribute"/> caches the
/// allowed attributes per schema instance and kind.
/// </remarks>
public static class CreationValues
{
    private const string Cn = "cn";
    private const string SamAccountName = "sAMAccountName";
    private const string Upn = "userPrincipalName";
    private const string Mail = "mail";
    private const string HomeDrive = "homeDrive";
    private const string HomeDirectory = "homeDirectory";

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

    /// <summary>Attributes the schema-less check treats as multi-valued (every other attribute is single-valued there).</summary>
    private static readonly FrozenSet<string> KnownMultiValued = new[]
    {
        "otherTelephone", "otherMobile", "otherHomePhone", "otherFacsimileTelephoneNumber", "otherIpPhone", "otherPager",
        "postalAddress", "postOfficeBox", "url", "proxyAddresses",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The attributes accepted without a schema (2.2): the targets of the synonym table and the attributes of 2.2's table.</summary>
    private static readonly Lazy<FrozenSet<string>> WithoutSchema = new(() => ColumnSynonyms.Entries
        .Select(e => e.Target.Attribute)
        .OfType<string>()
        .Concat([Cn, SamAccountName, Upn, Mail, "displayName", "givenName", "sn", "description", HomeDrive, HomeDirectory, "location",
            "postalAddress"])
        .Concat(PhoneAttributes)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The allowed attributes of the class chain of a kind, per schema instance (released with the schema).</summary>
    private static readonly ConditionalWeakTable<ISchemaCatalog, ConcurrentDictionary<ObjectKind, IReadOnlySet<string>>> Allowed = new();

    /// <summary>
    /// Finishes a value that comes from a rule (character-wise sanitizing, then the length rules); values from a column or
    /// an edited cell are only normalized and never pass through here, except for the computer rules (below).
    /// </summary>
    /// <remarks>
    /// Every single-line value: runs of spaces collapsed, trimmed (a multi-line value: trimmed). <c>cn</c>: all white space
    /// collapsed; computers upper case; <see cref="CreationFinishOptions.TruncateNames"/> cuts to 64. <c>sAMAccountName</c>:
    /// <see cref="CreationFinishOptions.SanitizeIdentifiers"/> – <see cref="CreationNames.SanitizeIdentifier"/> (a group's:
    /// only the forbidden characters); computers upper case and <c>$</c> appended when missing – not for a
    /// <see cref="CreationFinishOptions.IsCounterValue"/> part (finish the assembled value again, or derive it from
    /// <c>cn</c> as the plan does); <see cref="CreationFinishOptions.TruncateNames"/> cuts to 20 / 15 + <c>$</c> / 256.
    /// <c>userPrincipalName</c> and <c>mail</c>: the part before the last <c>@</c> sanitized, the domain lower-cased, never cut
    /// (too long is <see cref="IssueCodes.TooLong"/> of <see cref="Validate(ObjectKind, string, IReadOnlyList{string}, ISchemaCatalog)"/>).
    /// Phone attributes: <see cref="PhoneFormat.International"/> when chosen. <c>homeDrive</c>: upper case. The computer rules
    /// (upper case, <c>$</c>) apply whatever the options say, so the planner may pass a column value of a computer with
    /// <c>SanitizeIdentifiers</c> and <c>TruncateNames</c> off.
    /// <para>
    /// <see cref="CreationFinishOptions.IsCounterValue"/>: the value is the prefix or the suffix of a counter value (2.4 finishes
    /// them separately) and is finished character by character only – never cut, its ends neither trimmed nor stripped of
    /// dots (a run of spaces at an end becomes one space: <c>Praktykant </c> + <c>01</c>, <c>kd.</c> + <c>201</c>), no phone
    /// format (the digits of a part are no number of their own) and no <c>$</c>.
    /// </para>
    /// </remarks>
    public static FinishedValue Finish(ObjectKind kind, string attribute, string raw, CreationFinishOptions options)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(options);

        // A counter value is finished in parts (2.4: prefix and suffix around the number, separately): only character by
        // character – an end of a part may be where the number goes ("Praktykant " + 01, "kd." + 201).
        var edges = !options.IsCounterValue;
        var value = IsMultiLine(attribute) ? (edges ? raw.Trim() : raw) : CollapseSpaces(raw, allWhiteSpace: false, edges);
        var sanitized = false;
        var truncated = false;
        var mayCut = options.TruncateNames && !options.IsCounterValue;
        if (Is(attribute, Cn))
        {
            value = CollapseSpaces(value, allWhiteSpace: true, edges);
            if (kind == ObjectKind.Computer)
            {
                value = value.ToUpperInvariant();
            }

            if (mayCut && value.Length > CreationNames.MaxCn)
            {
                value = CreationNames.Cut(value, CreationNames.MaxCn).TrimEnd();
                truncated = true;
            }
        }
        else if (Is(attribute, SamAccountName) && CreationNames.MaxSamAccountName(kind) > 0)
        {
            if (options.SanitizeIdentifiers)
            {
                var clean = CreationNames.Sanitize(value, kind, attribute, edges);
                sanitized = !string.Equals(clean, value, StringComparison.Ordinal);
                value = clean;
            }

            if (kind == ObjectKind.Computer && value.Length > 0)
            {
                value = value.ToUpperInvariant();
                var name = value.EndsWith('$') ? value[..^1] : value;
                if (mayCut && name.Length > CreationNames.MaxComputerName)
                {
                    name = CreationNames.Cut(name, CreationNames.MaxComputerName).TrimEnd('.');
                    truncated = true;
                }

                value = options.IsCounterValue && !value.EndsWith('$') ? name : name + "$";
            }
            else if (kind != ObjectKind.Computer)
            {
                var max = CreationNames.MaxSamAccountName(kind);
                if (mayCut && value.Length > max)
                {
                    value = CreationNames.Cut(value, max).TrimEnd('.');
                    truncated = true;
                }
            }
        }
        else if (Is(attribute, Upn) || Is(attribute, Mail))
        {
            var at = value.LastIndexOf('@');
            var local = at < 0 ? value : value[..at];
            var domain = at < 0 ? null : value[(at + 1)..].Trim().ToLowerInvariant();
            if (options.SanitizeIdentifiers)
            {
                var clean = CreationNames.Sanitize(local, kind, attribute, edges);
                sanitized = !string.Equals(clean, local, StringComparison.Ordinal);
                local = clean;
            }

            value = domain is null ? local : local + "@" + domain;
        }
        else if (IsPhoneAttribute(attribute))
        {
            // A part of a counter value is no phone number of its own: formatting it would regroup the digits before the number.
            value = edges ? CellText.NormalizePhone(value, options.Phones) : value;
        }
        else if (Is(attribute, HomeDrive))
        {
            value = value.ToUpperInvariant();
        }

        return new FinishedValue(value, sanitized, truncated);
    }

    /// <summary>
    /// The findings of the values of one attribute of a created object (codes of <see cref="IssueCodes"/> and
    /// <see cref="BulkCreateIssueCodes"/>, the attribute set on each), without the phone-format and column-case checks
    /// (<see cref="PhoneFormat.AsIs"/>, values not from a column).
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(ObjectKind kind, string attribute, IReadOnlyList<string> values, ISchemaCatalog schema) =>
        Validate(kind, attribute, values, schema, PhoneFormat.AsIs, columnCase: null);

    /// <summary>
    /// The findings of the values of one attribute of a created object (2.2), with the run's phone format
    /// (<see cref="BulkCreateIssueCodes.InvalidPhone"/> when <see cref="PhoneFormat.International"/> could not format a
    /// value) and, for values that come from a mapped column, that column's case choice
    /// (<see cref="BulkCreateIssueCodes.AllCapsName"/> for <c>givenName</c>, <c>sn</c> and <c>displayName</c> in capitals with
    /// <see cref="TextCase.AsIs"/>; null = not from a column).
    /// </summary>
    /// <remarks>
    /// Errors: <c>cn</c> empty, over 64 or (computers) not <see cref="CreationNames.IsValidComputerName"/>;
    /// <c>sAMAccountName</c> empty, too long, forbidden characters, only dots and spaces, a trailing dot,
    /// <see cref="BulkCreateIssueCodes.TrailingDollar"/> (users: a trailing <c>$</c>; computers: not exactly one <c>$</c> at the
    /// end); <c>userPrincipalName</c> not <c>prefix@suffix</c>, an invalid prefix or suffix, over 1024; <c>mail</c> not one
    /// address, its local part over 64 or the address over 256; several values of a single-valued attribute (<c>description</c>
    /// always); a line break or control character in a single-line attribute
    /// (<see cref="BulkCreateIssueCodes.LineBreakInValue"/>); every other attribute: the schema's syntax and range
    /// (<c>PlanValidation.ValidateValue</c>). Warnings: <see cref="BulkCreateIssueCodes.ComputerNameNotDnsSafe"/>,
    /// <see cref="BulkCreateIssueCodes.ScientificNotation"/> and <see cref="BulkCreateIssueCodes.InvalidPhone"/> of phones,
    /// <see cref="IssueCodes.InvalidValue"/> for a <c>homeDrive</c> that is no drive letter and a <c>homeDirectory</c> that is
    /// neither <c>\\server\share…</c> nor <c>X:\…</c>, <see cref="BulkCreateIssueCodes.AllCapsName"/>. The suffix and domain
    /// checks against the forest are the planner's (2.4).
    /// </remarks>
    public static IReadOnlyList<ValidationIssue> Validate(
        ObjectKind kind,
        string attribute,
        IReadOnlyList<string> values,
        ISchemaCatalog schema,
        PhoneFormat phones,
        TextCase? columnCase)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(schema);
        var issues = new IssueList(attribute);
        if (values.Count > 1 && IsSingleValued(attribute, schema))
        {
            issues.Error(IssueCodes.SingleValued, $"'{attribute}' takes one value.");
        }

        var multiLine = IsMultiLine(attribute);
        foreach (var value in values)
        {
            if (!multiLine && value.Any(char.IsControl))
            {
                issues.Error(BulkCreateIssueCodes.LineBreakInValue, $"'{attribute}' is single-line: the value contains a line break or a control character.");
            }
            else if (multiLine && value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            {
                issues.Error(IssueCodes.InvalidValue, $"The value of '{attribute}' contains a control character.");
            }

            if (Is(attribute, Cn))
            {
                ValidateCn(kind, value, issues);
            }
            else if (Is(attribute, SamAccountName))
            {
                ValidateSamAccountName(kind, value, issues);
            }
            else if (Is(attribute, Upn))
            {
                ValidateUpn(value, issues);
            }
            else if (Is(attribute, Mail))
            {
                ValidateMail(value, issues);
            }
            else
            {
                ValidateOther(kind, attribute, value, schema, phones, columnCase, issues);
            }

            // Columns and edits are normalized and rule values trimmed – except a counter value, whose parts keep their ends
            // (Finish): a rule such as " Sala {$nr}" would create a name with a leading space.
            if (!multiLine && value.Trim().Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            {
                issues.Error(IssueCodes.InvalidValue, $"The value of '{attribute}' begins or ends with white space.");
            }
        }

        return issues.Items;
    }

    /// <summary>
    /// Never a rule target nor a mapped attribute (2.2): naming, class, password, account-control and security attributes,
    /// <c>proxyAddresses</c> (only through <c>AddPrimarySmtp</c>), <c>c</c> / <c>co</c> / <c>countryCode</c> (only through the
    /// <c>Country</c> target), the Exchange and Skype recipient attributes, DN-syntax attributes (<c>manager</c> /
    /// <c>managedBy</c> only through the <c>Manager</c> target), read-only, binary and secret attributes and attributes the
    /// schema does not allow for the class. <c>cn</c> is allowed (it becomes the RDN).
    /// </summary>
    /// <remarks>
    /// A schema without the structural class of <paramref name="kind"/> (no schema loaded) accepts only the attributes of the
    /// synonym table and of 2.2's table that the kind can have (<see cref="ColumnSynonyms"/>). DN syntaxes are
    /// distinguished name, DN with binary or string, OR name and access point.
    /// </remarks>
    public static bool IsBlockedAttribute(string attribute, ObjectKind kind, ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(schema);
        if (attribute.Length == 0 || IsNamedBlockedAttribute(attribute))
        {
            return true;
        }

        if (Is(attribute, Cn))
        {
            return false;
        }

        if (schema.FindClass(CreationNames.StructuralClassOf(kind)) is null)
        {
            return !WithoutSchema.Value.Contains(attribute) || !ColumnSynonyms.AttributeAppliesTo(attribute, kind);
        }

        if (schema.FindAttribute(attribute) is not { } definition || !AllowedFor(schema, kind).Contains(attribute))
        {
            return true;
        }

        return definition.Syntax is AttributeSyntax.DistinguishedName or AttributeSyntax.DnWithBinary or AttributeSyntax.DnWithString
                or AttributeSyntax.ORName or AttributeSyntax.AccessPoint
            || definition.IsReadOnly
            || schema.IsBinary(attribute)
            || SecretAttributes.IsSecret(attribute, schema);
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

    private static bool Is(string attribute, string name) => string.Equals(attribute, name, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> AllowedFor(ISchemaCatalog schema, ObjectKind kind) =>
        Allowed.GetOrCreateValue(schema).GetOrAdd(kind, k => new HashSet<string>(schema.GetAllowedAttributes(CreationNames.ClassChainOf(k)), StringComparer.OrdinalIgnoreCase));

    /// <summary><c>description</c> always (SAM, ADUC parity); otherwise the schema, or without it every attribute but the known multi-valued ones.</summary>
    private static bool IsSingleValued(string attribute, ISchemaCatalog schema) =>
        Is(attribute, "description") || (schema.FindAttribute(attribute) is { } definition ? definition.IsSingleValued : !KnownMultiValued.Contains(attribute));

    /// <summary>
    /// Runs of spaces (all white space with <paramref name="allWhiteSpace"/>) → one space, trimmed; without
    /// <paramref name="edges"/> (a part of a counter value) a run at either end becomes one space instead of none.
    /// </summary>
    private static string CollapseSpaces(string value, bool allWhiteSpace, bool edges)
    {
        var builder = new StringBuilder(value.Length);
        var pending = false;
        foreach (var c in value)
        {
            if (c == ' ' || (allWhiteSpace && char.IsWhiteSpace(c)))
            {
                pending = !edges || builder.Length > 0;
                continue;
            }

            if (pending)
            {
                builder.Append(' ');
                pending = false;
            }

            builder.Append(c);
        }

        if (pending && !edges)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static void ValidateCn(ObjectKind kind, string value, IssueList issues)
    {
        if (value.Trim().Length == 0)
        {
            issues.Error(IssueCodes.InvalidValue, "The name (cn) is empty.");
            return;
        }

        if (value.Length > CreationNames.MaxCn)
        {
            issues.Error(IssueCodes.TooLong, $"The name (cn) is longer than {CreationNames.MaxCn} characters.");
        }

        if (kind == ObjectKind.Computer)
        {
            if (!CreationNames.IsValidComputerName(value))
            {
                issues.Error(IssueCodes.InvalidValue,
                    $"A computer name has 1-{CreationNames.MaxComputerName} letters, digits and hyphens, not only digits, no dot, not starting or ending with a hyphen.");
            }
            else if (value.Contains('_', StringComparison.Ordinal))
            {
                issues.Warning(BulkCreateIssueCodes.ComputerNameNotDnsSafe, "The computer name contains '_', which is not valid in a DNS name.");
            }
        }
    }

    private static void ValidateSamAccountName(ObjectKind kind, string value, IssueList issues)
    {
        var max = CreationNames.MaxSamAccountName(kind);
        if (max == 0)
        {
            return;
        }

        if (value.Trim().Length == 0)
        {
            issues.Error(IssueCodes.InvalidValue, "The logon name (sAMAccountName) is empty.");
            return;
        }

        if (kind == ObjectKind.Computer && (!value.EndsWith('$') || value.Count(c => c == '$') != 1))
        {
            issues.Error(BulkCreateIssueCodes.TrailingDollar, "A computer's sAMAccountName ends with exactly one '$'.");
        }
        else if (kind is ObjectKind.User or ObjectKind.InetOrgPerson && value.EndsWith('$'))
        {
            issues.Error(BulkCreateIssueCodes.TrailingDollar, "A user's sAMAccountName must not end with '$'.");
        }

        if (value.Length > max)
        {
            issues.Error(IssueCodes.TooLong, $"The sAMAccountName is longer than {max} characters.");
        }

        if (value.Any(c => !char.IsControl(c) && CreationNames.IsInvalidSamCharacter(c)))
        {
            issues.Error(IssueCodes.InvalidValue, "The sAMAccountName contains a character that is not allowed (\"/\\[]:;|=,+*?<>@).");
        }

        var name = kind == ObjectKind.Computer ? value.TrimEnd('$') : value;
        if (name.All(c => c is '.' or ' '))
        {
            issues.Error(IssueCodes.InvalidValue, "The sAMAccountName cannot consist only of periods and spaces.");
        }
        else if (name.EndsWith('.'))
        {
            issues.Error(IssueCodes.InvalidValue, "The sAMAccountName cannot end with a period.");
        }
    }

    private static void ValidateUpn(string value, IssueList issues)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
        {
            issues.Error(IssueCodes.InvalidValue, "The user logon name (userPrincipalName) has the form prefix@suffix.");
            return;
        }

        if (!CreationNames.IsValidUpnPrefix(value[..at]))
        {
            issues.Error(IssueCodes.InvalidValue, "The prefix of the userPrincipalName contains a character that is not allowed.");
        }

        if (!IsDomainName(value[(at + 1)..]))
        {
            issues.Error(IssueCodes.InvalidValue, "The suffix of the userPrincipalName is not a domain name.");
        }

        if (value.Length > CreationNames.MaxUpn)
        {
            issues.Error(IssueCodes.TooLong, $"The userPrincipalName is longer than {CreationNames.MaxUpn} characters.");
        }
    }

    private static void ValidateMail(string value, IssueList issues)
    {
        if (!UserMessageRules.TryParseAddress(value, out var address))
        {
            issues.Error(IssueCodes.InvalidValue, "The e-mail address (mail) is not one valid address.");
            return;
        }

        if (address.LastIndexOf('@') > CreationNames.MaxMailLocalPart)
        {
            issues.Error(IssueCodes.TooLong, $"The local part of the e-mail address is longer than {CreationNames.MaxMailLocalPart} characters.");
        }
        else if (address.Length > 256)
        {
            issues.Error(IssueCodes.TooLong, "The e-mail address is longer than 256 characters.");
        }
    }

    private static void ValidateOther(ObjectKind kind, string attribute, string value, ISchemaCatalog schema, PhoneFormat phones, TextCase? columnCase, IssueList issues)
    {
        if (IsPhoneAttribute(attribute))
        {
            if (CellText.IsScientificNotation(value))
            {
                issues.Warning(BulkCreateIssueCodes.ScientificNotation, $"The phone number of '{attribute}' is in scientific notation (Excel shortened it).");
            }
            else if (phones == PhoneFormat.International && !CellText.TryFormatInternational(value, out _))
            {
                issues.Warning(BulkCreateIssueCodes.InvalidPhone, $"The phone number of '{attribute}' could not be written in the international format.");
            }
        }
        else if (Is(attribute, HomeDrive))
        {
            if (!(value.Length == 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
            {
                issues.Warning(IssueCodes.InvalidValue, "The home drive is a drive letter with a colon (H:).");
            }
        }
        else if (Is(attribute, HomeDirectory))
        {
            if (!IsFolderPath(value))
            {
                issues.Warning(IssueCodes.InvalidValue, "The home folder is a UNC path (\\\\server\\share\\...) or a local path (X:\\...).");
            }
        }
        else if (columnCase == TextCase.AsIs && (Is(attribute, "givenName") || Is(attribute, "sn") || Is(attribute, "displayName"))
            && CellText.IsAllCaps(value))
        {
            issues.Warning(BulkCreateIssueCodes.AllCapsName, $"'{attribute}' is written in capitals.");
        }

        if (schema.FindAttribute(attribute) is { } definition && PlanValidation.ValidateValue(definition, value) is { } issue)
        {
            issues.Add(issue.Severity, issue.Code, ValueFreeMessage(issue.Code, attribute, definition));
        }
    }

    /// <summary>The message of a schema check without the value (<c>PlanValidation.ValidateValue</c> quotes it).</summary>
    private static string ValueFreeMessage(string code, string attribute, AttributeDefinition definition) => code switch
    {
        IssueCodes.TooLong => $"The value of '{attribute}' is longer than {definition.RangeUpper} characters.",
        IssueCodes.TooShort => $"The value of '{attribute}' is shorter than {definition.RangeLower} characters.",
        _ => $"The value of '{attribute}' does not match its syntax ({definition.Syntax}) or range.",
    };

    /// <summary>A DNS-like domain: letters, digits, hyphens and single dots, not starting or ending with a dot.</summary>
    private static bool IsDomainName(string value) =>
        value.Length > 0
        && value[0] != '.' && value[^1] != '.'
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '.');

    /// <summary><c>\\server\share…</c> or <c>X:\…</c>.</summary>
    private static bool IsFolderPath(string value)
    {
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\')
        {
            return true;
        }

        if (!value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value[2..].Split('\\');
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0;
    }

    /// <summary>The issues of one attribute, each code once.</summary>
    private sealed class IssueList(string attribute)
    {
        private readonly HashSet<string> _codes = new(StringComparer.Ordinal);

        public List<ValidationIssue> Items { get; } = [];

        public void Error(string code, string message) => Add(IssueSeverity.Error, code, message);

        public void Warning(string code, string message) => Add(IssueSeverity.Warning, code, message);

        public void Add(IssueSeverity severity, string code, string message)
        {
            if (_codes.Add(code))
            {
                Items.Add(new ValidationIssue(severity, code, message, attribute));
            }
        }

        public override string ToString() => $"Issues of {attribute}: {Items.Count}";
    }
}
