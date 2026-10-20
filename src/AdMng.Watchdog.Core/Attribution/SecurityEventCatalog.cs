using System.Collections.Frozen;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Security event ids per watch-event kind and the ingestion set (section 11.2). Account-management events (47xx) are
/// matched by the strong key <c>TargetSid</c> (membership events: group SID + <c>MemberSid</c>), directory-service
/// events (5136–5141) by <c>ObjectGUID</c>. 4624/4771/4776/4625 are queried on demand, bounded (11.4 #6), from the DC
/// logs; in collector mode (Linux, stage B design 7.7) the DCs forward them and they are stored reduced to
/// <see cref="LogonRetainedFields"/> for <c>Retention.LogonRecordsHours</c>.
/// </summary>
public static class SecurityEventCatalog
{
    public const int UserCreated = 4720;
    public const int UserEnabled = 4722;
    public const int PasswordChangeAttempt = 4723;
    public const int PasswordReset = 4724;
    public const int UserDisabled = 4725;
    public const int UserDeleted = 4726;
    public const int UserChanged = 4738;
    public const int AccountLockedOut = 4740;
    public const int ComputerCreated = 4741;
    public const int ComputerChanged = 4742;
    public const int ComputerDeleted = 4743;
    public const int GroupTypeChanged = 4764;
    public const int AccountUnlocked = 4767;
    public const int AccountNameChanged = 4781;
    public const int DsrmPasswordSet = 4794;
    public const int AuditPolicyChanged = 4719;
    public const int ObjectModified = 5136;
    public const int ObjectCreated = 5137;
    public const int ObjectUndeleted = 5138;
    public const int ObjectMoved = 5139;
    public const int ObjectDeleted = 5141;
    public const int EventLogServiceShutdown = 1100;
    public const int SecurityLogCleared = 1102;
    public const int SecurityLogFull = 1104;
    public const int LogonSucceeded = 4624;
    public const int LogonFailed = 4625;
    public const int KerberosPreAuthFailed = 4771;
    public const int CredentialValidation = 4776;

    /// <summary>Provider of every Security-auditing record.</summary>
    public const string SecurityAuditingProvider = "Microsoft-Windows-Security-Auditing";

    /// <summary>Provider of the event-log service records 1100/1102/1104.</summary>
    public const string EventLogProvider = "Microsoft-Windows-Eventlog";

    /// <summary>Every id ingested from the DC logs (11.2), in the specification's order.</summary>
    public static IReadOnlyList<int> IngestionSet { get; } =
    [
        4720, 4722, 4723, 4724, 4725, 4726, 4738, 4740, 4767, 4781, 4765, 4766, 4794, 4741, 4742, 4743, 4727, 4728, 4729,
        4730, 4731, 4732, 4733, 4734, 4735, 4737, 4754, 4755, 4756, 4757, 4758, 4764, 4744, 4745, 4746, 4747, 4748, 4749,
        4750, 4751, 4752, 4753, 4759, 4760, 4761, 4762, 4763, 4719, 5136, 5137, 5138, 5139, 5141, 1102, 1104, 1100,
    ];

    /// <summary>Ids that are only queried on demand (logon source, lockout sources, spraying) – never ingested from a DC log
    /// read over RPC; in collector mode they are the logon and failure records of <see cref="LogonRecordCodes"/>.</summary>
    public static IReadOnlyList<int> OnDemandSet { get; } = [LogonSucceeded, LogonFailed, KerberosPreAuthFailed, CredentialValidation];

    /// <summary>Collector mode (stage B design 7.7): the logon and failure records stored for
    /// <c>Retention.LogonRecordsHours</c> and limited by <c>Watchdog:Collector:MaxLogonRecordsPerDcPerMinute</c>.</summary>
    public static IReadOnlyList<int> LogonRecordCodes => OnDemandSet;

    /// <summary>True for 4624, 4625, 4771 and 4776 (<see cref="LogonRecordCodes"/>).</summary>
    public static bool IsLogonRecord(int eventCode) =>
        eventCode is LogonSucceeded or LogonFailed or KerberosPreAuthFailed or CredentialValidation;

    /// <summary>The EventData fields a stored logon or failure record keeps (stage B design 7.7); every other field is
    /// dropped at ingestion.</summary>
    public static IReadOnlyDictionary<int, IReadOnlySet<string>> LogonRetainedFields { get; } = new Dictionary<int, IReadOnlySet<string>>
    {
        [LogonSucceeded] = new[]
        {
            "TargetUserSid", "TargetUserName", "TargetDomainName", "TargetLogonId", "LogonType", "IpAddress", "WorkstationName",
            "AuthenticationPackageName",
        }.ToFrozenSet(StringComparer.Ordinal),
        [LogonFailed] = new[]
        {
            "TargetUserName", "TargetDomainName", "Status", "SubStatus", "LogonType", "IpAddress", "WorkstationName",
        }.ToFrozenSet(StringComparer.Ordinal),
        [KerberosPreAuthFailed] = new[] { "TargetUserName", "TargetSid", "Status", "IpAddress" }.ToFrozenSet(StringComparer.Ordinal),
        [CredentialValidation] = new[] { "TargetUserName", "Workstation", "Status" }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary();

    /// <summary>The directory-service change events (keyed by ObjectGUID).</summary>
    public static IReadOnlyList<int> DirectoryServiceCodes { get; } = [ObjectModified, ObjectCreated, ObjectUndeleted, ObjectMoved, ObjectDeleted];

    /// <summary>Records written by the event-log service (provider <see cref="EventLogProvider"/>).</summary>
    public static IReadOnlyList<int> EventLogServiceCodes { get; } = [EventLogServiceShutdown, SecurityLogCleared, SecurityLogFull];

    public static IReadOnlyList<int> GroupCreatedCodes { get; } = [4727, 4731, 4754, 4744, 4749, 4759];
    public static IReadOnlyList<int> GroupDeletedCodes { get; } = [4730, 4734, 4758, 4748, 4753, 4763];
    public static IReadOnlyList<int> GroupChangedCodes { get; } = [4735, 4737, 4755, 4745, 4750, 4760];
    public static IReadOnlyList<int> MemberAddedCodes { get; } = [4728, 4732, 4756, 4751, 4746, 4761];
    public static IReadOnlyList<int> MemberRemovedCodes { get; } = [4729, 4733, 4757, 4752, 4747, 4762];
    public static IReadOnlyList<int> SidHistoryCodes { get; } = [4765, 4766];

    /// <summary>SAM field (4720/4738/4741/4742 Data name) → LDAP attribute (11.2).</summary>
    public static IReadOnlyDictionary<string, string> SamFieldAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SamAccountName"] = "sAMAccountName",
        ["DisplayName"] = "displayName",
        ["UserPrincipalName"] = "userPrincipalName",
        ["HomeDirectory"] = "homeDirectory",
        ["HomePath"] = "homeDrive",
        ["ScriptPath"] = "scriptPath",
        ["ProfilePath"] = "profilePath",
        ["UserWorkstations"] = "userWorkstations",
        ["PasswordLastSet"] = "pwdLastSet",
        ["AccountExpires"] = "accountExpires",
        ["PrimaryGroupId"] = "primaryGroupID",
        ["AllowedToDelegateTo"] = "msDS-AllowedToDelegateTo",
        ["OldUacValue"] = "userAccountControl",
        ["NewUacValue"] = "userAccountControl",
        ["SidHistory"] = "sIDHistory",
        ["LogonHours"] = "logonHours",
        ["DnsHostName"] = "dNSHostName",
        ["ServicePrincipalNames"] = "servicePrincipalName",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>EventData fields kept after parse-time reduction (11.1): the indexed keys of 16.2, the subject, the
    /// fields correlation and the event-log-only detections read, and the SAM fields.</summary>
    public static IReadOnlySet<string> RetainedFields { get; } = new[]
    {
        "TargetSid", "TargetUserName", "TargetDomainName", "SubjectUserSid", "SubjectUserName", "SubjectDomainName", "SubjectLogonId",
        "MemberSid", "MemberName", "ObjectGUID", "ObjectDN", "ObjectClass", "OpCorrelationID", "AttributeLDAPDisplayName",
        "AttributeValue", "OperationType", "OldObjectDN", "NewObjectDN", "OldTargetUserName", "NewTargetUserName",
        "SourceSid", "SourceUserName", SecurityEventParser.CallerComputerName, "CategoryId", "SubcategoryId", "SubcategoryGuid",
        "AuditPolicyChanges", "Workstation", "Status",
    }.Concat(SamFieldAttributes.Keys).ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<WatchEventKind> NotAttributable = new[]
    {
        WatchEventKind.AccountExpiring, WatchEventKind.AccountExpired, WatchEventKind.PasswordExpiring, WatchEventKind.MassLockout,
        WatchEventKind.MassDeletion, WatchEventKind.PasswordSprayingSuspected, WatchEventKind.ObjectAppeared,
        WatchEventKind.ObjectDisappeared, WatchEventKind.LockoutExpired, WatchEventKind.AccountLockedOut, WatchEventKind.Test,
        WatchEventKind.WatchdogConfigurationChanged, WatchEventKind.SecurityLogCleared, WatchEventKind.AuditPolicyChanged,
        WatchEventKind.DsrmPasswordChanged,
    }.Concat(EventKindCatalog.All.Where(k => k.IsHealth).Select(k => k.Kind)).ToFrozenSet();

    /// <summary>True when the kind gets an actor from the correlation (11.2): false for expiry, bursts, health,
    /// appeared/disappeared, lockout expiry, lockouts (sources only), and kinds resolved at creation
    /// (WatchdogConfigurationChanged, the event-log-only kinds). PrivilegeGranted/Revoked are attributable but inherit
    /// the attribution of their cause.</summary>
    public static bool IsAttributable(WatchEventKind kind) => !NotAttributable.Contains(kind);

    /// <summary>True for PrivilegeGranted/PrivilegeRevoked (attribution inherited from the membership change, primary
    /// group change or restore that caused it) and for GroupPolicySettingsChanged/GroupPolicyPathRedirected (inherited
    /// from the GroupPolicyChanged event of the same <c>versionNumber</c> / <c>gPCFileSysPath</c> change – stage B design
    /// 8.7). GroupPolicyContentOutOfBand is created final (Unknown, <c>SYSVOL_NOT_AUDITED</c>).</summary>
    public static bool InheritsAttribution(WatchEventKind kind) =>
        kind is WatchEventKind.PrivilegeGranted or WatchEventKind.PrivilegeRevoked
            or WatchEventKind.GroupPolicySettingsChanged or WatchEventKind.GroupPolicyPathRedirected;

    /// <summary>The account-management ids (keyed by TargetSid) that can attribute <paramref name="kind"/> on an
    /// object of <paramref name="objectKind"/>.</summary>
    public static IReadOnlyList<int> AccountCodes(WatchEventKind kind, WatchObjectKind objectKind)
    {
        var account = objectKind switch
        {
            WatchObjectKind.Computer or WatchObjectKind.ServiceAccount => (IReadOnlyList<int>)[ComputerChanged],
            WatchObjectKind.Group => GroupChangedCodes,
            _ => [UserChanged],
        };
        return kind switch
        {
            WatchEventKind.UserCreated => [UserCreated],
            WatchEventKind.ComputerCreated or WatchEventKind.ServiceAccountCreated => [ComputerCreated],
            WatchEventKind.GroupCreated => GroupCreatedCodes,
            WatchEventKind.ObjectDeleted => objectKind switch
            {
                WatchObjectKind.User => [UserDeleted],
                WatchObjectKind.Computer or WatchObjectKind.ServiceAccount => [ComputerDeleted],
                WatchObjectKind.Group => GroupDeletedCodes,
                _ => [],
            },
            WatchEventKind.AccountDisabled => [UserDisabled, .. account],
            WatchEventKind.AccountEnabled => [UserEnabled, .. account],
            WatchEventKind.AccountUnlocked => [AccountUnlocked],
            WatchEventKind.PasswordChanged => [PasswordReset, PasswordChangeAttempt],
            WatchEventKind.AccountNameChanged => [AccountNameChanged, .. account],
            WatchEventKind.SidHistoryChanged => [.. SidHistoryCodes, .. account],
            WatchEventKind.GroupMemberAdded => MemberAddedCodes,
            WatchEventKind.GroupMemberRemoved => MemberRemovedCodes,
            WatchEventKind.GroupTypeChanged => [GroupTypeChanged],
            WatchEventKind.PasswordAgeReset or WatchEventKind.PasswordMustChangeSet or WatchEventKind.AccountExpirationChanged
                or WatchEventKind.AttributesChanged or WatchEventKind.AccountFlagsChanged or WatchEventKind.DelegationChanged
                or WatchEventKind.PrimaryGroupChanged or WatchEventKind.ServicePrincipalNameAdded => account,
            _ => [],
        };
    }

    /// <summary>The directory-service ids (keyed by ObjectGUID) that can attribute <paramref name="kind"/>.</summary>
    public static IReadOnlyList<int> DirectoryCodes(WatchEventKind kind) => kind switch
    {
        _ when !IsAttributable(kind) || InheritsAttribution(kind) => [],
        WatchEventKind.UserCreated or WatchEventKind.ComputerCreated or WatchEventKind.GroupCreated
            or WatchEventKind.OrganizationalUnitCreated or WatchEventKind.ContactCreated or WatchEventKind.ServiceAccountCreated
            or WatchEventKind.DomainControllerAdded => [ObjectCreated],
        WatchEventKind.ObjectDeleted or WatchEventKind.DomainControllerRemoved => [ObjectDeleted],
        WatchEventKind.ObjectRestored => [ObjectUndeleted],
        WatchEventKind.ObjectMoved or WatchEventKind.ObjectRenamed => [ObjectMoved],
        WatchEventKind.PasswordChanged => [],
        WatchEventKind.GroupPolicyChanged or WatchEventKind.TrustChanged or WatchEventKind.PasswordSettingsChanged
            or WatchEventKind.CertificateTemplateChanged => [ObjectModified, ObjectCreated, ObjectDeleted],
        WatchEventKind.SchemaChanged => [ObjectModified, ObjectCreated],
        _ => [ObjectModified],
    };

    /// <summary>Every id that can attribute <paramref name="kind"/> (account ids first).</summary>
    public static IReadOnlyList<int> CandidateCodes(WatchEventKind kind, WatchObjectKind objectKind) =>
        [.. AccountCodes(kind, objectKind), .. DirectoryCodes(kind)];

    /// <summary>The LDAP attributes a kind concerns when its event carries no attribute change (lockout, enable/disable,
    /// password, membership) – used to confirm 5136 candidates.</summary>
    public static IReadOnlyList<string> ImpliedAttributes(WatchEventKind kind) => kind switch
    {
        WatchEventKind.AccountDisabled or WatchEventKind.AccountEnabled or WatchEventKind.AccountFlagsChanged => ["userAccountControl"],
        WatchEventKind.AccountUnlocked or WatchEventKind.AccountLockedOut => ["lockoutTime"],
        WatchEventKind.PasswordChanged or WatchEventKind.PasswordAgeReset or WatchEventKind.PasswordMustChangeSet => ["pwdLastSet"],
        WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved => ["member"],
        WatchEventKind.GroupTypeChanged => ["groupType"],
        WatchEventKind.AccountExpirationChanged => ["accountExpires"],
        WatchEventKind.PrimaryGroupChanged => ["primaryGroupID"],
        WatchEventKind.SidHistoryChanged => ["sIDHistory"],
        WatchEventKind.AdminCountSet => ["adminCount"],
        WatchEventKind.KeyCredentialChanged => ["msDS-KeyCredentialLink"],
        WatchEventKind.CertificateMappingChanged => ["altSecurityIdentities"],
        WatchEventKind.ManagedPasswordReadersChanged => ["msDS-GroupMSAMembership"],
        WatchEventKind.ServicePrincipalNameAdded => ["servicePrincipalName"],
        WatchEventKind.SecurityDescriptorChanged => ["nTSecurityDescriptor"],
        WatchEventKind.GroupPolicyLinkChanged => ["gPLink", "gPOptions"],
        WatchEventKind.FsmoRoleChanged => ["fSMORoleOwner"],
        _ => [],
    };

    /// <summary>True for a membership event (4728 … 4762).</summary>
    public static bool IsMembershipCode(int code) => MemberAddedCodes.Contains(code) || MemberRemovedCodes.Contains(code);

    /// <summary>True for a directory-service change event (5136–5141).</summary>
    public static bool IsDirectoryServiceCode(int code) => DirectoryServiceCodes.Contains(code);
}
