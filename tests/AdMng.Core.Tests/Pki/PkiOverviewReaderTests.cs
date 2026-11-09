using System.Formats.Asn1;
using System.Numerics;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Core.Schema;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <see cref="PkiOverviewReader"/> (docs/dev/p-certificate-services.md 2.1) on <see cref="FakeDirectoryBuilder.AddPublicKeySample"/>:
/// the one subtree search and its flags, the snapshot lists, the NTAuth and CDP matches, the CA computer resolution, the
/// read notices instead of exceptions, and <see cref="PkiOverviewReader.ReadDomainControllersAsync"/>.
/// </summary>
public sealed class PkiOverviewReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeDirectoryBuilder Builder, SampleCompany Company, PublicKeySample Sample) Create()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        return (builder, company, builder.AddPublicKeySample(company));
    }

    private static PkiReadOptions Options(PkiReadOptions? options = null) => (options ?? new PkiReadOptions()) with { Time = new ManualTimeProvider() };

    [Fact]
    public async Task OneSubtreeSearch_WithTheSecurityDescriptorsAndExtendedDns()
    {
        var (builder, _, _) = Create();
        var reader = new RecordingReader(builder.Session);

        await PkiOverviewReader.ReadAsync(reader, Options(), null, Ct);

        var search = reader.Searches[0];
        Assert.True(DistinguishedName.AreEqual(PkiSamples.PublicKeyServicesDn, search.BaseDn));
        Assert.Equal(SearchScope.Subtree, search.Scope);
        Assert.Equal(LdapFilter.Or(PkiClasses.All.Select(c => LdapFilter.Equal(Attr.ObjectClass, c))), search.Filter);
        Assert.Equal(PkiAttributes.OverviewAttributes, search.Attributes);
        Assert.True(search.ReadSecurityDescriptor);
        Assert.True(search.ExtendedDn);
        Assert.Equal(1000, search.PageSize);
        Assert.Single(reader.Searches, s => DistinguishedName.AreEqual(s.BaseDn, PkiSamples.PublicKeyServicesDn));

        // Binary values come with the search (never GetAllValuesAsync, which decodes UTF-8); nothing else is read or written.
        Assert.Equal(0, reader.Gets);
        Assert.Equal(0, reader.RangedReads);
        Assert.Empty(builder.Session.Writes);
    }

    [Fact]
    public async Task TheSecurityDescriptors_ComeOnlyWithTheSdFlag()
    {
        // The fake returns nTSecurityDescriptor whatever the flag; this reader drops it without the flag, as a DC does
        // for a caller without the SD flags control – the reader must ask for it.
        var (builder, _, _) = Create();
        var reader = new RecordingReader(builder.Session) { EnforceSecurityDescriptorFlag = true };

        var snapshot = await PkiOverviewReader.ReadAsync(reader, Options(), null, Ct);

        Assert.NotEmpty(snapshot.Templates);
        Assert.All(snapshot.Templates, t => Assert.NotNull(t.SecurityDescriptor));
        Assert.DoesNotContain(snapshot.Notices, n => n.Code == PkiReadNoticeCode.SecurityDescriptorUnreadable);
    }

    [Fact]
    public async Task TheEnterpriseCas_TheirCertificates_Templates_Ces_NtAuth_AndComputers()
    {
        var (builder, _, sample) = Create();

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        Assert.Empty(snapshot.Notices);
        Assert.Equal(PkiSamples.ConfigurationDn, snapshot.ConfigurationDn);
        Assert.Equal(PkiSamples.Now, snapshot.ReadAt);
        Assert.Equal([PkiSamples.RootCaName, PkiSamples.IssuingCaName], snapshot.EnterpriseCas.Select(c => c.Name));

        var root = snapshot.EnterpriseCas[0];
        Assert.True(DistinguishedName.AreEqual(sample.RootCa, root.Dn));
        Assert.Equal(GuidOf(builder, sample.RootCa), root.ObjectGuid);
        Assert.Equal(PkiSamples.RootCaName, root.DisplayName);
        Assert.Equal(PkiSamples.RootCaHost, root.HostName);
        Assert.True(DistinguishedName.AreEqual(sample.RootCaComputer, root.ComputerDn));
        Assert.Equal(builder.SidOf(sample.RootCaComputer), root.ComputerSid);
        Assert.Equal(PkiSamples.RootCaDer, Assert.Single(root.Certificates).Der);
        Assert.True(root.Certificates[0].Summary!.IsSelfSigned);
        Assert.Equal(PublicKeyTemplates.PublishedByRoot, root.PublishedTemplates);
        Assert.Empty(root.EnrollmentServers);
        Assert.False(root.InNtAuth);

        var issuing = snapshot.EnterpriseCas[1];
        Assert.Equal(PkiSamples.IssuingCaHost, issuing.HostName);
        Assert.True(DistinguishedName.AreEqual(sample.IssuingCaComputer, issuing.ComputerDn));
        Assert.Equal(builder.SidOf(sample.IssuingCaComputer), issuing.ComputerSid);
        Assert.Equal(PkiSamples.IssuingCaDer, Assert.Single(issuing.Certificates).Der);
        Assert.Equal(PublicKeyTemplates.PublishedByIssuing.Order(StringComparer.Ordinal), issuing.PublishedTemplates.Order(StringComparer.Ordinal));
        Assert.Equal(["https://pki.contoso.test/Kontoso%20Wystawiaj!0105cy%20CA_CES_Kerberos/service.svc/CES"], issuing.EnrollmentServers);
        Assert.True(issuing.InNtAuth);
    }

    [Fact]
    public async Task TheCaStores_Cdp_Kra_Oid_Templates_AndTheTrustMaterial()
    {
        var (builder, _, sample) = Create();

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var root = Assert.Single(snapshot.RootCas);
        Assert.Equal(PkiCaStore.RootCa, root.Store);
        Assert.Equal(PkiSamples.RootCaName, root.Name);
        Assert.Equal(PkiSamples.RootCaDer, Assert.Single(root.Certificates).Der);
        Assert.Equal(
            [PkiSamples.Sanitized(PkiSamples.RootCaName), PkiSamples.Sanitized(PkiSamples.IssuingCaName)],
            snapshot.AiaObjects.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.All(snapshot.AiaObjects, a => Assert.Equal(PkiCaStore.Aia, a.Store));
        Assert.Equal(PkiCaStore.NtAuth, snapshot.NtAuth!.Store);
        Assert.Equal(PkiSamples.IssuingCaDer, Assert.Single(snapshot.NtAuth.Certificates).Der);

        // CDP: matched to the CAs by issuer + key identifier, never by the sanitized cn.
        Assert.Equal([PkiSamples.RootCaComputer, PkiSamples.IssuingCaComputer], snapshot.CrlLists.Select(l => l.HostName));
        var rootCrl = snapshot.CrlLists[0];
        var issuingCrl = snapshot.CrlLists[1];
        Assert.Equal(PkiSamples.Sanitized(PkiSamples.RootCaName), rootCrl.Name);
        Assert.Equal(new BigInteger(7), rootCrl.Base!.Summary!.CrlNumber);
        Assert.Null(rootCrl.Delta);
        Assert.Null(rootCrl.Authority);
        Assert.Equal(GuidOf(builder, sample.RootCa), rootCrl.IssuerCaGuid);
        Assert.Equal(new BigInteger(42), issuingCrl.Base!.Summary!.CrlNumber);
        Assert.True(issuingCrl.Delta!.Summary!.IsDelta);
        Assert.Equal(new BigInteger(42), issuingCrl.Delta.Summary.BaseCrlNumber);
        Assert.Equal(new BigInteger(43), issuingCrl.Delta.Summary.CrlNumber);
        Assert.Equal(GuidOf(builder, sample.IssuingCa), issuingCrl.IssuerCaGuid);

        var kra = Assert.Single(snapshot.KraAgents);
        Assert.Equal("Agent odzyskiwania kluczy Kontoso", kra.Name);
        Assert.Equal(PkiSamples.KraDer(), Assert.Single(kra.Certificates).Der);

        Assert.Equal(2, snapshot.Oids.Count);
        var high = snapshot.Oids.Single(o => o.DisplayName == "Polityka wysokiej pewności");
        Assert.Equal(PkiSamples.ForestOid + ".500", high.Oid);
        Assert.Equal(2, high.Flags);
        Assert.Equal(GuidOf(builder, sample.HighAssuranceOid!), high.ObjectGuid);
        Assert.True(DistinguishedName.AreEqual(sample.LinkedGroup!, high.GroupLinkDn), high.GroupLinkDn); // the extended DN stripped
        Assert.Null(snapshot.Oids.Single(o => o.DisplayName == "Polityka średniej pewności").GroupLinkDn);

        Assert.Equal(PublicKeyTemplates.All.Order(StringComparer.Ordinal), snapshot.Templates.Select(t => t.Cn).Order(StringComparer.Ordinal));
        var esc1 = snapshot.Templates.Single(t => t.Cn == PublicKeyTemplates.Esc1);
        Assert.True(DistinguishedName.AreEqual(sample.Template(PublicKeyTemplates.Esc1), esc1.Dn));
        Assert.Equal(GuidOf(builder, sample.Template(PublicKeyTemplates.Esc1)), esc1.ObjectGuid);
        Assert.NotNull(esc1.SecurityDescriptor);

        Assert.Equal([PkiSamples.RootCaDer], snapshot.TrustMaterial.Roots);
        Assert.Equal(2, snapshot.TrustMaterial.Intermediates.Count);
        Assert.Contains(PkiSamples.IssuingCaDer, snapshot.TrustMaterial.Intermediates);
        Assert.Equal([PkiSamples.IssuingCaDer], snapshot.TrustMaterial.NtAuth);
    }

    [Fact]
    public async Task Renewals_ComeNewestFirst_AndInNtAuth_MatchesAnyOfThem()
    {
        var (builder, _, sample) = Create();
        var renewal = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = PkiSamples.IssuingCaName,
            ExtraRdns = [("O", PkiSamples.Organization), ("C", "PL")],
            SerialNumber = 0x1A2B3C4D03,
            NotBefore = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            NotAfter = new DateTimeOffset(2031, 9, 1, 8, 0, 0, TimeSpan.Zero),
            IsCa = true,
            PathLength = 0,
            Rsa = true,
            Issuer = PkiSamples.RootCa,
        });
        builder.Session.SetValues(sample.IssuingCa, "cACertificate", PkiSamples.IssuingCaDer, renewal);
        builder.Session.SetValues(sample.RootCa, "cACertificate", PkiSamples.RootCaDer);
        builder.Session.SetValues(sample.NtAuth, "cACertificate", renewal);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var issuing = snapshot.EnterpriseCas.Single(c => c.Name == PkiSamples.IssuingCaName);
        Assert.Equal([renewal, PkiSamples.IssuingCaDer], issuing.Certificates.Select(c => c.Der));
        Assert.True(issuing.InNtAuth);
        Assert.Equal(3, snapshot.TrustMaterial.Intermediates.Count);
    }

    [Fact]
    public async Task CrossCertificatePairs_AndTheArl_AreRead()
    {
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.RootAia, "crossCertificatePair", CertificatePair(PkiSamples.IssuingCaDer, PkiSamples.RootCaDer), PkiSamples.KraDer());
        builder.Session.SetValues(sample.RootCdp, "authorityRevocationList", PkiSamples.RootCrlDer());

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var rootAia = snapshot.AiaObjects.Single(a => a.Name == PkiSamples.Sanitized(PkiSamples.RootCaName));
        Assert.Equal([PkiSamples.IssuingCaDer, PkiSamples.RootCaDer, PkiSamples.KraDer()], rootAia.CrossCertificates.Select(c => c.Der));
        Assert.All(rootAia.CrossCertificates, c => Assert.NotNull(c.Summary));
        Assert.Contains(PkiSamples.KraDer(), snapshot.TrustMaterial.Intermediates);
        var rootCrl = snapshot.CrlLists.Single(l => l.HostName == PkiSamples.RootCaComputer);
        Assert.Equal(new BigInteger(7), rootCrl.Authority!.Summary!.CrlNumber);
        Assert.Empty(snapshot.Notices);
    }

    [Fact]
    public async Task MalformedValues_KeepTheirRow_WithANotice()
    {
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.IssuingAia, "cACertificate", PkiSamples.IssuingCaDer, PkiSamples.MalformedDer);
        builder.Session.SetValues(sample.IssuingCdp, "deltaRevocationList", PkiSamples.MalformedDer);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var aia = snapshot.AiaObjects.Single(a => a.Name == PkiSamples.Sanitized(PkiSamples.IssuingCaName));
        Assert.Equal(2, aia.Certificates.Count);
        Assert.Null(aia.Certificates[1].Summary);
        Assert.Equal(PkiSamples.MalformedDer, aia.Certificates[1].Der);
        var crl = snapshot.CrlLists.Single(l => l.HostName == PkiSamples.IssuingCaComputer);
        Assert.Null(crl.Delta!.Summary);
        Assert.NotNull(crl.Base!.Summary);
        Assert.Equal(GuidOf(builder, sample.IssuingCa), crl.IssuerCaGuid);

        Assert.Equal(2, snapshot.Notices.Count);
        var certificate = Assert.Single(snapshot.Notices, n => n.Code == PkiReadNoticeCode.CertificateMalformed);
        Assert.Equal(GuidOf(builder, sample.IssuingAia), certificate.ObjectGuid);
        Assert.True(DistinguishedName.AreEqual(sample.IssuingAia, certificate.ObjectDn));
        var crlNotice = Assert.Single(snapshot.Notices, n => n.Code == PkiReadNoticeCode.CrlMalformed);
        Assert.Equal(GuidOf(builder, sample.IssuingCdp), crlNotice.ObjectGuid);
    }

    [Fact]
    public async Task ThePlaceholderByte_OfAnEmptyStore_IsNoCertificate()
    {
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.NtAuth, "cACertificate", new byte[] { 0x00 });
        builder.Session.SetValues(sample.NtAuth, "certificateRevocationList", new byte[] { 0x00 });
        builder.Session.SetValues(sample.NtAuth, "authorityRevocationList", new byte[] { 0x00 });

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        Assert.Empty(snapshot.NtAuth!.Certificates);
        Assert.Empty(snapshot.TrustMaterial.NtAuth);
        Assert.All(snapshot.EnterpriseCas, c => Assert.False(c.InNtAuth));
        Assert.Empty(snapshot.Notices);
    }

    [Fact]
    public async Task ATemplateWithoutItsDescriptor_IsReported_AndKept()
    {
        var (builder, _, sample) = Create();
        var dn = sample.Template(PublicKeyTemplates.Esc4);
        builder.Session.SetValues(dn, Attr.NtSecurityDescriptor);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var template = snapshot.Templates.Single(t => t.Cn == PublicKeyTemplates.Esc4);
        Assert.Null(template.SecurityDescriptor);
        var notice = Assert.Single(snapshot.Notices);
        Assert.Equal(PkiReadNoticeCode.SecurityDescriptorUnreadable, notice.Code);
        Assert.Equal(template.ObjectGuid, notice.ObjectGuid);
        Assert.True(DistinguishedName.AreEqual(dn, notice.ObjectDn));
        Assert.Equal(PublicKeyTemplates.All.Count, snapshot.Templates.Count);
    }

    [Fact]
    public async Task NoPublicKeyServices_IsAnEmptySnapshot_WithBaseNotFound()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        var reader = new RecordingReader(builder.Session);

        var snapshot = await PkiOverviewReader.ReadAsync(reader, Options(), null, Ct);

        var notice = Assert.Single(snapshot.Notices);
        Assert.Equal(PkiReadNoticeCode.BaseNotFound, notice.Code);
        Assert.True(DistinguishedName.AreEqual(PkiSamples.PublicKeyServicesDn, notice.ObjectDn));
        Assert.Null(notice.ObjectGuid);
        Assert.Empty(snapshot.EnterpriseCas);
        Assert.Empty(snapshot.Templates);
        Assert.Null(snapshot.NtAuth);
        Assert.Equal(PkiSamples.Now, snapshot.ReadAt);
        Assert.Single(reader.Searches);
    }

    [Fact]
    public async Task ADeniedBase_IsAnEmptySnapshot_WithBaseAccessDenied()
    {
        var (builder, _, _) = Create();
        builder.Session.FailNext(FakeOperationKind.Search, PkiSamples.PublicKeyServicesDn, DirectoryErrorKind.AccessDenied);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        Assert.Equal(PkiReadNoticeCode.BaseAccessDenied, Assert.Single(snapshot.Notices).Code);
        Assert.Empty(snapshot.EnterpriseCas);
        Assert.Empty(snapshot.TrustMaterial.Roots);
    }

    [Fact]
    public async Task OtherDirectoryErrors_Propagate()
    {
        var (builder, _, _) = Create();
        builder.Session.FailNext(FakeOperationKind.Search, PkiSamples.PublicKeyServicesDn, DirectoryErrorKind.ServerUnavailable);

        var error = await Assert.ThrowsAsync<DirectoryException>(() => PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
    }

    [Fact]
    public async Task MissingWellKnownContainers_AreReported()
    {
        // The Watchdog infrastructure seeds only Public Key Services, Certificate Templates, Enrollment Services,
        // Certification Authorities and NTAuthCertificates (as the Samba lab has).
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        builder.AddWatchdogInfrastructure();

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        Assert.All(snapshot.Notices, n => Assert.Equal(PkiReadNoticeCode.ContainerMissing, n.Code));
        Assert.Equal(
            ["CN=AIA", "CN=CDP", "CN=KRA", "CN=OID"],
            snapshot.Notices.Select(n => DistinguishedName.Parse(n.ObjectDn!).Leaf.ToString()).Order(StringComparer.Ordinal));
        Assert.All(snapshot.Notices, n => Assert.True(DistinguishedName.Parse(n.ObjectDn!).Parent == DistinguishedName.Parse(PkiSamples.PublicKeyServicesDn)));
        Assert.NotNull(snapshot.NtAuth);
        Assert.Empty(snapshot.NtAuth.Certificates);
    }

    [Fact]
    public async Task ADeletedContainer_IsReported()
    {
        var (builder, _, sample) = Create();
        await builder.Session.DeleteAsync(sample.KraContainer, subtree: true, Ct);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var notice = Assert.Single(snapshot.Notices);
        Assert.Equal(PkiReadNoticeCode.ContainerMissing, notice.Code);
        Assert.True(DistinguishedName.AreEqual(sample.KraContainer, notice.ObjectDn));
        Assert.Empty(snapshot.KraAgents);
    }

    [Fact]
    public async Task TheCaComputerLookup_IsOneEscapedSearchPerCa_OnTheSession()
    {
        var (builder, _, _) = Create();
        var reader = new RecordingReader(builder.Session);

        await PkiOverviewReader.ReadAsync(reader, Options(), null, Ct);

        var lookups = reader.Searches.Skip(1).ToList();
        Assert.Equal(2, lookups.Count);
        Assert.All(lookups, s => Assert.Equal(builder.DomainDn, s.BaseDn));
        Assert.Equal(
            LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), LdapFilter.Or(LdapFilter.Equal(Attr.DnsHostName, PkiSamples.IssuingCaHost), LdapFilter.Equal(Attr.SamAccountName, "srv-ca02$"))),
            lookups[1].Filter);
    }

    [Fact]
    public async Task ACaComputer_ByItsPreWindows2000Name_WhenTheHostNameIsNotSet()
    {
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.IssuingCaComputer, Attr.DnsHostName);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var issuing = snapshot.EnterpriseCas.Single(c => c.Name == PkiSamples.IssuingCaName);
        Assert.True(DistinguishedName.AreEqual(sample.IssuingCaComputer, issuing.ComputerDn));
        Assert.Empty(snapshot.Notices);
    }

    [Fact]
    public async Task AnUnknownCaHostOfTheWindowsDomain_IsUnresolved()
    {
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.IssuingCa, Attr.DnsHostName, "srv-ca99.contoso.test");

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var issuing = snapshot.EnterpriseCas.Single(c => c.Name == PkiSamples.IssuingCaName);
        Assert.Null(issuing.ComputerDn);
        Assert.Null(issuing.ComputerSid);
        var notice = Assert.Single(snapshot.Notices);
        Assert.Equal(PkiReadNoticeCode.CaComputerUnresolved, notice.Code);
        Assert.Equal(issuing.ObjectGuid, notice.ObjectGuid);
        Assert.NotNull(snapshot.EnterpriseCas.Single(c => c.Name == PkiSamples.RootCaName).ComputerSid);
    }

    [Theory]
    [InlineData("srv-ca02.fabrikam.test")]
    [InlineData("srv-ca02.oddział.contoso.test")]
    public async Task ACaHostOfAnotherDomain_IsNeverTakenForAComputerWithTheSameName(string host)
    {
        // A child domain's srv-ca02 is not this domain's SRV-CA02$: its SID stays unknown (the CA computer is Other).
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.IssuingCa, Attr.DnsHostName, host);
        var reader = new RecordingReader(builder.Session);

        var snapshot = await PkiOverviewReader.ReadAsync(reader, Options(), null, Ct);

        var issuing = snapshot.EnterpriseCas.Single(c => c.Name == PkiSamples.IssuingCaName);
        Assert.Equal(host, issuing.HostName);
        Assert.Null(issuing.ComputerDn);
        Assert.Null(issuing.ComputerSid);
        Assert.Equal(PkiReadNoticeCode.CaComputerInOtherDomain, Assert.Single(snapshot.Notices).Code);
        Assert.Equal(
            LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), LdapFilter.Equal(Attr.DnsHostName, host)),
            reader.Searches.Last().Filter);
    }

    [Fact]
    public async Task ADisjointNamespace_ResolvesByTheExactHostName()
    {
        var (builder, _, sample) = Create();
        const string host = "srv-ca02.pki.kontoso.example";
        builder.Session.SetValues(sample.IssuingCa, Attr.DnsHostName, host);
        builder.Session.SetValues(sample.IssuingCaComputer, Attr.DnsHostName, host);

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        var issuing = snapshot.EnterpriseCas.Single(c => c.Name == PkiSamples.IssuingCaName);
        Assert.True(DistinguishedName.AreEqual(sample.IssuingCaComputer, issuing.ComputerDn));
        Assert.Equal(builder.SidOf(sample.IssuingCaComputer), issuing.ComputerSid);
        Assert.Empty(snapshot.Notices);
    }

    [Fact]
    public async Task TheLookup_CanBeSwitchedOff()
    {
        var (builder, _, sample) = Create();
        builder.Session.SetValues(sample.IssuingCa, Attr.DnsHostName, "srv-ca02.fabrikam.test");
        var reader = new RecordingReader(builder.Session);

        var snapshot = await PkiOverviewReader.ReadAsync(reader, Options(new PkiReadOptions { ResolveCaComputers = false }), null, Ct);

        Assert.Single(reader.Searches);
        Assert.All(snapshot.EnterpriseCas, c => Assert.Null(c.ComputerDn));
        Assert.Empty(snapshot.Notices);
    }

    [Fact]
    public async Task Progress_IsReportedPerPage_AndAtTheEnd()
    {
        var (builder, _, _) = Create();
        var progress = new ListProgress<PkiReadProgress>();

        await PkiOverviewReader.ReadAsync(builder.Session, Options(new PkiReadOptions { PageSize = 10 }), progress, Ct);

        Assert.NotEmpty(progress.Items);
        Assert.All(progress.Items.SkipLast(1), p => Assert.Equal(0, p.Objects % 10));
        Assert.Equal(progress.Items.Select(p => p.Objects).Order(), progress.Items.Select(p => p.Objects));
        Assert.True(progress.Items[^1].Objects > PublicKeyTemplates.All.Count);
    }

    [Fact]
    public async Task ACancelledRead_Throws()
    {
        var (builder, _, _) = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PkiOverviewReader.ReadAsync(builder.Session, Options(), null, cancelled.Token));
    }

    [Fact]
    public async Task TheSnapshot_PrintsCountsOnly()
    {
        var (builder, _, _) = Create();

        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, Options(), null, Ct);

        Assert.StartsWith("PkiSnapshot(2 enterprise CAs, 1 roots, 2 AIA, 2 CDP, 1 KRA, 2 OID,", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PkiSamples.SampleTexts, t => snapshot.ToString().Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheDomainControllers_WithTheirPublishedCertificates()
    {
        var (builder, _, _) = Create();
        builder.AddDomainController("rodc-gda01.contoso.test", new Guid("1f0e2d3c-4b5a-4968-8796-a5b4c3d2e1f0"), isReadOnly: true);
        var reader = new RecordingReader(builder.Session);

        var controllers = await PkiOverviewReader.ReadDomainControllersAsync(reader, Ct);

        var search = Assert.Single(reader.Searches);
        Assert.Equal(builder.DomainDn, search.BaseDn);
        Assert.Equal(PrivilegedAccessScanner.DomainControllerFilter, search.Filter);
        Assert.Equal(PkiAttributes.DomainControllerAttributes, search.Attributes);
        Assert.Equal(["dc01.contoso.test", "rodc-gda01.contoso.test"], controllers.Select(c => c.HostName));
        var dc01 = controllers[0];
        Assert.True(DistinguishedName.AreEqual(builder.DomainControllerDn, dc01.Dn));
        Assert.False(dc01.IsReadOnly);
        Assert.Equal(PkiSamples.DcKdcDer(), Assert.Single(dc01.Certificates));
        Assert.True(controllers[1].IsReadOnly);
        Assert.Empty(controllers[1].Certificates);
        Assert.Equal("PkiDomainController(DC, 1 certificates)", dc01.ToString());
    }

    private static Guid GuidOf(FakeDirectoryBuilder builder, string dn) => builder.Session.Snapshot(dn)!.ObjectGuid!.Value;

    /// <summary><c>CertificatePair ::= SEQUENCE { forward [0] Certificate OPTIONAL, reverse [1] Certificate OPTIONAL }</c> (explicit tags).</summary>
    private static byte[] CertificatePair(byte[] forward, byte[] reverse)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        var forwardTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        writer.PushSequence(forwardTag);
        writer.WriteEncodedValue(forward);
        writer.PopSequence(forwardTag);
        var reverseTag = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
        writer.PushSequence(reverseTag);
        writer.WriteEncodedValue(reverse);
        writer.PopSequence(reverseTag);
        writer.PopSequence();
        return writer.Encode();
    }

    private sealed class ListProgress<T> : IProgress<T>
    {
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_items)
                {
                    return [.. _items];
                }
            }
        }

        public void Report(T value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }

    /// <summary>
    /// Forwards to the fake session and records the calls; with <see cref="EnforceSecurityDescriptorFlag"/> it drops
    /// <c>nTSecurityDescriptor</c> from the results of a search without <see cref="DirectorySearch.ReadSecurityDescriptor"/>,
    /// as a DC does (the fake returns it either way).
    /// </summary>
    private sealed class RecordingReader(IDirectoryReader inner) : IDirectoryReader
    {
        private readonly List<DirectorySearch> _searches = [];

        public bool EnforceSecurityDescriptorFlag { get; init; }

        public IReadOnlyList<DirectorySearch> Searches
        {
            get
            {
                lock (_searches)
                {
                    return [.. _searches];
                }
            }
        }

        public int Gets { get; private set; }

        public int RangedReads { get; private set; }

        public SessionInfo Info => inner.Info;

        public ISchemaCatalog Schema => inner.Schema;

        public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_searches)
            {
                _searches.Add(search);
            }

            await foreach (var obj in inner.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                yield return EnforceSecurityDescriptorFlag && !search.ReadSecurityDescriptor && obj.Has(Attr.NtSecurityDescriptor)
                    ? new DirectoryObject(obj.DistinguishedName, obj.Attributes.Values.Where(a => !string.Equals(a.Name, Attr.NtSecurityDescriptor, StringComparison.OrdinalIgnoreCase)))
                    : obj;
            }
        }

        public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
        {
            Gets++;
            return inner.GetAsync(distinguishedName, attributes, cancellationToken);
        }

        public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken)
        {
            RangedReads++;
            return inner.GetAllValuesAsync(distinguishedName, attribute, cancellationToken);
        }
    }
}
