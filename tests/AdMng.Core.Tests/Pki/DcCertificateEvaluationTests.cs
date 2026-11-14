using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <see cref="DcCertificateEvaluation.Evaluate"/> (docs/dev/p-certificate-services.md 2.1) on hand-built chains at the fixed
/// <see cref="PkiSamples.Now"/>: names, Server Authentication, validity, the chain to the AD roots (custom root trust,
/// nothing downloaded) and the issuer in NTAuth.
/// </summary>
public sealed class DcCertificateEvaluationTests
{
    private static PkiTrustMaterial Trust => new([PkiSamples.RootCaDer], [PkiSamples.IssuingCaDer], [PkiSamples.IssuingCaDer]);

    [Fact]
    public void TheSampleLdapsCertificate_PassesEveryCheck()
    {
        var info = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.NotNull(info.Leaf);
        Assert.Equal("6100", info.Leaf.SerialNumber);
        Assert.True(info.NameMatchesDc);
        Assert.True(info.NameMatchesDomain);
        Assert.True(info.HasServerAuth);
        Assert.True(info.IsTimeValid);
        Assert.Equal(88, info.DaysLeft); // 2027-01-10 08:00 − 2026-10-13 12:00 = 88 days 20 h
        Assert.True(info.ChainsToAdRoot);
        Assert.True(info.IssuerInNtAuth);
        Assert.Empty(info.ChainStatus);
    }

    [Fact]
    public void TheIntermediate_ComesFromAd_WhenTheDcDoesNotPresentIt()
    {
        var info = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.True(info.ChainsToAdRoot);
        Assert.True(info.IssuerInNtAuth);
    }

    [Fact]
    public void APresentedIntermediate_CompletesTheChain()
    {
        var trust = new PkiTrustMaterial([PkiSamples.RootCaDer], [], []);

        var presented = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer], PkiSamples.DcHost, PkiSamples.DomainDns, trust, PkiSamples.Now);
        var alone = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, PkiSamples.DomainDns, trust, PkiSamples.Now);

        Assert.True(presented.ChainsToAdRoot);
        Assert.False(presented.IssuerInNtAuth);
        Assert.False(alone.ChainsToAdRoot);
        Assert.NotEmpty(alone.ChainStatus);
    }

    [Fact]
    public void WithoutAdRoots_NothingChains()
    {
        var info = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer], PkiSamples.DcHost, PkiSamples.DomainDns, PkiTrustMaterial.Empty, PkiSamples.Now);

        Assert.False(info.ChainsToAdRoot);
        Assert.False(info.IssuerInNtAuth);
        Assert.NotEmpty(info.ChainStatus);
        Assert.True(info.NameMatchesDc);
        Assert.True(info.IsTimeValid);
    }

    [Fact]
    public void ASelfSignedCertificate_DoesNotChain()
    {
        using var server = PkiSamples.ServerCertificate(PkiSamples.DcHost, selfSigned: true);

        var info = DcCertificateEvaluation.Evaluate([server.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.True(info.NameMatchesDc);
        Assert.True(info.HasServerAuth);
        Assert.True(info.Leaf!.IsSelfSigned);
        Assert.False(info.ChainsToAdRoot);
        Assert.False(info.IssuerInNtAuth);
        Assert.NotEmpty(info.ChainStatus);
    }

    [Fact]
    public void AnExpiredLeaf_IsNotTimeValid_ButStillChains()
    {
        using var server = PkiSamples.ServerCertificate(PkiSamples.DcHost, notBefore: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            notAfter: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        var info = DcCertificateEvaluation.Evaluate([server.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.False(info.IsTimeValid);
        Assert.Equal(-13, info.DaysLeft); // 12 days 12 h ago
        Assert.True(info.ChainsToAdRoot);
        Assert.True(info.IssuerInNtAuth);
        Assert.Contains("NotTimeValid", info.ChainStatus);
    }

    [Fact]
    public void ALeafNotYetValid_IsNotTimeValid()
    {
        using var server = PkiSamples.ServerCertificate(PkiSamples.DcHost, notBefore: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            notAfter: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var info = DcCertificateEvaluation.Evaluate([server.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.False(info.IsTimeValid);
        Assert.True(info.DaysLeft > 0);
    }

    [Fact]
    public void AnExpiredIntermediate_BreaksTheChain()
    {
        // Issued past the issuing CA's expiry (2027-02-28) – AD CS would not, a test can – and evaluated on 2027-03-15.
        var der = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = PkiSamples.DcHost,
            SerialNumber = 0x6400,
            NotBefore = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            NotAfter = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero),
            Ekus = [PkiSamples.ServerAuthentication],
            DnsNames = [PkiSamples.DcHost],
            Issuer = PkiSamples.IssuingCa,
        });

        var info = DcCertificateEvaluation.Evaluate([der], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, new DateTimeOffset(2027, 3, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.True(info.IsTimeValid);
        Assert.False(info.ChainsToAdRoot);
        Assert.Contains("NotTimeValid", info.ChainStatus);
    }

    [Fact]
    public void TheVerificationTime_IsTheGivenNow_NotTheClock()
    {
        var before = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var after = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.False(before.IsTimeValid);
        Assert.True(before.ChainsToAdRoot);
        Assert.False(after.IsTimeValid);
        Assert.False(after.ChainsToAdRoot);
    }

    [Fact]
    public void AnotherName_MatchesNeitherTheDcNorTheDomain()
    {
        using var server = PkiSamples.ServerCertificate("www.contoso.test");

        var info = DcCertificateEvaluation.Evaluate([server.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.False(info.NameMatchesDc);
        Assert.False(info.NameMatchesDomain);
        Assert.True(info.ChainsToAdRoot);
    }

    [Fact]
    public void WithoutSan_TheCommonNameCounts_WithSan_ItDoesNot()
    {
        var cnOnly = Issue(PkiSamples.DcHost, dnsNames: []);
        var sanOther = Issue(PkiSamples.DcHost, dnsNames: ["dc02.contoso.test"]);

        var cn = DcCertificateEvaluation.Evaluate([cnOnly], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);
        var san = DcCertificateEvaluation.Evaluate([sanOther], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.True(cn.NameMatchesDc);
        Assert.False(cn.NameMatchesDomain);
        Assert.False(san.NameMatchesDc);
    }

    [Theory]
    [InlineData("*.contoso.test", "dc01.contoso.test", true)]
    [InlineData("*.contoso.test", "contoso.test", false)]
    [InlineData("*.contoso.test", "dc01.oddział.contoso.test", false)]
    [InlineData("*.test", "contoso.test", false)]
    [InlineData("dc*.contoso.test", "dc01.contoso.test", false)]
    [InlineData("DC01.Contoso.Test", "dc01.contoso.test.", true)]
    [InlineData("dc01.contoso.test.", "DC01.CONTOSO.TEST", true)]
    public void HostNames_FollowRfc6125(string sanName, string dcFqdn, bool matches)
    {
        var der = Issue("serwer-łódź", dnsNames: [sanName]);

        var info = DcCertificateEvaluation.Evaluate([der], dcFqdn, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.Equal(matches, info.NameMatchesDc);
    }

    [Fact]
    public void ServerAuthentication_OrNoEku()
    {
        using var clientOnly = PkiSamples.ServerCertificate(PkiSamples.DcHost, serverAuth: false);
        var noEku = Issue(PkiSamples.DcHost, dnsNames: [PkiSamples.DcHost], ekus: []);

        Assert.False(DcCertificateEvaluation.Evaluate([clientOnly.RawData], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now).HasServerAuth);
        Assert.True(DcCertificateEvaluation.Evaluate([noEku], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now).HasServerAuth);
    }

    [Fact]
    public void AnIssuerOutsideNtAuth_StillChains()
    {
        var byRoot = Issue(PkiSamples.DcHost, dnsNames: [PkiSamples.DcHost], issuer: PkiSamples.RootCa);

        var info = DcCertificateEvaluation.Evaluate([byRoot], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        Assert.True(info.ChainsToAdRoot);
        Assert.False(info.IssuerInNtAuth);
    }

    [Fact]
    public void NtAuth_HoldingTheIssuersNameWithAnotherKey_IsNotTheIssuer()
    {
        var impostor = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = PkiSamples.IssuingCaName,
            ExtraRdns = [("O", PkiSamples.Organization), ("C", "PL")],
            SerialNumber = 0x6410,
            NotBefore = new DateTimeOffset(2024, 3, 1, 8, 0, 0, TimeSpan.Zero),
            NotAfter = new DateTimeOffset(2027, 2, 28, 8, 0, 0, TimeSpan.Zero),
            IsCa = true,
            Rsa = true,
            Issuer = PkiSamples.RootCa,
        });
        var trust = new PkiTrustMaterial([PkiSamples.RootCaDer], [PkiSamples.IssuingCaDer], [impostor]);

        var info = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, PkiSamples.DomainDns, trust, PkiSamples.Now);

        Assert.True(info.ChainsToAdRoot);
        Assert.False(info.IssuerInNtAuth);
    }

    [Fact]
    public void NoChain_OrAnUnparsableLeaf_GivesAnEmptyVerdict()
    {
        var empty = DcCertificateEvaluation.Evaluate([], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);
        var malformed = DcCertificateEvaluation.Evaluate([PkiSamples.MalformedDer, PkiSamples.IssuingCaDer], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);

        foreach (var info in new[] { empty, malformed })
        {
            Assert.Null(info.Leaf);
            Assert.False(info.NameMatchesDc);
            Assert.False(info.HasServerAuth);
            Assert.False(info.IsTimeValid);
            Assert.False(info.ChainsToAdRoot);
            Assert.False(info.IssuerInNtAuth);
            Assert.Empty(info.ChainStatus);
        }
    }

    [Fact]
    public void MalformedTrustMaterial_IsSkipped()
    {
        var trust = new PkiTrustMaterial([PkiSamples.MalformedDer, PkiSamples.RootCaDer], [PkiSamples.MalformedDer, PkiSamples.IssuingCaDer], [PkiSamples.MalformedDer, PkiSamples.IssuingCaDer]);

        var info = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer(), PkiSamples.MalformedDer], PkiSamples.DcHost, PkiSamples.DomainDns, trust, PkiSamples.Now);

        Assert.True(info.ChainsToAdRoot);
        Assert.True(info.IssuerInNtAuth);
    }

    [Fact]
    public void TheVerdict_PrintsFlagsOnly()
    {
        var info = DcCertificateEvaluation.Evaluate([PkiSamples.DcLdapsDer()], PkiSamples.DcHost, PkiSamples.DomainDns, Trust, PkiSamples.Now);
        var check = new DcTlsCheck(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, info);

        Assert.DoesNotContain(PkiSamples.DcHost, info.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DcTlsCheck(636: Ok)", check.ToString());
    }

    private static byte[] Issue(string commonName, IReadOnlyList<string> dnsNames, IReadOnlyList<string>? ekus = null, System.Security.Cryptography.X509Certificates.X509Certificate2? issuer = null) =>
        PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = commonName,
            SerialNumber = 0x6420 + dnsNames.Count,
            NotBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Ekus = ekus ?? [PkiSamples.ServerAuthentication],
            DnsNames = dnsNames,
            Issuer = issuer ?? PkiSamples.IssuingCa,
            AiaCaIssuers = [],
            AiaOcsp = [],
            CrlDistributionPoints = [],
        });
}
