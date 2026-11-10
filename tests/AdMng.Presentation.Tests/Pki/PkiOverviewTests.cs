using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// <i>Usługi kluczy publicznych</i> on <c>AddPublicKeySample</c> (docs/dev/p-certificate-services.md 2.5, 2.10): the enterprise
/// CAs with path, templates, CES, NTAuth match and CA-host navigation, the trust stores, CRLs, KRA and OIDs, the health tab
/// (re-evaluated after a threshold change and a clock move; the HTTP check on the button only) and the LDAPS tab (fake probe).
/// </summary>
public sealed class PkiOverviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PkiHarness H, PkiOverviewDocumentViewModel Document)> OpenAsync(CultureInfo? culture = null)
    {
        var h = await PkiHarness.CreateAsync(culture);
        var document = await h.OpenAsync<PkiOverviewDocumentViewModel>(new PkiDocumentRequest(PkiDocumentKind.Overview));
        return (h, document);
    }

    private static PkiCaRowViewModel Ca(PkiOverviewDocumentViewModel document, string name) => Assert.Single(document.Authorities, a => a.Name == name);

    [Fact]
    public async Task TheEnterpriseCas_ShowTheirCertificate_Path_Templates_AndNtAuth()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;

        Assert.Null(document.Error);
        Assert.Equal([PkiSamples.RootCaName, PkiSamples.IssuingCaName], document.Authorities.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Usługi kluczy publicznych", document.Title);
        Assert.Contains("Urzędy przedsiębiorstwa: 2", document.SummaryText, StringComparison.Ordinal);
        Assert.Equal(document.SummaryText, document.StatusText);
        Assert.Contains(PkiSamples.DcHost, document.ReadAtText, StringComparison.OrdinalIgnoreCase);

        var issuing = Ca(document, PkiSamples.IssuingCaName);
        Assert.Equal(PkiSamples.IssuingCaHost, issuing.Host);
        Assert.Equal("W magazynie NTAuth", issuing.NtAuthText);
        Assert.Equal([PkiSamples.IssuingCaName, PkiSamples.RootCaName + " (zaufany urząd główny w AD)"], issuing.Chain);
        Assert.Contains(PublicKeyTemplates.Esc1, issuing.PublishedTemplates);
        Assert.True(issuing.HasEnrollmentServers);
        Assert.False(issuing.HasNtAuthWarning);
        Assert.Contains(issuing.Details, d => d.Label == "Podmiot" && d.Value.Contains(PkiSamples.IssuingCaName, StringComparison.Ordinal));
        Assert.Contains(issuing.Details, d => d.Label == "SHA-256" && d.Value.Length == 64);

        // The root CA issues logon certificates (Logowanie-Kartą) but is not in NTAuth: README's warning.
        var root = Ca(document, PkiSamples.RootCaName);
        Assert.Equal("Poza magazynem NTAuth", root.NtAuthText);
        Assert.True(root.HasNtAuthWarning);
        Assert.Contains(PublicKeyTemplates.SmartCardLogon, root.NtAuthWarning, StringComparison.Ordinal);
        Assert.Equal([PkiSamples.RootCaName + " (zaufany urząd główny w AD)"], root.Chain);
    }

    [Fact]
    public async Task TheCaHost_OpensItsComputer_AndTheRemoteActionsNeedTheComputerDialogs()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        document.SelectedAuthority = Ca(document, PkiSamples.IssuingCaName);

        Assert.True(document.OpenComputerCommand.CanExecute(null));
        await document.OpenComputerCommand.ExecuteAsync(null);
        Assert.Equal([h.Sample.IssuingCaComputer], h.B2.Dialogs.PropertiesShown);
        Assert.False(document.CanOpenRemoteActionsAtAll);
        Assert.False(document.OpenRemoteActionsCommand.CanExecute(null));
        Assert.False(document.SelectedAuthority.HasComputerNote);
    }

    [Fact]
    public async Task TheTrustStores_MatchNtAuthToTheEnterpriseCas()
    {
        var (_, document) = await OpenAsync();
        using var __ = document;

        var ntAuth = Assert.Single(document.TrustRows, r => r.Store == PkiCaStore.NtAuth);
        Assert.Equal(PkiSamples.IssuingCaName, ntAuth.EnterpriseCaText);
        Assert.Equal(PkiSamples.IssuingCaName, ntAuth.Subject);
        var root = Assert.Single(document.TrustRows, r => r.Store == PkiCaStore.RootCa);
        Assert.Equal(PkiSamples.RootCaName, root.EnterpriseCaText);
        Assert.Equal(PkiHealthStatus.Ok, root.Status);
        Assert.Contains("NTAuth: 1", document.NtAuthText, StringComparison.Ordinal);
        Assert.Contains("1 z 2", document.NtAuthText, StringComparison.Ordinal);
        Assert.NotNull(document.SelectedCertificate);
    }

    [Fact]
    public async Task TheCrls_KraAndOids_AreListed()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;

        var issuing = document.CrlRows.Where(r => r.CaText == PkiSamples.IssuingCaName).ToList();
        Assert.Contains(issuing, r => r.NumberText == "42" && r.KindText == "Podstawowa lista CRL" && r.Status == PkiHealthStatus.Ok);
        Assert.Contains(issuing, r => r.NumberText == "43" && r.KindText == "Różnicowa lista CRL (do listy podstawowej nr 42)");
        Assert.Contains(document.CrlRows, r => r.CaText == PkiSamples.RootCaName && r.NumberText == "7");
        Assert.True(document.HasCrls);

        var kra = Assert.Single(document.KraRows);
        Assert.Equal(PkiHealthStatus.Ok, kra.Status);
        Assert.True(document.HasKra);

        var linked = Assert.Single(document.OidRows, o => o.HasGroupLink);
        Assert.Equal("Polityka wysokiej pewności", linked.DisplayName);
        Assert.Equal("GG_Administratorzy_PKI", linked.GroupLinkText);
        Assert.Equal("Zasady wystawiania", linked.KindText);
        await document.OpenGroupCommand.ExecuteAsync(linked);
        Assert.Equal([h.Sample.LinkedGroup!], h.B2.Dialogs.PropertiesShown);
    }

    [Fact]
    public async Task TheHealthTab_FollowsTheThresholdsAndTheClock_WhenShown()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        PkiHealthRowViewModel IssuingCa() => Assert.Single(document.HealthRows,
            r => r.Row.Item == PkiHealthItem.CaCertificate && r.CaText == PkiSamples.IssuingCaName);

        // 138 days left: within the 180-day CA threshold.
        Assert.NotEmpty(document.HealthRows);
        Assert.Equal(PkiHealthStatus.Expiring, IssuingCa().Status);
        Assert.Equal(PkiHealthStatus.Expiring, Ca(document, PkiSamples.IssuingCaName).Status);
        Assert.Contains("180", document.ThresholdsText, StringComparison.Ordinal);

        await h.Services.Settings.UpdateAsync(s => s.Pki.CaCertificateExpiryWarningDays = 100, Ct);
        await B2Harness.WaitUntilAsync(() => IssuingCa().Status == PkiHealthStatus.Ok);
        Assert.Equal(PkiHealthStatus.Ok, Ca(document, PkiSamples.IssuingCaName).Status);
        Assert.Contains("100", document.ThresholdsText, StringComparison.Ordinal);

        // The clock moves past the issuing CA's NotAfter: the tab re-evaluates when it is shown (no new read).
        h.B2.Time.Advance(TimeSpan.FromDays(150));
        document.SelectedTab = PkiOverviewTab.Health;
        Assert.Equal(PkiHealthStatus.Expired, IssuingCa().Status);
        Assert.Equal(1, h.Snapshot.Reads);
        Assert.Contains("Wygasła: ", document.HealthSummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(document.HealthRows, r => r.ItemText == "Lokalizacja AIA nr 1");
    }

    [Fact]
    public async Task TheHttpCheck_RunsOnlyOnTheButton_AndItsResultsReachTheHealthRows()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        Assert.Empty(h.HealthProbe.Calls);
        Assert.Equal(h.B2.Localizer[L.Pki.HttpNotChecked], document.HttpStatusText);
        h.HealthProbe.Set(PkiSamples.RootCrlHttpUrl, PkiHttpOutcome.NotFound, 404);
        h.HealthProbe.Set(PkiSamples.OcspUrl, PkiHttpOutcome.Ok, 400);
        document.SelectedTab = PkiOverviewTab.Health;
        Assert.Empty(h.HealthProbe.Calls);

        Assert.True(document.CheckHttpCommand.CanExecute(null));
        await document.CheckHttpCommand.ExecuteAsync(null);

        Assert.Single(h.HealthProbe.Calls);
        // The issuing CA's certificate names where its issuer's (the root's) base CRL is.
        var cdp = Assert.Single(document.HealthRows, r => r.Url == PkiSamples.RootCrlHttpUrl && r.Row.Item == PkiHealthItem.CdpLocation);
        Assert.Equal(PkiSamples.IssuingCaName, cdp.CaText);
        Assert.Equal(PkiHealthStatus.Unavailable, cdp.Status);
        Assert.Equal("Nie znaleziono (404)", cdp.HttpText);
        var ocsp = Assert.Single(document.HttpRows, r => r.Url == PkiSamples.OcspUrl);
        Assert.Equal("OCSP (osiągalność)", ocsp.KindText);
        Assert.Contains("400", ocsp.OutcomeText, StringComparison.Ordinal);
        Assert.Contains("OK", document.HttpStatusText, StringComparison.Ordinal);
        Assert.False(document.IsCheckingHttp);
    }

    [Fact]
    public async Task TheLdapsTab_ProbesOnItsFirstActivation_AndOnTheButton()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        h.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        Assert.Empty(h.TlsProbe.Calls);
        Assert.Equal(h.B2.Localizer[L.Pki.LdapsNotChecked], document.LdapsStatusText);

        document.SelectedTab = PkiOverviewTab.Ldaps;
        await B2Harness.WaitUntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps);

        var row = Assert.Single(document.LdapsRows, r => r.Port == 636 && r.Host == PkiSamples.DcHost);
        Assert.Equal("Przedstawiony certyfikat LDAPS", row.OutcomeText);
        Assert.Equal(PkiSamples.DcHost, row.SubjectText);
        Assert.StartsWith("Gotowy", row.KdcText, StringComparison.Ordinal);
        Assert.False(row.IsProblem);
        var calls = h.TlsProbe.Calls.Count;

        // Switching tabs does not probe again; the button does.
        document.SelectedTab = PkiOverviewTab.Health;
        document.SelectedTab = PkiOverviewTab.Ldaps;
        Assert.Equal(calls, h.TlsProbe.Calls.Count);
        await document.ProbeLdapsCommand.ExecuteAsync(null);
        Assert.Equal(2 * calls, h.TlsProbe.Calls.Count);
        Assert.Contains(document.LdapsRows.Count.ToString(CultureInfo.InvariantCulture), document.LdapsStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALdapsCertificateNearItsEnd_IsAWarning_AtTheEndEntityThreshold()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        h.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        await document.ProbeLdapsCommand.ExecuteAsync(null);
        var row = Assert.Single(document.LdapsRows, r => r.Port == 636 && r.Host == PkiSamples.DcHost);
        Assert.False(row.IsWarning);

        // 88 days left: a warning once the threshold is 90 days.
        await h.Services.Settings.UpdateAsync(s => s.Pki.CertificateExpiryWarningDays = 90, Ct);
        await B2Harness.WaitUntilAsync(() => row.IsWarning);
        Assert.Contains("88", row.ChecksText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAProbe_TheLdapsTabSaysUnavailable_AndStillShowsTheKdcColumn()
    {
        var h = await PkiHarness.CreateAsync();
        var services = new PkiServices(h.B2.Services, new CsvCodec(), h.Snapshot);
        using var document = new PkiOverviewDocumentViewModel(services);
        await document.LoadAsync();

        document.SelectedTab = PkiOverviewTab.Ldaps;
        await B2Harness.WaitUntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps);

        Assert.False(document.IsLdapsAvailable);
        Assert.False(document.IsHttpAvailable);
        Assert.False(document.CheckHttpCommand.CanExecute(null));
        Assert.Equal(h.B2.Localizer[L.Pki.LdapsUnavailable], document.LdapsStatusText);
        var row = Assert.Single(document.LdapsRows, r => r.Host == PkiSamples.DcHost);
        Assert.StartsWith("Gotowy", row.KdcText, StringComparison.Ordinal);
        Assert.Empty(h.TlsProbe.Calls);
    }

    [Fact]
    public async Task Refresh_ReadsAgain_AndTheRequestedCaIsSelected()
    {
        var h = await PkiHarness.CreateAsync();
        using var document = await h.OpenAsync<PkiOverviewDocumentViewModel>(new PkiDocumentRequest(PkiDocumentKind.Overview) { CaDn = h.Sample.RootCa });
        Assert.True(DistinguishedName.AreEqual(h.Sample.RootCa, document.SelectedAuthority!.Dn));

        await document.RefreshAsync();

        Assert.Equal(2, h.Snapshot.Reads);
        Assert.True(DistinguishedName.AreEqual(h.Sample.RootCa, document.SelectedAuthority!.Dn));
        Assert.Equal(PkiSamples.RootCaName, document.SelectionText);
    }

    [Fact]
    public async Task OnTheRealCache_TheDocumentsShareOneRead()
    {
        var h = await PkiHarness.CreateAsync();
        using var cache = new PkiSnapshotService(h.B2.Services, h.TlsProbe, h.HealthProbe);
        var services = new PkiServices(h.B2.Services, new CsvCodec(), cache, h.TlsProbe, h.HealthProbe);
        var factory = new PkiViewModelFactory(services);
        using var overview = (PkiOverviewDocumentViewModel)factory.Create(new PkiDocumentRequest(PkiDocumentKind.Overview));
        using var templates = (PkiTemplatesDocumentViewModel)factory.Create(new PkiDocumentRequest(PkiDocumentKind.Templates));

        await overview.LoadAsync();
        await templates.LoadAsync();

        Assert.Same(cache.Current, overview.Snapshot);
        Assert.Equal(overview.Snapshot!.Templates.Count, templates.Rows.Count);
        Assert.NotNull(cache.Expansion);
        Assert.True(PkiViewModelFactory.Matches(overview, new PkiDocumentRequest(PkiDocumentKind.Overview)));
        Assert.False(PkiViewModelFactory.Matches(overview, new PkiDocumentRequest(PkiDocumentKind.Templates)));
        Assert.True(PkiViewModelFactory.Matches(templates, new PkiDocumentRequest(PkiDocumentKind.Templates)));

        // F5 in the templates refreshes the cache: the overview follows from the cache.
        await templates.RefreshAsync();
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(overview.Snapshot, cache.Current) && !overview.IsLoading);
    }

    [Fact]
    public async Task NotConnected_TheDocumentIsEmpty_AndALanguageSwitchRebuildsTheTexts()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => document.SummaryText.StartsWith("2 enterprise CAs", StringComparison.Ordinal) && !document.IsLoading);
        Assert.Equal("Public Key Services", document.Title);

        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Authorities.Count == 0);
        Assert.Empty(document.HealthRows);
        Assert.Equal(h.B2.Localizer[L.Errors.NotConnected], document.Error);
    }

    [Fact]
    public async Task TheRows_PrintNoDirectoryValue()
    {
        var (_, document) = await OpenAsync();
        using var __ = document;
        object[] rows =
        [
            .. document.Authorities, .. document.TrustRows, .. document.CrlRows, .. document.KraRows, .. document.OidRows, .. document.HealthRows,
        ];

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var text = row.ToString()!;
            Assert.DoesNotContain("Kontoso", text, StringComparison.Ordinal);
            Assert.DoesNotContain("contoso", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CN=", text, StringComparison.Ordinal);
            Assert.DoesNotContain("http", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryDynamicKey_OfTheOverview_ExistsInBothLanguages()
    {
        var h = await PkiHarness.CreateAsync();
        var polish = h.B2.Localizer;
        foreach (var culture in new[] { "pl-PL", "en-US" })
        {
            polish.SetCulture(CultureInfo.GetCultureInfo(culture));
            foreach (var key in L.Pki.AllDynamicKeys())
            {
                Assert.True(polish.Contains(key), $"{culture}: {key}");
            }
        }
    }
}
