using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// GPO settings in the operator's language (K6, docs/dev/k-computers-and-gpo.md 20.1): GPMC's tree, units, special values,
    /// preferences and the consumers ("Ustawienia" tab, HTML report, comparison, preview, Watchdog rows). Dynamic families:
    /// <see cref="UnitKey"/>, <see cref="PreferenceTypeKey"/>, <see cref="PreferenceElementKey"/>,
    /// <see cref="PreferencePropertyKey"/>, <see cref="PreferenceFilterKey"/>, <see cref="AuditOptionKey"/>.
    /// </summary>
    public static class GpoSettings
    {
        // GPMC's tree
        public const string ComputerConfiguration = "GpoSettings.ComputerConfiguration";
        public const string UserConfiguration = "GpoSettings.UserConfiguration";
        public const string GpoLevel = "GpoSettings.GpoLevel";
        public const string Policies = "GpoSettings.Policies";
        public const string Preferences = "GpoSettings.Preferences";
        public const string WindowsSettings = "GpoSettings.WindowsSettings";
        public const string ControlPanelSettings = "GpoSettings.ControlPanelSettings";
        public const string SecuritySettings = "GpoSettings.SecuritySettings";
        public const string AccountPolicies = "GpoSettings.AccountPolicies";
        public const string LocalPolicies = "GpoSettings.LocalPolicies";
        public const string AuditPolicy = "GpoSettings.AuditPolicy";
        public const string UserRightsAssignment = "GpoSettings.UserRightsAssignment";
        public const string SecurityOptions = "GpoSettings.SecurityOptions";
        public const string EventLog = "GpoSettings.EventLog";
        public const string RestrictedGroups = "GpoSettings.RestrictedGroups";
        public const string SystemServices = "GpoSettings.SystemServices";
        public const string Registry = "GpoSettings.Registry";
        public const string FileSystem = "GpoSettings.FileSystem";
        public const string AdvancedAudit = "GpoSettings.AdvancedAudit";
        public const string AuditOptions = "GpoSettings.AuditOptions";
        public const string ExtraRegistry = "GpoSettings.ExtraRegistry";
        public const string PolicyComments = "GpoSettings.PolicyComments";
        public const string ScriptsComputer = "GpoSettings.ScriptsComputer";
        public const string ScriptsUser = "GpoSettings.ScriptsUser";
        public const string ScriptFiles = "GpoSettings.ScriptFiles";
        public const string UnreadableFiles = "GpoSettings.UnreadableFiles";
        public const string Other = "GpoSettings.Other";

        // Small formats
        public const string NamePart = "GpoSettings.NamePart";
        public const string LabelValue = "GpoSettings.LabelValue";
        public const string Bytes = "GpoSettings.Bytes";
        public const string Kilobytes = "GpoSettings.Kilobytes";
        public const string Megabytes = "GpoSettings.Megabytes";

        // Special values and notes
        public const string PasswordNeverExpires = "GpoSettings.PasswordNeverExpires";
        public const string PasswordChangeImmediately = "GpoSettings.PasswordChangeImmediately";
        public const string NoPasswordRequired = "GpoSettings.NoPasswordRequired";
        public const string NoPasswordHistory = "GpoSettings.NoPasswordHistory";
        public const string AccountNeverLocked = "GpoSettings.AccountNeverLocked";
        public const string LockedUntilAdmin = "GpoSettings.LockedUntilAdmin";
        public const string InactivityLimitOff = "GpoSettings.InactivityLimitOff";
        public const string EncryptionFuture = "GpoSettings.EncryptionFuture";
        public const string ExplainMaxClockSkew = "GpoSettings.ExplainMaxClockSkew";
        public const string ExplainMasked = "GpoSettings.ExplainMasked";
        public const string ExplainLongValue = "GpoSettings.ExplainLongValue";
        public const string ExplainTruncated = "GpoSettings.ExplainTruncated";
        public const string ExplainUnreadable = "GpoSettings.ExplainUnreadable";
        public const string ExplainScriptFile = "GpoSettings.ExplainScriptFile";
        public const string LongValue = "GpoSettings.LongValue";
        public const string LongValueHash = "GpoSettings.LongValueHash";

        // Audit
        public const string AuditExclusion = "GpoSettings.AuditExclusion";
        public const string SaclDefined = "GpoSettings.SaclDefined";

        // Groups, services, object security, event logs
        public const string RestrictedMembers = "GpoSettings.RestrictedMembers";
        public const string RestrictedMemberOf = "GpoSettings.RestrictedMemberOf";
        public const string NoGroups = "GpoSettings.NoGroups";
        public const string ServiceBoot = "GpoSettings.ServiceBoot";
        public const string ServiceSystem = "GpoSettings.ServiceSystem";
        public const string StartupMode = "GpoSettings.StartupMode";
        public const string Inheritance = "GpoSettings.Inheritance";
        public const string Permissions = "GpoSettings.Permissions";
        public const string PermissionsDefined = "GpoSettings.PermissionsDefined";
        public const string ModePropagate = "GpoSettings.ModePropagate";
        public const string ModeReplace = "GpoSettings.ModeReplace";
        public const string ModeDoNotReplace = "GpoSettings.ModeDoNotReplace";
        public const string LogSystem = "GpoSettings.LogSystem";
        public const string LogSecurity = "GpoSettings.LogSecurity";
        public const string LogApplication = "GpoSettings.LogApplication";
        public const string LogMaximumSize = "GpoSettings.LogMaximumSize";
        public const string LogRetentionMethod = "GpoSettings.LogRetentionMethod";
        public const string LogRetentionDays = "GpoSettings.LogRetentionDays";
        public const string LogRestrictGuests = "GpoSettings.LogRestrictGuests";
        public const string RetentionAsNeeded = "GpoSettings.RetentionAsNeeded";
        public const string RetentionByDays = "GpoSettings.RetentionByDays";
        public const string RetentionManual = "GpoSettings.RetentionManual";

        // Registry values
        public const string RegistryValue = "GpoSettings.RegistryValue";
        public const string RegistryDefaultValue = "GpoSettings.RegistryDefaultValue";
        public const string RegistryHiddenNames = "GpoSettings.RegistryHiddenNames";
        public const string RegistryDeleteValue = "GpoSettings.RegistryDeleteValue";
        public const string RegistryDeleteAllValues = "GpoSettings.RegistryDeleteAllValues";
        public const string RegistryCreateKey = "GpoSettings.RegistryCreateKey";
        public const string RegistryDeleteKey = "GpoSettings.RegistryDeleteKey";
        public const string RegistrySetIfMissing = "GpoSettings.RegistrySetIfMissing";

        // Scripts and comments
        public const string Script = "GpoSettings.Script";
        public const string PowerShellScript = "GpoSettings.PowerShellScript";
        public const string ScriptCommand = "GpoSettings.ScriptCommand";
        public const string ScriptParameters = "GpoSettings.ScriptParameters";
        public const string NoParameters = "GpoSettings.NoParameters";
        public const string PowerShellFirst = "GpoSettings.PowerShellFirst";
        public const string PhaseStartup = "GpoSettings.PhaseStartup";
        public const string PhaseShutdown = "GpoSettings.PhaseShutdown";
        public const string PhaseLogon = "GpoSettings.PhaseLogon";
        public const string PhaseLogoff = "GpoSettings.PhaseLogoff";
        public const string GpoComment = "GpoSettings.GpoComment";
        public const string PolicyComment = "GpoSettings.PolicyComment";

        // Preferences
        public const string PrefItem = "GpoSettings.PrefItem";
        public const string PrefItemType = "GpoSettings.PrefItemType";
        public const string PrefName = "GpoSettings.PrefName";
        public const string PrefAction = "GpoSettings.PrefAction";
        public const string PrefDisabled = "GpoSettings.PrefDisabled";
        public const string PrefRemovePolicy = "GpoSettings.PrefRemovePolicy";
        public const string PrefUserContext = "GpoSettings.PrefUserContext";
        public const string PrefBypassErrors = "GpoSettings.PrefBypassErrors";
        public const string PrefDescription = "GpoSettings.PrefDescription";
        public const string PrefChanged = "GpoSettings.PrefChanged";
        public const string PrefMembers = "GpoSettings.PrefMembers";
        public const string PrefTargeting = "GpoSettings.PrefTargeting";
        public const string PrefTargetingLabel = "GpoSettings.PrefTargetingLabel";
        public const string PrefAnd = "GpoSettings.PrefAnd";
        public const string PrefOr = "GpoSettings.PrefOr";
        public const string PrefNot = "GpoSettings.PrefNot";
        public const string PrefNoChange = "GpoSettings.PrefNoChange";
        public const string PrefServiceStart = "GpoSettings.PrefServiceStart";
        public const string PrefServiceStop = "GpoSettings.PrefServiceStop";
        public const string PrefServiceRestart = "GpoSettings.PrefServiceRestart";
        public const string TargetFileSystem = "GpoSettings.TargetFileSystem";
        public const string TargetUrl = "GpoSettings.TargetUrl";
        public const string TargetShell = "GpoSettings.TargetShell";

        // "Ustawienia" tab of the GPO document
        public const string SearchPlaceholder = "GpoSettings.SearchPlaceholder";
        public const string EditSetting = "GpoSettings.EditSetting";
        public const string CopySetting = "GpoSettings.CopySetting";
        public const string Copied = "GpoSettings.Copied";
        public const string HtmlReport = "GpoSettings.HtmlReport";
        public const string HtmlFilter = "GpoSettings.HtmlFilter";
        public const string ReportExported = "GpoSettings.ReportExported";
        public const string ReportTitle = "GpoSettings.ReportTitle";
        public const string ReportMeta = "GpoSettings.ReportMeta";
        public const string ReportNote = "GpoSettings.ReportNote";
        public const string ReportFailed = "GpoSettings.ReportFailed";
        public const string ColumnTechnical = "GpoSettings.ColumnTechnical";
        public const string DefinitionsLoading = "GpoSettings.DefinitionsLoading";
        public const string DefinitionsFailed = "GpoSettings.DefinitionsFailed";
        public const string DefinitionsNone = "GpoSettings.DefinitionsNone";
        public const string Shown = "GpoSettings.Shown";
        public const string NoMatches = "GpoSettings.NoMatches";
        public const string DetailsHint = "GpoSettings.DetailsHint";
        public const string DetailsWhere = "GpoSettings.DetailsWhere";
        public const string DetailsTechnical = "GpoSettings.DetailsTechnical";

        /// <summary>The three forms of a quantity (Polish: 1, 2–4, 5+).</summary>
        public enum PluralForm
        {
            One,
            Few,
            Many,
        }

        /// <summary>"{0} dni" (<c>GpoSettings.Unit.Days.Many</c>).</summary>
        public static string UnitKey(SecurityValueUnit unit, PluralForm form) => $"GpoSettings.Unit.{unit}.{form}";

        /// <summary>A GPP folder: "Lokalni użytkownicy i grupy" (<c>GpoSettings.Pref.Type.Groups</c>).</summary>
        public static string PreferenceTypeKey(string folder) => $"GpoSettings.Pref.Type.{folder}";

        /// <summary>A GPP item's element: "Grupa lokalna" (<c>GpoSettings.Pref.Element.Group</c>).</summary>
        public static string PreferenceElementKey(string element) => $"GpoSettings.Pref.Element.{element}";

        /// <summary>A GPP property: "Ścieżka" (<c>GpoSettings.Pref.Prop.path</c>, spelled as in the XML; resource names ignore case,
        /// so a property is listed once).</summary>
        public static string PreferencePropertyKey(string property) => $"GpoSettings.Pref.Prop.{property}";

        /// <summary>An item-level targeting filter: "komputer" (<c>GpoSettings.Pref.Filter.FilterComputer</c>).</summary>
        public static string PreferenceFilterKey(string filter) => $"GpoSettings.Pref.Filter.{filter}";

        /// <summary>An audit.csv option: <c>GpoSettings.AuditOption.CrashOnAuditFail</c>.</summary>
        public static string AuditOptionKey(string option) => $"GpoSettings.AuditOption.{option}";

        /// <summary>The units with names (every <see cref="SecurityValueUnit"/> but None).</summary>
        public static IEnumerable<SecurityValueUnit> NamedUnits => Enum.GetValues<SecurityValueUnit>().Where(u => u != SecurityValueUnit.None);

        /// <summary>The GPP folders named in the resources.</summary>
        public static IReadOnlyList<string> PreferenceTypes { get; } =
        [
            "Applications", "Drives", "EnvironmentVariables", "Files", "Folders", "IniFiles", "Registry", "NetworkShares", "Shortcuts",
            "DataSources", "Devices", "FolderOptions", "InternetSettings", "Groups", "NetworkOptions", "PowerOptions", "Printers",
            "RegionalOptions", "ScheduledTasks", "Services", "StartMenuTaskbar",
        ];

        /// <summary>The GPP item elements named in the resources.</summary>
        public static IReadOnlyList<string> PreferenceElements { get; } =
        [
            "Group", "User", "Drive", "File", "Folder", "Registry", "Collection", "Shortcut", "Task", "TaskV2", "ImmediateTask", "ImmediateTaskV2",
            "NTService", "EnvironmentVariable", "Ini", "DataSource", "SharedPrinter", "LocalPrinter", "PortPrinter", "NetShare", "PowerScheme",
            "PowerPlan", "GlobalPowerOptions", "GlobalPowerOptionsV2", "GlobalFolderOptions", "GlobalFolderOptionsVista", "OpenWith", "FileType",
            "DeviceClass", "DUN", "VPN", "Regional", "StartMenu", "StartMenuVista", "Internet",
        ];

        /// <summary>The GPP properties named in the resources (as spelled in the XML).</summary>
        public static IReadOnlyList<string> PreferenceProperties { get; } =
        [
            "groupName", "groupSid", "newName", "description", "deleteAllUsers", "deleteAllGroups", "removeAccounts", "userName", "fullName",
            "changeLogon", "noChange", "neverExpires", "acctDisabled", "expires", "cpassword", "path", "label", "letter", "persistent", "useLetter",
            "thisDrive", "allDrives", "fromPath", "targetPath", "readOnly", "archive", "hidden", "suppress", "deleteFolder", "deleteSubFolders",
            "deleteFiles", "deleteReadOnly", "deleteIgnoreErrors", "hive", "key", "name", "type", "value", "default", "displayDecimal",
            "shortcutPath", "targetType", "arguments", "startIn", "iconPath", "window", "comment", "runAs", "logonType", "appName", "args",
            "enabled", "serviceName", "startupType", "serviceAction", "accountName", "timeout", "user", "partial", "section", "property", "dsn",
            "driver", "userDSN", "location", "port", "ipAddress", "localName", "Command", "WorkingDirectory", "UserId", "Author", "RunLevel",
            "shortcutKey", "iconIndex",
        ];

        /// <summary>The item-level targeting filters named in the resources.</summary>
        public static IReadOnlyList<string> PreferenceFilters { get; } =
        [
            "FilterComputer", "FilterGroup", "FilterUser", "FilterOrgUnit", "FilterSite", "FilterDomain", "FilterOs", "FilterWmi", "FilterRegistry",
            "FilterFile", "FilterIpRange", "FilterLanguage", "FilterCollection", "FilterVariable", "FilterBattery", "FilterCpu", "FilterDate",
            "FilterDisk", "FilterDun", "FilterLdap", "FilterMacRange", "FilterMsi", "FilterPcmcia", "FilterPortable", "FilterProcMode",
            "FilterRam", "FilterTerminal", "FilterTime", "FilterVpn",
        ];

        /// <summary>The audit.csv options named in the resources.</summary>
        public static IReadOnlyList<string> AuditOptionNames { get; } = ["CrashOnAuditFail", "FullPrivilegeAuditing", "AuditBaseObjects", "AuditBaseDirectories"];

        /// <summary>Every dynamic key of the presenter (the localization tests check both languages have them).</summary>
        public static IEnumerable<string> DynamicKeys()
        {
            foreach (var unit in NamedUnits)
            {
                foreach (var form in Enum.GetValues<PluralForm>())
                {
                    yield return UnitKey(unit, form);
                }
            }

            foreach (var key in PreferenceTypes.Select(PreferenceTypeKey)
                         .Concat(PreferenceElements.Select(PreferenceElementKey))
                         .Concat(PreferenceProperties.Select(PreferencePropertyKey))
                         .Concat(PreferenceFilters.Select(PreferenceFilterKey))
                         .Concat(AuditOptionNames.Select(AuditOptionKey)))
            {
                yield return key;
            }
        }
    }
}
