using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Core.Pki;

/// <summary>
/// One <c>pKICertificateTemplate</c> object (docs/dev/p-certificate-services.md 2.2), in memory only (CLAUDE.md rule 1).
/// The contract commit fills the identity members (<see cref="Dn"/>, <see cref="ObjectGuid"/>, <see cref="Cn"/>,
/// <see cref="DisplayName"/>, <see cref="SecurityDescriptor"/>); C2 fills the rest in <see cref="Of"/>. Derived members
/// are computed from the masks. <see cref="ToString"/> carries the GUID only.
/// </summary>
public sealed record CertificateTemplate
{
    public required string Dn { get; init; }

    public Guid ObjectGuid { get; init; }

    /// <summary>The template's cn (what <c>certificateTemplates</c> of an enterprise CA lists).</summary>
    public required string Cn { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The raw <c>nTSecurityDescriptor</c> (owner + DACL); null when it was not returned (no READ_CONTROL – the rights are <i>nieocenione</i>).</summary>
    public byte[]? SecurityDescriptor { get; init; }

    /// <summary><c>msPKI-Template-Schema-Version</c> (1–4; absent = 1).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary><c>revision</c>.</summary>
    public int Revision { get; init; }

    /// <summary><c>msPKI-Template-Minor-Revision</c>.</summary>
    public int MinorRevision { get; init; }

    /// <summary><c>msPKI-Cert-Template-OID</c>.</summary>
    public string? Oid { get; init; }

    /// <summary><c>pKIExtendedKeyUsage</c> (what Certipy and Locksmith read).</summary>
    public IReadOnlyList<string> Ekus { get; init; } = [];

    /// <summary><c>msPKI-Certificate-Application-Policy</c>.</summary>
    public IReadOnlyList<string> ApplicationPolicies { get; init; } = [];

    /// <summary>
    /// What the CA puts into issued certificates: <see cref="ApplicationPolicies"/> when <see cref="SchemaVersion"/> ≥ 2 and
    /// it is not empty, else <see cref="Ekus"/>.
    /// </summary>
    public IReadOnlyList<string> EffectiveEku => SchemaVersion >= 2 && ApplicationPolicies.Count > 0 ? ApplicationPolicies : Ekus;

    /// <summary><c>pKIKeyUsage</c> (raw bytes).</summary>
    public byte[]? KeyUsage { get; init; }

    /// <summary><c>msPKI-Certificate-Name-Flag</c> (absent = 0).</summary>
    public uint NameFlag { get; init; }

    /// <summary><c>msPKI-Enrollment-Flag</c> (absent = 0).</summary>
    public uint EnrollmentFlag { get; init; }

    /// <summary><c>msPKI-Private-Key-Flag</c> (absent = 0).</summary>
    public uint PrivateKeyFlag { get; init; }

    /// <summary>The general <c>flags</c> (absent = 0).</summary>
    public uint GeneralFlag { get; init; }

    /// <summary><c>msPKI-RA-Signature</c>: authorized signatures required (absent = 0).</summary>
    public int RaSignatures { get; init; }

    /// <summary>The application-policy OIDs of <c>msPKI-RA-Application-Policies</c> an authorized signature must carry.</summary>
    public IReadOnlyList<string> RaApplicationPolicies { get; init; } = [];

    /// <summary>The cryptography settings of a schema-4 <c>msPKI-RA-Application-Policies</c> (<c>msPKI-Asymmetric-Algorithm</c> …, name → value).</summary>
    public IReadOnlyDictionary<string, string> CryptoSettings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>msPKI-RA-Policies</c>: issuance policies an authorized signature must carry.</summary>
    public IReadOnlyList<string> RaIssuancePolicies { get; init; } = [];

    /// <summary><c>msPKI-Certificate-Policy</c>: the issuance policies of issued certificates (ESC13).</summary>
    public IReadOnlyList<string> IssuancePolicies { get; init; } = [];

    /// <summary><c>pKIExpirationPeriod</c>.</summary>
    public PkiPeriod? Validity { get; init; }

    /// <summary><c>pKIOverlapPeriod</c>.</summary>
    public PkiPeriod? RenewalOverlap { get; init; }

    /// <summary><c>msPKI-Minimal-Key-Size</c>.</summary>
    public int? MinimalKeySize { get; init; }

    /// <summary><c>pKIDefaultCSPs</c>.</summary>
    public IReadOnlyList<string> CryptoProviders { get; init; } = [];

    /// <summary><c>msPKI-Supersede-Templates</c>.</summary>
    public IReadOnlyList<string> SupersededTemplates { get; init; } = [];

    /// <summary>The enrollee supplies the subject (name flag <c>0x1</c>, <c>CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT</c>).</summary>
    public bool EnrolleeSuppliesSubject => (NameFlag & 0x1) != 0;

    /// <summary>CA certificate manager approval (enrollment flag <c>0x2</c>, <c>CT_FLAG_PEND_ALL_REQUESTS</c>).</summary>
    public bool RequiresManagerApproval => (EnrollmentFlag & 0x2) != 0;

    /// <summary>No SID security extension in issued certificates (enrollment flag <c>0x80000</c>, ESC9).</summary>
    public bool NoSecurityExtension => (EnrollmentFlag & 0x80000) != 0;

    /// <summary>At least one authorized signature is required (<see cref="RaSignatures"/> ≥ 1).</summary>
    public bool RequiresAuthorizedSignature => RaSignatures >= 1;

    /// <summary>
    /// Builds the record from one <c>pKICertificateTemplate</c> object read with <see cref="PkiAttributes.TemplateAttributes"/>.
    /// The contract commit fills the identity members; C2 the rest (multi-valued strings with <c>GetStrings</c>, the
    /// masks with <c>GetInt32</c> cast to <c>uint</c>, the periods with <see cref="PkiPeriod.Decode"/>).
    /// </summary>
    public static CertificateTemplate Of(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return new CertificateTemplate
        {
            Dn = obj.DistinguishedName,
            ObjectGuid = obj.ObjectGuid ?? Guid.Empty,
            Cn = obj.GetString(Attr.Cn) ?? obj.Dn.Name,
            DisplayName = obj.GetString(Attr.DisplayName),
            SecurityDescriptor = obj.GetBytes(Attr.NtSecurityDescriptor),
        };
    }

    /// <summary>The GUID only (never a name, DN or flag value).</summary>
    public override string ToString() => $"{nameof(CertificateTemplate)} {ObjectGuid:B}";
}
