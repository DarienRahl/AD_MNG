using AdMng.Core.Pki;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// The pkiview-style health rules (docs/dev/p-certificate-services.md 2.4, decision 2) on hand-built summaries at the fixed
/// clock: the CA (180 d) and end-entity (30 d) thresholds at their edges, the CRL margin <c>max(1 d, 20 %)</c>, a CRL without
/// <c>nextUpdate</c>, <c>ldap:///</c> locations found / missing / outside the PKI subtree, <c>ldap://host</c>, <c>file:</c>,
/// UNC and <c>http</c> with and without results, the base / delta CRL rows, KRA and NTAuth, and the HTTP URL list.
/// </summary>
public sealed class PkiHealthTests
{
    private static readonly DateTimeOffset Now = PkiSamples.Now;
    private static readonly Guid RootGuid = new("1f0e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");
    private static readonly Guid IssuingGuid = new("2a1b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
    private const string RootSubject = "CN=Kontoso Główny CA, O=Kontoso Sp. z o.o., C=PL";
    private const string IssuingSubject = "CN=Kontoso Wystawiający CA, O=Kontoso Sp. z o.o., C=PL";
    private const string RootSki = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
    private const string IssuingSki = "B1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

    private static string RootAiaDn => "CN=" + PkiSamples.Sanitized(PkiSamples.RootCaName) + ",CN=AIA," + PkiSamples.PublicKeyServicesDn;

    private static string RootCdpDn => "CN=" + PkiSamples.Sanitized(PkiSamples.RootCaName) + ",CN=" + PkiSamples.RootCaComputer + ",CN=CDP," + PkiSamples.PublicKeyServicesDn;

    private static string IssuingCdpDn => "CN=" + PkiSamples.Sanitized(PkiSamples.IssuingCaName) + ",CN=" + PkiSamples.IssuingCaComputer + ",CN=CDP," + PkiSamples.PublicKeyServicesDn;

    private static string RootDeltaLdapUrl => "ldap:///" + RootCdpDn + "?deltaRevocationList?base?objectClass=cRLDistributionPoint";

    [Theory]
    [InlineData(180 * 24 * 3600L, PkiHealthStatus.Expiring)]
    [InlineData(180 * 24 * 3600L + 1, PkiHealthStatus.Ok)]
    [InlineData(1L, PkiHealthStatus.Expiring)]
    [InlineData(0L, PkiHealthStatus.Expiring)]
    [InlineData(-1L, PkiHealthStatus.Expired)]
    public void CaCertificate_UsesTheCaThreshold(long secondsLeft, PkiHealthStatus expected)
    {
        var notAfter = Now.AddSeconds(secondsLeft);
        var snapshot = Snapshot(cas: [Ca(IssuingGuid, Issuing(notAfter))]);

        var row = Rows(snapshot).First(r => r.Item == PkiHealthItem.CaCertificate);

        Assert.Equal(expected, row.Status);
        Assert.Equal(notAfter, row.ValidUntil);
        Assert.Equal(IssuingGuid, row.CaGuid);
    }

    [Fact]
    public void ACertificate_NotYetValid_IsUnknown_AndAMissingOneUnavailable()
    {
        var future = Issuing(Now.AddYears(2)) with { NotBefore = Now.AddHours(1) };
        var rows = Rows(Snapshot(cas:
        [
            Ca(IssuingGuid, future),
            new PkiEnrollmentService("CN=Pusty CA", RootGuid, "Pusty CA", null, null, null, null, [], [], [], false),
            Ca(Guid.NewGuid(), null),
        ]));

        var certificates = rows.Where(r => r.Item == PkiHealthItem.CaCertificate).ToList();
        Assert.Equal([PkiHealthStatus.Unknown, PkiHealthStatus.Unavailable, PkiHealthStatus.Unavailable], certificates.Select(r => r.Status));
        Assert.Null(certificates[1].ValidUntil);
    }

    [Theory]
    [InlineData(30 * 24 * 3600L, PkiHealthStatus.Expiring)]
    [InlineData(30 * 24 * 3600L + 1, PkiHealthStatus.Ok)]
    [InlineData(-60L, PkiHealthStatus.Expired)]
    public void KraCertificates_UseTheEndEntityThreshold(long secondsLeft, PkiHealthStatus expected)
    {
        var kra = new CertificateSummary { Subject = "CN=Agent odzyskiwania kluczy Kontoso", Issuer = IssuingSubject, NotBefore = Now.AddYears(-1), NotAfter = Now.AddSeconds(secondsLeft) };
        var snapshot = Snapshot(kra: [new PkiKraAgent("CN=Agent odzyskiwania kluczy Kontoso,CN=KRA," + PkiSamples.PublicKeyServicesDn, Guid.NewGuid(), "Agent", [Certificate(kra)])]);

        var row = Assert.Single(Rows(snapshot));

        Assert.Equal(PkiHealthItem.KraCertificate, row.Item);
        Assert.Null(row.CaGuid);
        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void TheThresholds_ComeFromTheSettings()
    {
        var kra = new CertificateSummary { NotBefore = Now.AddYears(-1), NotAfter = Now.AddDays(11) };
        var ca = Issuing(Now.AddDays(100));
        var snapshot = Snapshot(cas: [Ca(IssuingGuid, ca)], kra: [new PkiKraAgent("CN=KRA1", Guid.NewGuid(), "KRA1", [Certificate(kra)])]);

        var strict = PkiHealth.Evaluate(snapshot, new PkiHealthThresholds(10, 90), Now, null);
        var defaults = PkiHealth.Evaluate(snapshot, PkiHealthThresholds.Default, Now, null);

        Assert.Equal(PkiHealthStatus.Ok, strict.Single(r => r.Item == PkiHealthItem.KraCertificate).Status);
        Assert.Equal(PkiHealthStatus.Ok, strict.Single(r => r.Item == PkiHealthItem.CaCertificate).Status);
        Assert.Equal(PkiHealthStatus.Expiring, defaults.Single(r => r.Item == PkiHealthItem.KraCertificate).Status);
        Assert.Equal(PkiHealthStatus.Expiring, defaults.Single(r => r.Item == PkiHealthItem.CaCertificate).Status);
    }

    public static TheoryData<double, double?, PkiHealthStatus> CrlCases() => new()
    {
        // thisUpdate / nextUpdate in hours from now: margin = max(1 day, 20 % of the period).
        { -6 * 24, 24, PkiHealthStatus.Expiring },           // 7 d period: margin 33.6 h, 24 h left
        { -5.6 * 24, 1.4 * 24, PkiHealthStatus.Ok },         // exactly the margin left: not "less than"
        { -24, 23, PkiHealthStatus.Expiring },               // 47 h period: 20 % < 1 d, so 1 d
        { -24, 24, PkiHealthStatus.Ok },                     // exactly one day left
        { -300 * 24, 65 * 24, PkiHealthStatus.Expiring },    // a year: 73 d margin
        { -300 * 24, 80 * 24, PkiHealthStatus.Ok },          // 380 d period: 76 d margin, 80 d left
        { -10 * 24, -1.0 / 3600, PkiHealthStatus.Expired },  // one second past nextUpdate
        { 1, 7 * 24, PkiHealthStatus.Unknown },              // not valid yet (clock skew)
        { -24, null, PkiHealthStatus.Unknown },              // no nextUpdate: unknown, never expired
    };

    [Theory]
    [MemberData(nameof(CrlCases))]
    public void Crls_FollowTheMargin(double thisHours, double? nextHours, PkiHealthStatus expected)
    {
        var crl = Crl(IssuingSki, Now.AddHours(thisHours), nextHours is { } next ? Now.AddHours(next) : null);
        var snapshot = Snapshot(cas: [Ca(IssuingGuid, Issuing(Now.AddYears(2)))], crls: [new PkiCrlList(IssuingCdpDn, Guid.NewGuid(), "x", "SRV-CA02", new PkiCrl([], crl), null, IssuingGuid)]);

        var row = Rows(snapshot).Single(r => r.Item == PkiHealthItem.BaseCrl);

        Assert.Equal(expected, row.Status);
        Assert.Equal(crl.NextUpdate, row.ValidUntil);
        Assert.DoesNotContain(Rows(snapshot), r => r.Item == PkiHealthItem.DeltaCrl);
    }

    [Fact]
    public void BaseAndDeltaRows_ComeFromTheListsOfTheCa()
    {
        var baseCrl = Crl(IssuingSki, Now.AddDays(-3), Now.AddDays(4)) with { FreshestCrl = [PkiSamples.IssuingDeltaCrlHttpUrl] };
        var delta = Crl(IssuingSki, Now.AddHours(-12), Now.AddHours(6)) with { IsDelta = true };
        var snapshot = Snapshot(
            cas: [Ca(RootGuid, Root()), Ca(IssuingGuid, Issuing(Now.AddYears(2)))],
            crls:
            [
                new PkiCrlList(IssuingCdpDn, Guid.NewGuid(), "x", "SRV-CA02", new PkiCrl([], baseCrl), new PkiCrl([], delta), IssuingGuid),
                new PkiCrlList("CN=Kontoso Wystawiający CA(1),CN=SRV-CA02,CN=CDP," + PkiSamples.PublicKeyServicesDn, Guid.NewGuid(), "x(1)", "SRV-CA02",
                    new PkiCrl([], baseCrl), null, IssuingGuid),
                new PkiCrlList("CN=Obcy,CN=CDP," + PkiSamples.PublicKeyServicesDn, Guid.NewGuid(), "Obcy", null, new PkiCrl([], null), null, null),
            ]);

        var rows = Rows(snapshot);

        // The root has no list of its own: one unavailable base row; the foreign list belongs to nobody.
        var root = rows.Where(r => r.CaGuid == RootGuid && r.Item is PkiHealthItem.BaseCrl or PkiHealthItem.DeltaCrl).ToList();
        Assert.Equal((PkiHealthItem.BaseCrl, PkiHealthStatus.Unavailable), (Assert.Single(root).Item, root[0].Status));

        var issuing = rows.Where(r => r.CaGuid == IssuingGuid && r.Item is PkiHealthItem.BaseCrl or PkiHealthItem.DeltaCrl).ToList();
        Assert.Equal(
            [(PkiHealthItem.BaseCrl, 0, PkiHealthStatus.Ok), (PkiHealthItem.DeltaCrl, 0, PkiHealthStatus.Expiring), (PkiHealthItem.BaseCrl, 1, PkiHealthStatus.Ok), (PkiHealthItem.DeltaCrl, 1, PkiHealthStatus.Unavailable)],
            issuing.Select(r => (r.Item, r.Index, r.Status)));
        Assert.Equal(Now.AddHours(6), issuing[1].ValidUntil);
    }

    [Fact]
    public void AnUnparseableBaseCrl_IsUnavailable()
    {
        var snapshot = Snapshot(cas: [Ca(IssuingGuid, Issuing(Now.AddYears(2)))], crls: [new PkiCrlList(IssuingCdpDn, Guid.NewGuid(), "x", null, new PkiCrl(PkiSamples.MalformedDer, null), null, IssuingGuid)]);

        Assert.Equal(PkiHealthStatus.Unavailable, Rows(snapshot).Single(r => r.Item == PkiHealthItem.BaseCrl).Status);
    }

    [Fact]
    public void LdapLocations_ResolveAgainstTheSnapshot()
    {
        var issuing = Issuing(Now.AddYears(2),
            aia: [PkiSamples.RootCrtLdapUrl, "ldap:///CN=Brak,CN=AIA," + PkiSamples.PublicKeyServicesDn + "?cACertificate?base?objectClass=certificationAuthority"],
            cdp: [PkiSamples.RootCrlLdapUrl, "ldap:///CN=Brak,CN=SRV-CA01,CN=CDP," + PkiSamples.PublicKeyServicesDn + "?certificateRevocationList?base?objectClass=cRLDistributionPoint"],
            freshest: [RootDeltaLdapUrl]);
        var rootCrl = Crl(RootSki, Now.AddDays(-100), Now.AddDays(200));
        var rootDelta = Crl(RootSki, Now.AddDays(-1), Now.AddHours(10)) with { IsDelta = true };
        var snapshot = Snapshot(
            cas: [Ca(RootGuid, Root()), Ca(IssuingGuid, issuing)],
            aia: [new PkiCaObject(RootAiaDn, Guid.NewGuid(), PkiSamples.Sanitized(PkiSamples.RootCaName), PkiCaStore.Aia, [Certificate(Root())], [])],
            crls: [new PkiCrlList(RootCdpDn, Guid.NewGuid(), "x", "SRV-CA01", new PkiCrl([], rootCrl), new PkiCrl([], rootDelta), RootGuid)]);

        var rows = Rows(snapshot).Where(r => r.CaGuid == IssuingGuid).ToList();

        Assert.Equal(
            [
                (PkiHealthItem.AiaLocation, 0, PkiHealthStatus.Ok), (PkiHealthItem.AiaLocation, 1, PkiHealthStatus.Unavailable),
                (PkiHealthItem.CdpLocation, 0, PkiHealthStatus.Ok), (PkiHealthItem.CdpLocation, 1, PkiHealthStatus.Unavailable),
                (PkiHealthItem.DeltaCrlLocation, 0, PkiHealthStatus.Expiring),
            ],
            rows.Where(r => r.Url is not null).Select(r => (r.Item, r.Index, r.Status)));
        Assert.Equal(Root().NotAfter, rows.First(r => r.Item == PkiHealthItem.AiaLocation).ValidUntil);
        Assert.Equal(rootCrl.NextUpdate, rows.First(r => r.Item == PkiHealthItem.CdpLocation).ValidUntil);
        Assert.Equal(PkiSamples.RootCrtLdapUrl, rows.First(r => r.Item == PkiHealthItem.AiaLocation).Url);
    }

    [Fact]
    public void AnLdapLocation_HoldingAnotherCertificateOrCrl_IsUnavailable()
    {
        var stranger = new CertificateSummary { Subject = "CN=Obcy CA", Issuer = "CN=Obcy CA", SubjectKeyIdentifier = "FFEE", NotBefore = Now.AddYears(-1), NotAfter = Now.AddYears(5) };
        var issuing = Issuing(Now.AddYears(2), aia: [PkiSamples.RootCrtLdapUrl], cdp: [PkiSamples.RootCrlLdapUrl]);
        var snapshot = Snapshot(
            cas: [Ca(IssuingGuid, issuing)],
            aia: [new PkiCaObject(RootAiaDn, Guid.NewGuid(), "x", PkiCaStore.Aia, [Certificate(stranger)], [])],
            crls: [new PkiCrlList(RootCdpDn, Guid.NewGuid(), "x", null, new PkiCrl([], Crl("FFEE", Now.AddDays(-1), Now.AddDays(6))), null, null)]);

        var rows = Rows(snapshot).Where(r => r.Url is not null).ToList();

        Assert.All(rows, r => Assert.Equal(PkiHealthStatus.Unavailable, r.Status));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void PercentEncodedLdapUrls_AsAdCsWritesThem_Resolve()
    {
        var encoded = PkiSamples.RootCrtLdapUrl.Replace(" ", "%20", StringComparison.Ordinal);
        var snapshot = Snapshot(
            cas: [Ca(IssuingGuid, Issuing(Now.AddYears(2), aia: [encoded]))],
            aia: [new PkiCaObject(RootAiaDn, Guid.NewGuid(), "x", PkiCaStore.Aia, [Certificate(Root())], [])]);

        Assert.Equal(PkiHealthStatus.Ok, Rows(snapshot).Single(r => r.Item == PkiHealthItem.AiaLocation).Status);
    }

    [Theory]
    [InlineData("ldap://srv-ca02.contoso.test/CN=Kontoso,CN=AIA,CN=Public Key Services,CN=Services,CN=Configuration,DC=contoso,DC=test?cACertificate")]
    [InlineData("ldap:///CN=Kontoso,OU=PKI,DC=contoso,DC=test?cACertificate?base?objectClass=certificationAuthority")]
    [InlineData(@"file://\\srv-ca02.contoso.test\CertEnroll\Kontoso.crt")]
    [InlineData(@"\\srv-ca02.contoso.test\CertEnroll\Kontoso.crt")]
    [InlineData("http://pki.contoso.test/CertEnroll/Kontoso.crt")]
    [InlineData("ftp://pki.contoso.test/Kontoso.crt")]
    [InlineData("Żółć-Gęślą-Jaźń")]
    public void LocationsNotChecked_AreUnknown(string url)
    {
        var snapshot = Snapshot(cas: [Ca(IssuingGuid, Issuing(Now.AddYears(2), aia: [url]))]);

        var row = Rows(snapshot).Single(r => r.Item == PkiHealthItem.AiaLocation);

        Assert.Equal(PkiHealthStatus.Unknown, row.Status);
        Assert.Null(row.ValidUntil);
        Assert.Equal(url, row.Url);
    }

    public static TheoryData<string, PkiHttpOutcome, string, PkiHealthStatus> HttpCases() => new()
    {
        { "aia", PkiHttpOutcome.Ok, "issuer", PkiHealthStatus.Ok },
        { "aia", PkiHttpOutcome.Ok, "expired issuer", PkiHealthStatus.Expired },
        { "aia", PkiHttpOutcome.Ok, "stranger", PkiHealthStatus.Unavailable },
        { "aia", PkiHttpOutcome.NotFound, "none", PkiHealthStatus.Unavailable },
        { "aia", PkiHttpOutcome.Timeout, "none", PkiHealthStatus.Unavailable },
        { "aia", PkiHttpOutcome.InvalidContent, "none", PkiHealthStatus.Unavailable },
        { "aia", PkiHttpOutcome.NotChecked, "none", PkiHealthStatus.Unknown },
        { "cdp", PkiHttpOutcome.Ok, "base", PkiHealthStatus.Ok },
        { "cdp", PkiHttpOutcome.Ok, "delta", PkiHealthStatus.Unavailable },
        { "cdp", PkiHttpOutcome.HttpError, "none", PkiHealthStatus.Unavailable },
        { "delta", PkiHttpOutcome.Ok, "delta", PkiHealthStatus.Expiring },
        { "delta", PkiHttpOutcome.Ok, "base", PkiHealthStatus.Unavailable },
    };

    [Theory]
    [MemberData(nameof(HttpCases))]
    public void HttpLocations_TakeTheirResult(string location, PkiHttpOutcome outcome, string content, PkiHealthStatus expected)
    {
        var issuing = Issuing(Now.AddYears(2), aia: [PkiSamples.RootCrtHttpUrl], cdp: [PkiSamples.RootCrlHttpUrl], freshest: [PkiSamples.IssuingDeltaCrlHttpUrl]);
        var (item, kind, url) = location switch
        {
            "aia" => (PkiHealthItem.AiaLocation, PkiUrlKind.AiaCaIssuers, PkiSamples.RootCrtHttpUrl),
            "cdp" => (PkiHealthItem.CdpLocation, PkiUrlKind.Cdp, PkiSamples.RootCrlHttpUrl),
            _ => (PkiHealthItem.DeltaCrlLocation, PkiUrlKind.DeltaCdp, PkiSamples.IssuingDeltaCrlHttpUrl),
        };
        (CertificateSummary? Certificate, CrlSummary? Crl) fetched = content switch
        {
            "issuer" => (Root(), null),
            "expired issuer" => (Root() with { NotAfter = Now.AddDays(-1) }, null),
            "stranger" => (new CertificateSummary { Subject = "CN=Obcy", NotAfter = Now.AddYears(1) }, null),
            "base" => (null, Crl(RootSki, Now.AddDays(-1), Now.AddDays(30))),
            "delta" => (null, Crl(RootSki, Now.AddHours(-20), Now.AddHours(4)) with { IsDelta = true }),
            _ => (null, null),
        };
        var results = new List<PkiHttpCheckResult>
        {
            // A result for the same URL under another kind is not this row's.
            new(url, PkiUrlKind.Ocsp, PkiHttpOutcome.NotFound, 404, null, null),
            new(url, kind, outcome, outcome == PkiHttpOutcome.Ok ? 200 : null, fetched.Certificate, fetched.Crl),
        };
        var snapshot = Snapshot(cas: [Ca(IssuingGuid, issuing)]);

        var row = PkiHealth.Evaluate(snapshot, PkiHealthThresholds.Default, Now, results).Single(r => r.Item == item);
        var without = PkiHealth.Evaluate(snapshot, PkiHealthThresholds.Default, Now, null).Single(r => r.Item == item);

        Assert.Equal(expected, row.Status);
        Assert.Equal(PkiHealthStatus.Unknown, without.Status);
    }

    [Fact]
    public void NtAuthRows_UseTheCaThreshold_AndAMissingStoreIsUnavailable()
    {
        var ntAuth = new PkiCaObject("CN=NTAuthCertificates," + PkiSamples.PublicKeyServicesDn, Guid.NewGuid(), "NTAuthCertificates", PkiCaStore.NtAuth,
            [Certificate(Issuing(Now.AddDays(100))), Certificate(Root()), new PkiCertificate(PkiSamples.MalformedDer, null)], []);

        var rows = Rows(Snapshot(ntAuth: ntAuth)).Where(r => r.Item == PkiHealthItem.NtAuthCertificate).ToList();
        Assert.Equal([PkiHealthStatus.Expiring, PkiHealthStatus.Ok, PkiHealthStatus.Unavailable], rows.Select(r => r.Status));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Index));
        Assert.All(rows, r => Assert.Null(r.CaGuid));

        var missing = Rows(Snapshot(cas: [Ca(IssuingGuid, Issuing(Now.AddYears(2)))])).Single(r => r.Item == PkiHealthItem.NtAuthCertificate);
        Assert.Equal(PkiHealthStatus.Unavailable, missing.Status);
    }

    [Fact]
    public void AnEmptySnapshot_HasNoRows()
    {
        Assert.Empty(PkiHealth.Evaluate(PkiSnapshot.Empty(PkiSamples.ConfigurationDn, Now), PkiHealthThresholds.Default, Now, null));
        Assert.Empty(PkiHealth.HttpUrls(PkiSnapshot.Empty(PkiSamples.ConfigurationDn, Now)));
    }

    [Fact]
    public void TheRowsOfACa_ComeInPkiviewOrder_AndMoveWithTheClock()
    {
        var issuing = Issuing(Now.AddDays(200), aia: [PkiSamples.RootCrtLdapUrl, PkiSamples.RootCrtHttpUrl], cdp: [PkiSamples.RootCrlHttpUrl], freshest: [PkiSamples.IssuingDeltaCrlHttpUrl]);
        var snapshot = Snapshot(
            cas: [Ca(IssuingGuid, issuing)],
            crls: [new PkiCrlList(IssuingCdpDn, Guid.NewGuid(), "x", null, new PkiCrl([], Crl(IssuingSki, Now.AddDays(-1), Now.AddDays(6))), null, IssuingGuid)]);

        var rows = Rows(snapshot);
        Assert.Equal(
            [PkiHealthItem.CaCertificate, PkiHealthItem.AiaLocation, PkiHealthItem.AiaLocation, PkiHealthItem.CdpLocation, PkiHealthItem.DeltaCrlLocation, PkiHealthItem.BaseCrl, PkiHealthItem.NtAuthCertificate],
            rows.Select(r => r.Item));
        Assert.Equal(PkiHealthStatus.Ok, rows[0].Status);

        // Health is computed when shown: the same snapshot a month later.
        Assert.Equal(PkiHealthStatus.Expiring, PkiHealth.Evaluate(snapshot, PkiHealthThresholds.Default, Now.AddDays(30), null)[0].Status);
    }

    [Fact]
    public void HttpUrls_ListEveryHttpLocationOncePerKind()
    {
        var issuing = Issuing(Now.AddYears(2), aia: [PkiSamples.RootCrtLdapUrl, PkiSamples.RootCrtHttpUrl], cdp: [PkiSamples.RootCrlLdapUrl, PkiSamples.RootCrlHttpUrl, PkiSamples.IssuingCrlFileUrl])
            with { AiaOcsp = [PkiSamples.OcspUrl] };
        var renewal = Issuing(Now.AddYears(-1), aia: [PkiSamples.RootCrtHttpUrl], cdp: ["HTTPS://pki.contoso.test/CertEnroll/Stary.crl"]);
        var kra = new CertificateSummary { CrlDistributionPoints = [PkiSamples.IssuingCrlHttpUrl], AiaCaIssuers = [PkiSamples.IssuingCrtHttpUrl], AiaOcsp = [PkiSamples.OcspUrl] };
        var baseCrl = Crl(IssuingSki, Now.AddDays(-1), Now.AddDays(6)) with { FreshestCrl = [PkiSamples.IssuingDeltaCrlHttpUrl, "ldap:///CN=x?deltaRevocationList"] };
        var snapshot = Snapshot(
            cas: [new PkiEnrollmentService("CN=Kontoso Wystawiający CA", IssuingGuid, PkiSamples.IssuingCaName, null, PkiSamples.IssuingCaHost, null, null,
                [Certificate(issuing), Certificate(renewal), new PkiCertificate(PkiSamples.MalformedDer, null)], [], [], true)],
            kra: [new PkiKraAgent("CN=KRA1", Guid.NewGuid(), "KRA1", [Certificate(kra)])],
            crls: [new PkiCrlList(IssuingCdpDn, Guid.NewGuid(), "x", null, new PkiCrl([], baseCrl), null, IssuingGuid)]);

        var urls = PkiHealth.HttpUrls(snapshot);

        Assert.Equal(
            [
                new PkiUrl(PkiSamples.RootCrtHttpUrl, PkiUrlKind.AiaCaIssuers),
                new PkiUrl(PkiSamples.RootCrlHttpUrl, PkiUrlKind.Cdp),
                new PkiUrl(PkiSamples.OcspUrl, PkiUrlKind.Ocsp),
                new PkiUrl("HTTPS://pki.contoso.test/CertEnroll/Stary.crl", PkiUrlKind.Cdp),
                new PkiUrl(PkiSamples.IssuingCrtHttpUrl, PkiUrlKind.AiaCaIssuers),
                new PkiUrl(PkiSamples.IssuingCrlHttpUrl, PkiUrlKind.Cdp),
                new PkiUrl(PkiSamples.IssuingDeltaCrlHttpUrl, PkiUrlKind.DeltaCdp),
            ],
            urls);
    }

    [Fact]
    public void HttpUrls_IncludeTheStoresAndTheFreshestCrlOfCertificates()
    {
        var cross = new CertificateSummary { AiaCaIssuers = ["http://partner.example/cross.crt"], FreshestCrl = ["http://partner.example/delta.crl"] };
        var snapshot = Snapshot(
            aia: [new PkiCaObject(RootAiaDn, Guid.NewGuid(), "x", PkiCaStore.Aia, [Certificate(Root())], [Certificate(cross)])],
            ntAuth: new PkiCaObject("CN=NTAuthCertificates", Guid.NewGuid(), "NTAuthCertificates", PkiCaStore.NtAuth,
                [Certificate(new CertificateSummary { CrlDistributionPoints = ["http://pki.contoso.test/ntauth.crl"] })], []));

        Assert.Equal(
            [
                new PkiUrl("http://partner.example/cross.crt", PkiUrlKind.AiaCaIssuers),
                new PkiUrl("http://partner.example/delta.crl", PkiUrlKind.DeltaCdp),
                new PkiUrl("http://pki.contoso.test/ntauth.crl", PkiUrlKind.Cdp),
            ],
            PkiHealth.HttpUrls(snapshot));
    }

    [Fact]
    public void ARow_PrintsNoUrl()
    {
        var row = new PkiHealthRow(IssuingGuid, PkiHealthItem.CdpLocation, 1, PkiHealthStatus.Unavailable, Now, PkiSamples.IssuingCrlHttpUrl);

        Assert.Equal("CdpLocation[1] Unavailable " + IssuingGuid.ToString("B"), row.ToString());
    }

    [Fact(Skip = "P1 wave 1 merge")]
    public async Task TheSample_ReadByTheOverviewReader_IsHealthyWhereItShouldBe()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        builder.AddPublicKeySample(company);
        var snapshot = await PkiOverviewReader.ReadAsync(builder.Session, new PkiReadOptions(), null, TestContext.Current.CancellationToken);

        var rows = PkiHealth.Evaluate(snapshot, PkiHealthThresholds.Default, Now, null);
        var issuing = snapshot.EnterpriseCas.Single(ca => ca.Name == PkiSamples.IssuingCaName).ObjectGuid;
        var root = snapshot.EnterpriseCas.Single(ca => ca.Name == PkiSamples.RootCaName).ObjectGuid;

        // The issuing CA ends 2027-02-28: inside the 180-day CA threshold; its locations point at the root's AIA and CDP objects.
        Assert.Equal(PkiHealthStatus.Expiring, rows.Single(r => r.CaGuid == issuing && r.Item == PkiHealthItem.CaCertificate).Status);
        Assert.Equal(PkiHealthStatus.Ok, rows.Single(r => r.CaGuid == root && r.Item == PkiHealthItem.CaCertificate).Status);
        Assert.Equal(PkiHealthStatus.Ok, rows.First(r => r.CaGuid == issuing && r.Url == PkiSamples.RootCrtLdapUrl).Status);
        Assert.Equal(PkiHealthStatus.Ok, rows.First(r => r.CaGuid == issuing && r.Url == PkiSamples.RootCrlLdapUrl).Status);
        Assert.All(rows.Where(r => r.Url?.StartsWith("http", StringComparison.Ordinal) == true), r => Assert.Equal(PkiHealthStatus.Unknown, r.Status));
        // Base 42 (2026-10-10 – 10-17 12:00) has 4 days of a 1.5-day margin; delta 43 (10-12 – 10-14) 12 hours of one day.
        Assert.Equal(
            [PkiHealthStatus.Ok, PkiHealthStatus.Expiring],
            rows.Where(r => r.CaGuid == issuing && r.Item is PkiHealthItem.BaseCrl or PkiHealthItem.DeltaCrl).Select(r => r.Status));
        Assert.Equal(PkiHealthStatus.Ok, rows.Single(r => r.CaGuid == root && r.Item == PkiHealthItem.BaseCrl).Status);
        Assert.Equal(PkiHealthStatus.Ok, rows.Single(r => r.Item == PkiHealthItem.KraCertificate).Status);
        Assert.Contains(PkiHealth.HttpUrls(snapshot), u => u.Url == PkiSamples.IssuingDeltaCrlHttpUrl && u.Kind == PkiUrlKind.DeltaCdp);
    }

    private static IReadOnlyList<PkiHealthRow> Rows(PkiSnapshot snapshot) => PkiHealth.Evaluate(snapshot, PkiHealthThresholds.Default, Now, null);

    private static CertificateSummary Root() => new()
    {
        Subject = RootSubject,
        Issuer = RootSubject,
        SubjectKeyIdentifier = RootSki,
        IsCa = true,
        IsSelfSigned = true,
        NotBefore = new(2021, 3, 1, 8, 0, 0, TimeSpan.Zero),
        NotAfter = new(2041, 3, 1, 8, 0, 0, TimeSpan.Zero),
    };

    private static CertificateSummary Issuing(DateTimeOffset notAfter, IReadOnlyList<string>? aia = null, IReadOnlyList<string>? cdp = null, IReadOnlyList<string>? freshest = null) => new()
    {
        Subject = IssuingSubject,
        Issuer = RootSubject,
        SubjectKeyIdentifier = IssuingSki,
        AuthorityKeyIdentifier = RootSki,
        IsCa = true,
        NotBefore = new(2024, 3, 1, 8, 0, 0, TimeSpan.Zero),
        NotAfter = notAfter,
        AiaCaIssuers = aia ?? [],
        CrlDistributionPoints = cdp ?? [],
        FreshestCrl = freshest ?? [],
    };

    private static CrlSummary Crl(string authorityKeyIdentifier, DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate) => new()
    {
        Issuer = authorityKeyIdentifier == RootSki ? RootSubject : IssuingSubject,
        AuthorityKeyIdentifier = authorityKeyIdentifier,
        ThisUpdate = thisUpdate,
        NextUpdate = nextUpdate,
    };

    private static PkiCertificate Certificate(CertificateSummary summary) => new([], summary);

    private static PkiEnrollmentService Ca(Guid guid, CertificateSummary? certificate) => new(
        "CN=" + (guid == RootGuid ? PkiSamples.RootCaName : PkiSamples.IssuingCaName) + ",CN=Enrollment Services," + PkiSamples.PublicKeyServicesDn,
        guid, guid == RootGuid ? PkiSamples.RootCaName : PkiSamples.IssuingCaName, null, null, null, null,
        certificate is null ? [new PkiCertificate(PkiSamples.MalformedDer, null)] : [Certificate(certificate)], [], [], false);

    private static PkiSnapshot Snapshot(
        IReadOnlyList<PkiEnrollmentService>? cas = null,
        IReadOnlyList<PkiCaObject>? aia = null,
        IReadOnlyList<PkiCrlList>? crls = null,
        IReadOnlyList<PkiKraAgent>? kra = null,
        PkiCaObject? ntAuth = null) =>
        PkiSnapshot.Empty(PkiSamples.ConfigurationDn, Now) with
        {
            EnterpriseCas = cas ?? [],
            AiaObjects = aia ?? [],
            CrlLists = crls ?? [],
            KraAgents = kra ?? [],
            NtAuth = ntAuth,
        };
}
