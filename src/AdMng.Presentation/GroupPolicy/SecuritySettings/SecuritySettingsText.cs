using AdMng.Core.GroupPolicy.Security;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>A local group or well-known principal offered by the security settings editor (K4).</summary>
/// <param name="Sid">The SID written to the files.</param>
/// <param name="DisplayName">The localized name shown.</param>
/// <param name="GppName">The name GPMC writes into a GPP local group item (<c>Administrators (built-in)</c>); null for principals that are not local groups.</param>
public sealed record PrincipalChoice(string Sid, string DisplayName, string? GppName)
{
    public bool IsLocalGroup => GppName is not null;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Resource keys of the security settings editor's catalog names (dynamic families of <see cref="L.SecuritySettings"/>) and the
/// built-in local groups and well-known principals it names by SID.
/// </summary>
public static class SecuritySettingsText
{
    /// <summary>The name of a user right as GPMC shows it (<c>SecRight.SeDebugPrivilege</c>).</summary>
    public static string RightKey(string constant) => $"SecRight.{constant}";

    /// <summary>The name of an account policy or security option (<c>SecOption.MinimumPasswordLength</c>).</summary>
    public static string OptionKey(string id) => $"SecOption.{id}";

    /// <summary>The label of one choice of an option (<c>SecOption.LmCompatibilityLevel.5</c>).</summary>
    public static string OptionChoiceKey(string id, long value) => $"SecOption.{id}.{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The name of a group of options (<c>SecOptionGroup.PasswordPolicy</c>).</summary>
    public static string OptionGroupKey(SecurityOptionGroup group) => $"SecOptionGroup.{group}";

    /// <summary>The name of a legacy audit category (<c>SecEventAudit.AuditLogonEvents</c>).</summary>
    public static string EventAuditKey(string key) => $"SecEventAudit.{key}";

    /// <summary>The name of an advanced audit category (<c>SecAuditCategory.LogonLogoff</c>).</summary>
    public static string AuditCategoryKey(string category) => $"SecAuditCategory.{category}";

    /// <summary>The name of an advanced audit subcategory (<c>SecAudit.Logon</c>).</summary>
    public static string AuditSubcategoryKey(string id) => $"SecAudit.{id}";

    /// <summary>The name of a well-known principal (<c>SecPrincipal.LocalService</c>).</summary>
    public static string PrincipalKey(string id) => $"SecPrincipal.{id}";

    /// <summary>The built-in local groups (id, SID, English name as GPMC writes it before " (built-in)").</summary>
    public static IReadOnlyList<(string Id, string Sid, string EnglishName)> LocalGroups { get; } =
    [
        ("Administrators", LocalRightsCatalog.AdministratorsSid, "Administrators"),
        ("Users", LocalRightsCatalog.UsersSid, "Users"),
        ("Guests", "S-1-5-32-546", "Guests"),
        ("PowerUsers", LocalRightsCatalog.PowerUsersSid, "Power Users"),
        ("AccountOperators", "S-1-5-32-548", "Account Operators"),
        ("ServerOperators", "S-1-5-32-549", "Server Operators"),
        ("PrintOperators", "S-1-5-32-550", "Print Operators"),
        ("BackupOperators", LocalRightsCatalog.BackupOperatorsSid, "Backup Operators"),
        ("RemoteDesktopUsers", LocalRightsCatalog.RemoteDesktopUsersSid, "Remote Desktop Users"),
        ("NetworkConfigurationOperators", "S-1-5-32-556", "Network Configuration Operators"),
        ("PerformanceMonitorUsers", "S-1-5-32-558", "Performance Monitor Users"),
        ("PerformanceLogUsers", "S-1-5-32-559", "Performance Log Users"),
        ("DistributedComUsers", "S-1-5-32-562", "Distributed COM Users"),
        ("CryptographicOperators", "S-1-5-32-569", "Cryptographic Operators"),
        ("EventLogReaders", "S-1-5-32-573", "Event Log Readers"),
        ("HyperVAdministrators", "S-1-5-32-578", "Hyper-V Administrators"),
        ("AccessControlAssistanceOperators", "S-1-5-32-579", "Access Control Assistance Operators"),
        ("RemoteManagementUsers", LocalRightsCatalog.RemoteManagementUsersSid, "Remote Management Users"),
    ];

    /// <summary>Well-known principals of user rights that are not domain accounts (id, SID).</summary>
    public static IReadOnlyList<(string Id, string Sid)> WellKnownPrincipals { get; } =
    [
        ("Everyone", "S-1-1-0"),
        ("AuthenticatedUsers", "S-1-5-11"),
        ("LocalSystem", "S-1-5-18"),
        ("LocalService", "S-1-5-19"),
        ("NetworkService", "S-1-5-20"),
        ("Service", "S-1-5-6"),
        ("Interactive", "S-1-5-4"),
        ("Network", "S-1-5-2"),
        ("Batch", "S-1-5-3"),
        ("AnonymousLogon", "S-1-5-7"),
        ("EnterpriseDomainControllers", "S-1-5-9"),
        ("LocalAccount", "S-1-5-113"),
        ("LocalAccountAndAdministrator", "S-1-5-114"),
        ("AllServices", "S-1-5-80-0"),
        ("WindowManagerGroup", "S-1-5-90-0"),
        ("VirtualMachines", "S-1-5-83-0"),
    ];

    /// <summary>The resource key of a local group's localized name (U4c's names where the catalog has them).</summary>
    public static string LocalGroupNameKey(string id) =>
        LocalRightsCatalog.All.Any(d => d.Kind == LocalRightKind.LocalGroup && d.Id == id) ? PrivilegedAccessText.GroupNameKey(id) : $"SecLocalGroup.{id}";

    /// <summary>Every dynamic key of the editor (the localization tests check that both languages have them).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var right in SecuritySettingsCatalog.UserRights)
        {
            yield return RightKey(right);
        }

        foreach (var option in SecuritySettingsCatalog.Options)
        {
            yield return OptionKey(option.Id);
            foreach (var choice in option.Choices ?? [])
            {
                yield return OptionChoiceKey(option.Id, choice);
            }
        }

        foreach (var group in Enum.GetValues<SecurityOptionGroup>())
        {
            yield return OptionGroupKey(group);
        }

        foreach (var key in SecuritySettingsCatalog.AuditCategories)
        {
            yield return EventAuditKey(key);
        }

        foreach (var category in SecuritySettingsCatalog.AdvancedAuditCategories)
        {
            yield return AuditCategoryKey(category);
        }

        foreach (var subcategory in SecuritySettingsCatalog.AuditSubcategories)
        {
            yield return AuditSubcategoryKey(subcategory.Id);
        }

        foreach (var (id, _, _) in LocalGroups)
        {
            yield return LocalGroupNameKey(id);
        }

        foreach (var (id, _) in WellKnownPrincipals)
        {
            yield return PrincipalKey(id);
        }
    }

    /// <summary>The localized local groups (with their GPP names) and well-known principals, local groups first.</summary>
    public static IReadOnlyList<PrincipalChoice> Choices(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var compare = StringComparer.Create(localizer.Culture, ignoreCase: true);
        var groups = LocalGroups.Select(g => new PrincipalChoice(g.Sid, localizer[LocalGroupNameKey(g.Id)], g.EnglishName + " (built-in)")).OrderBy(c => c.DisplayName, compare);
        var principals = WellKnownPrincipals.Select(p => new PrincipalChoice(p.Sid, localizer[PrincipalKey(p.Id)], null)).OrderBy(c => c.DisplayName, compare);
        return [.. groups, .. principals];
    }
}
