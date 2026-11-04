using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Localization;

/// <summary>
/// Localized names of enums, issue codes and attributes. Every key family below is complete in both resource
/// files (enforced by unit tests that enumerate the enums / constants).
/// </summary>
public static partial class DisplayNames
{
    /// <summary>
    /// Attributes with a localized fallback name (used when the forest has no display specifier for the UI language).
    /// </summary>
    public static IReadOnlyList<string> CommonAttributes { get; } =
    [
        Attr.Name, Attr.Cn, Attr.DisplayName, Attr.Description, Attr.GivenName, Attr.Initials, Attr.Sn,
        Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.TelephoneNumber, Attr.Mobile, Attr.HomePhone,
        Attr.Pager, Attr.FacsimileTelephoneNumber, Attr.IpPhone, Attr.Office, Attr.WwwHomePage, Attr.StreetAddress,
        Attr.PostOfficeBox, Attr.City, Attr.State, Attr.PostalCode, Attr.Country, Attr.CountryName, Attr.Title,
        Attr.Department, Attr.Company, Attr.Manager, Attr.EmployeeId, Attr.EmployeeNumber, Attr.EmployeeType, Attr.Info,
        Attr.UserAccountControl, Attr.AccountExpires, Attr.PwdLastSet, Attr.LockoutTime, Attr.LastLogon,
        Attr.LastLogonTimestamp, Attr.LogonCount, Attr.BadPwdCount, Attr.ProfilePath, Attr.ScriptPath,
        Attr.HomeDirectory, Attr.HomeDrive, Attr.MemberOf, Attr.Member, Attr.GroupType, Attr.ManagedBy,
        Attr.DnsHostName, Attr.OperatingSystem, Attr.OperatingSystemVersion, Attr.Location, Attr.WhenCreated,
        Attr.WhenChanged, Attr.CanonicalName, Attr.DistinguishedName, Attr.ObjectClass, Attr.ObjectGuid, Attr.ObjectSid,
        Attr.ProxyAddresses, Attr.ServicePrincipalName, Attr.AdminCount, Attr.PasswordExpiryComputed,
    ];

    public static string ObjectKindKey(ObjectKind kind) => $"ObjectKind.{kind}";

    public static string ErrorKindKey(DirectoryErrorKind kind) => $"ErrorKind.{kind}";

    public static string IssueKey(string code) => $"Issue.{code}";

    public static string IssueSeverityKey(IssueSeverity severity) => $"Severity.{severity}";

    public static string UacFlagKey(UserAccountControl flag) => $"Uac.{flag}";

    public static string GroupScopeKey(GroupScope scope) => $"GroupScope.{scope}";

    /// <summary>ADUC "Type" column text for groups, e.g. "Security Group - Global".</summary>
    public static string GroupTypeKey(bool security, GroupScope scope) => $"GroupType.{(security ? "Security" : "Distribution")}.{scope}";

    public static string AttributeKey(string ldapName) => $"Attribute.{ldapName}";

    public static string ThemeKey(ThemePreference theme) => $"Theme.{theme}";

    /// <summary>"Connections.Color.Blue": the name of a connection colour (connect dialog, badge tool tip).</summary>
    public static string ConnectionColorKey(Connections.ConnectionColor color) => $"Connections.Color.{color}";

    public static string AuthenticationKey(AuthenticationMode mode) => $"Auth.{mode}";

    public static string SecurityKey(TransportSecurity security) => $"Security.{security}";

    public static string ScopeKey(SearchScope scope) => $"Scope.{scope}";

    public static string ChipKey(QuickFilterKind kind) => $"Chip.{kind}";

    public static string BuiltInViewKey(BuiltInViewKind kind) => $"BuiltInView.{kind}";

    /// <summary>Group scopes that have a display name (all except Unknown).</summary>
    public static IReadOnlyList<GroupScope> NamedGroupScopes { get; } =
        [GroupScope.DomainLocal, GroupScope.Global, GroupScope.Universal, GroupScope.BuiltinLocal];

    /// <summary>Individual userAccountControl flags (without None).</summary>
    public static IReadOnlyList<UserAccountControl> UacFlags { get; } =
        [.. Enum.GetValues<UserAccountControl>().Where(f => f != UserAccountControl.None)];

    public static string ObjectKindName(this ILocalizer localizer, ObjectKind kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[ObjectKindKey(kind)];
    }

    public static string ErrorKindName(this ILocalizer localizer, DirectoryErrorKind kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[ErrorKindKey(kind)];
    }

    public static string UacFlagName(this ILocalizer localizer, UserAccountControl flag)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[UacFlagKey(flag)];
    }

    public static string GroupScopeName(this ILocalizer localizer, GroupScope scope)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[GroupScopeKey(scope)];
    }

    /// <summary>"Security Group - Global" / "Grupa zabezpieczeń - globalna" from a raw groupType value.</summary>
    public static string GroupTypeName(this ILocalizer localizer, int groupType)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var scope = GroupTypes.GetScope(groupType);
        return scope == GroupScope.Unknown
            ? localizer[ObjectKindKey(ObjectKind.Group)]
            : localizer[GroupTypeKey(GroupTypes.IsSecurity(groupType), scope)];
    }

    /// <summary>ADUC "Type" column text of an object.</summary>
    public static string TypeName(this ILocalizer localizer, DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(obj);
        return obj.Kind == ObjectKind.Group && obj.GetInt32(Attr.GroupType) is { } groupType
            ? localizer.GroupTypeName(groupType)
            : localizer.ObjectKindName(obj.Kind);
    }

    /// <summary>
    /// Friendly attribute name: display specifier of the forest for the UI language, else the localized fallback
    /// of <see cref="CommonAttributes"/>, else the LDAP name.
    /// </summary>
    public static string AttributeName(this ILocalizer localizer, string ldapName, ISchemaCatalog? schema)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldapName);
        if (schema is not null)
        {
            var fromSchema = schema.GetDisplayName(ldapName, localizer.Culture);
            if (!string.Equals(fromSchema, ldapName, StringComparison.OrdinalIgnoreCase))
            {
                return fromSchema;
            }
        }

        var key = AttributeKey(ldapName);
        return localizer.Contains(key) ? localizer[key] : ldapName;
    }

    /// <summary>Localized text of a validation issue (falls back to the planner's English message for unknown codes).</summary>
    public static string IssueText(this ILocalizer localizer, ValidationIssue issue, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(issue);
        var key = IssueKey(issue.Code);
        if (!localizer.Contains(key))
        {
            return issue.Message;
        }

        var template = localizer[key];
        if (issue.Arguments.Count > 0 && template.Contains("{0}", StringComparison.Ordinal))
        {
            return localizer.Format(key, [.. issue.Arguments]);
        }

        if (issue.Attribute is null)
        {
            // Attribute specific texts need the attribute; fall back to the planner's detailed message.
            return template.Contains("{0}", StringComparison.Ordinal) ? issue.Message : template;
        }

        return localizer.Format(key, localizer.AttributeName(issue.Attribute, schema));
    }
}
