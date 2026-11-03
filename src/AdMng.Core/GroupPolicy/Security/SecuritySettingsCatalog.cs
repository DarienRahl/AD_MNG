namespace AdMng.Core.GroupPolicy.Security;

/// <summary>How a security setting is edited.</summary>
public enum SecurityValueKind
{
    /// <summary>A whole number (<see cref="SecurityOptionDefinition.Minimum"/>–<see cref="SecurityOptionDefinition.Maximum"/>).</summary>
    Number,

    /// <summary>Enabled (1) or disabled (0).</summary>
    Boolean,

    /// <summary>One of <see cref="SecurityOptionDefinition.Choices"/>.</summary>
    Choice,

    /// <summary>Text (quoted in the template).</summary>
    Text,

    /// <summary>Lines of text (REG_MULTI_SZ).</summary>
    MultiText,
}

/// <summary>Where a setting lives in <c>GptTmpl.inf</c>.</summary>
public enum SecurityOptionLocation
{
    /// <summary><c>[System Access]</c> (account policies, account options).</summary>
    SystemAccess,

    /// <summary><c>[Kerberos Policy]</c>.</summary>
    KerberosPolicy,

    /// <summary><c>[Registry Values]</c> (security options).</summary>
    RegistryValue,
}

/// <summary>The group a setting is listed in.</summary>
public enum SecurityOptionGroup
{
    PasswordPolicy,
    AccountLockout,
    Kerberos,
    Accounts,
    NetworkSecurity,
    NetworkAccess,
    MicrosoftNetwork,
    DomainController,
    DomainMember,
    InteractiveLogon,
    UserAccountControl,
    Audit,
}

/// <summary>
/// One account policy or security option the editor knows (K4). <see cref="Id"/> is the stable id the UI localizes
/// (<c>SecOption.&lt;Id&gt;</c>); <see cref="Key"/> is the <c>[System Access]</c>/<c>[Kerberos Policy]</c> key or the
/// secedit registry path (<c>MACHINE\System\…\Name</c>); <see cref="RegistryType"/> is the type a registry value is written
/// with (4 REG_DWORD, 1 REG_SZ, 7 REG_MULTI_SZ).
/// </summary>
public sealed record SecurityOptionDefinition(
    string Id,
    SecurityOptionGroup Group,
    SecurityOptionLocation Location,
    string Key,
    SecurityValueKind Kind,
    int RegistryType = 4,
    long Minimum = 0,
    long Maximum = uint.MaxValue,
    IReadOnlyList<long>? Choices = null)
{
    /// <summary>The template section of the setting.</summary>
    public string Section => Location switch
    {
        SecurityOptionLocation.SystemAccess => SecurityTemplateDocument.SystemAccess,
        SecurityOptionLocation.KerberosPolicy => SecurityTemplateDocument.KerberosPolicy,
        _ => SecurityTemplateDocument.RegistryValues,
    };
}

/// <summary>One advanced audit subcategory ([MS-GPAC] 2.2.1.2; <c>auditpol /list /subcategory:* /v</c>).</summary>
/// <param name="Id">The stable id the UI localizes (<c>SecAudit.&lt;Id&gt;</c>).</param>
/// <param name="Category">The category id (<c>SecAuditCategory.&lt;Category&gt;</c>).</param>
/// <param name="Guid">The subcategory GUID written to audit.csv.</param>
/// <param name="Name">The English name; audit.csv's Subcategory column is "Audit " + this.</param>
public sealed record AuditSubcategoryDefinition(string Id, string Category, Guid Guid, string Name)
{
    /// <summary>The Subcategory column GPMC writes.</summary>
    public string CsvName => "Audit " + Name;
}

/// <summary>
/// The security settings the K4 editor lists by name (design note docs/dev/k-computers-and-gpo.md 19): every user right of
/// <c>[Privilege Rights]</c>, the account policies and account options of <c>[System Access]</c> and <c>[Kerberos Policy]</c>,
/// the most used security options (<c>[Registry Values]</c>), the legacy audit categories (<c>[Event Audit]</c>) and the
/// advanced audit subcategories (audit.csv). Settings of a GPO that are not listed here are shown and kept as raw entries.
/// Ids only: names and descriptions are localized in the Presentation resources.
/// </summary>
public static class SecuritySettingsCatalog
{
    /// <summary>The user rights of "Przypisywanie praw użytkownika" in GPMC's order (by English display name).</summary>
    public static IReadOnlyList<string> UserRights { get; } =
    [
        "SeTrustedCredManAccessPrivilege",
        "SeNetworkLogonRight",
        "SeTcbPrivilege",
        "SeMachineAccountPrivilege",
        "SeIncreaseQuotaPrivilege",
        "SeInteractiveLogonRight",
        "SeRemoteInteractiveLogonRight",
        "SeBackupPrivilege",
        "SeChangeNotifyPrivilege",
        "SeSystemtimePrivilege",
        "SeTimeZonePrivilege",
        "SeCreatePagefilePrivilege",
        "SeCreateTokenPrivilege",
        "SeCreateGlobalPrivilege",
        "SeCreatePermanentPrivilege",
        "SeCreateSymbolicLinkPrivilege",
        "SeDebugPrivilege",
        "SeDenyNetworkLogonRight",
        "SeDenyBatchLogonRight",
        "SeDenyServiceLogonRight",
        "SeDenyInteractiveLogonRight",
        "SeDenyRemoteInteractiveLogonRight",
        "SeEnableDelegationPrivilege",
        "SeRemoteShutdownPrivilege",
        "SeAuditPrivilege",
        "SeImpersonatePrivilege",
        "SeIncreaseWorkingSetPrivilege",
        "SeIncreaseBasePriorityPrivilege",
        "SeLoadDriverPrivilege",
        "SeLockMemoryPrivilege",
        "SeBatchLogonRight",
        "SeServiceLogonRight",
        "SeSecurityPrivilege",
        "SeRelabelPrivilege",
        "SeSystemEnvironmentPrivilege",
        "SeDelegateSessionUserImpersonatePrivilege",
        "SeManageVolumePrivilege",
        "SeProfileSingleProcessPrivilege",
        "SeSystemProfilePrivilege",
        "SeAssignPrimaryTokenPrivilege",
        "SeRestorePrivilege",
        "SeShutdownPrivilege",
        "SeSyncAgentPrivilege",
        "SeTakeOwnershipPrivilege",
        "SeUndockPrivilege",
    ];

    private const string Lsa = @"MACHINE\System\CurrentControlSet\Control\Lsa\";
    private const string Msv = @"MACHINE\System\CurrentControlSet\Control\Lsa\MSV1_0\";
    private const string Server = @"MACHINE\System\CurrentControlSet\Services\LanManServer\Parameters\";
    private const string Workstation = @"MACHINE\System\CurrentControlSet\Services\LanmanWorkstation\Parameters\";
    private const string Netlogon = @"MACHINE\System\CurrentControlSet\Services\Netlogon\Parameters\";
    private const string PoliciesSystem = @"MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\";

    /// <summary>Account policies, account options and security options.</summary>
    public static IReadOnlyList<SecurityOptionDefinition> Options { get; } =
    [
        Access("PasswordHistorySize", SecurityOptionGroup.PasswordPolicy, SecurityValueKind.Number, 0, 24),
        Access("MaximumPasswordAge", SecurityOptionGroup.PasswordPolicy, SecurityValueKind.Number, -1, 999),
        Access("MinimumPasswordAge", SecurityOptionGroup.PasswordPolicy, SecurityValueKind.Number, 0, 998),
        Access("MinimumPasswordLength", SecurityOptionGroup.PasswordPolicy, SecurityValueKind.Number, 0, 128),
        Access("PasswordComplexity", SecurityOptionGroup.PasswordPolicy, SecurityValueKind.Boolean),
        Access("ClearTextPassword", SecurityOptionGroup.PasswordPolicy, SecurityValueKind.Boolean),
        Access("LockoutBadCount", SecurityOptionGroup.AccountLockout, SecurityValueKind.Number, 0, 999),
        Access("LockoutDuration", SecurityOptionGroup.AccountLockout, SecurityValueKind.Number, -1, 99999),
        Access("ResetLockoutCount", SecurityOptionGroup.AccountLockout, SecurityValueKind.Number, 1, 99999),
        new("MaxTicketAge", SecurityOptionGroup.Kerberos, SecurityOptionLocation.KerberosPolicy, "MaxTicketAge", SecurityValueKind.Number, Minimum: 0, Maximum: 99999),
        new("MaxRenewAge", SecurityOptionGroup.Kerberos, SecurityOptionLocation.KerberosPolicy, "MaxRenewAge", SecurityValueKind.Number, Minimum: 0, Maximum: 99999),
        new("MaxServiceAge", SecurityOptionGroup.Kerberos, SecurityOptionLocation.KerberosPolicy, "MaxServiceAge", SecurityValueKind.Number, Minimum: 10, Maximum: 99999),
        new("MaxClockSkew", SecurityOptionGroup.Kerberos, SecurityOptionLocation.KerberosPolicy, "MaxClockSkew", SecurityValueKind.Number, Minimum: 0, Maximum: 99999),
        new("TicketValidateClient", SecurityOptionGroup.Kerberos, SecurityOptionLocation.KerberosPolicy, "TicketValidateClient", SecurityValueKind.Boolean),

        Access("EnableAdminAccount", SecurityOptionGroup.Accounts, SecurityValueKind.Boolean),
        Access("EnableGuestAccount", SecurityOptionGroup.Accounts, SecurityValueKind.Boolean),
        Access("NewAdministratorName", SecurityOptionGroup.Accounts, SecurityValueKind.Text),
        Access("NewGuestName", SecurityOptionGroup.Accounts, SecurityValueKind.Text),
        Registry("LimitBlankPasswordUse", SecurityOptionGroup.Accounts, Lsa + "LimitBlankPasswordUse", SecurityValueKind.Boolean),
        Access("LSAAnonymousNameLookup", SecurityOptionGroup.NetworkAccess, SecurityValueKind.Boolean),
        Access("ForceLogoffWhenHourExpire", SecurityOptionGroup.NetworkSecurity, SecurityValueKind.Boolean),

        Registry("LmCompatibilityLevel", SecurityOptionGroup.NetworkSecurity, Lsa + "LmCompatibilityLevel", SecurityValueKind.Choice, choices: [0, 1, 2, 3, 4, 5]),
        Registry("NoLMHash", SecurityOptionGroup.NetworkSecurity, Lsa + "NoLMHash", SecurityValueKind.Boolean),
        Registry("NTLMMinClientSec", SecurityOptionGroup.NetworkSecurity, Msv + "NTLMMinClientSec", SecurityValueKind.Choice, choices: [0, 524288, 536870912, 537395200]),
        Registry("NTLMMinServerSec", SecurityOptionGroup.NetworkSecurity, Msv + "NTLMMinServerSec", SecurityValueKind.Choice, choices: [0, 524288, 536870912, 537395200]),
        Registry("RestrictSendingNTLMTraffic", SecurityOptionGroup.NetworkSecurity, Msv + "RestrictSendingNTLMTraffic", SecurityValueKind.Choice, choices: [0, 1, 2]),
        Registry("SupportedEncryptionTypes", SecurityOptionGroup.NetworkSecurity, PoliciesSystem + @"Kerberos\Parameters\SupportedEncryptionTypes", SecurityValueKind.Number),
        Registry("LDAPClientIntegrity", SecurityOptionGroup.NetworkSecurity, @"MACHINE\System\CurrentControlSet\Services\LDAP\LDAPClientIntegrity", SecurityValueKind.Choice, choices: [0, 1, 2]),

        Registry("RestrictAnonymousSAM", SecurityOptionGroup.NetworkAccess, Lsa + "RestrictAnonymousSAM", SecurityValueKind.Boolean),
        Registry("RestrictAnonymous", SecurityOptionGroup.NetworkAccess, Lsa + "RestrictAnonymous", SecurityValueKind.Boolean),
        Registry("EveryoneIncludesAnonymous", SecurityOptionGroup.NetworkAccess, Lsa + "EveryoneIncludesAnonymous", SecurityValueKind.Boolean),
        Registry("DisableDomainCreds", SecurityOptionGroup.NetworkAccess, Lsa + "DisableDomainCreds", SecurityValueKind.Boolean),

        Registry("ServerRequireSecuritySignature", SecurityOptionGroup.MicrosoftNetwork, Server + "RequireSecuritySignature", SecurityValueKind.Boolean),
        Registry("ServerEnableSecuritySignature", SecurityOptionGroup.MicrosoftNetwork, Server + "EnableSecuritySignature", SecurityValueKind.Boolean),
        Registry("WorkstationRequireSecuritySignature", SecurityOptionGroup.MicrosoftNetwork, Workstation + "RequireSecuritySignature", SecurityValueKind.Boolean),
        Registry("WorkstationEnablePlainTextPassword", SecurityOptionGroup.MicrosoftNetwork, Workstation + "EnablePlainTextPassword", SecurityValueKind.Boolean),

        Registry("LDAPServerIntegrity", SecurityOptionGroup.DomainController, @"MACHINE\System\CurrentControlSet\Services\NTDS\Parameters\LDAPServerIntegrity", SecurityValueKind.Choice, choices: [1, 2]),
        Registry("RefusePasswordChange", SecurityOptionGroup.DomainController, Netlogon + "RefusePasswordChange", SecurityValueKind.Boolean),

        Registry("RequireSignOrSeal", SecurityOptionGroup.DomainMember, Netlogon + "RequireSignOrSeal", SecurityValueKind.Boolean),
        Registry("SealSecureChannel", SecurityOptionGroup.DomainMember, Netlogon + "SealSecureChannel", SecurityValueKind.Boolean),
        Registry("SignSecureChannel", SecurityOptionGroup.DomainMember, Netlogon + "SignSecureChannel", SecurityValueKind.Boolean),
        Registry("RequireStrongKey", SecurityOptionGroup.DomainMember, Netlogon + "RequireStrongKey", SecurityValueKind.Boolean),
        Registry("DisablePasswordChange", SecurityOptionGroup.DomainMember, Netlogon + "DisablePasswordChange", SecurityValueKind.Boolean),
        Registry("MaximumPasswordAgeMachine", SecurityOptionGroup.DomainMember, Netlogon + "MaximumPasswordAge", SecurityValueKind.Number, maximum: 999),

        Registry("DisableCAD", SecurityOptionGroup.InteractiveLogon, PoliciesSystem + "DisableCAD", SecurityValueKind.Boolean),
        Registry("DontDisplayLastUserName", SecurityOptionGroup.InteractiveLogon, PoliciesSystem + "DontDisplayLastUserName", SecurityValueKind.Boolean),
        Registry("InactivityTimeoutSecs", SecurityOptionGroup.InteractiveLogon, PoliciesSystem + "InactivityTimeoutSecs", SecurityValueKind.Number, maximum: 599940),
        Registry("LegalNoticeCaption", SecurityOptionGroup.InteractiveLogon, PoliciesSystem + "LegalNoticeCaption", SecurityValueKind.Text, registryType: 1),
        Registry("LegalNoticeText", SecurityOptionGroup.InteractiveLogon, PoliciesSystem + "LegalNoticeText", SecurityValueKind.MultiText, registryType: 7),
        Registry("CachedLogonsCount", SecurityOptionGroup.InteractiveLogon, @"MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\CachedLogonsCount", SecurityValueKind.Text, registryType: 1),

        Registry("EnableLUA", SecurityOptionGroup.UserAccountControl, PoliciesSystem + "EnableLUA", SecurityValueKind.Boolean),
        Registry("ConsentPromptBehaviorAdmin", SecurityOptionGroup.UserAccountControl, PoliciesSystem + "ConsentPromptBehaviorAdmin", SecurityValueKind.Choice, choices: [0, 1, 2, 3, 4, 5]),
        Registry("ConsentPromptBehaviorUser", SecurityOptionGroup.UserAccountControl, PoliciesSystem + "ConsentPromptBehaviorUser", SecurityValueKind.Choice, choices: [0, 1, 3]),
        Registry("FilterAdministratorToken", SecurityOptionGroup.UserAccountControl, PoliciesSystem + "FilterAdministratorToken", SecurityValueKind.Boolean),
        Registry("PromptOnSecureDesktop", SecurityOptionGroup.UserAccountControl, PoliciesSystem + "PromptOnSecureDesktop", SecurityValueKind.Boolean),
        Registry("EnableInstallerDetection", SecurityOptionGroup.UserAccountControl, PoliciesSystem + "EnableInstallerDetection", SecurityValueKind.Boolean),

        Registry("SCENoApplyLegacyAuditPolicy", SecurityOptionGroup.Audit, Lsa + "SCENoApplyLegacyAuditPolicy", SecurityValueKind.Boolean),
        Registry("CrashOnAuditFail", SecurityOptionGroup.Audit, Lsa + "CrashOnAuditFail", SecurityValueKind.Boolean),
    ];

    /// <summary>
    /// "Inspekcja: wymuś ustawienia podkategorii zasad inspekcji…": with it the advanced audit policy (audit.csv) overrides
    /// the legacy categories of <c>[Event Audit]</c> (it is on by default since Windows Vista when audit.csv is applied).
    /// </summary>
    public static SecurityOptionDefinition ForceAuditSubcategories => Options.First(o => o.Id == "SCENoApplyLegacyAuditPolicy");

    /// <summary>The legacy audit categories of <c>[Event Audit]</c> (values 0–3).</summary>
    public static IReadOnlyList<string> AuditCategories { get; } =
    [
        "AuditAccountLogon",
        "AuditAccountManage",
        "AuditDSAccess",
        "AuditLogonEvents",
        "AuditObjectAccess",
        "AuditPolicyChange",
        "AuditPrivilegeUse",
        "AuditProcessTracking",
        "AuditSystemEvents",
    ];

    /// <summary>The advanced audit categories in GPMC's order.</summary>
    public static IReadOnlyList<string> AdvancedAuditCategories { get; } =
        ["AccountLogon", "AccountManagement", "DetailedTracking", "DsAccess", "LogonLogoff", "ObjectAccess", "PolicyChange", "PrivilegeUse", "System", "GlobalObjectAccess"];

    /// <summary>The 59 advanced audit subcategories (Global Object Access Auditing is a SACL, not a subcategory: kept as read).</summary>
    public static IReadOnlyList<AuditSubcategoryDefinition> AuditSubcategories { get; } =
    [
        Sub("CredentialValidation", "AccountLogon", 0x3F, "Credential Validation"),
        Sub("KerberosAuthenticationService", "AccountLogon", 0x42, "Kerberos Authentication Service"),
        Sub("KerberosServiceTicketOperations", "AccountLogon", 0x40, "Kerberos Service Ticket Operations"),
        Sub("OtherAccountLogonEvents", "AccountLogon", 0x41, "Other Account Logon Events"),
        Sub("ApplicationGroupManagement", "AccountManagement", 0x39, "Application Group Management"),
        Sub("ComputerAccountManagement", "AccountManagement", 0x36, "Computer Account Management"),
        Sub("DistributionGroupManagement", "AccountManagement", 0x38, "Distribution Group Management"),
        Sub("OtherAccountManagementEvents", "AccountManagement", 0x3A, "Other Account Management Events"),
        Sub("SecurityGroupManagement", "AccountManagement", 0x37, "Security Group Management"),
        Sub("UserAccountManagement", "AccountManagement", 0x35, "User Account Management"),
        Sub("DpapiActivity", "DetailedTracking", 0x2D, "DPAPI Activity"),
        Sub("PlugAndPlayEvents", "DetailedTracking", 0x48, "PNP Activity"),
        Sub("ProcessCreation", "DetailedTracking", 0x2B, "Process Creation"),
        Sub("ProcessTermination", "DetailedTracking", 0x2C, "Process Termination"),
        Sub("RpcEvents", "DetailedTracking", 0x2E, "RPC Events"),
        Sub("TokenRightAdjusted", "DetailedTracking", 0x4A, "Token Right Adjusted Events"),
        Sub("DetailedDirectoryServiceReplication", "DsAccess", 0x3E, "Detailed Directory Service Replication"),
        Sub("DirectoryServiceAccess", "DsAccess", 0x3B, "Directory Service Access"),
        Sub("DirectoryServiceChanges", "DsAccess", 0x3C, "Directory Service Changes"),
        Sub("DirectoryServiceReplication", "DsAccess", 0x3D, "Directory Service Replication"),
        Sub("AccountLockout", "LogonLogoff", 0x17, "Account Lockout"),
        Sub("UserDeviceClaims", "LogonLogoff", 0x47, "User / Device Claims"),
        Sub("GroupMembership", "LogonLogoff", 0x49, "Group Membership"),
        Sub("IpsecExtendedMode", "LogonLogoff", 0x1A, "IPsec Extended Mode"),
        Sub("IpsecMainMode", "LogonLogoff", 0x18, "IPsec Main Mode"),
        Sub("IpsecQuickMode", "LogonLogoff", 0x19, "IPsec Quick Mode"),
        Sub("Logoff", "LogonLogoff", 0x16, "Logoff"),
        Sub("Logon", "LogonLogoff", 0x15, "Logon"),
        Sub("NetworkPolicyServer", "LogonLogoff", 0x43, "Network Policy Server"),
        Sub("OtherLogonLogoffEvents", "LogonLogoff", 0x1C, "Other Logon/Logoff Events"),
        Sub("SpecialLogon", "LogonLogoff", 0x1B, "Special Logon"),
        Sub("ApplicationGenerated", "ObjectAccess", 0x22, "Application Generated"),
        Sub("CertificationServices", "ObjectAccess", 0x21, "Certification Services"),
        Sub("DetailedFileShare", "ObjectAccess", 0x44, "Detailed File Share"),
        Sub("FileShare", "ObjectAccess", 0x24, "File Share"),
        Sub("FileSystem", "ObjectAccess", 0x1D, "File System"),
        Sub("FilteringPlatformConnection", "ObjectAccess", 0x26, "Filtering Platform Connection"),
        Sub("FilteringPlatformPacketDrop", "ObjectAccess", 0x25, "Filtering Platform Packet Drop"),
        Sub("HandleManipulation", "ObjectAccess", 0x23, "Handle Manipulation"),
        Sub("KernelObject", "ObjectAccess", 0x1F, "Kernel Object"),
        Sub("OtherObjectAccessEvents", "ObjectAccess", 0x27, "Other Object Access Events"),
        Sub("Registry", "ObjectAccess", 0x1E, "Registry"),
        Sub("RemovableStorage", "ObjectAccess", 0x45, "Removable Storage"),
        Sub("Sam", "ObjectAccess", 0x20, "SAM"),
        Sub("CentralAccessPolicyStaging", "ObjectAccess", 0x46, "Central Access Policy Staging"),
        Sub("AuditPolicyChange", "PolicyChange", 0x2F, "Audit Policy Change"),
        Sub("AuthenticationPolicyChange", "PolicyChange", 0x30, "Authentication Policy Change"),
        Sub("AuthorizationPolicyChange", "PolicyChange", 0x31, "Authorization Policy Change"),
        Sub("FilteringPlatformPolicyChange", "PolicyChange", 0x33, "Filtering Platform Policy Change"),
        Sub("MpssvcRuleLevelPolicyChange", "PolicyChange", 0x32, "MPSSVC Rule-Level Policy Change"),
        Sub("OtherPolicyChangeEvents", "PolicyChange", 0x34, "Other Policy Change Events"),
        Sub("NonSensitivePrivilegeUse", "PrivilegeUse", 0x29, "Non Sensitive Privilege Use"),
        Sub("OtherPrivilegeUseEvents", "PrivilegeUse", 0x2A, "Other Privilege Use Events"),
        Sub("SensitivePrivilegeUse", "PrivilegeUse", 0x28, "Sensitive Privilege Use"),
        Sub("IpsecDriver", "System", 0x13, "IPsec Driver"),
        Sub("OtherSystemEvents", "System", 0x14, "Other System Events"),
        Sub("SecurityStateChange", "System", 0x10, "Security State Change"),
        Sub("SecuritySystemExtension", "System", 0x11, "Security System Extension"),
        Sub("SystemIntegrity", "System", 0x12, "System Integrity"),
    ];

    /// <summary>The service start modes the editor writes (0 boot and 1 system are kept as read).</summary>
    public static IReadOnlyList<int> ServiceStartModes { get; } = [2, 3, 4];

    /// <summary>The definition of a <c>[System Access]</c>/<c>[Kerberos Policy]</c> key or registry path, null when not listed.</summary>
    public static SecurityOptionDefinition? FindOption(string section, string key)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        return Options.FirstOrDefault(o => o.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && o.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The subcategory of a GUID, null when not listed.</summary>
    public static AuditSubcategoryDefinition? FindSubcategory(Guid guid) => AuditSubcategories.FirstOrDefault(s => s.Guid == guid);

    private static SecurityOptionDefinition Access(string key, SecurityOptionGroup group, SecurityValueKind kind, long minimum = 0, long maximum = 1) =>
        new(key, group, SecurityOptionLocation.SystemAccess, key, kind, Minimum: minimum, Maximum: kind == SecurityValueKind.Boolean ? 1 : maximum);

    private static SecurityOptionDefinition Registry(string id, SecurityOptionGroup group, string path, SecurityValueKind kind,
        IReadOnlyList<long>? choices = null, int registryType = 4, long maximum = uint.MaxValue) =>
        new(id, group, SecurityOptionLocation.RegistryValue, path, kind, registryType, 0, kind == SecurityValueKind.Boolean ? 1 : maximum, choices);

    private static AuditSubcategoryDefinition Sub(string id, string category, int low, string name) =>
        new(id, category, new Guid(0x0CCE9200u + (uint)low, 0x69AE, 0x11D9, 0xBE, 0xD3, 0x50, 0x50, 0x54, 0x50, 0x30, 0x30), name);
}
