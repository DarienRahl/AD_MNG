using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The signer of a package (K3 review: "signed by Microsoft" was a substring test on a formatted subject): the organization
/// is read from the parsed subject and compared exactly; certificates are synthetic, self-signed.
/// </summary>
public class PackageSignerTests
{
    private const string MicrosoftSubject = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public void MicrosoftsOwnCertificate_IsAMicrosoftPublisher_AndShowsItsOrganization()
    {
        using var certificate = Certificate(MicrosoftSubject, "1.3.6.1.5.5.7.3.3");

        Assert.True(PackageSigner.ClaimsMicrosoft(certificate));
        Assert.True(PackageSigner.IsMicrosoftPublisher(certificate));
        Assert.Equal("Microsoft Corporation", PackageSigner.DisplayName(certificate));
    }

    [Theory]
    [InlineData("CN=\"Build, O=Microsoft Corporation\", O=Contoso", "Contoso")]
    [InlineData("OU=\"O=Microsoft Corporation\", O=Contoso", "Contoso")]
    [InlineData("CN=Kompilacja, O=Microsoft Corporation Fans Sp. z o.o.", "Microsoft Corporation Fans Sp. z o.o.")]
    [InlineData("CN=Kompilacja, O=microsoft corporation", "microsoft corporation")]
    [InlineData("CN=Microsoft Corporation, O=Wydział Informatyki", "Wydział Informatyki")]
    [InlineData("CN=Microsoft Corporation, O=Micr\u043Esoft Corporation", "Micr\u043Esoft Corporation")] // a Cyrillic "о"
    public void ACertificateThatIsNotExactlyMicrosoft_IsNot_AndShowsItsRealOrganization(string subject, string organization)
    {
        using var certificate = Certificate(subject);

        Assert.False(PackageSigner.ClaimsMicrosoft(certificate));
        Assert.False(PackageSigner.IsMicrosoftPublisher(certificate));
        Assert.Equal(organization, PackageSigner.DisplayName(certificate));
    }

    [Fact]
    public void ACertificateMicrosoftSignsOtherVendorsDriversWith_IsNotAMicrosoftPublisher()
    {
        using var whql = Certificate("CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            "1.3.6.1.4.1.311.10.3.5", "1.3.6.1.5.5.7.3.3");
        using var attested = Certificate(MicrosoftSubject, "1.3.6.1.4.1.311.10.3.5.1");

        Assert.True(PackageSigner.ClaimsMicrosoft(whql));
        Assert.False(PackageSigner.IsMicrosoftPublisher(whql));
        Assert.False(PackageSigner.IsMicrosoftPublisher(attested));
    }

    [Fact]
    public void Organization_IsNull_WhenAmbiguous()
    {
        var twice = new X500DistinguishedNameBuilder();
        twice.AddCommonName("Kompilacja");
        twice.AddOrganizationName("Microsoft Corporation");
        twice.AddOrganizationName("Contoso");

        Assert.Null(PackageSigner.Organization(twice.Build()));
        Assert.Null(PackageSigner.Organization(MultiValued(("2.5.4.10", "Microsoft Corporation"), ("2.5.4.11", "Dział IT"))));
        Assert.Null(PackageSigner.Organization(new X500DistinguishedName("CN=Zespół Wdrożeń")));
        Assert.Equal("Microsoft Corporation", PackageSigner.Organization(new X500DistinguishedName(MicrosoftSubject)));
    }

    [Fact]
    public void ACertificateWithAMultiValuedName_IsNotMicrosoft()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(MultiValued(("2.5.4.10", "Microsoft Corporation"), ("2.5.4.3", "Microsoft Corporation")), key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(PackageSigner.ClaimsMicrosoft(certificate));
        Assert.False(PackageSigner.IsMicrosoftPublisher(certificate));
    }

    [Fact]
    public void DisplayName_FallsBackToTheCommonName_AndDropsInvisibleCharacters()
    {
        using var noOrganization = Certificate("CN=Zespół Wdrożeń");
        var spoofed = new X500DistinguishedNameBuilder();
        spoofed.AddOrganizationName("Contoso‮gnp.exe​\u0007");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bidi = new CertificateRequest(spoofed.Build(), key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal("Zespół Wdrożeń", PackageSigner.DisplayName(noOrganization));
        Assert.Equal("Contosognp.exe", PackageSigner.DisplayName(bidi));
        Assert.Null(PackageSigner.Printable(" ‎\u0000 "));
        Assert.Equal(new string('ż', PackageSigner.MaxDisplayLength) + "…", PackageSigner.Printable(new string('ż', PackageSigner.MaxDisplayLength + 10)));
        Assert.Equal("𝐌icrosoft", PackageSigner.Printable("𝐌icrosoft"));
    }

    private static X509Certificate2 Certificate(string subject, params string[] usages)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        if (usages.Length > 0)
        {
            var oids = new OidCollection();
            foreach (var usage in usages)
            {
                oids.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>A name of one RDN holding several attributes ("O=… + OU=…"), in DER.</summary>
    private static X500DistinguishedName MultiValued(params (string Oid, string Value)[] attributes)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSetOf())
            {
                foreach (var (oid, value) in attributes)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(oid);
                        writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
                    }
                }
            }
        }

        return new X500DistinguishedName(writer.Encode());
    }
}
