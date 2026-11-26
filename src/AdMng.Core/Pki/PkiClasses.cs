namespace AdMng.Core.Pki;

/// <summary>
/// The object classes of <c>CN=Public Key Services,CN=Services,&lt;configuration&gt;</c> that the P1 overview reads
/// (docs/dev/p-certificate-services.md 2.1) and the U4b PKI pass scans (2.3).
/// </summary>
public static class PkiClasses
{
    /// <summary>A certificate template (<c>CN=Certificate Templates</c>).</summary>
    public const string CertificateTemplate = "pKICertificateTemplate";

    /// <summary>An enterprise CA (<c>CN=Enrollment Services</c>).</summary>
    public const string EnrollmentService = "pKIEnrollmentService";

    /// <summary>A CA certificate store: <c>CN=Certification Authorities</c>, <c>CN=AIA</c> and <c>CN=NTAuthCertificates</c>.</summary>
    public const string CertificationAuthority = "certificationAuthority";

    /// <summary>A CRL distribution point (<c>CN=CDP,CN=&lt;host&gt;,CN=&lt;CA&gt;</c>).</summary>
    public const string CrlDistributionPoint = "cRLDistributionPoint";

    /// <summary>An OID object of <c>CN=OID</c> (template, issuance and application policies).</summary>
    public const string EnterpriseOid = "msPKI-Enterprise-Oid";

    /// <summary>A key recovery agent (<c>CN=KRA</c>).</summary>
    public const string KeyRecoveryAgent = "msPKI-PrivateKeyRecoveryAgent";

    /// <summary>The containers of the PKI subtree.</summary>
    public const string Container = "container";

    /// <summary>The seven classes, in the order of 2.1.</summary>
    public static IReadOnlyList<string> All { get; } =
        [EnrollmentService, CertificateTemplate, CertificationAuthority, CrlDistributionPoint, EnterpriseOid, KeyRecoveryAgent, Container];
}
