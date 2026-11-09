using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Pki;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <see cref="CertificateSummary.Of"/> (docs/dev/p-certificate-services.md 2.1) on the synthetic certificates of
/// <see cref="PkiSamples"/>: names, validity, SAN (DNS, UPN, e-mail, the SID URL), EKU and application policies, the AIA /
/// CDP / Freshest CRL URLs, the key identifiers, basic constraints, the Microsoft template and SID extensions, the key
/// and the self-signed check.
/// </summary>
public sealed class CertificateSummaryTests
{
    private const string JanSid = "S-1-5-21-3623811015-3361044348-30300820-1103";

    [Fact]
    public void TheRootCa_IsASelfSignedRsaCa()
    {
        var summary = CertificateSummary.Of(PkiSamples.RootCaDer)!;

        Assert.Contains("CN=Kontoso Główny CA", summary.Subject, StringComparison.Ordinal);
        Assert.Contains("O=Kontoso Sp. z o.o.", summary.Subject, StringComparison.Ordinal);
        Assert.Equal(summary.Subject, summary.Issuer);
        Assert.Equal("1A2B3C4D01", summary.SerialNumber);
        Assert.Equal(new DateTimeOffset(2021, 3, 1, 8, 0, 0, TimeSpan.Zero), summary.NotBefore);
        Assert.Equal(new DateTimeOffset(2041, 3, 1, 8, 0, 0, TimeSpan.Zero), summary.NotAfter);
        Assert.Equal(TimeSpan.Zero, summary.NotAfter.Offset);
        Assert.True(summary.IsCa);
        Assert.Null(summary.PathLength);
        Assert.True(summary.IsSelfSigned);
        Assert.Equal("RSA", summary.KeyAlgorithm);
        Assert.Equal(2048, summary.KeySize);
        Assert.Equal("sha256RSA", summary.SignatureAlgorithm);
        Assert.Empty(summary.Ekus);
        Assert.Empty(summary.AiaCaIssuers);
        Assert.Empty(summary.CrlDistributionPoints);
        Assert.Null(summary.AuthorityKeyIdentifier);
        Assert.Equal(Ski(PkiSamples.RootCa), summary.SubjectKeyIdentifier);
    }

    [Fact]
    public void Thumbprints_AreTheHashesOfTheDer()
    {
        var der = PkiSamples.IssuingCaDer;
        var summary = CertificateSummary.Of(der)!;

        Assert.Equal(PkiSamples.IssuingCa.Thumbprint, summary.Thumbprint);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(der)), summary.Thumbprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(der)), summary.Sha256);
        Assert.Equal(64, summary.Sha256.Length);
        Assert.True(summary.Sha256.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F'));
    }

    [Fact]
    public void TheIssuingCa_ChainsToTheRoot_WithPathLength0_AiaAndCdp()
    {
        var summary = CertificateSummary.Of(PkiSamples.IssuingCaDer)!;
        var root = CertificateSummary.Of(PkiSamples.RootCaDer)!;

        Assert.Contains("CN=Kontoso Wystawiający CA", summary.Subject, StringComparison.Ordinal);
        Assert.Equal(root.Subject, summary.Issuer);
        Assert.True(summary.IsCa);
        Assert.Equal(0, summary.PathLength);
        Assert.False(summary.IsSelfSigned);
        Assert.Equal(root.SubjectKeyIdentifier, summary.AuthorityKeyIdentifier);
        Assert.Equal([PkiSamples.RootCrtLdapUrl, PkiSamples.RootCrtHttpUrl], summary.AiaCaIssuers);
        Assert.Empty(summary.AiaOcsp);
        Assert.Equal([PkiSamples.RootCrlLdapUrl, PkiSamples.RootCrlHttpUrl], summary.CrlDistributionPoints);
        Assert.Equal(new DateTimeOffset(2027, 2, 28, 8, 0, 0, TimeSpan.Zero), summary.NotAfter);
    }

    [Fact]
    public void AUserCertificate_HasTheSanUpnEmailSidUrl_TheSidExtension_AndTheV1TemplateName()
    {
        var der = PkiSamples.UserCertificateDer("Łukasz Wiśniewski", "lukasz.wisniewski@contoso.test", JanSid, 0x6310,
            new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var summary = CertificateSummary.Of(der)!;

        Assert.Contains("CN=Łukasz Wiśniewski", summary.Subject, StringComparison.Ordinal);
        Assert.Equal("6310", summary.SerialNumber);
        Assert.Equal(["lukasz.wisniewski@contoso.test"], summary.SanUpn);
        Assert.Equal(["lukasz.wisniewski@contoso.test"], summary.SanEmail);
        Assert.Equal(["tag:microsoft.com,2022-09-14:sid:" + JanSid], summary.SanUri);
        Assert.Equal(JanSid, summary.SanSid);
        Assert.Equal(JanSid, summary.SidExtension);
        Assert.Empty(summary.SanDns);
        Assert.Equal("User", summary.TemplateName);
        Assert.Null(summary.TemplateOid);
        Assert.Equal([PkiSamples.EncryptingFileSystem, PkiSamples.SecureEmail, PkiSamples.ClientAuthentication], summary.Ekus);
        Assert.False(summary.IsCa);
        Assert.False(summary.IsSelfSigned);
        Assert.Equal("ECC", summary.KeyAlgorithm);
        Assert.Equal(256, summary.KeySize);
        Assert.Equal("sha256RSA", summary.SignatureAlgorithm);
        Assert.Equal([PkiSamples.IssuingCrtLdapUrl, PkiSamples.IssuingCrtHttpUrl], summary.AiaCaIssuers);
        Assert.Equal([PkiSamples.OcspUrl], summary.AiaOcsp);
        Assert.Equal([PkiSamples.IssuingCrlLdapUrl, PkiSamples.IssuingCrlHttpUrl], summary.CrlDistributionPoints);
        Assert.Equal(CertificateSummary.Of(PkiSamples.IssuingCaDer)!.SubjectKeyIdentifier, summary.AuthorityKeyIdentifier);
    }

    [Fact]
    public void TheSidUrl_WithoutTheSecurityExtension()
    {
        var der = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = "Zofia Żurawska",
            SerialNumber = 0x6320,
            NotBefore = PkiSamples.Now.AddDays(-1),
            NotAfter = PkiSamples.Now.AddDays(30),
            Upn = "zofia.zurawska@contoso.test",
            Sid = JanSid,
            SidExtension = false,
            Issuer = PkiSamples.IssuingCa,
        });
        var summary = CertificateSummary.Of(der)!;

        Assert.Equal(JanSid, summary.SanSid);
        Assert.Null(summary.SidExtension);
        Assert.Equal(["zofia.zurawska@contoso.test"], summary.SanUpn);
        Assert.Empty(summary.SanEmail);
    }

    [Fact]
    public void TheKdcCertificate_HasTheTemplateInformation_DnsNames_AndTheKdcEku()
    {
        var summary = CertificateSummary.Of(PkiSamples.DcKdcDer())!;

        Assert.Equal([PkiSamples.DcHost, PkiSamples.DomainDns, "CONTOSO"], summary.SanDns);
        Assert.Equal(PkiSamples.ForestOid + ".1.20", summary.TemplateOid);
        Assert.Equal(100, summary.TemplateMajorVersion);
        Assert.Equal(4, summary.TemplateMinorVersion);
        Assert.Null(summary.TemplateName);
        Assert.Contains(PkiSamples.KdcAuthentication, summary.Ekus);
        Assert.Contains(PkiSamples.SmartCardLogon, summary.Ekus);
        Assert.Null(summary.SanSid);
        Assert.Null(summary.SidExtension);
    }

    [Fact]
    public void ApplicationPolicies_FreshestCrl_AndExplicitLocations()
    {
        var der = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = "Serwer Łódź",
            SerialNumber = 0x6330,
            NotBefore = PkiSamples.Now.AddDays(-1),
            NotAfter = PkiSamples.Now.AddDays(90),
            Ekus = [PkiSamples.ServerAuthentication],
            ApplicationPolicies = [PkiSamples.ServerAuthentication, PkiSamples.ClientAuthentication],
            DnsNames = ["lodz.contoso.test"],
            Issuer = PkiSamples.IssuingCa,
            AiaCaIssuers = ["http://pki.contoso.test/a.crt", "ldap:///CN=Kontoso!0141,CN=AIA?cACertificate"],
            AiaOcsp = ["http://ocsp.contoso.test", "http://ocsp2.contoso.test"],
            CrlDistributionPoints = ["http://pki.contoso.test/a.crl"],
            FreshestCrl = ["http://pki.contoso.test/a+.crl"],
        });
        var summary = CertificateSummary.Of(der)!;

        Assert.Equal([PkiSamples.ServerAuthentication], summary.Ekus);
        Assert.Equal([PkiSamples.ServerAuthentication, PkiSamples.ClientAuthentication], summary.ApplicationPolicies);
        Assert.Equal(["http://pki.contoso.test/a.crt", "ldap:///CN=Kontoso!0141,CN=AIA?cACertificate"], summary.AiaCaIssuers);
        Assert.Equal(["http://ocsp.contoso.test", "http://ocsp2.contoso.test"], summary.AiaOcsp);
        Assert.Equal(["http://pki.contoso.test/a.crl"], summary.CrlDistributionPoints);
        Assert.Equal(["http://pki.contoso.test/a+.crl"], summary.FreshestCrl);
        Assert.Equal(["lodz.contoso.test"], summary.SanDns);
    }

    [Fact]
    public void ARenewalLinkCertificate_WithTheSameNameButAnotherKey_IsNotSelfSigned()
    {
        // A CA renewed with a new key: the link certificate has subject = issuer, but the old key signed it.
        using var oldKey = PkiSamples.Issue(new PkiCertificateOptions
        {
            CommonName = "Kontoso Gdańsk CA",
            SerialNumber = 0x6340,
            NotBefore = PkiSamples.Now.AddYears(-5),
            NotAfter = PkiSamples.Now.AddYears(5),
            IsCa = true,
        });
        var link = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = "Kontoso Gdańsk CA",
            SerialNumber = 0x6341,
            NotBefore = PkiSamples.Now.AddYears(-1),
            NotAfter = PkiSamples.Now.AddYears(4),
            IsCa = true,
            Issuer = oldKey,
            AiaCaIssuers = [],
            AiaOcsp = [],
            CrlDistributionPoints = [],
        });

        var summary = CertificateSummary.Of(link)!;
        var old = CertificateSummary.Of(oldKey.RawData)!;

        Assert.Equal(summary.Subject, summary.Issuer);
        Assert.False(summary.IsSelfSigned);
        Assert.True(old.IsSelfSigned);
        Assert.Equal(old.SubjectKeyIdentifier, summary.AuthorityKeyIdentifier);
        Assert.NotEqual(summary.SubjectKeyIdentifier, summary.AuthorityKeyIdentifier);
    }

    [Fact]
    public void AnEcdsaSelfSignedCertificate_IsSelfSigned()
    {
        using var certificate = PkiSamples.Issue(new PkiCertificateOptions
        {
            CommonName = "Główny ECC",
            SerialNumber = 0x6350,
            NotBefore = PkiSamples.Now.AddDays(-1),
            NotAfter = PkiSamples.Now.AddYears(1),
            IsCa = true,
            PathLength = 2,
        });

        var summary = CertificateSummary.Of(certificate.RawData)!;

        Assert.True(summary.IsSelfSigned);
        Assert.True(summary.IsCa);
        Assert.Equal(2, summary.PathLength);
        Assert.Equal("sha256ECDSA", summary.SignatureAlgorithm);
    }

    [Fact]
    public void ACertificateWithAMalformedExtension_StillParses_WithoutThatExtension()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Żółć", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension("2.5.29.17", [0x30, 0x03, 0x82, 0x05, 0x41], critical: false)); // truncated SAN
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.311.21.7", [0x04, 0x00], critical: false)); // not a SEQUENCE
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(PkiSamples.ClientAuthentication)], critical: false));
        using var certificate = request.CreateSelfSigned(PkiSamples.Now.AddDays(-1), PkiSamples.Now.AddDays(1));

        var summary = CertificateSummary.Of(certificate.RawData)!;

        Assert.Empty(summary.SanDns);
        Assert.Null(summary.TemplateOid);
        Assert.Equal([PkiSamples.ClientAuthentication], summary.Ekus);
        Assert.True(summary.IsSelfSigned);
    }

    [Fact]
    public void SanOtherNames_InOtherStringTypes_AndAnUnknownOtherName()
    {
        // GeneralNames: otherName UPN as BMPString, an unknown otherName, a directoryName, then a DNS name.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        WriteOtherName(writer, "1.3.6.1.4.1.311.20.2.3", w => w.WriteCharacterString(UniversalTagNumber.BMPString, "grażyna@contoso.test"));
        WriteOtherName(writer, "1.2.3.4", w => w.WriteInteger(5));
        writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true));
        writer.WriteEncodedValue(new X500DistinguishedName("CN=Grażyna").RawData);
        writer.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true));
        writer.WriteCharacterString(UniversalTagNumber.IA5String, "host.contoso.test", new Asn1Tag(TagClass.ContextSpecific, 2));
        writer.PopSequence();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Grażyna Dąbrowska", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension("2.5.29.17", writer.Encode(), critical: false));
        using var certificate = request.CreateSelfSigned(PkiSamples.Now.AddDays(-1), PkiSamples.Now.AddDays(1));

        var summary = CertificateSummary.Of(certificate.RawData)!;

        Assert.Equal(["grażyna@contoso.test"], summary.SanUpn);
        Assert.Equal(["host.contoso.test"], summary.SanDns);
        Assert.Empty(summary.SanUri);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x30, 0x82, 0x01, 0x00, 0x02, 0x01 })]
    [InlineData(new byte[] { 0x04, 0x02, 0x01, 0x02 })]
    public void MalformedDer_IsNull(byte[] der)
    {
        Assert.Null(CertificateSummary.Of(der));
    }

    [Fact]
    public void ACrl_IsNotACertificate()
    {
        Assert.Null(CertificateSummary.Of(PkiSamples.IssuingCrlDer()));
        Assert.Null(CertificateSummary.Of(PkiSamples.MalformedDer));
    }

    [Fact]
    public void ToString_CarriesNoSubjectNameOrThumbprint()
    {
        var summary = CertificateSummary.Of(PkiSamples.UserCertificateDer("Jan Kowalski", "jan.kowalski@contoso.test", JanSid, 0x6300,
            new DateTimeOffset(2025, 10, 30, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)))!;
        var root = CertificateSummary.Of(PkiSamples.RootCaDer)!;

        Assert.Equal("CertificateSummary(end entity)", summary.ToString());
        Assert.Equal("CertificateSummary(CA, self-signed)", root.ToString());
        Assert.DoesNotContain(summary.Thumbprint, summary.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Ski(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier!;

    private static void WriteOtherName(AsnWriter writer, string oid, Action<AsnWriter> value)
    {
        var otherName = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        writer.PushSequence(otherName);
        writer.WriteObjectIdentifier(oid);
        writer.PushSequence(otherName);
        value(writer);
        writer.PopSequence(otherName);
        writer.PopSequence(otherName);
    }
}
