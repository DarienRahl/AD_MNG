using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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

    /// <summary>
    /// Parses a DER certificate; null when it is malformed. A malformed extension leaves only its own members at their
    /// defaults (the certificate itself still parses).
    /// </summary>
    public static CertificateSummary? Of(ReadOnlySpan<byte> der)
    {
        if (der.IsEmpty)
        {
            return null;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException)
        {
            return null;
        }

        using (certificate)
        {
            try
            {
                return From(certificate);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>Kind flags only (never a subject, issuer, name, serial or thumbprint).</summary>
    public override string ToString() => $"{nameof(CertificateSummary)}({(IsCa ? "CA" : "end entity")}{(IsSelfSigned ? ", self-signed" : string.Empty)})";

    private static CertificateSummary From(X509Certificate2 certificate)
    {
        var raw = certificate.RawDataMemory;
        var fields = new ExtensionFields();
        foreach (var extension in certificate.Extensions)
        {
            fields.Read(extension.Oid?.Value, extension.RawData);
        }

        // Self-signed: the same name and the signature verifies with the certificate's own key (a renewal link certificate
        // of a CA has the same name but is signed by the other key). Where the signature cannot be checked here (an
        // unsupported algorithm or PSS salt length), the key identifiers decide.
        var namesEqual = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        var selfSigned = namesEqual
            && (PkiDer.VerifySignature(raw, certificate)
                ?? (fields.AuthorityKeyIdentifier is null || string.Equals(fields.AuthorityKeyIdentifier, fields.SubjectKeyIdentifier, StringComparison.Ordinal)));
        var sanUris = fields.San.Uri;
        return new CertificateSummary
        {
            Subject = certificate.SubjectName.Name,
            Issuer = certificate.IssuerName.Name,
            SerialNumber = certificate.SerialNumber,
            NotBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            SanDns = fields.San.Dns,
            SanUpn = fields.San.Upn,
            SanEmail = fields.San.Email,
            SanUri = sanUris,
            SanSid = sanUris.Where(u => u.StartsWith(PkiDer.SidUrlPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(u => u[PkiDer.SidUrlPrefix.Length..])
                .FirstOrDefault(s => s.Length > 0),
            Ekus = fields.Ekus,
            ApplicationPolicies = fields.ApplicationPolicies,
            KeyAlgorithm = KeyAlgorithmName(certificate.PublicKey.Oid),
            KeySize = KeySizeOf(certificate),
            SignatureAlgorithm = SignatureAlgorithmName(certificate.SignatureAlgorithm),
            Thumbprint = Convert.ToHexString(SHA1.HashData(raw.Span)),
            Sha256 = Convert.ToHexString(SHA256.HashData(raw.Span)),
            SubjectKeyIdentifier = fields.SubjectKeyIdentifier,
            AuthorityKeyIdentifier = fields.AuthorityKeyIdentifier,
            IsCa = fields.IsCa,
            PathLength = fields.PathLength,
            AiaCaIssuers = fields.AiaCaIssuers,
            AiaOcsp = fields.AiaOcsp,
            CrlDistributionPoints = fields.CrlDistributionPoints,
            FreshestCrl = fields.FreshestCrl,
            TemplateName = fields.TemplateName,
            TemplateOid = fields.TemplateOid,
            TemplateMajorVersion = fields.TemplateMajorVersion,
            TemplateMinorVersion = fields.TemplateMinorVersion,
            SidExtension = fields.SidExtension,
            IsSelfSigned = selfSigned,
        };
    }

    /// <summary>The names <c>certutil</c> shows, the same on every platform; else the friendly name or the OID.</summary>
    private static string KeyAlgorithmName(Oid oid) => oid.Value switch
    {
        "1.2.840.113549.1.1.1" => "RSA",
        "1.2.840.10045.2.1" => "ECC",
        "1.2.840.10040.4.1" => "DSA",
        "1.3.101.112" => "Ed25519",
        "1.3.101.113" => "Ed448",
        var value => oid.FriendlyName is { Length: > 0 } friendly ? friendly : value ?? string.Empty,
    };

    private static string SignatureAlgorithmName(Oid oid) => oid.Value switch
    {
        "1.2.840.113549.1.1.4" => "md5RSA",
        "1.2.840.113549.1.1.5" => "sha1RSA",
        "1.2.840.113549.1.1.10" => "RSASSA-PSS",
        "1.2.840.113549.1.1.11" => "sha256RSA",
        "1.2.840.113549.1.1.12" => "sha384RSA",
        "1.2.840.113549.1.1.13" => "sha512RSA",
        "1.2.840.10045.4.1" => "sha1ECDSA",
        "1.2.840.10045.4.3.2" => "sha256ECDSA",
        "1.2.840.10045.4.3.3" => "sha384ECDSA",
        "1.2.840.10045.4.3.4" => "sha512ECDSA",
        var value => oid.FriendlyName is { Length: > 0 } friendly ? friendly : value ?? string.Empty,
    };

    private static int? KeySizeOf(X509Certificate2 certificate)
    {
        try
        {
            using (var rsa = certificate.GetRSAPublicKey())
            {
                if (rsa is not null)
                {
                    return rsa.KeySize;
                }
            }

            using (var ecdsa = certificate.GetECDsaPublicKey())
            {
                if (ecdsa is not null)
                {
                    return ecdsa.KeySize;
                }
            }

            using var dsa = certificate.GetDSAPublicKey();
            return dsa?.KeySize;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The members read from the extensions; each extension is parsed on its own (a malformed one is skipped).</summary>
    private sealed class ExtensionFields
    {
        public GeneralNameLists San { get; } = new();

        public List<string> Ekus { get; } = [];

        public List<string> ApplicationPolicies { get; } = [];

        public string? SubjectKeyIdentifier { get; private set; }

        public string? AuthorityKeyIdentifier { get; private set; }

        public bool IsCa { get; private set; }

        public int? PathLength { get; private set; }

        public List<string> AiaCaIssuers { get; } = [];

        public List<string> AiaOcsp { get; } = [];

        public List<string> CrlDistributionPoints { get; } = [];

        public List<string> FreshestCrl { get; } = [];

        public string? TemplateName { get; private set; }

        public string? TemplateOid { get; private set; }

        public int? TemplateMajorVersion { get; private set; }

        public int? TemplateMinorVersion { get; private set; }

        public string? SidExtension { get; private set; }

        public void Read(string? oid, byte[] value)
        {
            try
            {
                switch (oid)
                {
                    case PkiDer.SubjectAltNameOid:
                        PkiDer.ReadGeneralNames(new AsnReader(value, AsnEncodingRules.DER).ReadSequence(), San);
                        break;
                    case PkiDer.EkuOid:
                        Ekus.AddRange(PkiDer.ReadOidSequence(value));
                        break;
                    case PkiDer.ApplicationPoliciesOid:
                        ApplicationPolicies.AddRange(PkiDer.ReadPolicyOids(value));
                        break;
                    case PkiDer.SubjectKeyIdentifierOid:
                        SubjectKeyIdentifier = Convert.ToHexString(new AsnReader(value, AsnEncodingRules.DER).ReadOctetString());
                        break;
                    case PkiDer.AuthorityKeyIdentifierOid:
                        AuthorityKeyIdentifier = PkiDer.ReadAuthorityKeyIdentifier(value, AsnEncodingRules.DER);
                        break;
                    case PkiDer.BasicConstraintsOid:
                        ReadBasicConstraints(value);
                        break;
                    case PkiDer.AuthorityInfoAccessOid:
                        ReadAuthorityInfoAccess(value);
                        break;
                    case PkiDer.CrlDistributionPointsOid:
                        CrlDistributionPoints.AddRange(PkiDer.ReadDistributionPointUrls(value, AsnEncodingRules.DER));
                        break;
                    case PkiDer.FreshestCrlOid:
                        FreshestCrl.AddRange(PkiDer.ReadDistributionPointUrls(value, AsnEncodingRules.DER));
                        break;
                    case PkiDer.TemplateNameOid:
                        TemplateName = PkiDer.ReadAnyString(new AsnReader(value, AsnEncodingRules.DER));
                        break;
                    case PkiDer.TemplateInformationOid:
                        ReadTemplateInformation(value);
                        break;
                    case PkiDer.SecurityExtensionOid:
                        var names = new GeneralNameLists();
                        PkiDer.ReadGeneralNames(new AsnReader(value, AsnEncodingRules.DER).ReadSequence(), names);
                        SidExtension = names.ObjectSid.FirstOrDefault();
                        break;
                }
            }
            catch (AsnContentException)
            {
                // A malformed extension: its members keep their defaults.
            }
            catch (CryptographicException)
            {
                // The same (an OID or a string the platform cannot decode).
            }
        }

        private void ReadBasicConstraints(byte[] value)
        {
            var constraints = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
            if (constraints.HasData && constraints.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
            {
                IsCa = constraints.ReadBoolean();
            }

            if (constraints.HasData && constraints.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                PathLength = PkiDer.ReadOptionalInt32(constraints);
            }
        }

        private void ReadAuthorityInfoAccess(byte[] value)
        {
            var descriptions = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
            while (descriptions.HasData)
            {
                var description = descriptions.ReadSequence();
                var method = description.ReadObjectIdentifier();
                var location = new GeneralNameLists();
                PkiDer.ReadGeneralNames(description, location);
                switch (method)
                {
                    case PkiDer.CaIssuersAccessMethod:
                        AiaCaIssuers.AddRange(location.Uri);
                        break;
                    case PkiDer.OcspAccessMethod:
                        AiaOcsp.AddRange(location.Uri);
                        break;
                }
            }
        }

        /// <summary>MS-WCCE 2.2.2.7.7.2: <c>SEQUENCE { templateID OID, templateMajorVersion INTEGER OPTIONAL, templateMinorVersion INTEGER OPTIONAL }</c>.</summary>
        private void ReadTemplateInformation(byte[] value)
        {
            var template = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
            TemplateOid = template.ReadObjectIdentifier();
            if (template.HasData && template.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                TemplateMajorVersion = PkiDer.ReadOptionalInt32(template);
            }

            if (template.HasData && template.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                TemplateMinorVersion = PkiDer.ReadOptionalInt32(template);
            }
        }
    }
}

/// <summary>The general names of one <c>GeneralNames</c> value (2.1): the kinds the PKI documents show.</summary>
internal sealed class GeneralNameLists
{
    /// <summary><c>dNSName</c> [2].</summary>
    public List<string> Dns { get; } = [];

    /// <summary><c>rfc822Name</c> [1].</summary>
    public List<string> Email { get; } = [];

    /// <summary><c>uniformResourceIdentifier</c> [6].</summary>
    public List<string> Uri { get; } = [];

    /// <summary><c>otherName</c> UPN (<c>1.3.6.1.4.1.311.20.2.3</c>).</summary>
    public List<string> Upn { get; } = [];

    /// <summary><c>otherName</c> <c>szOID_NTDS_OBJECTSID</c> (<c>1.3.6.1.4.1.311.25.2.1</c>, the SID as ASCII in an OCTET STRING).</summary>
    public List<string> ObjectSid { get; } = [];

    /// <summary>Counts only.</summary>
    public override string ToString() => $"{nameof(GeneralNameLists)}({Dns.Count + Email.Count + Uri.Count + Upn.Count + ObjectSid.Count} names)";
}

/// <summary>
/// The DER readers shared by <see cref="CertificateSummary"/>, <see cref="CrlSummary"/>, the overview reader and the TLS
/// evaluations (RFC 5280, MS-WCCE): <c>System.Formats.Asn1</c> only, nothing platform-specific.
/// </summary>
internal static class PkiDer
{
    public const string SubjectAltNameOid = "2.5.29.17";
    public const string EkuOid = "2.5.29.37";
    public const string SubjectKeyIdentifierOid = "2.5.29.14";
    public const string AuthorityKeyIdentifierOid = "2.5.29.35";
    public const string BasicConstraintsOid = "2.5.29.19";
    public const string CrlDistributionPointsOid = "2.5.29.31";
    public const string CrlNumberOid = "2.5.29.20";
    public const string DeltaCrlIndicatorOid = "2.5.29.27";
    public const string FreshestCrlOid = "2.5.29.46";
    public const string AuthorityInfoAccessOid = "1.3.6.1.5.5.7.1.1";
    public const string CaIssuersAccessMethod = "1.3.6.1.5.5.7.48.2";
    public const string OcspAccessMethod = "1.3.6.1.5.5.7.48.1";

    /// <summary>szOID_APPLICATION_CERT_POLICIES (CertificatePolicies syntax).</summary>
    public const string ApplicationPoliciesOid = "1.3.6.1.4.1.311.21.10";

    /// <summary>szOID_ENROLL_CERTTYPE_EXTENSION: the v1 template name (a BMPString).</summary>
    public const string TemplateNameOid = "1.3.6.1.4.1.311.20.2";

    /// <summary>szOID_CERTIFICATE_TEMPLATE: the template OID with its major and minor version.</summary>
    public const string TemplateInformationOid = "1.3.6.1.4.1.311.21.7";

    /// <summary>szOID_NTDS_CA_SECURITY_EXT (KB5014754).</summary>
    public const string SecurityExtensionOid = "1.3.6.1.4.1.311.25.2";

    /// <summary>szOID_NTDS_OBJECTSID: the other name inside <see cref="SecurityExtensionOid"/>.</summary>
    public const string ObjectSidOtherNameOid = "1.3.6.1.4.1.311.25.2.1";

    /// <summary>szOID_NT_PRINCIPAL_NAME: the UPN other name of a SAN.</summary>
    public const string UpnOtherNameOid = "1.3.6.1.4.1.311.20.2.3";

    /// <summary>The SAN URL of the strong mapping (KB5014754, September 2022).</summary>
    public const string SidUrlPrefix = "tag:microsoft.com,2022-09-14:sid:";

    private static readonly Asn1Tag Explicit0 = new(TagClass.ContextSpecific, 0, isConstructed: true);

    /// <summary>
    /// The one-byte <c>0x00</c> (or empty) value AD keeps in the mandatory <c>cACertificate</c>,
    /// <c>certificateRevocationList</c> and <c>authorityRevocationList</c> of an empty <c>certificationAuthority</c> object –
    /// not a malformed certificate or CRL.
    /// </summary>
    public static bool IsPlaceholder(ReadOnlySpan<byte> value) => value.Length == 0 || (value.Length == 1 && value[0] == 0);

    /// <summary>SHA-256 of a DER value, upper-case hex.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));

    /// <summary>
    /// Reads the <c>GeneralName</c> elements left in <paramref name="names"/> (the content of a <c>GeneralNames</c>
    /// SEQUENCE, a <c>fullName</c> or the location of an <c>AccessDescription</c>) into <paramref name="sink"/>; other name
    /// kinds and an element that does not decode are skipped.
    /// </summary>
    public static void ReadGeneralNames(AsnReader names, GeneralNameLists sink)
    {
        while (names.HasData)
        {
            var tag = names.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific)
            {
                names.ReadEncodedValue();
                continue;
            }

            switch (tag.TagValue)
            {
                case 0 when tag.IsConstructed:
                    var otherName = names.ReadSequence(tag);
                    try
                    {
                        ReadOtherName(otherName, sink);
                    }
                    catch (AsnContentException)
                    {
                        // One malformed other name: the next element is still read.
                    }

                    break;
                case 1:
                    Add(sink.Email, ReadIa5(names, tag));
                    break;
                case 2:
                    Add(sink.Dns, ReadIa5(names, tag));
                    break;
                case 6:
                    Add(sink.Uri, ReadIa5(names, tag));
                    break;
                default:
                    names.ReadEncodedValue();
                    break;
            }
        }

        static void Add(List<string> list, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                list.Add(value);
            }
        }
    }

    /// <summary>
    /// The URLs of a CRLDistributionPoints value (the CDP and Freshest CRL extensions, RFC 5280 4.2.1.13): the URIs of each
    /// point's <c>fullName</c> (<c>distributionPoint [0]</c> → <c>fullName [0]</c>); <c>nameRelativeToCRLIssuer</c>, the
    /// reasons and the CRL issuer are not locations.
    /// </summary>
    public static IReadOnlyList<string> ReadDistributionPointUrls(ReadOnlyMemory<byte> value, AsnEncodingRules rules)
    {
        var names = new GeneralNameLists();
        var points = new AsnReader(value, rules).ReadSequence();
        while (points.HasData)
        {
            var point = points.ReadSequence();
            if (!point.HasData || !point.PeekTag().HasSameClassAndValue(Explicit0))
            {
                continue;
            }

            var name = point.ReadSequence(Explicit0);
            if (name.HasData && name.PeekTag().HasSameClassAndValue(Explicit0))
            {
                ReadGeneralNames(name.ReadSequence(Explicit0), names);
            }
        }

        return names.Uri;
    }

    /// <summary>The key identifier of an AuthorityKeyIdentifier value (<c>keyIdentifier [0] IMPLICIT OCTET STRING</c>), upper-case hex, or null.</summary>
    public static string? ReadAuthorityKeyIdentifier(ReadOnlyMemory<byte> value, AsnEncodingRules rules)
    {
        var identifier = new AsnReader(value, rules).ReadSequence();
        var keyId = new Asn1Tag(TagClass.ContextSpecific, 0);
        return identifier.HasData && identifier.PeekTag().HasSameClassAndValue(keyId)
            ? Convert.ToHexString(identifier.ReadOctetString(keyId))
            : null;
    }

    /// <summary>A <c>SEQUENCE OF OBJECT IDENTIFIER</c> (the EKU extension).</summary>
    public static List<string> ReadOidSequence(ReadOnlyMemory<byte> value)
    {
        var oids = new List<string>();
        var sequence = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
        while (sequence.HasData)
        {
            oids.Add(sequence.ReadObjectIdentifier());
        }

        return oids;
    }

    /// <summary>The policy identifiers of a CertificatePolicies-syntax value (the application policies of AD CS).</summary>
    public static List<string> ReadPolicyOids(ReadOnlyMemory<byte> value)
    {
        var oids = new List<string>();
        var sequence = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
        while (sequence.HasData)
        {
            oids.Add(sequence.ReadSequence().ReadObjectIdentifier());
        }

        return oids;
    }

    /// <summary>A character string of any of the usual string types (UTF8, IA5, Printable, BMP, Visible, T61, Numeric), or null (skipped).</summary>
    public static string? ReadAnyString(AsnReader reader)
    {
        var tag = reader.PeekTag();
        if (tag.TagClass == TagClass.Universal && !tag.IsConstructed)
        {
            var number = (UniversalTagNumber)tag.TagValue;
            if (number is UniversalTagNumber.UTF8String or UniversalTagNumber.IA5String or UniversalTagNumber.PrintableString
                or UniversalTagNumber.BMPString or UniversalTagNumber.VisibleString or UniversalTagNumber.T61String or UniversalTagNumber.NumericString)
            {
                try
                {
                    return reader.ReadCharacterString(number);
                }
                catch (AsnContentException)
                {
                    // Not valid for its type: skipped below.
                }
            }
        }

        reader.ReadEncodedValue();
        return null;
    }

    /// <summary>An INTEGER that fits an <see cref="int"/>, else null (the value is consumed either way).</summary>
    public static int? ReadOptionalInt32(AsnReader reader)
    {
        if (reader.TryReadInt32(out var value))
        {
            return value;
        }

        reader.ReadEncodedValue();
        return null;
    }

    /// <summary>
    /// Whether <paramref name="signer"/>'s public key verifies the signature of the DER certificate
    /// <paramref name="certificateDer"/>: RSA PKCS #1 v1.5 (MD5, SHA-1, SHA-2), RSA-PSS with the salt length of its hash,
    /// ECDSA and DSA. Null when it cannot be decided here (another algorithm or PSS salt length, a hash the platform
    /// refuses).
    /// </summary>
    public static bool? VerifySignature(ReadOnlyMemory<byte> certificateDer, X509Certificate2 signer)
    {
        try
        {
            var certificate = new AsnReader(certificateDer, AsnEncodingRules.DER).ReadSequence();
            var tbs = certificate.ReadEncodedValue();
            var algorithm = certificate.ReadSequence();
            var oid = algorithm.ReadObjectIdentifier();
            var parameters = algorithm.HasData ? algorithm.ReadEncodedValue() : ReadOnlyMemory<byte>.Empty;
            var signature = certificate.ReadBitString(out var unusedBits);
            if (unusedBits != 0)
            {
                return false;
            }

            return oid switch
            {
                "1.2.840.113549.1.1.4" => VerifyRsa(signer, tbs, signature, HashAlgorithmName.MD5, RSASignaturePadding.Pkcs1),
                "1.2.840.113549.1.1.5" => VerifyRsa(signer, tbs, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1),
                "1.2.840.113549.1.1.11" => VerifyRsa(signer, tbs, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "1.2.840.113549.1.1.12" => VerifyRsa(signer, tbs, signature, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1),
                "1.2.840.113549.1.1.13" => VerifyRsa(signer, tbs, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1),
                // A PSS signature that does not verify may use another salt length: undecided, not false.
                "1.2.840.113549.1.1.10" => PssHash(parameters) is { } pss && VerifyRsa(signer, tbs, signature, pss, RSASignaturePadding.Pss) ? true : null,
                "1.2.840.10045.4.1" => VerifyEcdsa(signer, tbs, signature, HashAlgorithmName.SHA1),
                "1.2.840.10045.4.3.2" => VerifyEcdsa(signer, tbs, signature, HashAlgorithmName.SHA256),
                "1.2.840.10045.4.3.3" => VerifyEcdsa(signer, tbs, signature, HashAlgorithmName.SHA384),
                "1.2.840.10045.4.3.4" => VerifyEcdsa(signer, tbs, signature, HashAlgorithmName.SHA512),
                "1.2.840.10040.4.3" => VerifyDsa(signer, tbs, signature, HashAlgorithmName.SHA1),
                "2.16.840.1.101.3.4.3.2" => VerifyDsa(signer, tbs, signature, HashAlgorithmName.SHA256),
                _ => null,
            };
        }
        catch (AsnContentException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool VerifyRsa(X509Certificate2 signer, ReadOnlyMemory<byte> tbs, byte[] signature, HashAlgorithmName hash, RSASignaturePadding padding)
    {
        using var rsa = signer.GetRSAPublicKey();
        return rsa is not null && rsa.VerifyData(tbs.Span, signature, hash, padding);
    }

    private static bool VerifyEcdsa(X509Certificate2 signer, ReadOnlyMemory<byte> tbs, byte[] signature, HashAlgorithmName hash)
    {
        using var ecdsa = signer.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.VerifyData(tbs.Span, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool VerifyDsa(X509Certificate2 signer, ReadOnlyMemory<byte> tbs, byte[] signature, HashAlgorithmName hash)
    {
        using var dsa = signer.GetDSAPublicKey();
        return dsa is not null && dsa.VerifyData(tbs.Span, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>
    /// The hash of RSASSA-PSS-params (RFC 4055: explicit tags, SHA-1 and salt 20 by default) when its salt length is the
    /// hash length (what <see cref="RSASignaturePadding.Pss"/> verifies), else null.
    /// </summary>
    private static HashAlgorithmName? PssHash(ReadOnlyMemory<byte> parameters)
    {
        if (parameters.IsEmpty)
        {
            return null;
        }

        var sequence = new AsnReader(parameters, AsnEncodingRules.DER).ReadSequence();
        var hash = HashAlgorithmName.SHA1;
        var saltLength = 20;
        if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(Explicit0))
        {
            var oid = sequence.ReadSequence(Explicit0).ReadSequence().ReadObjectIdentifier();
            hash = oid switch
            {
                "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
                "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
                "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                _ => default,
            };
        }

        var maskGen = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
        if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(maskGen))
        {
            sequence.ReadEncodedValue();
        }

        var salt = new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true);
        if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(salt))
        {
            saltLength = ReadOptionalInt32(sequence.ReadSequence(salt)) ?? -1;
        }

        var hashLength = hash == HashAlgorithmName.SHA1 ? 20
            : hash == HashAlgorithmName.SHA256 ? 32
            : hash == HashAlgorithmName.SHA384 ? 48
            : hash == HashAlgorithmName.SHA512 ? 64
            : 0;
        return hashLength > 0 && saltLength == hashLength ? hash : null;
    }

    /// <summary>An <c>otherName</c>: <c>SEQUENCE { type-id OID, value [0] EXPLICIT ANY }</c> (implicitly tagged [0]).</summary>
    private static void ReadOtherName(AsnReader otherName, GeneralNameLists sink)
    {
        var typeId = otherName.ReadObjectIdentifier();
        if (!otherName.HasData || !otherName.PeekTag().HasSameClassAndValue(Explicit0))
        {
            return;
        }

        var value = otherName.ReadSequence(Explicit0);
        if (!value.HasData)
        {
            return;
        }

        switch (typeId)
        {
            case UpnOtherNameOid when ReadAnyString(value) is { Length: > 0 } upn:
                sink.Upn.Add(upn);
                break;
            case ObjectSidOtherNameOid:
                var sid = value.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString)
                    ? Encoding.ASCII.GetString(value.ReadOctetString()).TrimEnd('\0')
                    : ReadAnyString(value);
                if (!string.IsNullOrEmpty(sid))
                {
                    sink.ObjectSid.Add(sid);
                }

                break;
        }
    }

    /// <summary>An implicitly tagged IA5String; a value with non-ASCII bytes is decoded as UTF-8 (some issuers write it so).</summary>
    private static string? ReadIa5(AsnReader reader, Asn1Tag tag)
    {
        try
        {
            return reader.ReadCharacterString(UniversalTagNumber.IA5String, tag);
        }
        catch (AsnContentException)
        {
            // Not 7-bit: below.
        }

        try
        {
            return Encoding.UTF8.GetString(reader.ReadOctetString(tag));
        }
        catch (AsnContentException)
        {
            reader.ReadEncodedValue();
            return null;
        }
    }
}
