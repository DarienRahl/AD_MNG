namespace AdMng.Watchdog.Core.Detection;

/// <summary>objectClass names the watchdog selects that are not in <c>AdMng.Core.ActiveDirectory.ObjectClasses</c>.</summary>
public static class WatchdogObjectClasses
{
    public const string GroupPolicyContainer = "groupPolicyContainer";
    public const string TrustedDomain = "trustedDomain";
    public const string PasswordSettings = "msDS-PasswordSettings";
    public const string RidManager = "rIDManager";
    public const string NtdsDsa = "nTDSDSA";
    public const string AttributeSchema = "attributeSchema";
    public const string ClassSchema = "classSchema";
    public const string CertificateTemplate = "pKICertificateTemplate";
    public const string EnrollmentService = "pKIEnrollmentService";
    public const string CertificationAuthority = "certificationAuthority";
    public const string Person = "person";

    /// <summary>objectCategory RDN of read-only domain controllers' NTDS Settings objects.</summary>
    public const string ReadOnlyNtdsDsaCategory = "NTDS-DSA-RO";
}
