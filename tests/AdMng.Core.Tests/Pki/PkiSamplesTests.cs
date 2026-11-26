using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// What the sample certificates promise the tests that use them. <see cref="PkiSamples.IssuingCaForTls"/> is what a
/// test TLS server presents: a platform TLS stack builds the chain of the certificate it serves and may follow the AIA
/// of an intermediate whose issuer it does not have - so a live handshake gets the issuing CA without any pointer.
/// </summary>
public sealed class PkiSamplesTests
{
    private const string AuthorityInformationAccess = "1.3.6.1.5.5.7.1.1";
    private const string CrlDistributionPoints = "2.5.29.31";

    [Fact]
    public void TheIssuingCaOfATlsServer_IsTheIssuingCa_WithoutPointersAPlatformCouldFollow()
    {
        var issuing = PkiSamples.IssuingCa;
        var forTls = PkiSamples.IssuingCaForTls;

        // The same CA: subject, issuer, key (so the key identifier a leaf names as its authority) and validity.
        Assert.Equal(issuing.SubjectName.RawData, forTls.SubjectName.RawData);
        Assert.Equal(issuing.IssuerName.RawData, forTls.IssuerName.RawData);
        Assert.Equal(issuing.GetPublicKey(), forTls.GetPublicKey());
        Assert.Equal(KeyIdentifier(issuing), KeyIdentifier(forTls));
        Assert.Equal(issuing.NotBefore, forTls.NotBefore);
        Assert.Equal(issuing.NotAfter, forTls.NotAfter);

        // Another certificate of it: one issuer never signs one serial number twice.
        Assert.NotEqual(issuing.SerialNumber, forTls.SerialNumber);
        Assert.NotEqual(issuing.RawData, forTls.RawData);

        // No AIA, no CDP, no Freshest CRL - nothing that names a place to fetch from. The issuing CA itself keeps its
        // pointers: they are the data of the AIA / CDP checks.
        Assert.DoesNotContain(forTls.Extensions, e => e.Oid?.Value is AuthorityInformationAccess or CrlDistributionPoints or PkiSamples.FreshestCrlOid);
        Assert.Contains(issuing.Extensions, e => e.Oid?.Value == AuthorityInformationAccess);
        Assert.Contains(issuing.Extensions, e => e.Oid?.Value == CrlDistributionPoints);
    }

    [Fact]
    public void ACertificateOfTheIssuingCa_ChainsToTheAdRoot_ThroughTheOneATlsServerPresents()
    {
        // Only the root is known from the directory: the chain has to go through the presented intermediate.
        var trust = new PkiTrustMaterial([PkiSamples.RootCaDer], [], []);

        var info = DcCertificateEvaluation.Evaluate(
            [PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaForTls.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, trust, PkiSamples.Now);

        Assert.True(info.ChainsToAdRoot);
        Assert.True(info.IsTimeValid);
    }

    private static string KeyIdentifier(X509Certificate2 certificate) =>
        Assert.Single(certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>()).SubjectKeyIdentifier!;
}
