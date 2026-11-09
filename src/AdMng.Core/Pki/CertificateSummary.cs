namespace AdMng.Core.Pki;

/// <summary>
/// The public fields of one X.509 certificate (docs/dev/p-certificate-services.md 2.1), copied out of an
/// <c>X509Certificate2</c> that <see cref="Of"/> loads with <c>X509CertificateLoader.LoadCertificate</c> and disposes. In
/// memory only (CLAUDE.md rule 1); <see cref="ToString"/> carries no subject, issuer, name, serial or thumbprint.
/// Hand-built instances (tests, other waves) set only what they need – every member has a default.
/// </summary>
public sealed record CertificateSummary
{
    /// <summary>The subject DN as .NET formats it.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The issuer DN as .NET formats it.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>The serial number, upper-case hex as stored (big-endian).</summary>
    public string SerialNumber { get; init; } = string.Empty;

    public DateTimeOffset NotBefore { get; init; }

    public DateTimeOffset NotAfter { get; init; }

    /// <summary>Subject alternative name: DNS names.</summary>
    public IReadOnlyList<string> SanDns { get; init; } = [];

    /// <summary>Subject alternative name: UPNs (other name <c>1.3.6.1.4.1.311.20.2.3</c>).</summary>
    public IReadOnlyList<string> SanUpn { get; init; } = [];

    /// <summary>Subject alternative name: RFC 822 e-mail addresses.</summary>
    public IReadOnlyList<string> SanEmail { get; init; } = [];

    /// <summary>Subject alternative name: URIs (incl. the <c>tag:microsoft.com,2022-09-14:sid:</c> URL).</summary>
    public IReadOnlyList<string> SanUri { get; init; } = [];

    /// <summary>The SID of a <c>tag:microsoft.com,2022-09-14:sid:&lt;SID&gt;</c> SAN URL, or null.</summary>
    public string? SanSid { get; init; }

    /// <summary>Extended key usage OIDs (empty: no EKU extension).</summary>
    public IReadOnlyList<string> Ekus { get; init; } = [];

    /// <summary>Application policy OIDs (<c>1.3.6.1.4.1.311.21.10</c>).</summary>
    public IReadOnlyList<string> ApplicationPolicies { get; init; } = [];

    /// <summary>Public key algorithm (friendly name or OID, e.g. <c>RSA</c>, <c>ECC</c>).</summary>
    public string KeyAlgorithm { get; init; } = string.Empty;

    /// <summary>Public key size in bits, null when unknown.</summary>
    public int? KeySize { get; init; }

    /// <summary>Signature algorithm (friendly name or OID).</summary>
    public string SignatureAlgorithm { get; init; } = string.Empty;

    /// <summary>SHA-1 of the DER, upper-case hex (the thumbprint consoles show).</summary>
    public string Thumbprint { get; init; } = string.Empty;

    /// <summary>SHA-256 of the DER, upper-case hex.</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Subject Key Identifier, upper-case hex, or null.</summary>
    public string? SubjectKeyIdentifier { get; init; }

    /// <summary>Authority Key Identifier (key id), upper-case hex, or null.</summary>
    public string? AuthorityKeyIdentifier { get; init; }

    /// <summary>Basic constraints: a CA certificate.</summary>
    public bool IsCa { get; init; }

    /// <summary>Basic constraints: the path length constraint, null when absent.</summary>
    public int? PathLength { get; init; }

    /// <summary>AIA CA Issuers URLs.</summary>
    public IReadOnlyList<string> AiaCaIssuers { get; init; } = [];

    /// <summary>AIA OCSP URLs.</summary>
    public IReadOnlyList<string> AiaOcsp { get; init; } = [];

    /// <summary>CRL distribution point URLs.</summary>
    public IReadOnlyList<string> CrlDistributionPoints { get; init; } = [];

    /// <summary>Freshest CRL URLs (<c>2.5.29.46</c>, same syntax as the CDP extension): where the delta CRL is.</summary>
    public IReadOnlyList<string> FreshestCrl { get; init; } = [];

    /// <summary>The v1 template name extension (<c>1.3.6.1.4.1.311.20.2</c>), or null.</summary>
    public string? TemplateName { get; init; }

    /// <summary>The template OID of the template information extension (<c>1.3.6.1.4.1.311.21.7</c>), or null.</summary>
    public string? TemplateOid { get; init; }

    /// <summary>Template major version of <c>1.3.6.1.4.1.311.21.7</c>.</summary>
    public int? TemplateMajorVersion { get; init; }

    /// <summary>Template minor version of <c>1.3.6.1.4.1.311.21.7</c>.</summary>
    public int? TemplateMinorVersion { get; init; }

    /// <summary>The SID of the security extension <c>szOID_NTDS_CA_SECURITY_EXT</c> (<c>1.3.6.1.4.1.311.25.2</c>), or null.</summary>
    public string? SidExtension { get; init; }

    /// <summary>Subject and issuer equal and the signature verifies with its own key.</summary>
    public bool IsSelfSigned { get; init; }

    /// <summary>Parses a DER certificate; null when it is malformed.</summary>
    public static CertificateSummary? Of(ReadOnlySpan<byte> der) => throw new NotImplementedException("P1 C1: CertificateSummary.Of");

    /// <summary>Kind flags only (never a subject, issuer, name, serial or thumbprint).</summary>
    public override string ToString() => $"{nameof(CertificateSummary)}({(IsCa ? "CA" : "end entity")}{(IsSelfSigned ? ", self-signed" : string.Empty)})";
}
