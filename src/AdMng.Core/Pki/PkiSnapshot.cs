using System.Globalization;

namespace AdMng.Core.Pki;

/// <summary>One certificate as stored (DER) with its parsed summary (null when malformed – <see cref="PkiReadNoticeCode.CertificateMalformed"/>).</summary>
public sealed record PkiCertificate(byte[] Der, CertificateSummary? Summary)
{
    /// <summary>Whether it parsed and its size only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiCertificate)}({(Summary is null ? "unparsed" : "parsed")}, {Der.Length} bytes)");
}

/// <summary>One CRL as stored (DER) with its parsed header (null when malformed – <see cref="PkiReadNoticeCode.CrlMalformed"/>).</summary>
public sealed record PkiCrl(byte[] Der, CrlSummary? Summary)
{
    /// <summary>Whether it parsed and its size only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiCrl)}({(Summary is null ? "unparsed" : "parsed")}, {Der.Length} bytes)");
}

/// <summary>The store a <see cref="PkiCaObject"/> comes from.</summary>
public enum PkiCaStore
{
    /// <summary><c>CN=Certification Authorities</c> – trusted roots.</summary>
    RootCa,

    /// <summary><c>CN=AIA</c> – intermediates and cross certificates.</summary>
    Aia,

    /// <summary><c>CN=NTAuthCertificates</c>.</summary>
    NtAuth,
}

/// <summary>A <c>certificationAuthority</c> object of a CA store.</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="ObjectGuid">Its objectGUID.</param>
/// <param name="Name">Its cn (certutil-sanitized – never matched as text).</param>
/// <param name="Store">The store.</param>
/// <param name="Certificates"><c>cACertificate</c>.</param>
/// <param name="CrossCertificates"><c>crossCertificatePair</c>.</param>
public sealed record PkiCaObject(
    string Dn,
    Guid ObjectGuid,
    string Name,
    PkiCaStore Store,
    IReadOnlyList<PkiCertificate> Certificates,
    IReadOnlyList<PkiCertificate> CrossCertificates)
{
    /// <summary>Store, GUID and counts only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Store} {ObjectGuid:B} ({Certificates.Count} certificates, {CrossCertificates.Count} cross)");
}

/// <summary>An enterprise CA (<c>pKIEnrollmentService</c> of <c>CN=Enrollment Services</c>).</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="ObjectGuid">Its objectGUID.</param>
/// <param name="Name">Its cn (the CA name).</param>
/// <param name="DisplayName">Its displayName.</param>
/// <param name="HostName"><c>dNSHostName</c> of the CA host.</param>
/// <param name="ComputerDn">The CA host's computer object (resolved in the window's domain), or null.</param>
/// <param name="ComputerSid">The CA host's objectSid (decision 4: Tier 0), or null when unresolved or in another domain.</param>
/// <param name="Certificates"><c>cACertificate</c> – renewals included, newest first.</param>
/// <param name="PublishedTemplates"><c>certificateTemplates</c> (template cn values).</param>
/// <param name="EnrollmentServers">The CES URIs of <c>msPKI-Enrollment-Servers</c>.</param>
/// <param name="InNtAuth">An NTAuth certificate has the SHA-256 of one of <see cref="Certificates"/>.</param>
public sealed record PkiEnrollmentService(
    string Dn,
    Guid ObjectGuid,
    string Name,
    string? DisplayName,
    string? HostName,
    string? ComputerDn,
    string? ComputerSid,
    IReadOnlyList<PkiCertificate> Certificates,
    IReadOnlyList<string> PublishedTemplates,
    IReadOnlyList<string> EnrollmentServers,
    bool InNtAuth)
{
    /// <summary>GUID and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiEnrollmentService)} {ObjectGuid:B} ({Certificates.Count} certificates, {PublishedTemplates.Count} templates{(InNtAuth ? ", in NTAuth" : string.Empty)})");
}

/// <summary>A CRL distribution point (<c>cRLDistributionPoint</c> of <c>CN=CDP,CN=&lt;host&gt;</c>).</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="ObjectGuid">Its objectGUID.</param>
/// <param name="Name">Its cn (certutil-sanitized CA name with the key index).</param>
/// <param name="HostName">The cn of the parent container (the CA host's short name).</param>
/// <param name="Base"><c>certificateRevocationList</c>, null when absent.</param>
/// <param name="Delta"><c>deltaRevocationList</c>, null when absent.</param>
/// <param name="IssuerCaGuid">
/// The enterprise CA whose certificate subject and Subject Key Identifier equal the CRL issuer and Authority Key
/// Identifier (never matched by RDN text), or null.
/// </param>
public sealed record PkiCrlList(
    string Dn,
    Guid ObjectGuid,
    string Name,
    string? HostName,
    PkiCrl? Base,
    PkiCrl? Delta,
    Guid? IssuerCaGuid)
{
    /// <summary>GUIDs and presence only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiCrlList)} {ObjectGuid:B} (base {Base is not null}, delta {Delta is not null}{(IssuerCaGuid is { } ca ? ", CA " + ca.ToString("B") : string.Empty)})");
}

/// <summary>A key recovery agent (<c>msPKI-PrivateKeyRecoveryAgent</c> of <c>CN=KRA</c>).</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="ObjectGuid">Its objectGUID.</param>
/// <param name="Name">Its cn.</param>
/// <param name="Certificates"><c>userCertificate</c>.</param>
public sealed record PkiKraAgent(string Dn, Guid ObjectGuid, string Name, IReadOnlyList<PkiCertificate> Certificates)
{
    /// <summary>GUID and count only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiKraAgent)} {ObjectGuid:B} ({Certificates.Count} certificates)");
}

/// <summary>An OID object (<c>msPKI-Enterprise-Oid</c> of <c>CN=OID</c>).</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="ObjectGuid">Its objectGUID.</param>
/// <param name="Oid"><c>msPKI-Cert-Template-OID</c> (ESC13 joins templates by this string, never by DN).</param>
/// <param name="DisplayName">Its displayName.</param>
/// <param name="Flags"><c>flags</c>: 1 template, 2 issuance policy, 3 application policy.</param>
/// <param name="GroupLinkDn"><c>msDS-OIDToGroupLink</c>, or null.</param>
public sealed record PkiOidObject(string Dn, Guid ObjectGuid, string Oid, string? DisplayName, int Flags, string? GroupLinkDn)
{
    /// <summary>GUID and whether it links a group only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiOidObject)} {ObjectGuid:B}{(GroupLinkDn is null ? string.Empty : " (group link)")}");
}

/// <summary>Raw DER lists for chain building (<c>TrustMode = CustomRootTrust</c>, nothing downloaded).</summary>
/// <param name="Roots">The certificates of <c>CN=Certification Authorities</c>.</param>
/// <param name="Intermediates">The certificates of <c>CN=AIA</c> and the enterprise CAs.</param>
/// <param name="NtAuth">The certificates of <c>CN=NTAuthCertificates</c>.</param>
public sealed record PkiTrustMaterial(IReadOnlyList<byte[]> Roots, IReadOnlyList<byte[]> Intermediates, IReadOnlyList<byte[]> NtAuth)
{
    /// <summary>No trust material (an empty snapshot).</summary>
    public static PkiTrustMaterial Empty { get; } = new([], [], []);

    /// <summary>Counts only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiTrustMaterial)}({Roots.Count} roots, {Intermediates.Count} intermediates, {NtAuth.Count} NTAuth)");
}

/// <summary>A domain controller of the window's domain with the certificates its object publishes (the KDC indicator, 2.1).</summary>
/// <param name="Dn">The computer object.</param>
/// <param name="HostName"><c>dNSHostName</c>, or null.</param>
/// <param name="IsReadOnly">An RODC (<c>PARTIAL_SECRETS_ACCOUNT</c>).</param>
/// <param name="Certificates"><c>userCertificate</c> (DER).</param>
public sealed record PkiDomainController(string Dn, string? HostName, bool IsReadOnly, IReadOnlyList<byte[]> Certificates)
{
    /// <summary>Kind and count only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiDomainController)}({(IsReadOnly ? "RODC" : "DC")}, {Certificates.Count} certificates)");
}

/// <summary>What the PKI reads observed (2.1, 2.3); the code set is fixed by the contract commit.</summary>
public enum PkiReadNoticeCode
{
    /// <summary>The base search failed with <c>NoSuchObject</c>: no AD CS (or the Samba lab) – an empty snapshot.</summary>
    BaseNotFound,

    /// <summary>The base search was denied: a visible "not evaluated" state.</summary>
    BaseAccessDenied,

    /// <summary>A well-known PKI object did not come back (<i>niewidoczny – brak obiektu albo brak prawa odczytu</i>).</summary>
    ContainerMissing,

    /// <summary>A template came back without <c>nTSecurityDescriptor</c>: its rights are <i>nieocenione</i>.</summary>
    SecurityDescriptorUnreadable,

    /// <summary>A certificate value is not valid DER (its row stays, the summary is null).</summary>
    CertificateMalformed,

    /// <summary>A CRL value is not valid DER (its row stays, the summary is null).</summary>
    CrlMalformed,

    /// <summary>The computer object of an enterprise CA host of the window's domain was not found.</summary>
    CaComputerUnresolved,

    /// <summary>The CA host is in another domain: its SID stays unknown (the CA computer is then <i>Other</i>).</summary>
    CaComputerInOtherDomain,

    /// <summary>A trustee of another forest domain (or an OID group link) the window could not resolve (2.3, 2.4).</summary>
    TrusteeUnresolved,
}

/// <summary>One notice of a PKI read (resource <c>Pki.Notice.&lt;Code&gt;</c>).</summary>
/// <param name="Code">What was observed.</param>
/// <param name="ObjectDn">The object concerned (a well-known container's DN for <see cref="PkiReadNoticeCode.ContainerMissing"/>), or null.</param>
/// <param name="ObjectGuid">Its objectGUID, or null.</param>
public sealed record PkiReadNotice(PkiReadNoticeCode Code, string? ObjectDn, Guid? ObjectGuid)
{
    /// <summary>The code and the GUID only (never the DN).</summary>
    public override string ToString() => $"{Code}{(ObjectGuid is { } id ? " " + id.ToString("B") : string.Empty)}";
}

/// <summary>
/// The forest PKI as one DC sees it (2.1): read once per window into memory (CLAUDE.md rule 1), never cached on disk.
/// Health rows are not part of it – <c>PkiHealth.Evaluate</c> computes them at display time (decision 2).
/// </summary>
/// <param name="ConfigurationDn">The configuration naming context read.</param>
/// <param name="ReadAt">When it was read (the window's clock).</param>
/// <param name="EnterpriseCas">The enterprise CAs.</param>
/// <param name="RootCas"><c>CN=Certification Authorities</c>.</param>
/// <param name="AiaObjects"><c>CN=AIA</c>.</param>
/// <param name="NtAuth"><c>CN=NTAuthCertificates</c> (<see cref="PkiCaStore.NtAuth"/>), null when not visible.</param>
/// <param name="CrlLists"><c>CN=CDP</c>.</param>
/// <param name="KraAgents"><c>CN=KRA</c>.</param>
/// <param name="Oids"><c>CN=OID</c>.</param>
/// <param name="Templates"><c>CN=Certificate Templates</c>.</param>
/// <param name="Notices">What the read observed.</param>
/// <param name="TrustMaterial">The raw DER lists for chain building.</param>
public sealed record PkiSnapshot(
    string ConfigurationDn,
    DateTimeOffset ReadAt,
    IReadOnlyList<PkiEnrollmentService> EnterpriseCas,
    IReadOnlyList<PkiCaObject> RootCas,
    IReadOnlyList<PkiCaObject> AiaObjects,
    PkiCaObject? NtAuth,
    IReadOnlyList<PkiCrlList> CrlLists,
    IReadOnlyList<PkiKraAgent> KraAgents,
    IReadOnlyList<PkiOidObject> Oids,
    IReadOnlyList<CertificateTemplate> Templates,
    IReadOnlyList<PkiReadNotice> Notices,
    PkiTrustMaterial TrustMaterial)
{
    /// <summary>A snapshot with no PKI objects (no AD CS, the base denied) carrying <paramref name="notices"/>.</summary>
    public static PkiSnapshot Empty(string configurationDn, DateTimeOffset readAt, IReadOnlyList<PkiReadNotice>? notices = null) =>
        new(configurationDn, readAt, [], [], [], null, [], [], [], [], notices ?? [], PkiTrustMaterial.Empty);

    /// <summary>Counts only (never a DN, name or certificate field).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiSnapshot)}({EnterpriseCas.Count} enterprise CAs, {RootCas.Count} roots, {AiaObjects.Count} AIA, {CrlLists.Count} CDP, {KraAgents.Count} KRA, {Oids.Count} OID, {Templates.Count} templates, {Notices.Count} notices)");
}
