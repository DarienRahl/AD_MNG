using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// The shared P1 sample (docs/dev/p-certificate-services.md 2.10): <see cref="FakeDirectoryBuilder.AddPublicKeySample"/> seeds
/// what the waves read, and the certificates and CRLs of <see cref="PkiSamples"/> carry the fixed dates and serials.
/// </summary>
public sealed class PublicKeySampleTests
{
    private static (FakeDirectoryBuilder Builder, SampleCompany Company, PublicKeySample Sample) Create()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        return (builder, company, builder.AddPublicKeySample(company));
    }

    [Fact]
    public void Containers_Stores_Cdp_Kra_AndOid_AreSeeded()
    {
        var (builder, _, sample) = Create();
        var session = builder.Session;

        foreach (var dn in new[] { sample.AiaContainer, sample.CdpContainer, sample.KraContainer, sample.OidContainer, builder.CertificateTemplatesDn,
            builder.EnrollmentServicesDn, builder.CertificationAuthoritiesDn, sample.NtAuth })
        {
            Assert.True(session.Exists(dn), dn);
            Assert.NotNull(session.Snapshot(dn)!.GetBytes(Attr.NtSecurityDescriptor));
        }

        Assert.Equal(PkiSamples.RootCaDer, Bytes(session, sample.RootStore, "cACertificate").Single());
        Assert.Equal(PkiSamples.IssuingCaDer, Bytes(session, sample.IssuingAia, "cACertificate").Single());
        Assert.Equal(PkiSamples.IssuingCaDer, Bytes(session, sample.NtAuth, "cACertificate").Single());
        Assert.Equal("CN=Kontoso Wystawiaj!0105cy CA,CN=AIA," + builder.PublicKeyServicesDn, sample.IssuingAia);

        Assert.Equal(new BigInteger(7), CrlNumber(Bytes(session, sample.RootCdp, "certificateRevocationList").Single()));
        Assert.Equal(new BigInteger(42), CrlNumber(Bytes(session, sample.IssuingCdp, "certificateRevocationList").Single()));
        Assert.Single(Bytes(session, sample.IssuingCdp, "deltaRevocationList"));
        using var kra = X509CertificateLoader.LoadCertificate(Bytes(session, sample.KraAgent, "userCertificate").Single());
        Assert.Contains(PkiSamples.KeyRecoveryAgent, Ekus(kra));

        var high = session.Snapshot(sample.HighAssuranceOid!)!;
        Assert.Equal(PkiSamples.ForestOid + ".500", high.GetString("msPKI-Cert-Template-OID"));
        Assert.True(DistinguishedName.AreEqual(sample.LinkedGroup!, high.GetString("msDS-OIDToGroupLink")!));
        // Universal in universal (Enterprise Admins): AD forbids a universal group in a global one such as Domain Admins.
        Assert.Contains(sample.LinkedGroup!, session.GetValues(sample.EnterpriseAdmins, Attr.Member), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(sample.LinkedGroup!, session.GetValues(builder.DomainAdminsDn, Attr.Member), StringComparer.OrdinalIgnoreCase);
        Assert.Null(session.Snapshot(sample.MediumAssuranceOid!)!.GetString("msDS-OIDToGroupLink"));
    }

    [Fact]
    public void TheEnterpriseCas_TheirComputers_AndThePublishedTemplates()
    {
        var (builder, _, sample) = Create();
        var session = builder.Session;
        var root = session.Snapshot(sample.RootCa)!;
        var issuing = session.Snapshot(sample.IssuingCa)!;

        Assert.Equal(PkiSamples.RootCaHost, root.GetString(Attr.DnsHostName));
        Assert.Equal(PkiSamples.IssuingCaHost, issuing.GetString(Attr.DnsHostName));
        Assert.Equal(PkiSamples.IssuingCaHost, session.Snapshot(sample.IssuingCaComputer)!.GetString(Attr.DnsHostName));
        Assert.Contains(sample.IssuingCaComputer, session.GetValues(sample.CertPublishers, Attr.Member), StringComparer.OrdinalIgnoreCase);
        Assert.Equal($"{builder.DomainSid}-517", session.Snapshot(sample.CertPublishers)!.ObjectSid);
        Assert.Equal(PublicKeyTemplates.PublishedByRoot, root.GetStrings("certificateTemplates"));
        Assert.Equal(PublicKeyTemplates.PublishedByIssuing.Order(StringComparer.Ordinal), issuing.GetStrings("certificateTemplates").Order(StringComparer.Ordinal));
        Assert.DoesNotContain(PublicKeyTemplates.Esc1NotPublished, issuing.GetStrings("certificateTemplates"));
        Assert.StartsWith("1\n2\n0\nhttps://", issuing.GetStrings("msPKI-Enrollment-Servers").Single(), StringComparison.Ordinal); // 2 = Kerberos

        Assert.Equal(PublicKeyTemplates.All.Count, sample.Templates.Count);
        Assert.Equal(PublicKeyTemplates.All.Count, PublicKeyTemplates.All.Distinct(StringComparer.Ordinal).Count());
        foreach (var (cn, dn) in sample.Templates)
        {
            var template = session.Snapshot(dn)!;
            Assert.Equal(cn, template.Dn.Name);
            Assert.NotNull(template.GetBytes(Attr.NtSecurityDescriptor));
            Assert.Equal(8, template.GetBytes("pKIExpirationPeriod")!.Length);
        }

        var esc1 = session.Snapshot(sample.Template(PublicKeyTemplates.Esc1))!;
        Assert.Equal(1, esc1.GetInt32("msPKI-Certificate-Name-Flag")! & 0x1);
        Assert.Equal(2, esc1.GetInt32("msPKI-Template-Schema-Version"));
        Assert.Equal(2, session.Snapshot(sample.Template(PublicKeyTemplates.Esc1Safe))!.GetInt32("msPKI-Enrollment-Flag")! & 0x2);
        Assert.Null(session.Snapshot(sample.Template(PublicKeyTemplates.Esc15))!.GetInt32("msPKI-Template-Schema-Version"));
        Assert.Equal(0x80000, session.Snapshot(sample.Template(PublicKeyTemplates.Esc9))!.GetInt32("msPKI-Enrollment-Flag"));
        Assert.Contains("`PZPWSTR`", session.Snapshot(sample.Template(PublicKeyTemplates.Esc3TargetSafe))!.GetString("msPKI-RA-Application-Policies"), StringComparison.Ordinal);
        Assert.Contains(PublicKeyTemplates.All, t => t.Any(c => "ąćęłńóśźż".Contains(c, StringComparison.Ordinal)));
    }

    [Fact]
    public void Accounts_CarryMappings_Certificates_KeyCredentials_AndKeyMaterial()
    {
        var (builder, company, sample) = Create();
        var session = builder.Session;

        Assert.StartsWith("X509:<I>", session.GetValues(builder.AdministratorDn, "altSecurityIdentities").Single(), StringComparison.Ordinal);
        Assert.Contains("<S>", session.GetValues(builder.AdministratorDn, "altSecurityIdentities").Single(), StringComparison.Ordinal);
        Assert.Contains("<SR>0063", session.GetValues(company.JanKowalski, "altSecurityIdentities").Single(), StringComparison.Ordinal);
        Assert.StartsWith("X509:<RFC822>", session.GetValues(company.LukaszWisniewski, "altSecurityIdentities").Single(), StringComparison.Ordinal);
        Assert.StartsWith("Kerberos:", session.GetValues(company.PawelLewandowski, "altSecurityIdentities").Single(), StringComparison.Ordinal);

        using var jan = X509CertificateLoader.LoadCertificate(Bytes(session, company.JanKowalski, "userCertificate").Single());
        Assert.Equal(new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc), jan.NotAfter.ToUniversalTime());
        Assert.Contains(jan.Extensions, e => e.Oid?.Value == "1.3.6.1.4.1.311.25.2");
        using var expired = X509CertificateLoader.LoadCertificate(Bytes(session, company.GrazynaDabrowska, "userCertificate").Single());
        Assert.True(expired.NotAfter.ToUniversalTime() < PkiSamples.Now.UtcDateTime);
        using var kdc = X509CertificateLoader.LoadCertificate(Bytes(session, builder.DomainControllerDn, "userCertificate").Single());
        Assert.Contains(PkiSamples.KdcAuthentication, Ekus(kdc));
        using var zofia = X509CertificateLoader.LoadCertificate(Bytes(session, sample.Contact!, "userCertificate").Single());
        Assert.True(zofia.NotAfter <= PkiSamples.IssuingCa.NotAfter, "AD CS never issues past the CA's own expiry.");
        Assert.Single(Bytes(session, company.JanKowalski, "userPKCS12"));

        var link = session.GetValues(company.JanKowalski, "msDS-KeyCredentialLink").Single();
        var parts = link.Split(':', 4);
        Assert.Equal("B", parts[0]);
        Assert.Equal(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2].Length);
        Assert.Equal("00020000", parts[2][..8]);
        Assert.True(DistinguishedName.AreEqual(company.JanKowalski, parts[3]));
    }

    [Fact]
    public void Certificates_HaveTheFixedDatesSerialsAndPolishSubjects()
    {
        var root = PkiSamples.RootCa;
        var issuing = PkiSamples.IssuingCa;

        Assert.True(root.HasPrivateKey);
        Assert.Contains("Kontoso Główny CA", root.Subject, StringComparison.Ordinal);
        Assert.Equal(root.Subject, root.Issuer);
        Assert.Equal("1A2B3C4D01", root.SerialNumber);
        Assert.Equal(new DateTime(2021, 3, 1, 8, 0, 0, DateTimeKind.Utc), root.NotBefore.ToUniversalTime());
        Assert.Contains("Kontoso Wystawiający CA", issuing.Subject, StringComparison.Ordinal);
        Assert.Equal(root.Subject, issuing.Issuer);
        Assert.Equal(new DateTime(2027, 2, 28, 8, 0, 0, DateTimeKind.Utc), issuing.NotAfter.ToUniversalTime());

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.Add(issuing);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = PkiSamples.Now.UtcDateTime;
        using var ldaps = X509CertificateLoader.LoadCertificate(PkiSamples.DcLdapsDer());
        Assert.True(chain.Build(ldaps), string.Join(", ", chain.ChainStatus.Select(s => s.Status)));
        Assert.Equal("6100", ldaps.SerialNumber);

        using var kra = X509CertificateLoader.LoadCertificate(PkiSamples.KraDer());
        Assert.True(kra.NotAfter <= issuing.NotAfter, "AD CS never issues past the CA's own expiry.");
        using var kdc = X509CertificateLoader.LoadCertificate(PkiSamples.DcKdcDer());
        Assert.True(kdc.NotAfter <= issuing.NotAfter);
        Assert.True(ldaps.NotAfter <= issuing.NotAfter);

        using var server = PkiSamples.ServerCertificate();
        Assert.True(server.HasPrivateKey);
        Assert.Contains(PkiSamples.ServerAuthentication, Ekus(server));
    }

    [Fact]
    public void TheFixedCertificates_AreTheSameBytesOnEveryCall()
    {
        Assert.Equal(PkiSamples.DcLdapsDer(), PkiSamples.DcLdapsDer());
        Assert.Equal(PkiSamples.DcKdcDer(), PkiSamples.DcKdcDer());
        Assert.Equal(PkiSamples.KraDer(), PkiSamples.KraDer());
        var notBefore = new DateTimeOffset(2025, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var notAfter = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var jan = PkiSamples.UserCertificateDer("Jan Kowalski", "jan.kowalski@contoso.test", null, 0x6300, notBefore, notAfter);
        Assert.Equal(jan, PkiSamples.UserCertificateDer("Jan Kowalski", "jan.kowalski@contoso.test", null, 0x6300, notBefore, notAfter));
        Assert.NotEqual(jan, PkiSamples.UserCertificateDer("Jan Kowalski", "jan.kowalski@contoso.test", null, 0x6399, notBefore, notAfter));

        // A copy per call: a test that changes the bytes does not change the sample.
        var kra = PkiSamples.KraDer();
        kra[^1] ^= 0xFF;
        Assert.NotEqual(kra, PkiSamples.KraDer());

        var (builder, _, sample) = Create();
        Assert.Equal(PkiSamples.KraDer(), Bytes(builder.Session, sample.KraAgent, "userCertificate").Single());
        Assert.Equal(PkiSamples.DcKdcDer(), Bytes(builder.Session, builder.DomainControllerDn, "userCertificate").Single());
    }

    [Fact]
    public void Crls_TheDeltaIndicator_TheFreshestCrl_AndTheCrlWithoutNextUpdate()
    {
        Assert.Equal(new BigInteger(42), CrlNumber(PkiSamples.IssuingCrlDer()));
        var issuingBase = TbsExtensions(PkiSamples.IssuingCrlDer(), out _);
        Assert.Contains(PkiSamples.FreshestCrlOid, issuingBase);
        Assert.DoesNotContain("2.5.29.27", issuingBase);
        Assert.Equal([PkiSamples.IssuingDeltaCrlHttpUrl], FreshestCrlUrls(PkiSamples.IssuingCrlDer()));
        Assert.Equal(PkiSamples.IssuingCrlDer(), PkiSamples.IssuingCrlDer());
        Assert.DoesNotContain(PkiSamples.FreshestCrlOid, TbsExtensions(PkiSamples.RootCrlDer(), out _));

        var delta = TbsExtensions(PkiSamples.IssuingDeltaCrlDer(), out var deltaNext);
        Assert.Contains("2.5.29.27", delta);
        Assert.Contains("2.5.29.20", delta);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), deltaNext);

        var withoutNext = TbsExtensions(PkiSamples.CrlWithoutNextUpdateDer(), out var next);
        Assert.Null(next);
        Assert.DoesNotContain("2.5.29.27", withoutNext);
    }

    [Fact]
    public void TheAclSample_HasOneDelegationPerTargetKind()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPublicKeyAclSample(company);

        foreach (var dn in new[] { sample.PublicKeyServices, sample.NtAuth, sample.SalesPolicyOid, sample.IssuingCa, sample.UserTemplate, sample.IssuingCaComputer, sample.Administrator })
        {
            Assert.NotNull(builder.Session.Snapshot(dn)!.GetBytes(Attr.NtSecurityDescriptor));
        }

        Assert.Equal(PublicKeyTemplates.Defaults.Count, sample.Pki.Templates.Count);
        Assert.True(builder.Session.Exists($"CN=pKICertificateTemplate,{builder.SchemaDn}"));
        Assert.True(builder.Session.Exists($"CN=Certificate-Enrollment,CN=Extended-Rights,{builder.ConfigurationDn}"));
    }

    private static IReadOnlyList<byte[]> Bytes(FakeDirectorySession session, string dn, string attribute) =>
        session.Snapshot(dn)!.TryGet(attribute, out var values) ? [.. values.OfType<byte[]>()] : [];

    private static List<string> Ekus(X509Certificate2 certificate) =>
        [.. certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()).Select(o => o.Value!)];

    private static BigInteger CrlNumber(byte[] crl)
    {
        _ = CertificateRevocationListBuilder.Load(crl, out var number);
        return number;
    }

    /// <summary>The URIs of the Freshest CRL extension of a DER CRL (CRLDistributionPoints syntax, full names only).</summary>
    private static List<string> FreshestCrlUrls(byte[] crl)
    {
        var tbs = new AsnReader(crl, AsnEncodingRules.DER).ReadSequence().ReadSequence();
        var extensions = default(AsnReader);
        while (tbs.HasData)
        {
            var tag = tbs.PeekTag();
            if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0)
            {
                extensions = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                break;
            }

            tbs.ReadEncodedValue();
        }

        var urls = new List<string>();
        while (extensions is { HasData: true })
        {
            var extension = extensions.ReadSequence();
            if (extension.ReadObjectIdentifier() != PkiSamples.FreshestCrlOid)
            {
                continue;
            }

            if (extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
            {
                extension.ReadBoolean();
            }

            var points = new AsnReader(extension.ReadOctetString(), AsnEncodingRules.DER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                var fullName = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                while (fullName.HasData)
                {
                    urls.Add(fullName.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
                }
            }
        }

        return urls;
    }

    /// <summary>The extension OIDs and nextUpdate of a DER CRL.</summary>
    private static List<string> TbsExtensions(byte[] crl, out DateTimeOffset? nextUpdate)
    {
        var tbs = new AsnReader(crl, AsnEncodingRules.DER).ReadSequence().ReadSequence();
        _ = tbs.ReadInteger();
        tbs.ReadSequence();
        tbs.ReadEncodedValue();
        _ = tbs.ReadUtcTime();
        nextUpdate = tbs.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? tbs.ReadUtcTime() : null;
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            tbs.ReadSequence();
        }

        var oids = new List<string>();
        var extensions = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        while (extensions.HasData)
        {
            oids.Add(extensions.ReadSequence().ReadObjectIdentifier());
        }

        return oids;
    }
}
