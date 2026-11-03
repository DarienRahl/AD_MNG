namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>The severity, area and rule group of a finding code.</summary>
/// <param name="Code">The code.</param>
/// <param name="Severity">Its severity.</param>
/// <param name="Area">Its area.</param>
/// <param name="Group">The data its rule needs.</param>
public sealed record GpoFindingDefinition(string Code, GpoFindingSeverity Severity, GpoFindingArea Area, GpoRuleGroup Group);

/// <summary>
/// The codes of the GPO diagnostics (design note docs/dev/k-computers-and-gpo.md 20.2) - stable ids the UI localizes
/// (<c>GpoFinding.&lt;Code&gt;</c>). The comment of each code lists its <see cref="GpoFinding.Arguments"/> in order.
/// </summary>
public static class GpoFindingCodes
{
    /// <summary>No link in this domain. Arguments: none.</summary>
    public const string GpoNotLinked = nameof(GpoNotLinked);

    /// <summary>Every link disabled; the container is the first link, the fix enables it. Arguments: number of links.</summary>
    public const string GpoLinksAllDisabled = nameof(GpoLinksAllDisabled);

    /// <summary>A gPLink names a GPO of this domain that does not exist (GpoId: that GUID). Arguments: the GUID in braces.</summary>
    public const string LinkToMissingGpo = nameof(LinkToMissingGpo);

    /// <summary>gPLink entries that cannot be read or name GPOs of other domains. Arguments: unreadable entries, other domains' entries.</summary>
    public const string LinkMalformed = nameof(LinkMalformed);

    /// <summary>The same GPO more than once in one gPLink. Arguments: number of entries of the GPO.</summary>
    public const string DuplicateLink = nameof(DuplicateLink);

    /// <summary>Linked (an enabled link) with the status "all settings disabled". Arguments: none.</summary>
    public const string LinkedAllSettingsDisabled = nameof(LinkedAllSettingsDisabled);

    /// <summary>One part disabled while its version is above 0. Arguments: part (<c>User</c>/<c>Computer</c>), its version.</summary>
    public const string PartDisabledWithSettings = nameof(PartDisabledWithSettings);

    /// <summary>A linked GPO's part enabled with version 0 while the other part has settings. Arguments: part (<c>User</c>/<c>Computer</c>).</summary>
    public const string PartEnabledWithoutSettings = nameof(PartEnabledWithoutSettings);

    /// <summary>Both versions 0, linked. Arguments: none.</summary>
    public const string EmptyGpoLinked = nameof(EmptyGpoLinked);

    /// <summary>User settings, the links reach no users and no loopback reaches the GPO. Arguments: none.</summary>
    public const string UserSettingsWithoutUsers = nameof(UserSettingsWithoutUsers);

    /// <summary>Computer settings, the links reach no computers. Arguments: none.</summary>
    public const string ComputerSettingsWithoutComputers = nameof(ComputerSettingsWithoutComputers);

    /// <summary>Password, lockout or Kerberos policy in a GPO without an enabled link to the domain head. Arguments: none (SettingKey: the first such key).</summary>
    public const string AccountPolicyBelowDomain = nameof(AccountPolicyBelowDomain);

    /// <summary>Loopback set in a GPO whose links reach no computers. Arguments: mode (<c>Merge</c>/<c>Replace</c>).</summary>
    public const string LoopbackWithoutComputers = nameof(LoopbackWithoutComputers);

    /// <summary>A linked GPO nobody may apply (no Apply Group Policy left). Arguments: none.</summary>
    public const string NoApplyRight = nameof(NoApplyRight);

    /// <summary>User settings of a linked GPO that computers cannot read (MS16-072). Arguments: none.</summary>
    public const string Ms16072ComputersCannotRead = nameof(Ms16072ComputersCannotRead);

    /// <summary>The GPO links a WMI filter that does not exist. Arguments: the filter id.</summary>
    public const string WmiFilterMissing = nameof(WmiFilterMissing);

    /// <summary>A WMI filter no GPO uses (the fix carries its DN). Arguments: filter name, filter id.</summary>
    public const string WmiFilterUnused = nameof(WmiFilterUnused);

    /// <summary>A deny ACE in the GPO's DACL (one finding per trustee). Arguments: the trustee SID.</summary>
    public const string DenyInSecurityFiltering = nameof(DenyInSecurityFiltering);

    /// <summary>A domain SID in the GPO's permissions that resolves to nothing. Arguments: the SID.</summary>
    public const string OrphanedTrustee = nameof(OrphanedTrustee);

    /// <summary>Everyone, Authenticated Users, Domain Users (or another broad group) can edit the GPO. Arguments: the trustee SID.</summary>
    public const string BroadEditRights = nameof(BroadEditRights);

    /// <summary>AD and GPT.INI versions differ. Arguments: AD user, AD computer, GPT.INI user, GPT.INI computer.</summary>
    public const string VersionMismatch = nameof(VersionMismatch);

    /// <summary>No GPO folder (or no GPT.INI) on SYSVOL. Arguments: <c>Folder</c> (the folder is missing) or <c>GptIni</c> (GPT.INI was not found).</summary>
    public const string SysvolFolderMissing = nameof(SysvolFolderMissing);

    /// <summary>The folder ACL differs from the GPO's permissions. Arguments: none.</summary>
    public const string FolderSecurityMismatch = nameof(FolderSecurityMismatch);

    /// <summary>Files whose client-side extension is not listed (clients ignore them). Arguments: the file paths, comma separated.</summary>
    public const string ExtensionNamesMissing = nameof(ExtensionNamesMissing);

    /// <summary>A settings file that cannot be parsed (SettingKey <c>File/&lt;path&gt;</c>). Arguments: the path.</summary>
    public const string UnreadableFile = nameof(UnreadableFile);

    /// <summary><c>*.admng-*.tmp</c> left by an interrupted save. Arguments: their number.</summary>
    public const string Leftovers = nameof(Leftovers);

    /// <summary>gPCFileSysPath points outside the domain's SYSVOL. Arguments: the path as stored.</summary>
    public const string PathRedirected = nameof(PathRedirected);

    /// <summary>One local group in Restricted Groups (Members) and in GPP Local Users and Groups of the same GPO. Arguments: the group (SID or name).</summary>
    public const string RestrictedAndPreferenceSameGroup = nameof(RestrictedAndPreferenceSameGroup);

    /// <summary>Administrators' Members defined without Administrator or Domain Admins. Arguments: none.</summary>
    public const string RestrictedAdministratorsNoAdmins = nameof(RestrictedAdministratorsNoAdmins);

    /// <summary>[Event Audit] and audit.csv in one GPO without forcing the subcategories. Arguments: none.</summary>
    public const string LegacyAndAdvancedAudit = nameof(LegacyAndAdvancedAudit);

    /// <summary>Minimum password length above 14 without the relax option. Arguments: the length.</summary>
    public const string PasswordLengthNeedsRelax = nameof(PasswordLengthNeedsRelax);

    /// <summary>A principal written as a name, not a SID. Arguments: the name.</summary>
    public const string TypedPrincipalName = nameof(TypedPrincipalName);

    /// <summary>A domain SID in rights or groups that resolves to nothing. Arguments: the SID.</summary>
    public const string UnresolvedPrincipal = nameof(UnresolvedPrincipal);

    /// <summary>A deny-logon right for Everyone, Authenticated Users, Administrators or Domain Users. Arguments: the principal.</summary>
    public const string DenyLogonBroadGroup = nameof(DenyLogonBroadGroup);

    /// <summary>
    /// GPOs reaching one container set a setting differently (GpoId: the winner; <see cref="GpoFinding.RelatedGpoIds"/>: the
    /// losers). Arguments: winner name, loser names (comma separated), winner value, loser values (<c>"; "</c> separated; both
    /// empty for a preference item, whose properties are compared together).
    /// </summary>
    public const string ConflictingSetting = nameof(ConflictingSetting);

    /// <summary>
    /// The same value from several GPOs on one path (GpoId: the winner; <see cref="GpoFinding.RelatedGpoIds"/>: the others).
    /// Arguments: winner name, other names (comma separated), the value (empty for a preference item).
    /// </summary>
    public const string RedundantSetting = nameof(RedundantSetting);

    /// <summary><c>cpassword</c> in preferences (MS14-025; SettingKey: the masked key). Arguments: the file path.</summary>
    public const string GppPassword = nameof(GppPassword);

    /// <summary>A sensitive user right for a broad group. Arguments: the principal.</summary>
    public const string DangerousRightBroadGroup = nameof(DangerousRightBroadGroup);

    /// <summary>A broad group made a member of a privileged local group. Arguments: the principal, the local group's SID.</summary>
    public const string BroadGroupInPrivilegedLocalGroup = nameof(BroadGroupInPrivilegedLocalGroup);

    /// <summary><c>LmCompatibilityLevel</c> below 3. Arguments: the value.</summary>
    public const string LmCompatibilityWeak = nameof(LmCompatibilityWeak);

    /// <summary><c>NoLMHash</c> = 0. Arguments: none.</summary>
    public const string LmHashStored = nameof(LmHashStored);

    /// <summary>SMB server signing not required in a GPO that reaches the DCs. Arguments: none.</summary>
    public const string SmbSigningNotRequired = nameof(SmbSigningNotRequired);

    /// <summary><c>LDAPServerIntegrity</c> = 1 (or 0) in a GPO that reaches the DCs. Arguments: the value.</summary>
    public const string LdapSigningNotRequired = nameof(LdapSigningNotRequired);

    /// <summary>Anonymous SAM enumeration, anonymous name lookup or Everyone includes anonymous. Arguments: the option id (<c>RestrictAnonymousSAM</c>, <c>LSAAnonymousNameLookup</c>, <c>EveryoneIncludesAnonymous</c>).</summary>
    public const string AnonymousAccessAllowed = nameof(AnonymousAccessAllowed);

    /// <summary><c>ClearTextPassword</c> = 1. Arguments: none.</summary>
    public const string ReversibleEncryption = nameof(ReversibleEncryption);

    /// <summary><c>EnableGuestAccount</c> = 1. Arguments: none.</summary>
    public const string GuestEnabled = nameof(GuestEnabled);

    /// <summary><c>EnableLUA</c> = 0. Arguments: none.</summary>
    public const string UacDisabled = nameof(UacDisabled);

    /// <summary><c>EnablePlainTextPassword</c> = 1 (SMB client). Arguments: none.</summary>
    public const string PlainTextSmbPassword = nameof(PlainTextSmbPassword);

    /// <summary>A GPO linked to the domain head: minimum length below 8, complexity off or lockout threshold 0. Arguments: the option id, the value.</summary>
    public const string WeakDomainPasswordPolicy = nameof(WeakDomainPasswordPolicy);

    /// <summary>Every code with its severity, area and rule group, in the order of the design note's table.</summary>
    public static IReadOnlyList<GpoFindingDefinition> All { get; } =
    [
        new(GpoNotLinked, GpoFindingSeverity.Info, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(GpoLinksAllDisabled, GpoFindingSeverity.Low, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(LinkToMissingGpo, GpoFindingSeverity.Medium, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(LinkMalformed, GpoFindingSeverity.Low, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(DuplicateLink, GpoFindingSeverity.Low, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(LinkedAllSettingsDisabled, GpoFindingSeverity.Medium, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(PartDisabledWithSettings, GpoFindingSeverity.Medium, GpoFindingArea.Scope, GpoRuleGroup.Directory),
        new(PartEnabledWithoutSettings, GpoFindingSeverity.Info, GpoFindingArea.Scope, GpoRuleGroup.Directory),
        new(EmptyGpoLinked, GpoFindingSeverity.Low, GpoFindingArea.Links, GpoRuleGroup.Directory),
        new(UserSettingsWithoutUsers, GpoFindingSeverity.Medium, GpoFindingArea.Scope, GpoRuleGroup.ObjectCounts),
        new(ComputerSettingsWithoutComputers, GpoFindingSeverity.Medium, GpoFindingArea.Scope, GpoRuleGroup.ObjectCounts),
        new(AccountPolicyBelowDomain, GpoFindingSeverity.Medium, GpoFindingArea.Scope, GpoRuleGroup.Folders),
        new(LoopbackWithoutComputers, GpoFindingSeverity.Low, GpoFindingArea.Scope, GpoRuleGroup.ObjectCounts),
        new(NoApplyRight, GpoFindingSeverity.High, GpoFindingArea.Filtering, GpoRuleGroup.Directory),
        new(Ms16072ComputersCannotRead, GpoFindingSeverity.High, GpoFindingArea.Filtering, GpoRuleGroup.Directory),
        new(WmiFilterMissing, GpoFindingSeverity.Medium, GpoFindingArea.Filtering, GpoRuleGroup.Directory),
        new(WmiFilterUnused, GpoFindingSeverity.Info, GpoFindingArea.Filtering, GpoRuleGroup.Directory),
        new(DenyInSecurityFiltering, GpoFindingSeverity.Info, GpoFindingArea.Filtering, GpoRuleGroup.Directory),
        new(OrphanedTrustee, GpoFindingSeverity.Low, GpoFindingArea.Filtering, GpoRuleGroup.Principals),
        new(BroadEditRights, GpoFindingSeverity.High, GpoFindingArea.Security, GpoRuleGroup.Directory),
        new(VersionMismatch, GpoFindingSeverity.Medium, GpoFindingArea.Versions, GpoRuleGroup.SysvolVersions),
        new(SysvolFolderMissing, GpoFindingSeverity.High, GpoFindingArea.Folder, GpoRuleGroup.SysvolVersions),
        new(FolderSecurityMismatch, GpoFindingSeverity.Medium, GpoFindingArea.Folder, GpoRuleGroup.Folders),
        new(ExtensionNamesMissing, GpoFindingSeverity.Medium, GpoFindingArea.Folder, GpoRuleGroup.Folders),
        new(UnreadableFile, GpoFindingSeverity.Medium, GpoFindingArea.Folder, GpoRuleGroup.Folders),
        new(Leftovers, GpoFindingSeverity.Low, GpoFindingArea.Folder, GpoRuleGroup.Folders),
        new(PathRedirected, GpoFindingSeverity.High, GpoFindingArea.Folder, GpoRuleGroup.Directory),
        new(RestrictedAndPreferenceSameGroup, GpoFindingSeverity.Medium, GpoFindingArea.Content, GpoRuleGroup.Folders),
        new(RestrictedAdministratorsNoAdmins, GpoFindingSeverity.High, GpoFindingArea.Content, GpoRuleGroup.Folders),
        new(LegacyAndAdvancedAudit, GpoFindingSeverity.Medium, GpoFindingArea.Content, GpoRuleGroup.Folders),
        new(PasswordLengthNeedsRelax, GpoFindingSeverity.Medium, GpoFindingArea.Content, GpoRuleGroup.Folders),
        new(TypedPrincipalName, GpoFindingSeverity.Info, GpoFindingArea.Content, GpoRuleGroup.Folders),
        new(UnresolvedPrincipal, GpoFindingSeverity.Low, GpoFindingArea.Content, GpoRuleGroup.Principals),
        new(DenyLogonBroadGroup, GpoFindingSeverity.High, GpoFindingArea.Content, GpoRuleGroup.Folders),
        new(ConflictingSetting, GpoFindingSeverity.Info, GpoFindingArea.Conflict, GpoRuleGroup.Conflicts),
        new(RedundantSetting, GpoFindingSeverity.Info, GpoFindingArea.Conflict, GpoRuleGroup.Conflicts),
        new(GppPassword, GpoFindingSeverity.Critical, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(DangerousRightBroadGroup, GpoFindingSeverity.High, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(BroadGroupInPrivilegedLocalGroup, GpoFindingSeverity.High, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(LmCompatibilityWeak, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(LmHashStored, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(SmbSigningNotRequired, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(LdapSigningNotRequired, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(AnonymousAccessAllowed, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(ReversibleEncryption, GpoFindingSeverity.High, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(GuestEnabled, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(UacDisabled, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(PlainTextSmbPassword, GpoFindingSeverity.High, GpoFindingArea.Security, GpoRuleGroup.Folders),
        new(WeakDomainPasswordPolicy, GpoFindingSeverity.Medium, GpoFindingArea.Security, GpoRuleGroup.Folders),
    ];

    private static readonly Dictionary<string, GpoFindingDefinition> ByCode = All.ToDictionary(d => d.Code, StringComparer.Ordinal);

    /// <summary>The definition of a code.</summary>
    /// <exception cref="KeyNotFoundException">Not a code of this class.</exception>
    public static GpoFindingDefinition Find(string code) =>
        ByCode.TryGetValue(code ?? throw new ArgumentNullException(nameof(code)), out var definition)
            ? definition
            : throw new KeyNotFoundException("Unknown GPO finding code.");
}
