using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Comparison;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>Localized texts of the Group Policy model (status, levels, link kinds, versions, setting categories).</summary>
public static class GroupPolicyText
{
    public static string Status(ILocalizer localizer, GpoStatus status)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[status switch
        {
            GpoStatus.UserSettingsDisabled => L.GroupPolicy.StatusUserDisabled,
            GpoStatus.ComputerSettingsDisabled => L.GroupPolicy.StatusComputerDisabled,
            GpoStatus.AllSettingsDisabled => L.GroupPolicy.StatusAllDisabled,
            _ => L.GroupPolicy.StatusEnabled,
        }];
    }

    public static string Level(ILocalizer localizer, GpoPermissionLevel level)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[level switch
        {
            GpoPermissionLevel.Read => L.GroupPolicy.LevelRead,
            GpoPermissionLevel.Apply => L.GroupPolicy.LevelApply,
            GpoPermissionLevel.Edit => L.GroupPolicy.LevelEdit,
            GpoPermissionLevel.EditDeleteModifySecurity => L.GroupPolicy.LevelEditDeleteModifySecurity,
            _ => L.GroupPolicy.LevelCustom,
        }];
    }

    public static string SomKind(ILocalizer localizer, GroupPolicySomKind kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[kind switch
        {
            GroupPolicySomKind.Domain => L.GroupPolicy.KindDomain,
            GroupPolicySomKind.Site => L.GroupPolicy.KindSite,
            _ => L.GroupPolicy.KindOrganizationalUnit,
        }];
    }

    /// <summary>"komputer 3, użytkownik 1" (or a dash when unknown).</summary>
    public static string Version(ILocalizer localizer, GpoVersion? version)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return version is { } v
            ? localizer.Format(L.PlanPreview.VersionValue, v.Computer.ToString(localizer.Culture), v.User.ToString(localizer.Culture))
            : "–";
    }

    public static string YesNo(ILocalizer localizer, bool value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[value ? L.GroupPolicy.Yes : L.GroupPolicy.No];
    }

    /// <summary>"Komputer / Zabezpieczenia" for a setting.</summary>
    public static string SettingPath(ILocalizer localizer, GpoSetting setting)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(setting);
        return Scope(localizer, setting) + " / " + Category(localizer, setting.Category);
    }

    /// <summary>"Komputer", "Użytkownik" or "GPO" from the first segment of the setting's key.</summary>
    public static string Scope(ILocalizer localizer, GpoSetting setting)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(setting);
        var first = setting.Key.Split('/')[0];
        return localizer[first.Equals("User", StringComparison.OrdinalIgnoreCase) ? L.GroupPolicy.ScopeUser
            : first.Equals("Computer", StringComparison.OrdinalIgnoreCase) ? L.GroupPolicy.ScopeComputer
            : L.GroupPolicy.ScopeGpo];
    }

    /// <summary>The key without its scope segment ("Security/System Access/MinimumPasswordLength").</summary>
    public static string SettingName(GpoSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var slash = setting.Key.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && setting.Key[..slash] is "Computer" or "User" ? setting.Key[(slash + 1)..] : setting.Key;
    }

    public static string Category(ILocalizer localizer, GpoSettingCategory category)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var key = "GroupPolicy.Category." + category.ToString();
        var text = localizer[key];
        return string.Equals(text, key, StringComparison.Ordinal) ? category.ToString() : text;
    }

    /// <summary>The value shown for a setting (masked values never).</summary>
    public static string SettingValue(ILocalizer localizer, GpoSetting setting)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(setting);
        return setting.Kind switch
        {
            GpoSettingKind.Masked => localizer[L.GroupPolicy.MaskedValue],
            GpoSettingKind.Unreadable => localizer[L.GroupPolicy.UnreadableValue],
            _ when setting.IsList => string.Join(", ", setting.Elements()),
            _ => setting.Value ?? string.Empty,
        };
    }

    /// <summary>The section name of a comparison row.</summary>
    public static string Section(ILocalizer localizer, GpoComparisonSection section)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[section switch
        {
            GpoComparisonSection.Core => L.GroupPolicy.SectionCore,
            GpoComparisonSection.Permissions => L.GroupPolicy.SectionPermissions,
            GpoComparisonSection.Links => L.GroupPolicy.SectionLinks,
            GpoComparisonSection.Settings => L.GroupPolicy.SectionSettings,
            _ => L.GroupPolicy.SectionFiles,
        }];
    }

    public static string State(ILocalizer localizer, GpoComparisonState state)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[state switch
        {
            GpoComparisonState.Same => L.GroupPolicy.StateSame,
            GpoComparisonState.Different => L.GroupPolicy.StateDifferent,
            GpoComparisonState.OnlyInA => L.GroupPolicy.StateOnlyInA,
            _ => L.GroupPolicy.StateOnlyInB,
        }];
    }

    /// <summary>The label of a core key of the comparison.</summary>
    public static string CoreKey(ILocalizer localizer, string key)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return key switch
        {
            GroupPolicyComparer.StatusKey => localizer[L.GroupPolicy.ColumnStatus],
            GroupPolicyComparer.UserVersionKey => localizer[L.GroupPolicy.UserVersion],
            GroupPolicyComparer.ComputerVersionKey => localizer[L.GroupPolicy.ComputerVersion],
            GroupPolicyComparer.WmiFilterKey => localizer[L.GroupPolicy.ColumnWmiFilter],
            GroupPolicyComparer.MachineExtensionsKey => localizer[L.GroupPolicy.MachineExtensions],
            GroupPolicyComparer.UserExtensionsKey => localizer[L.GroupPolicy.UserExtensions],
            _ => key,
        };
    }

    /// <summary>A date in the operator's culture and time zone (a dash when unknown).</summary>
    public static string Date(ILocalizer localizer, DateTimeOffset? value, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(zone);
        return value is { } date ? TimeZoneInfo.ConvertTime(date, zone).ToString("g", localizer.Culture) : "–";
    }

    /// <summary>The GPO's GUID as GPMC shows it (<c>{…}</c>, upper case).</summary>
    public static string Guid(Guid id) => id.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>The failures a Group Policy read, plan or backup reports to the operator (anything else is a bug).</summary>
    public static bool IsExpected(Exception exception) => exception is DirectoryException or SysvolWriteException or SysvolUnavailableException
        or GpoTooLargeException or GpoBackupException or GpoParseException or OperationCanceledException or SessionEndedException
        or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    /// <summary>The localized text of a failure of <see cref="IsExpected"/> (never a path or content).</summary>
    public static string Error(ILocalizer localizer, ErrorTranslator errors, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            GpoTooLargeException => localizer[L.GroupPolicy.TooLarge],
            SysvolUnavailableException => localizer[L.GroupPolicy.SysvolFailedShort],
            SysvolWriteException write => localizer.Format(L.GroupPolicy.SysvolWriteFailed, SysvolError(localizer, write.Error)),
            GpoBackupException => localizer[L.GroupPolicy.BackupInvalid],
            GpoParseException => localizer[L.GroupPolicy.ParseFailed],
            IOException or UnauthorizedAccessException => localizer[L.GroupPolicy.FolderFailed],
            _ => errors.Translate(exception),
        };
    }

    /// <summary>The kind of a SYSVOL write failure.</summary>
    public static string SysvolError(ILocalizer localizer, SysvolWriteError error)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[error switch
        {
            SysvolWriteError.AccessDenied => L.GroupPolicy.SysvolErrorAccessDenied,
            SysvolWriteError.NotFound => L.GroupPolicy.SysvolErrorNotFound,
            SysvolWriteError.AlreadyExists => L.GroupPolicy.SysvolErrorAlreadyExists,
            SysvolWriteError.Unreachable => L.GroupPolicy.SysvolErrorUnreachable,
            SysvolWriteError.LogonFailure => L.GroupPolicy.SysvolErrorLogonFailure,
            SysvolWriteError.DiskFull => L.GroupPolicy.SysvolErrorDiskFull,
            _ => L.GroupPolicy.SysvolErrorFailed,
        }];
    }
}
