using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// Schema additions for AD CS (P1, docs/dev/p-certificate-services.md 2.10): the template, CA, CDP, KRA and OID attributes
/// the overview reader and the template decoders read (binary ones with <see cref="AttributeSyntax.OctetString"/>), the
/// classes <c>cRLDistributionPoint</c>, <c>msPKI-Enterprise-Oid</c> and <c>msPKI-PrivateKeyRecoveryAgent</c> with their
/// possible superiors, the PKI attributes on the existing <c>pKICertificateTemplate</c>, <c>pKIEnrollmentService</c> and
/// <c>certificationAuthority</c> classes of <see cref="ExtendForWatchdog"/>, and the certificate attributes of users,
/// computers and contacts (<c>userSMIMECertificate</c>, <c>userPKCS12</c>, <c>altSecurityIdentities</c>,
/// <c>msDS-KeyCredentialLink</c>). Every attribute gets the deterministic schemaIDGUID of <see cref="Def"/> (samples read it
/// from the session's schema, as the U4 sample does); the PKI classes get the real schemaIDGUIDs of the base schema
/// (<see cref="FakeDirectoryBuilder.AclGuids"/>), so an ACE inherited for <c>pKICertificateTemplate</c> resolves. Adding
/// definitions only relaxes the fake, so existing tests keep their behaviour.
/// </summary>
public static partial class FakeSchema
{
    private static readonly Dictionary<string, string[]> PublicKeyMayContain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pKICertificateTemplate"] =
        [
            "msPKI-Private-Key-Flag", "msPKI-Template-Schema-Version", "msPKI-Template-Minor-Revision", "revision",
            "pKIExpirationPeriod", "pKIOverlapPeriod", "msPKI-Minimal-Key-Size", "msPKI-Cert-Template-OID",
            "msPKI-Certificate-Policy", "msPKI-RA-Policies", "msPKI-RA-Application-Policies", "msPKI-Supersede-Templates",
            "pKIDefaultCSPs", "pKIKeyUsage", "flags", "displayName",
        ],
        ["pKIEnrollmentService"] = ["msPKI-Enrollment-Servers", "displayName"],
        ["certificationAuthority"] =
        [
            "crossCertificatePair", "certificateRevocationList", "deltaRevocationList", "authorityRevocationList",
        ],
        ["user"] = ["userSMIMECertificate", "userPKCS12", "altSecurityIdentities", "msDS-KeyCredentialLink"],
        ["computer"] = ["userSMIMECertificate", "userPKCS12", "altSecurityIdentities", "msDS-KeyCredentialLink"],
        ["contact"] = ["userCertificate", "userSMIMECertificate", "userPKCS12", "altSecurityIdentities", "msDS-KeyCredentialLink"],
    };

    /// <summary>The real schemaIDGUIDs of the PKI classes (base schema, the same in every forest).</summary>
    private static readonly Dictionary<string, Guid> PublicKeyClassGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pKICertificateTemplate"] = FakeDirectoryBuilder.AclGuids.PkiCertificateTemplate,
        ["pKIEnrollmentService"] = FakeDirectoryBuilder.AclGuids.PkiEnrollmentService,
        ["certificationAuthority"] = FakeDirectoryBuilder.AclGuids.CertificationAuthority,
        ["cRLDistributionPoint"] = FakeDirectoryBuilder.AclGuids.CrlDistributionPoint,
        ["msPKI-Enterprise-Oid"] = FakeDirectoryBuilder.AclGuids.EnterpriseOid,
        ["msPKI-PrivateKeyRecoveryAgent"] = FakeDirectoryBuilder.AclGuids.PrivateKeyRecoveryAgent,
    };

    private static List<AttributeDefinition> CreatePublicKeyAttributes() =>
    [
        // Templates (2.2)
        Def("msPKI-Private-Key-Flag", AttributeSyntax.Integer),
        Def("msPKI-Template-Schema-Version", AttributeSyntax.Integer),
        Def("msPKI-Template-Minor-Revision", AttributeSyntax.Integer),
        Def("revision", AttributeSyntax.Integer),
        Def("pKIExpirationPeriod", AttributeSyntax.OctetString, lower: 8, upper: 8),
        Def("pKIOverlapPeriod", AttributeSyntax.OctetString, lower: 8, upper: 8),
        Def("msPKI-Minimal-Key-Size", AttributeSyntax.Integer),
        Def("msPKI-Cert-Template-OID", AttributeSyntax.UnicodeString),
        Def("msPKI-Certificate-Policy", AttributeSyntax.UnicodeString, single: false),
        Def("msPKI-RA-Policies", AttributeSyntax.UnicodeString, single: false),
        Def("msPKI-RA-Application-Policies", AttributeSyntax.UnicodeString, single: false),
        Def("msPKI-Supersede-Templates", AttributeSyntax.UnicodeString, single: false),
        Def("pKIDefaultCSPs", AttributeSyntax.UnicodeString, single: false),
        Def("pKIKeyUsage", AttributeSyntax.OctetString),

        // Enterprise CAs, CA stores, CDP, OID (2.1)
        Def("msPKI-Enrollment-Servers", AttributeSyntax.UnicodeString, single: false),
        Def("certificateRevocationList", AttributeSyntax.OctetString, single: false),
        Def("deltaRevocationList", AttributeSyntax.OctetString, single: false),
        Def("authorityRevocationList", AttributeSyntax.OctetString, single: false),
        Def("crossCertificatePair", AttributeSyntax.OctetString, single: false),
        Def("msDS-OIDToGroupLink", AttributeSyntax.DistinguishedName),

        // Accounts (2.6)
        Def("userSMIMECertificate", AttributeSyntax.OctetString, single: false),
        Def("userPKCS12", AttributeSyntax.OctetString, single: false),
    ];

    private static void ExtendForPublicKey(List<ClassDefinition> classes, string schemaNc)
    {
        classes.AddRange(
        [
            Class("cRLDistributionPoint", "top", schemaNc, "CRL-Distribution-Point",
                ["certificateRevocationList", "deltaRevocationList", "authorityRevocationList", "cn"]),
            Class("msPKI-Enterprise-Oid", "top", schemaNc, "ms-PKI-Enterprise-Oid",
                ["msPKI-Cert-Template-OID", "msDS-OIDToGroupLink", "flags", "displayName"]),
            Class("msPKI-PrivateKeyRecoveryAgent", "top", schemaNc, "ms-PKI-Private-Key-Recovery-Agent", ["userCertificate"]),
        ]);

        for (var i = 0; i < classes.Count; i++)
        {
            var definition = classes[i];
            if (PublicKeyMayContain.TryGetValue(definition.LdapName, out var extra))
            {
                definition = definition with { MayContain = [.. definition.MayContain.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase)] };
            }

            if (PublicKeyClassGuids.TryGetValue(definition.LdapName, out var guid))
            {
                definition = definition with { SchemaIdGuid = guid };
            }

            // The PKI objects live in containers of the configuration (CN=CDP/<host>, CN=OID, CN=KRA …).
            if (PublicKeyClassGuids.ContainsKey(definition.LdapName))
            {
                definition = definition with { PossibleSuperiors = [.. definition.PossibleSuperiors.Append("container").Distinct(StringComparer.OrdinalIgnoreCase)] };
            }

            classes[i] = definition;
        }
    }
}
