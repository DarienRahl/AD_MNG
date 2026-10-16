namespace AdMng.Watchdog.Contracts.Events;

/// <summary>Stored as int – values are stable, never renumber. Defaults: section 7 / EventKindCatalog.</summary>
public enum WatchEventKind
{
    UserCreated = 100, ComputerCreated = 101, GroupCreated = 102, OrganizationalUnitCreated = 103, ContactCreated = 104,
    ServiceAccountCreated = 105, ObjectDeleted = 110, ObjectRestored = 111, ObjectMoved = 112, ObjectRenamed = 113,
    ObjectAppeared = 114, ObjectDisappeared = 115,
    AccountDisabled = 200, AccountEnabled = 201, AccountLockedOut = 202, AccountUnlocked = 203, LockoutExpired = 204,
    PasswordChanged = 205, PasswordMustChangeSet = 206, AccountFlagsChanged = 207, AccountExpirationChanged = 208,
    AccountNameChanged = 209, AttributesChanged = 210,
    PasswordAgeReset = 211,          // pwdLastSet moved forward without a unicodePwd write (0 → -1 trick)
    GroupMemberAdded = 300, GroupMemberRemoved = 301, GroupTypeChanged = 302, PrivilegeGranted = 303, PrivilegeRevoked = 304,
    AdminCountSet = 400, SidHistoryChanged = 401, DelegationChanged = 402, ServicePrincipalNameAdded = 403,
    KeyCredentialChanged = 404, PrimaryGroupChanged = 405, SecurityDescriptorChanged = 406, ComputerNameSpoofing = 407,
    ManagedAccountLinkChanged = 408,      // dMSA migration links on either side (BadSuccessor)
    CertificateMappingChanged = 409,      // altSecurityIdentities (explicit certificate mapping, ESC14)
    ManagedPasswordReadersChanged = 410,  // msDS-GroupMSAMembership (who may read a gMSA/dMSA password)
    DomainPolicyChanged = 500, PasswordSettingsChanged = 501, GroupPolicyLinkChanged = 502, GroupPolicyChanged = 503,
    TrustChanged = 504, DomainControllerAdded = 505, SchemaChanged = 506,
    DomainControllerRemoved = 507, FsmoRoleChanged = 508, CertificateTemplateChanged = 509,
    AccountExpiring = 600, AccountExpired = 601, PasswordExpiring = 602, MassLockout = 603, MassDeletion = 604,
    PasswordSprayingSuspected = 605,      // optional detector on failed-logon events (10.15), not recorded by default
    SecurityLogCleared = 700, AuditPolicyChanged = 701, DsrmPasswordChanged = 702,
    HealthDirectoryUnavailable = 900, HealthResync = 901, HealthPermissionGap = 902, HealthAttributionDegraded = 903,
    HealthEventLogGap = 904, HealthDeliveryFailing = 905, HealthStorage = 906, HealthBaselineCompleted = 907,
    WatchdogConfigurationChanged = 908,   // runtime configuration saved through the API (17.2)
    Test = 999,
}
