namespace AdMng.Watchdog.Core.Detection;

/// <summary>LDAP display names the watchdog reads that are not in <c>AdMng.Core.ActiveDirectory.Attr</c>.</summary>
public static class WatchdogAttributes
{
    // RootDSE
    public const string HighestCommittedUsn = "highestCommittedUSN";
    public const string DsServiceName = "dsServiceName";
    public const string IsSynchronized = "isSynchronized";

    // Domain controllers, FSMO holders and forest singletons
    public const string InvocationId = "invocationId";
    public const string FsmoRoleOwner = "fSMORoleOwner";
    public const string HasDomainNcs = "msDS-HasDomainNCs";
    public const string WellKnownObjects = "wellKnownObjects";
    public const string EnabledFeature = "msDS-EnabledFeature";
    public const string DsHeuristics = "dSHeuristics";

    // Replication metadata and schema
    public const string ReplAttributeMetaData = "msDS-ReplAttributeMetaData";
    public const string ReplPropertyMetaData = "replPropertyMetaData";
    public const string IntId = "msDS-IntId";
    public const string LdapDisplayName = "lDAPDisplayName";

    // Domain policy (domain head)
    public const string LockoutThreshold = "lockoutThreshold";
    public const string LockoutDuration = "lockoutDuration";
    public const string LockoutObservationWindow = "lockOutObservationWindow";
    public const string MaxPwdAge = "maxPwdAge";
    public const string MinPwdAge = "minPwdAge";
    public const string MinPwdLength = "minPwdLength";
    public const string PwdHistoryLength = "pwdHistoryLength";
    public const string PwdProperties = "pwdProperties";
    public const string MachineAccountQuota = "ms-DS-MachineAccountQuota";
    public const string BehaviorVersion = "msDS-Behavior-Version";
    public const string GpOptions = "gPOptions";

    // Group policy containers
    public const string VersionNumber = "versionNumber";
    public const string Flags = "flags";
    public const string GpcFileSysPath = "gPCFileSysPath";

    // Trusts
    public const string TrustDirection = "trustDirection";
    public const string TrustType = "trustType";
    public const string TrustAttributes = "trustAttributes";
    public const string FlatName = "flatName";
    public const string TrustPartner = "trustPartner";

    // Fine-grained password policies
    public const string PsoPrecedence = "msDS-PasswordSettingsPrecedence";
    public const string PsoMinimumPasswordLength = "msDS-MinimumPasswordLength";
    public const string PsoPasswordHistoryLength = "msDS-PasswordHistoryLength";
    public const string PsoComplexityEnabled = "msDS-PasswordComplexityEnabled";
    public const string PsoReversibleEncryptionEnabled = "msDS-PasswordReversibleEncryptionEnabled";
    public const string PsoMinimumPasswordAge = "msDS-MinimumPasswordAge";
    public const string PsoMaximumPasswordAge = "msDS-MaximumPasswordAge";
    public const string PsoLockoutThreshold = "msDS-LockoutThreshold";
    public const string PsoLockoutObservationWindow = "msDS-LockoutObservationWindow";
    public const string PsoLockoutDuration = "msDS-LockoutDuration";
    public const string PsoAppliesTo = "msDS-PSOAppliesTo";
    public const string ResultantPso = "msDS-ResultantPSO";

    // Security-relevant account attributes
    public const string AllowedToActOnBehalfOfOtherIdentity = "msDS-AllowedToActOnBehalfOfOtherIdentity";
    public const string KeyCredentialLink = "msDS-KeyCredentialLink";
    public const string UserCertificate = "userCertificate";
    public const string AltSecurityIdentities = "altSecurityIdentities";
    public const string CreatorSid = "mS-DS-CreatorSID";
    public const string GroupMsaMembership = "msDS-GroupMSAMembership";
    public const string ManagedAccountPrecededByLink = "msDS-ManagedAccountPrecededByLink";
    public const string DelegatedMsaState = "msDS-DelegatedMSAState";
    public const string SupersededManagedAccountLink = "msDS-SupersededManagedAccountLink";
    public const string SupersededServiceAccountState = "msDS-SupersededServiceAccountState";

    // PKI (Configuration NC)
    public const string CertificateNameFlag = "msPKI-Certificate-Name-Flag";
    public const string EnrollmentFlag = "msPKI-Enrollment-Flag";
    public const string RaSignature = "msPKI-RA-Signature";
    public const string ExtendedKeyUsage = "pKIExtendedKeyUsage";
    public const string ApplicationPolicy = "msPKI-Certificate-Application-Policy";
    public const string CertificateTemplates = "certificateTemplates";
    public const string CaCertificate = "cACertificate";

    // Inactive accounts / lockout monitor
    public const string UserPasswordExpiryTimeComputed = "msDS-UserPasswordExpiryTimeComputed";
}
