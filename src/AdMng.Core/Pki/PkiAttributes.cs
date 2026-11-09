using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Pki;

/// <summary>
/// The attribute names of the P1 PKI reads (docs/dev/p-certificate-services.md 2.1–2.4) and the attribute lists of each
/// read. Core keeps its own constants: it must not depend on <c>AdMng.Watchdog.Core</c>.
/// </summary>
public static class PkiAttributes
{
    // Templates (2.2)

    /// <summary><c>msPKI-Certificate-Name-Flag</c> (MS-CRTD 2.28).</summary>
    public const string CertificateNameFlag = "msPKI-Certificate-Name-Flag";

    /// <summary><c>msPKI-Enrollment-Flag</c> (MS-CRTD 2.26).</summary>
    public const string EnrollmentFlag = "msPKI-Enrollment-Flag";

    /// <summary><c>msPKI-Private-Key-Flag</c> (MS-CRTD 2.27).</summary>
    public const string PrivateKeyFlag = "msPKI-Private-Key-Flag";

    /// <summary>The general <c>flags</c> of a template (MS-CRTD 2.4; chosen by the template class – GPOs have <c>flags</c> too).</summary>
    public const string Flags = "flags";

    /// <summary><c>msPKI-RA-Signature</c>: the number of authorized signatures (absent = 0).</summary>
    public const string RaSignature = "msPKI-RA-Signature";

    /// <summary><c>msPKI-RA-Application-Policies</c> (schema 4: backquote-delimited triples; older: OIDs).</summary>
    public const string RaApplicationPolicies = "msPKI-RA-Application-Policies";

    /// <summary><c>msPKI-RA-Policies</c>: the issuance policies an authorized signature must carry.</summary>
    public const string RaPolicies = "msPKI-RA-Policies";

    /// <summary><c>msPKI-Certificate-Policy</c>: the issuance policies of issued certificates (ESC13).</summary>
    public const string CertificatePolicy = "msPKI-Certificate-Policy";

    /// <summary><c>pKIExtendedKeyUsage</c>.</summary>
    public const string ExtendedKeyUsage = "pKIExtendedKeyUsage";

    /// <summary><c>msPKI-Certificate-Application-Policy</c> (the CA issues schema-2+ templates from it).</summary>
    public const string ApplicationPolicy = "msPKI-Certificate-Application-Policy";

    /// <summary><c>msPKI-Template-Schema-Version</c> (1–4, absent = 1).</summary>
    public const string TemplateSchemaVersion = "msPKI-Template-Schema-Version";

    /// <summary><c>msPKI-Template-Minor-Revision</c>.</summary>
    public const string TemplateMinorRevision = "msPKI-Template-Minor-Revision";

    /// <summary><c>revision</c> (the template's major version).</summary>
    public const string Revision = "revision";

    /// <summary><c>msPKI-Cert-Template-OID</c> (templates and OID objects).</summary>
    public const string TemplateOid = "msPKI-Cert-Template-OID";

    /// <summary><c>pKIKeyUsage</c> (binary key usage bits).</summary>
    public const string KeyUsage = "pKIKeyUsage";

    /// <summary><c>pKIExpirationPeriod</c> (8-byte negative 100-ns interval).</summary>
    public const string ExpirationPeriod = "pKIExpirationPeriod";

    /// <summary><c>pKIOverlapPeriod</c> (8-byte negative 100-ns interval).</summary>
    public const string OverlapPeriod = "pKIOverlapPeriod";

    /// <summary><c>msPKI-Minimal-Key-Size</c>.</summary>
    public const string MinimalKeySize = "msPKI-Minimal-Key-Size";

    /// <summary><c>pKIDefaultCSPs</c>.</summary>
    public const string DefaultCsps = "pKIDefaultCSPs";

    /// <summary><c>msPKI-Supersede-Templates</c>.</summary>
    public const string SupersedeTemplates = "msPKI-Supersede-Templates";

    // Enterprise CAs, CA stores, CDP, KRA, OID (2.1)

    /// <summary><c>certificateTemplates</c> of an enrollment service (the cn values of the published templates).</summary>
    public const string CertificateTemplates = "certificateTemplates";

    /// <summary><c>msPKI-Enrollment-Servers</c> (the CES URIs).</summary>
    public const string EnrollmentServers = "msPKI-Enrollment-Servers";

    /// <summary><c>cACertificate</c> (binary, multi-valued).</summary>
    public const string CaCertificate = "cACertificate";

    /// <summary><c>crossCertificatePair</c> (binary, multi-valued).</summary>
    public const string CrossCertificatePair = "crossCertificatePair";

    /// <summary><c>certificateRevocationList</c> (binary).</summary>
    public const string CertificateRevocationList = "certificateRevocationList";

    /// <summary><c>deltaRevocationList</c> (binary).</summary>
    public const string DeltaRevocationList = "deltaRevocationList";

    /// <summary><c>authorityRevocationList</c> (binary).</summary>
    public const string AuthorityRevocationList = "authorityRevocationList";

    /// <summary><c>msDS-OIDToGroupLink</c> of an OID object (ESC13).</summary>
    public const string OidToGroupLink = "msDS-OIDToGroupLink";

    // Accounts (2.3, 2.6)

    /// <summary><c>userCertificate</c> (binary, multi-valued: accounts, KRA agents, domain controllers).</summary>
    public const string UserCertificate = "userCertificate";

    /// <summary><c>userSMIMECertificate</c> (binary, multi-valued).</summary>
    public const string UserSmimeCertificate = "userSMIMECertificate";

    /// <summary><c>userPKCS12</c> – key material, a secret that is never shown (decision 7).</summary>
    public const string UserPkcs12 = "userPKCS12";

    /// <summary><c>altSecurityIdentities</c> (explicit certificate mappings, KB5014754).</summary>
    public const string AltSecurityIdentities = "altSecurityIdentities";

    /// <summary><c>msDS-KeyCredentialLink</c> (DN-Binary; delivered as UTF-8 bytes).</summary>
    public const string KeyCredentialLink = "msDS-KeyCredentialLink";

    /// <summary>
    /// The nine template attributes whose write is an ESC4 path (2.3) – the one list used by the rights evaluator, the
    /// ESC analyzer and U4b's <c>WriteTemplateProperty</c>.
    /// </summary>
    public static IReadOnlyList<string> SensitiveTemplateAttributes { get; } =
    [
        CertificateNameFlag, EnrollmentFlag, ExtendedKeyUsage, ApplicationPolicy, RaSignature, RaApplicationPolicies,
        RaPolicies, CertificatePolicy, TemplateSchemaVersion,
    ];

    /// <summary>What <c>CertificateTemplate.Of</c> reads from a <c>pKICertificateTemplate</c> object (2.2).</summary>
    public static IReadOnlyList<string> TemplateAttributes { get; } =
    [
        Attr.Cn, Attr.DisplayName, Attr.ObjectGuid, Attr.NtSecurityDescriptor, TemplateSchemaVersion, Revision,
        TemplateMinorRevision, TemplateOid, ExtendedKeyUsage, ApplicationPolicy, KeyUsage, CertificateNameFlag,
        EnrollmentFlag, PrivateKeyFlag, Flags, RaSignature, RaApplicationPolicies, RaPolicies, CertificatePolicy,
        ExpirationPeriod, OverlapPeriod, MinimalKeySize, DefaultCsps, SupersedeTemplates,
    ];

    /// <summary>The attributes of the one subtree search of the overview reader (2.1): every PKI class at once.</summary>
    public static IReadOnlyList<string> OverviewAttributes { get; } =
    [
        Attr.ObjectClass, Attr.Name, Attr.DnsHostName, CertificateTemplates, EnrollmentServers, CaCertificate,
        CrossCertificatePair, CertificateRevocationList, DeltaRevocationList, AuthorityRevocationList, UserCertificate,
        OidToGroupLink, .. TemplateAttributes,
    ];

    /// <summary>The attributes of the one account pass of the enrollment expansion (2.3).</summary>
    public static IReadOnlyList<string> AccountAttributes { get; } =
    [
        Attr.ObjectSid, Attr.PrimaryGroupId, Attr.MemberOf, Attr.UserAccountControl, Attr.SamAccountName, Attr.Name,
        AltSecurityIdentities,
    ];

    /// <summary>The attributes of <c>PkiOverviewReader.ReadDomainControllersAsync</c> (2.1).</summary>
    public static IReadOnlyList<string> DomainControllerAttributes { get; } =
        [Attr.DnsHostName, Attr.ObjectSid, Attr.UserAccountControl, UserCertificate];
}
