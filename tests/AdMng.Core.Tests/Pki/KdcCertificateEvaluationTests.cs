using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <see cref="KdcCertificateEvaluation.Evaluate"/> (docs/dev/p-certificate-services.md 2.1): the KDC indicator from the
/// certificates a DC object publishes, at the fixed <see cref="PkiSamples.Now"/>.
/// </summary>
public sealed class KdcCertificateEvaluationTests
{
    private static PkiTrustMaterial Trust => new([PkiSamples.RootCaDer], [PkiSamples.IssuingCaDer], [PkiSamples.IssuingCaDer]);

    [Fact]
    public void TheSampleKerberosAuthenticationCertificate_IsReady()
    {
        var info = KdcCertificateEvaluation.Evaluate([PkiSamples.DcKdcDer()], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal(KdcReadinessState.Ready, info.State);
        Assert.Contains(PkiSamples.KdcAuthentication, info.Best!.Ekus);
        Assert.Equal("6101", info.Best.SerialNumber);
    }

    [Fact]
    public void NothingPublished_IsUnknownNotAFailure()
    {
        var none = KdcCertificateEvaluation.Evaluate([], PkiSamples.DcHost, Trust, PkiSamples.Now);
        var unparsable = KdcCertificateEvaluation.Evaluate([PkiSamples.MalformedDer, [0x00]], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal(KdcReadinessState.NoPublishedCertificate, none.State);
        Assert.Null(none.Best);
        Assert.Equal(KdcReadinessState.NoPublishedCertificate, unparsable.State);
        Assert.Null(unparsable.Best);
    }

    [Fact]
    public void AV1DomainControllerCertificate_HasNoKdcEku()
    {
        // The v1 Domain Controller template: Client + Server Authentication only.
        var info = KdcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal(KdcReadinessState.NoKdcEku, info.State);
        Assert.Equal("6100", info.Best!.SerialNumber);
    }

    [Fact]
    public void TheDomainControllerAuthenticationPair_SmartCardLogonAndServerAuthentication_IsAKdcCertificate()
    {
        var pair = Issue([PkiSamples.ClientAuthentication, PkiSamples.ServerAuthentication, PkiSamples.SmartCardLogon]);
        var smartCardOnly = Issue([PkiSamples.ClientAuthentication, PkiSamples.SmartCardLogon]);

        Assert.Equal(KdcReadinessState.Ready, KdcCertificateEvaluation.Evaluate([pair], PkiSamples.DcHost, Trust, PkiSamples.Now).State);
        Assert.Equal(KdcReadinessState.NoKdcEku, KdcCertificateEvaluation.Evaluate([smartCardOnly], PkiSamples.DcHost, Trust, PkiSamples.Now).State);
    }

    [Fact]
    public void AKdcCertificateFromACaOutsideNtAuth_IsReported()
    {
        // The root CA is not in NTAuth: a DC certificate it issues does not support smart-card logon.
        var byRoot = Issue([PkiSamples.KdcAuthentication, PkiSamples.ServerAuthentication], issuer: PkiSamples.RootCa);

        var info = KdcCertificateEvaluation.Evaluate([byRoot], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal(KdcReadinessState.IssuerNotInNtAuth, info.State);
        Assert.NotNull(info.Best);
    }

    [Fact]
    public void AnNtAuthCaWithoutSubjectKeyIdentifier_IsStillTheIssuer()
    {
        // A third-party CA certificate without SKI (older CAs omit it) whose leaves carry an AKI: the key identifiers cannot
        // be compared, so the signature decides – the CA is in NTAuth.
        using var caKey = System.Security.Cryptography.RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Zewnętrzny CA Kart Ąę, O=Kontoso, C=PL", caKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        using var ca = caRequest.CreateSelfSigned(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(CertificateSummary.Of(ca.RawData)!.SubjectKeyIdentifier);

        using var dcKey = System.Security.Cryptography.RSA.Create(2048);
        var dcRequest = new CertificateRequest("CN=" + PkiSamples.DcHost, dcKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        dcRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid(PkiSamples.KdcAuthentication)], critical: false));
        dcRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(System.Security.Cryptography.SHA1.HashData(ca.PublicKey.EncodedKeyValue.RawData)));
        using var dc = dcRequest.Create(ca, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero), [0x65, 0x77]);
        Assert.NotNull(CertificateSummary.Of(dc.RawData)!.AuthorityKeyIdentifier);

        // The same name without SKI but another key is not the issuer (the signature does not verify).
        using var impostorKey = System.Security.Cryptography.RSA.Create(2048);
        var impostorRequest = new CertificateRequest(ca.SubjectName, impostorKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        impostorRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        using var impostor = impostorRequest.CreateSelfSigned(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var inNtAuth = KdcCertificateEvaluation.Evaluate([dc.RawData], PkiSamples.DcHost, new PkiTrustMaterial([ca.RawData], [], [ca.RawData]), PkiSamples.Now);
        var outside = KdcCertificateEvaluation.Evaluate([dc.RawData], PkiSamples.DcHost, Trust, PkiSamples.Now);
        var byImpostor = KdcCertificateEvaluation.Evaluate([dc.RawData], PkiSamples.DcHost, new PkiTrustMaterial([ca.RawData], [], [impostor.RawData]), PkiSamples.Now);
        var ldaps = DcCertificateEvaluation.Evaluate([dc.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, new PkiTrustMaterial([ca.RawData], [], [ca.RawData]), PkiSamples.Now);

        Assert.Equal(KdcReadinessState.Ready, inNtAuth.State);
        Assert.Equal(KdcReadinessState.IssuerNotInNtAuth, outside.State);
        Assert.Equal(KdcReadinessState.IssuerNotInNtAuth, byImpostor.State);
        Assert.True(ldaps.IssuerInNtAuth);
    }

    [Fact]
    public void OnlyExpiredKdcCertificates_AreExpired()
    {
        var expired = Issue([PkiSamples.KdcAuthentication], notBefore: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), notAfter: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var info = KdcCertificateEvaluation.Evaluate([expired, PkiSamples.DcLdapsDer()], PkiSamples.DcHost, Trust, PkiSamples.Now);
        var later = KdcCertificateEvaluation.Evaluate([PkiSamples.DcKdcDer()], PkiSamples.DcHost, Trust, new DateTimeOffset(2027, 2, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(KdcReadinessState.Expired, info.State);
        Assert.Contains(PkiSamples.KdcAuthentication, info.Best!.Ekus);
        Assert.Equal(KdcReadinessState.Expired, later.State);
    }

    [Fact]
    public void AValidCertificate_WinsOverAnExpiredOne()
    {
        var expired = Issue([PkiSamples.KdcAuthentication], notBefore: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), notAfter: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var info = KdcCertificateEvaluation.Evaluate([expired, PkiSamples.DcKdcDer(), [0x00]], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal(KdcReadinessState.Ready, info.State);
        Assert.Equal("6101", info.Best!.SerialNumber);
    }

    [Fact]
    public void TheBest_NamesTheDc_ThenLastsLongest()
    {
        var otherHost = Issue([PkiSamples.KdcAuthentication], dnsNames: ["dc02.contoso.test"], notAfter: new DateTimeOffset(2027, 2, 20, 0, 0, 0, TimeSpan.Zero));
        var shorter = Issue([PkiSamples.KdcAuthentication], notAfter: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var longer = Issue([PkiSamples.KdcAuthentication], notAfter: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));

        var info = KdcCertificateEvaluation.Evaluate([otherHost, shorter, longer], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal(KdcReadinessState.Ready, info.State);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), info.Best!.NotAfter);
    }

    [Fact]
    public void TheIndicator_PrintsTheStateOnly()
    {
        var info = KdcCertificateEvaluation.Evaluate([PkiSamples.DcKdcDer()], PkiSamples.DcHost, Trust, PkiSamples.Now);

        Assert.Equal("KdcCertificateInfo(Ready)", info.ToString());
    }

    private static int _serial = 0x6500;

    private static byte[] Issue(
        IReadOnlyList<string> ekus,
        X509Certificate2? issuer = null,
        IReadOnlyList<string>? dnsNames = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null) =>
        PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = PkiSamples.DcHost,
            SerialNumber = Interlocked.Increment(ref _serial),
            NotBefore = notBefore ?? new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            NotAfter = notAfter ?? new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
            Ekus = ekus,
            DnsNames = dnsNames ?? [PkiSamples.DcHost, PkiSamples.DomainDns],
            Issuer = issuer ?? PkiSamples.IssuingCa,
            AiaCaIssuers = [],
            AiaOcsp = [],
            CrlDistributionPoints = [],
        });
}
