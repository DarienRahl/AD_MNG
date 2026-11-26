using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Testing;

/// <summary>
/// The opt-in SAM rules and the channel encryption of the fake (docs/dev/phase3-bulk-create.md 2.13, the F3 contract):
/// what AD's SAM and the DC enforce on Add and Modify and what the bulk creation wizard's planner must anticipate.
/// <see cref="SamRules"/> is off by default (existing tests seed duplicates freely) and switched on by
/// <c>FakeDirectoryBuilder.AddBulkCreateSample</c>.
/// </summary>
public sealed partial class FakeDirectorySession
{
    /// <summary>ERROR_USER_EXISTS: a duplicate sAMAccountName in the domain.</summary>
    public const int Win32UserExists = 0x524;

    /// <summary>ERROR_DS_UPN_VALUE_NOT_UNIQUE_IN_FOREST (Windows Server 2012 R2+): a duplicate userPrincipalName.</summary>
    public const int Win32UpnNotUnique = 0x21C8;

    /// <summary>ERROR_DS_OBJ_STRING_NAME_EXISTS: a sibling of another RDN type already has the name.</summary>
    public const int Win32NameExists = 0x2071;

    /// <summary>ERROR_PASSWORD_RESTRICTION: an enabled user without a password.</summary>
    public const int Win32PasswordRestriction = 0x52D;

    private const int AccountDisable = 0x2;
    private const int PasswordNotRequired = 0x20;

    private static readonly string[] SingleDescriptionClasses = ["user", "group", "computer"];

    /// <summary>
    /// Opt-in rules of AD's SAM (off by default; on after <c>AddBulkCreateSample</c>): a duplicate sAMAccountName in the
    /// domain → 68 / 0x524 on add and modify; a duplicate UPN in the directory → 19 / 0x21C8; an RDN value already used by
    /// a sibling of another RDN type → 68 / 0x2071; an enabled user without a password (UAC given without 0x2 and 0x20, no
    /// <c>unicodePwd</c>) → 53 / 0x52D; a computer sAMAccountName without exactly one trailing <c>$</c> → 19; more than one
    /// <c>description</c> value on a user, group or computer → 19. Deleted objects never collide.
    /// </summary>
    public bool SamRules { get; set; }

    /// <summary>
    /// The session's channel is encrypted (default true, <see cref="SessionInfo.IsChannelEncrypted"/> of <see cref="Info"/>):
    /// false refuses, with <see cref="DirectoryErrorKind.StrongAuthRequired"/> and before anything else is checked, what
    /// <c>LdapDirectorySession</c> refuses on a plain LDAP connection – <see cref="ResetPasswordAsync"/>, an Add with a
    /// non-empty <c>unicodePwd</c> or <c>userPassword</c> (an empty value list is not sent, so it is not refused) and a
    /// Modify of either attribute.
    /// </summary>
    public bool IsChannelEncrypted
    {
        get => Info.IsChannelEncrypted;
        set => Info = Info with { IsChannelEncrypted = value };
    }

    private void CheckChannelForPassword(string distinguishedName)
    {
        if (!IsChannelEncrypted)
        {
            throw Error(8, "Passwords can only be set over an encrypted connection.", distinguishedName, DirectoryErrorKind.StrongAuthRequired);
        }
    }

    /// <summary>The channel check at the very top of <c>ApplyAdd</c> (as <c>LdapDirectorySession.AddAsync</c>: non-empty password values only).</summary>
    private void CheckChannelOnAdd(IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, string distinguishedName)
    {
        if (attributes.Any(kv => IsPasswordAttribute(kv.Key) && kv.Value is { Count: > 0 }))
        {
            CheckChannelForPassword(distinguishedName);
        }
    }

    /// <summary>The channel check at the top of <c>ApplyModify</c> (as <c>LdapDirectorySession.ModifyAsync</c>: any password modification).</summary>
    private void CheckChannelOnModify(IReadOnlyList<AttributeModification> modifications, string distinguishedName)
    {
        if (modifications.Any(m => m is not null && IsPasswordAttribute(m.Attribute)))
        {
            CheckChannelForPassword(distinguishedName);
        }
    }

    /// <summary>The SAM rules of <c>ApplyAdd</c> (after the parent check).</summary>
    private void CheckBulkCreateRulesOnAdd(DistinguishedName dn, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, string distinguishedName)
    {
        if (!SamRules)
        {
            return;
        }

        // The attributes as ApplyAdd stores them: names case-insensitive, the last non-empty value list wins, empty lists
        // are not set; the requested classes with their superclasses (inetOrgPerson is a user).
        IReadOnlyList<object>? ValueOf(string name) => LastNonEmpty(attributes, name);
        var classes = ValueOf(Attr.ObjectClass) is { } requested
            ? ExpandClasses(requested.OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        CheckSamIdentifiers(null, classes, ValueOf, distinguishedName);
        if (dn.Parent is { } parent
            && _entries.Values.Any(e => !e.IsDeleted && e.Dn.Parent == parent && string.Equals(e.Dn.Name, dn.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw SamError(68, Win32NameExists, $"An object named '{dn.Name}' already exists in the container.", distinguishedName);
        }

        if (classes.Contains("user") && !classes.Contains("computer")
            && FirstText(ValueOf(Attr.UserAccountControl)) is { } uacText
            && int.TryParse(uacText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uac)
            && (uac & (AccountDisable | PasswordNotRequired)) == 0
            && ValueOf(Attr.UnicodePwd) is null)
        {
            throw SamError(53, Win32PasswordRestriction, "An enabled account needs a password.", distinguishedName, DirectoryErrorKind.PasswordPolicy);
        }
    }

    /// <summary>The sAMAccountName / UPN check of the modify path (also the computer <c>$</c> and the single description).</summary>
    private void CheckBulkCreateRulesOnModify(Entry entry, Dictionary<string, List<object>> work, IReadOnlyCollection<string> touched)
    {
        if (!SamRules)
        {
            return;
        }

        var classes = entry.Attributes.TryGetValue(Attr.ObjectClass, out var objectClasses)
            ? objectClasses.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        CheckSamIdentifiers(
            entry,
            classes,
            name => touched.Contains(name) && work.TryGetValue(name, out var values) && values.Count > 0 ? values : null,
            entry.Dn.ToString());
    }

    private void CheckSamIdentifiers(Entry? self, HashSet<string> classes, Func<string, IReadOnlyList<object>?> valueOf, string distinguishedName)
    {
        var domain = FakeDn.Parse(Info.DefaultNamingContext);
        if (FirstText(valueOf(Attr.SamAccountName)) is { } sam)
        {
            if (_entries.Values.Any(e => !ReferenceEquals(e, self) && !e.IsDeleted && FakeDn.IsSameOrBelow(e.Dn, domain)
                && string.Equals(e.First(Attr.SamAccountName), sam, StringComparison.OrdinalIgnoreCase)))
            {
                throw SamError(68, Win32UserExists, $"The account name '{sam}' is already in use.", distinguishedName);
            }

            if (classes.Contains("computer") && !(sam.Length >= 2 && sam[^1] == '$' && sam[^2] != '$'))
            {
                throw SamError(19, null, "A computer account name must end with exactly one '$'.", distinguishedName);
            }
        }

        if (FirstText(valueOf(Attr.UserPrincipalName)) is { } upn
            && _entries.Values.Any(e => !ReferenceEquals(e, self) && !e.IsDeleted
                && string.Equals(e.First(Attr.UserPrincipalName), upn, StringComparison.OrdinalIgnoreCase)))
        {
            throw SamError(19, Win32UpnNotUnique, $"The user principal name '{upn}' is not unique in the forest.", distinguishedName);
        }

        if (SingleDescriptionClasses.Any(classes.Contains) && valueOf("description") is { Count: > 1 })
        {
            throw SamError(19, null, "The description of a SAM object is single-valued.", distinguishedName);
        }
    }

    private static bool IsPasswordAttribute(string? attribute) =>
        string.Equals(attribute, Attr.UnicodePwd, StringComparison.OrdinalIgnoreCase)
        || string.Equals(attribute, "userPassword", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last non-empty value list of <paramref name="name"/> (case-insensitive), as <c>ApplyAdd</c> keeps it.</summary>
    private static IReadOnlyList<object>? LastNonEmpty(IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, string name)
    {
        IReadOnlyList<object>? found = null;
        foreach (var (key, values) in attributes)
        {
            if (values is { Count: > 0 } && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                found = values;
            }
        }

        return found;
    }

    private static string? FirstText(IReadOnlyList<object>? values) => values is [string text, ..] ? text : null;

    private static DirectoryException SamError(int ldapCode, int? win32, string message, string distinguishedName, DirectoryErrorKind? kind = null) =>
        new(kind ?? DirectoryException.Classify(ldapCode, win32), message)
        {
            LdapResultCode = ldapCode,
            Win32Error = win32,
            DistinguishedName = distinguishedName,
            ServerMessage = $"{win32 ?? ldapCode:X8}: FakeDirectory: {message}",
        };
}
