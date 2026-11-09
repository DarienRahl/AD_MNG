using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AdMng.Testing.Pki;

/// <summary>What <see cref="PkiSamples.Issue"/> puts into a certificate (synthetic test data only).</summary>
public sealed record PkiCertificateOptions
{
    /// <summary>The subject's common name (Polish diacritics welcome).</summary>
    public required string CommonName { get; init; }

    /// <summary>Further RDNs after the CN (e.g. <c>OU=IT</c>), most specific first.</summary>
    public IReadOnlyList<(string Type, string Value)> ExtraRdns { get; init; } = [];

    public required long SerialNumber { get; init; }

    public required DateTimeOffset NotBefore { get; init; }

    public required DateTimeOffset NotAfter { get; init; }

    /// <summary>Extended key usage OIDs (empty: no EKU extension).</summary>
    public IReadOnlyList<string> Ekus { get; init; } = [];

    /// <summary>Application policy OIDs (<c>1.3.6.1.4.1.311.21.10</c>; empty: none).</summary>
    public IReadOnlyList<string> ApplicationPolicies { get; init; } = [];

    public IReadOnlyList<string> DnsNames { get; init; } = [];

    public string? Upn { get; init; }

    public string? Email { get; init; }

    /// <summary>The SID put into the SAN URL <c>tag:microsoft.com,2022-09-14:sid:</c> and the security extension.</summary>
    public string? Sid { get; init; }

    /// <summary>Put <see cref="Sid"/> into the SID security extension <c>1.3.6.1.4.1.311.25.2</c> too (default true).</summary>
    public bool SidExtension { get; init; } = true;

    /// <summary>A v1 template name (<c>1.3.6.1.4.1.311.20.2</c>, BMPString).</summary>
    public string? TemplateName { get; init; }

    /// <summary>A template OID (<c>1.3.6.1.4.1.311.21.7</c>) with major version 100 and minor version 4.</summary>
    public string? TemplateOid { get; init; }

    public bool IsCa { get; init; }

    /// <summary>The path length constraint of a CA certificate (null: none).</summary>
    public int? PathLength { get; init; }

    /// <summary>The issuer; null: self-signed.</summary>
    public X509Certificate2? Issuer { get; init; }

    /// <summary>AIA CA Issuers URLs (default: the issuing CA's URLs of <see cref="PkiSamples"/> when issued by it).</summary>
    public IReadOnlyList<string>? AiaCaIssuers { get; init; }

    public IReadOnlyList<string>? AiaOcsp { get; init; }

    public IReadOnlyList<string>? CrlDistributionPoints { get; init; }

    /// <summary>Use an RSA 2048 key instead of ECDSA P-256 (CAs).</summary>
    public bool Rsa { get; init; }
}

/// <summary>
/// Synthetic certificates, CRLs and key credentials of the P1 tests (docs/dev/p-certificate-services.md 2.10): the forest
/// PKI of <see cref="FakeDirectoryBuilder.AddPublicKeySample"/> – the enterprise root <see cref="RootCaName"/> and the
/// issuing <see cref="IssuingCaName"/> (RSA 2048), the DC's LDAPS and KDC certificates, the KRA agent, user certificates,
/// base and delta CRLs – generated in memory with <see cref="CertificateRequest"/> and
/// <see cref="CertificateRevocationListBuilder"/> (the delta and the CRL without <c>nextUpdate</c> are encoded with
/// <see cref="AsnWriter"/>: the builder writes neither). Fixed dates around the fake clock <see cref="Now"/>, deterministic
/// serial numbers, Polish names with diacritics; never written to disk or a certificate store. The CA certificates are
/// created once per test process (their keys are random, so their DER differs between processes, never within one).
/// </summary>
public static class PkiSamples
{
    /// <summary>The fake clock of the P1 tests (the default of <see cref="ManualTimeProvider"/>).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public const string Organization = "Kontoso Sp. z o.o.";

    public const string RootCaName = "Kontoso Główny CA";

    public const string IssuingCaName = "Kontoso Wystawiający CA";

    /// <summary>The computer (cn) of the root CA in the sample domain.</summary>
    public const string RootCaComputer = "SRV-CA01";

    /// <summary>The computer (cn) of the issuing CA in the sample domain.</summary>
    public const string IssuingCaComputer = "SRV-CA02";

    public const string RootCaHost = "srv-ca01.contoso.test";

    public const string IssuingCaHost = "srv-ca02.contoso.test";

    /// <summary>The DC of the fake directory.</summary>
    public const string DcHost = "dc01.contoso.test";

    public const string DomainDns = "contoso.test";

    public const string ConfigurationDn = "CN=Configuration,DC=contoso,DC=test";

    public const string PublicKeyServicesDn = "CN=Public Key Services,CN=Services," + ConfigurationDn;

    /// <summary>The forest's OID base (<c>msPKI-Cert-Template-OID</c> of the templates and issuance policies).</summary>
    public const string ForestOid = "1.3.6.1.4.1.311.21.8.4823519.1237865.8352142.2814157.6720531.42";

    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    public const string SmartCardLogon = "1.3.6.1.4.1.311.20.2.2";
    public const string KdcAuthentication = "1.3.6.1.5.2.3.5";
    public const string CertificateRequestAgent = "1.3.6.1.4.1.311.20.2.1";
    public const string AnyPurpose = "2.5.29.37.0";
    public const string SecureEmail = "1.3.6.1.5.5.7.3.4";
    public const string EncryptingFileSystem = "1.3.6.1.4.1.311.10.3.4";
    public const string KeyRecoveryAgent = "1.3.6.1.4.1.311.21.6";

    /// <summary>The OCSP responder of the issuing CA.</summary>
    public const string OcspUrl = "http://pki.contoso.test/ocsp";

    /// <summary>A <c>file:</c> location (never contacted: <i>nie sprawdzono</i>).</summary>
    public static string IssuingCrlFileUrl => @"file://\\" + IssuingCaHost + @"\CertEnroll\" + Sanitized(IssuingCaName) + ".crl";

    public static string RootCrlHttpUrl => "http://pki.contoso.test/CertEnroll/" + Url(Sanitized(RootCaName)) + ".crl";

    public static string RootCrtHttpUrl => "http://pki.contoso.test/CertEnroll/" + RootCaHost + "_" + Url(Sanitized(RootCaName)) + ".crt";

    public static string IssuingCrlHttpUrl => "http://pki.contoso.test/CertEnroll/" + Url(Sanitized(IssuingCaName)) + ".crl";

    public static string IssuingDeltaCrlHttpUrl => "http://pki.contoso.test/CertEnroll/" + Url(Sanitized(IssuingCaName)) + "+.crl";

    public static string IssuingCrtHttpUrl => "http://pki.contoso.test/CertEnroll/" + IssuingCaHost + "_" + Url(Sanitized(IssuingCaName)) + ".crt";

    public static string RootCrlLdapUrl => CdpLdapUrl(RootCaName, RootCaComputer);

    public static string IssuingCrlLdapUrl => CdpLdapUrl(IssuingCaName, IssuingCaComputer);

    public static string RootCrtLdapUrl => AiaLdapUrl(RootCaName);

    public static string IssuingCrtLdapUrl => AiaLdapUrl(IssuingCaName);

    private static readonly Lazy<RSA> RootKey = new(() => RSA.Create(2048));
    private static readonly Lazy<RSA> IssuingKey = new(() => RSA.Create(2048));

    private static readonly Lazy<X509Certificate2> LazyRoot = new(() => CreateCa(RootCaName, 0x1A2B3C4D01, new(2021, 3, 1, 8, 0, 0, TimeSpan.Zero),
        new(2041, 3, 1, 8, 0, 0, TimeSpan.Zero), RootKey.Value, issuer: null, issuerKey: null, pathLength: null));

    private static readonly Lazy<X509Certificate2> LazyIssuing = new(() => CreateCa(IssuingCaName, 0x1A2B3C4D02, new(2024, 3, 1, 8, 0, 0, TimeSpan.Zero),
        new(2027, 2, 28, 8, 0, 0, TimeSpan.Zero), IssuingKey.Value, LazyRoot.Value, RootKey.Value, pathLength: 0));

    /// <summary>
    /// The enterprise root CA (self-signed, with its private key; 2021-03-01 – 2041-03-01). Shared by the process – never
    /// dispose it.
    /// </summary>
    public static X509Certificate2 RootCa => LazyRoot.Value;

    /// <summary>
    /// The issuing enterprise CA (issued by <see cref="RootCa"/>, path length 0, with its private key; 2024-03-01 –
    /// 2027-02-28: within the 180-day CA threshold at <see cref="Now"/>). Shared by the process – never dispose it.
    /// </summary>
    public static X509Certificate2 IssuingCa => LazyIssuing.Value;

    public static byte[] RootCaDer => RootCa.RawData;

    public static byte[] IssuingCaDer => IssuingCa.RawData;

    /// <summary>Not a certificate or a CRL (a truncated DER SEQUENCE).</summary>
    public static byte[] MalformedDer => [0x30, 0x82, 0x01, 0x00, 0x02, 0x01];

    /// <summary>
    /// The DC's LDAPS certificate (issued by <see cref="IssuingCa"/>, SAN <see cref="DcHost"/> and <see cref="DomainDns"/>,
    /// Server + Client Authentication, template <c>DomainController</c>; 2026-01-10 – 2027-01-10).
    /// </summary>
    public static byte[] DcLdapsDer() => IssueDer(new PkiCertificateOptions
    {
        CommonName = DcHost,
        SerialNumber = 0x6100,
        NotBefore = new(2026, 1, 10, 8, 0, 0, TimeSpan.Zero),
        NotAfter = new(2027, 1, 10, 8, 0, 0, TimeSpan.Zero),
        Ekus = [ServerAuthentication, ClientAuthentication],
        DnsNames = [DcHost, DomainDns],
        TemplateName = "DomainController",
        Issuer = IssuingCa,
    });

    /// <summary>
    /// The DC's KDC certificate published on its object (template <c>KerberosAuthentication</c>: KDC Authentication,
    /// Smart-Card Logon, Client and Server Authentication; issuer in NTAuth; 2026-02-01 – 2027-02-01).
    /// </summary>
    public static byte[] DcKdcDer() => IssueDer(new PkiCertificateOptions
    {
        CommonName = DcHost,
        SerialNumber = 0x6101,
        NotBefore = new(2026, 2, 1, 8, 0, 0, TimeSpan.Zero),
        NotAfter = new(2027, 2, 1, 8, 0, 0, TimeSpan.Zero),
        Ekus = [ClientAuthentication, ServerAuthentication, SmartCardLogon, KdcAuthentication],
        DnsNames = [DcHost, DomainDns, "CONTOSO"],
        TemplateOid = ForestOid + ".1.20",
        Issuer = IssuingCa,
    });

    /// <summary>The key recovery agent of <c>CN=KRA</c> (Agent odzyskiwania kluczy; 2026-03-01 – 2028-03-01).</summary>
    public static byte[] KraDer() => IssueDer(new PkiCertificateOptions
    {
        CommonName = "Agent odzyskiwania kluczy Kontoso",
        SerialNumber = 0x6200,
        NotBefore = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero),
        NotAfter = new(2028, 3, 1, 8, 0, 0, TimeSpan.Zero),
        Ekus = [KeyRecoveryAgent],
        TemplateOid = ForestOid + ".1.21",
        Issuer = IssuingCa,
    });

    /// <summary>
    /// A user certificate published on an account (issued by <see cref="IssuingCa"/>, template <c>User</c>, Client
    /// Authentication, secure e-mail, EFS, the UPN and e-mail in the SAN, the SID in the SAN URL and the security extension).
    /// </summary>
    public static byte[] UserCertificateDer(string commonName, string upn, string? sid, long serialNumber, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        IssueDer(new PkiCertificateOptions
        {
            CommonName = commonName,
            SerialNumber = serialNumber,
            NotBefore = notBefore,
            NotAfter = notAfter,
            Ekus = [EncryptingFileSystem, SecureEmail, ClientAuthentication],
            Upn = upn,
            Email = upn,
            Sid = sid,
            TemplateName = "User",
            Issuer = IssuingCa,
        });

    /// <summary>The base CRL of <see cref="RootCa"/> (CRL number 7, 2026-06-01 – 2027-06-01, nothing revoked).</summary>
    public static byte[] RootCrlDer() => BuildCrl(RootCa, 7, new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), new(2027, 6, 1, 8, 0, 0, TimeSpan.Zero), []);

    /// <summary>
    /// The base CRL of <see cref="IssuingCa"/> (CRL number 42, 2026-10-10 – 2026-10-17 12:00: OK at <see cref="Now"/>, two
    /// revoked serials).
    /// </summary>
    public static byte[] IssuingCrlDer() => BuildCrl(IssuingCa, 42, new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
        [(Serial(0x5001), new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero)), (Serial(0x5002), new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero))]);

    /// <summary>
    /// The delta CRL of <see cref="IssuingCa"/> (CRL number 43, Delta CRL Indicator 42, 2026-10-12 – 2026-10-14 00:00: less than
    /// a day left at <see cref="Now"/> (12 h) – <i>Wygasa</i>; one revoked serial).
    /// </summary>
    public static byte[] IssuingDeltaCrlDer() => EncodeCrl(IssuingCa, 43, new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero),
        deltaBaseCrlNumber: 42, [(Serial(0x5003), new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero))]);

    /// <summary>A base CRL of <see cref="IssuingCa"/> without <c>nextUpdate</c> (CRL number 44; its state is <i>nieznany</i>).</summary>
    public static byte[] CrlWithoutNextUpdateDer() => EncodeCrl(IssuingCa, 44, new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), nextUpdate: null, deltaBaseCrlNumber: null, []);

    /// <summary>A certificate of the options (DER; the key is not kept).</summary>
    public static byte[] IssueDer(PkiCertificateOptions options)
    {
        using var certificate = Issue(options);
        return certificate.RawData;
    }

    /// <summary>A certificate of the options with its private key (the caller disposes it).</summary>
    public static X509Certificate2 Issue(PkiCertificateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using RSA? rsa = options.Rsa ? RSA.Create(2048) : null;
        using ECDsa? ecdsa = options.Rsa ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = rsa is not null
            ? new CertificateRequest(Name(options.CommonName, options.ExtraRdns), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(Name(options.CommonName, options.ExtraRdns), ecdsa!, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(options.IsCa, options.PathLength is not null, options.PathLength ?? 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(options.IsCa
            ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature
            : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (options.Ekus.Count > 0)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(Oids(options.Ekus), critical: false));
        }

        if (options.ApplicationPolicies.Count > 0)
        {
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.311.21.10", ApplicationPolicies(options.ApplicationPolicies), critical: false));
        }

        if (options.DnsNames.Count > 0 || options.Upn is not null || options.Email is not null || options.Sid is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var dns in options.DnsNames)
            {
                san.AddDnsName(dns);
            }

            if (options.Upn is { } upn)
            {
                san.AddUserPrincipalName(upn);
            }

            if (options.Email is { } email)
            {
                san.AddEmailAddress(email);
            }

            if (options.Sid is { } sid)
            {
                san.AddUri(new Uri("tag:microsoft.com,2022-09-14:sid:" + sid));
            }

            request.CertificateExtensions.Add(san.Build());
        }

        if (options.Sid is { } securitySid && options.SidExtension)
        {
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.311.25.2", SidExtension(securitySid), critical: false));
        }

        if (options.TemplateName is { } templateName)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.WriteCharacterString(UniversalTagNumber.BMPString, templateName);
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.311.20.2", writer.Encode(), critical: false));
        }

        if (options.TemplateOid is { } templateOid)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence();
            writer.WriteObjectIdentifier(templateOid);
            writer.WriteInteger(100);
            writer.WriteInteger(4);
            writer.PopSequence();
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.311.21.7", writer.Encode(), critical: false));
        }

        var issuedByIssuing = options.Issuer is not null && ReferenceEquals(options.Issuer, LazyIssuing.IsValueCreated ? LazyIssuing.Value : null);
        var issuedByRoot = options.Issuer is not null && ReferenceEquals(options.Issuer, LazyRoot.IsValueCreated ? LazyRoot.Value : null);
        var caIssuers = options.AiaCaIssuers ?? (issuedByIssuing ? [IssuingCrtLdapUrl, IssuingCrtHttpUrl] : issuedByRoot ? [RootCrtLdapUrl, RootCrtHttpUrl] : []);
        var ocsp = options.AiaOcsp ?? (issuedByIssuing ? [OcspUrl] : []);
        if (caIssuers.Count > 0 || ocsp.Count > 0)
        {
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(ocsp, caIssuers, critical: false));
        }

        var cdp = options.CrlDistributionPoints ?? (issuedByIssuing ? [IssuingCrlLdapUrl, IssuingCrlHttpUrl] : issuedByRoot ? [RootCrlLdapUrl, RootCrlHttpUrl] : []);
        if (cdp.Count > 0)
        {
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(cdp, critical: false));
        }

        if (options.Issuer is { } issuer)
        {
            // The generator overload: the issuer may have another key type than the subject, and a test may want a
            // certificate outside the issuer's validity (CertificateRequest.Create(issuer, …) refuses both).
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
            using var issuerRsa = issuer.GetRSAPrivateKey();
            using var issuerEcdsa = issuerRsa is null ? issuer.GetECDsaPrivateKey() : null;
            var issuerGenerator = issuerRsa is not null
                ? X509SignatureGenerator.CreateForRSA(issuerRsa, RSASignaturePadding.Pkcs1)
                : X509SignatureGenerator.CreateForECDsa(issuerEcdsa ?? throw new ArgumentException("The issuer has no private key.", nameof(options)));
            using var issued = request.Create(issuer.SubjectName, issuerGenerator, options.NotBefore, options.NotAfter, Serial(options.SerialNumber));
            return rsa is not null ? issued.CopyWithPrivateKey(rsa) : issued.CopyWithPrivateKey(ecdsa!);
        }

        var generator = rsa is not null ? X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1) : X509SignatureGenerator.CreateForECDsa(ecdsa!);
        using var self = request.Create(request.SubjectName, generator, options.NotBefore, options.NotAfter, Serial(options.SerialNumber));
        return rsa is not null ? self.CopyWithPrivateKey(rsa) : self.CopyWithPrivateKey(ecdsa!);
    }

    /// <summary>
    /// A TLS server certificate for the in-process TLS probe tests (issued by <paramref name="issuer"/> – default
    /// <see cref="IssuingCa"/>, Server Authentication, SAN <paramref name="dnsName"/>), PFX round-tripped with
    /// <c>X509CertificateLoader.LoadPkcs12</c> so SChannel accepts its key on Windows. The caller disposes it.
    /// </summary>
    /// <param name="dnsName">SAN DNS name and CN (default <c>localhost</c>).</param>
    /// <param name="notBefore">Default 2026-01-01.</param>
    /// <param name="notAfter">Default 2036-01-01.</param>
    /// <param name="issuer">The issuer (default <see cref="IssuingCa"/>); a self-signed certificate when <paramref name="selfSigned"/>.</param>
    /// <param name="selfSigned">Self-signed (does not chain to the AD roots).</param>
    /// <param name="serverAuth">Include the Server Authentication EKU.</param>
    public static X509Certificate2 ServerCertificate(
        string dnsName = "localhost",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        X509Certificate2? issuer = null,
        bool selfSigned = false,
        bool serverAuth = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsName);
        using var created = Issue(new PkiCertificateOptions
        {
            CommonName = dnsName,
            SerialNumber = 0x7000 + dnsName.Aggregate(0, (hash, c) => ((hash * 31) + c) & 0x0FFF),
            NotBefore = notBefore ?? new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            NotAfter = notAfter ?? new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Ekus = serverAuth ? [ServerAuthentication] : [ClientAuthentication],
            DnsNames = [dnsName],
            Issuer = selfSigned ? null : issuer ?? IssuingCa,
            AiaCaIssuers = [],
            AiaOcsp = [],
            CrlDistributionPoints = [],
        });
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>A base CRL through <see cref="CertificateRevocationListBuilder"/> (AKI and CRL number written by the builder).</summary>
    public static byte[] BuildCrl(X509Certificate2 issuer, long crlNumber, DateTimeOffset thisUpdate, DateTimeOffset nextUpdate, IReadOnlyList<(byte[] Serial, DateTimeOffset RevokedAt)> revoked)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(revoked);
        var builder = new CertificateRevocationListBuilder();
        foreach (var (serial, revokedAt) in revoked)
        {
            builder.AddEntry(serial, revokedAt, X509RevocationReason.Superseded);
        }

        return builder.Build(issuer, new BigInteger(crlNumber), nextUpdate, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, thisUpdate);
    }

    /// <summary>
    /// A CRL encoded by hand (RFC 5280 5.1, signed with the issuer's RSA key): what the builder cannot write – a missing
    /// <paramref name="nextUpdate"/> and the Delta CRL Indicator (<c>2.5.29.27</c>, critical) of a delta CRL. Extensions: AKI
    /// (the issuer's SKI), CRL number, and the indicator when <paramref name="deltaBaseCrlNumber"/> is given.
    /// </summary>
    public static byte[] EncodeCrl(
        X509Certificate2 issuer,
        long crlNumber,
        DateTimeOffset thisUpdate,
        DateTimeOffset? nextUpdate,
        long? deltaBaseCrlNumber,
        IReadOnlyList<(byte[] Serial, DateTimeOffset RevokedAt)> revoked)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(revoked);
        using var key = issuer.GetRSAPrivateKey() ?? throw new ArgumentException("The issuer needs an RSA private key.", nameof(issuer));
        var ski = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifierBytes.ToArray();

        var tbs = new AsnWriter(AsnEncodingRules.DER);
        tbs.PushSequence();
        tbs.WriteInteger(1); // v2
        WriteSha256WithRsa(tbs);
        tbs.WriteEncodedValue(issuer.SubjectName.RawData);
        tbs.WriteUtcTime(thisUpdate);
        if (nextUpdate is { } next)
        {
            tbs.WriteUtcTime(next);
        }

        if (revoked.Count > 0)
        {
            tbs.PushSequence();
            foreach (var (serial, revokedAt) in revoked)
            {
                tbs.PushSequence();
                tbs.WriteIntegerUnsigned(serial);
                tbs.WriteUtcTime(revokedAt);
                tbs.PopSequence();
            }

            tbs.PopSequence();
        }

        var extensions = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        tbs.PushSequence(extensions);
        tbs.PushSequence();
        WriteExtension(tbs, "2.5.29.35", critical: false, w =>
        {
            w.PushSequence();
            w.WriteOctetString(ski, new Asn1Tag(TagClass.ContextSpecific, 0));
            w.PopSequence();
        });
        WriteExtension(tbs, "2.5.29.20", critical: false, w => w.WriteInteger(crlNumber));
        if (deltaBaseCrlNumber is { } baseNumber)
        {
            WriteExtension(tbs, "2.5.29.27", critical: true, w => w.WriteInteger(baseNumber));
        }

        tbs.PopSequence();
        tbs.PopSequence(extensions);
        tbs.PopSequence();
        var tbsBytes = tbs.Encode();

        var crl = new AsnWriter(AsnEncodingRules.DER);
        crl.PushSequence();
        crl.WriteEncodedValue(tbsBytes);
        WriteSha256WithRsa(crl);
        crl.WriteBitString(key.SignData(tbsBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        crl.PopSequence();
        return crl.Encode();
    }

    /// <summary>
    /// One <c>msDS-KeyCredentialLink</c> value as the DC delivers it (<c>B:&lt;hexlen&gt;:&lt;hex&gt;:&lt;dn&gt;</c>): a
    /// KEYCREDENTIALLINK_BLOB version <c>0x0200</c> with KeyID, KeyHash, KeyMaterial (a synthetic 32-byte value – no real
    /// key), KeyUsage, KeySource, DeviceId, KeyApproximateLastLogonTimeStamp and KeyCreationTime (FILETIME).
    /// </summary>
    public static string KeyCredentialLinkValue(string ownerDn, byte[] keyId, Guid deviceId, DateTimeOffset created, DateTimeOffset? lastLogon = null, byte usage = 0x01, byte source = 0x00)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerDn);
        ArgumentNullException.ThrowIfNull(keyId);
        var tail = new List<byte>();
        Entry(tail, 0x03, SHA256.HashData(Encoding.UTF8.GetBytes("klucz testowy " + deviceId.ToString("D"))));
        Entry(tail, 0x04, [usage]);
        Entry(tail, 0x05, [source]);
        Entry(tail, 0x06, deviceId.ToByteArray());
        if (lastLogon is { } logon)
        {
            Entry(tail, 0x08, BitConverter.GetBytes(logon.ToFileTime()));
        }

        Entry(tail, 0x09, BitConverter.GetBytes(created.ToFileTime()));
        var blob = new List<byte> { 0x00, 0x02, 0x00, 0x00 };
        Entry(blob, 0x01, keyId);
        Entry(blob, 0x02, SHA256.HashData([.. tail]));
        blob.AddRange(tail);
        var hex = Convert.ToHexString([.. blob]);
        return $"B:{hex.Length}:{hex}:{ownerDn}";

        static void Entry(List<byte> target, byte id, byte[] value)
        {
            target.AddRange(BitConverter.GetBytes((ushort)value.Length));
            target.Add(id);
            target.AddRange(value);
        }
    }

    /// <summary>The certutil-sanitized short name of a CA (the cn of its <c>CN=AIA</c> / <c>CN=CDP</c> objects): non-ASCII and reserved characters as <c>!xxxx</c>.</summary>
    public static string Sanitized(string caName)
    {
        ArgumentNullException.ThrowIfNull(caName);
        var builder = new StringBuilder(caName.Length);
        foreach (var c in caName)
        {
            if (c > 0x7E || c < 0x20 || "<>\"/\\:|?*#,+;!".Contains(c, StringComparison.Ordinal))
            {
                builder.Append('!').Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>A serial number of <paramref name="value"/> (big-endian, no sign byte for these small values).</summary>
    public static byte[] Serial(long value)
    {
        var bytes = new BigInteger(value).ToByteArray(isUnsigned: true, isBigEndian: true);
        return bytes.Length == 0 ? [0x01] : bytes;
    }

    /// <summary>
    /// The Polish sample values of the P1 samples (names, DNs, hosts, URLs, UPNs, a SID) – what a value-free
    /// <c>ToString</c> must never contain (<c>PkiContractTests</c>).
    /// </summary>
    public static IReadOnlyList<string> SampleTexts { get; } =
    [
        RootCaName, IssuingCaName, "Użytkownik-VPN-Podatny", "Użytkownik-VPN-Bezpieczny", "Agent odzyskiwania kluczy Kontoso",
        "CN=Kontoso Wystawiający CA," + "CN=Enrollment Services," + PublicKeyServicesDn, IssuingCaHost, "jan.kowalski@contoso.test",
        "http://pki.contoso.test/CertEnroll/Kontoso%20Wystawiaj!0105cy%20CA.crl", "S-1-5-21-3623811015-3361044348-30300820-1103",
        "Polityka wysokiej pewności", "GG_Księgowość_Certyfikaty", "Łukasz Wiśniewski", "Żółć-Gęślą-Jaźń",
    ];

    private static X509Certificate2 CreateCa(string name, long serial, DateTimeOffset notBefore, DateTimeOffset notAfter, RSA key, X509Certificate2? issuer, RSA? issuerKey, int? pathLength)
    {
        var request = new CertificateRequest(Name(name, [("O", Organization), ("C", "PL")]), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, pathLength is not null, pathLength ?? 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (issuer is null)
        {
            using var self = request.Create(request.SubjectName, X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1), notBefore, notAfter, Serial(serial));
            return self.CopyWithPrivateKey(key);
        }

        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [RootCrtLdapUrl, RootCrtHttpUrl], critical: false));
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([RootCrlLdapUrl, RootCrlHttpUrl], critical: false));
        using var issued = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey!, RSASignaturePadding.Pkcs1), notBefore, notAfter, Serial(serial));
        return issued.CopyWithPrivateKey(key);
    }

    private static X500DistinguishedName Name(string commonName, IReadOnlyList<(string Type, string Value)> extra)
    {
        // X500DistinguishedNameBuilder writes the RDNs in encoding order (least specific first): the extra RDNs reversed, then the CN.
        var builder = new X500DistinguishedNameBuilder();
        foreach (var (type, value) in extra.Reverse())
        {
            switch (type)
            {
                case "C":
                    builder.AddCountryOrRegion(value);
                    break;
                case "O":
                    builder.AddOrganizationName(value);
                    break;
                case "OU":
                    builder.AddOrganizationalUnitName(value);
                    break;
                case "DC":
                    builder.AddDomainComponent(value);
                    break;
                default:
                    throw new ArgumentException("Unsupported RDN type " + type + ".", nameof(extra));
            }
        }

        builder.AddCommonName(commonName);
        return builder.Build();
    }

    private static OidCollection Oids(IEnumerable<string> values)
    {
        var oids = new OidCollection();
        foreach (var value in values)
        {
            oids.Add(new Oid(value));
        }

        return oids;
    }

    private static byte[] ApplicationPolicies(IReadOnlyList<string> policies)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        foreach (var policy in policies)
        {
            writer.PushSequence();
            writer.WriteObjectIdentifier(policy);
            writer.PopSequence();
        }

        writer.PopSequence();
        return writer.Encode();
    }

    /// <summary>szOID_NTDS_CA_SECURITY_EXT: GeneralNames with one otherName <c>1.3.6.1.4.1.311.25.2.1</c> holding the SID as ASCII.</summary>
    private static byte[] SidExtension(string sid)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        var otherName = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        writer.PushSequence(otherName);
        writer.WriteObjectIdentifier("1.3.6.1.4.1.311.25.2.1");
        var value = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        writer.PushSequence(value);
        writer.WriteOctetString(Encoding.ASCII.GetBytes(sid));
        writer.PopSequence(value);
        writer.PopSequence(otherName);
        writer.PopSequence();
        return writer.Encode();
    }

    private static void WriteSha256WithRsa(AsnWriter writer)
    {
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.113549.1.1.11");
        writer.WriteNull();
        writer.PopSequence();
    }

    private static void WriteExtension(AsnWriter writer, string oid, bool critical, Action<AsnWriter> value)
    {
        var inner = new AsnWriter(AsnEncodingRules.DER);
        value(inner);
        writer.PushSequence();
        writer.WriteObjectIdentifier(oid);
        if (critical)
        {
            writer.WriteBoolean(true);
        }

        writer.WriteOctetString(inner.Encode());
        writer.PopSequence();
    }

    private static string CdpLdapUrl(string caName, string computer) =>
        $"ldap:///CN={Sanitized(caName)},CN={computer},CN=CDP,{PublicKeyServicesDn}?certificateRevocationList?base?objectClass=cRLDistributionPoint";

    private static string AiaLdapUrl(string caName) =>
        $"ldap:///CN={Sanitized(caName)},CN=AIA,{PublicKeyServicesDn}?cACertificate?base?objectClass=certificationAuthority";

    private static string Url(string value) => value.Replace(" ", "%20", StringComparison.Ordinal);
}
