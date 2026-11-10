using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// <i>Gotowość na silne mapowanie i NTAuth</i> (P1 W6, docs/dev/p-certificate-services.md 2.5, 2.10) on the Polish PKI sample:
/// the six areas with their status rows (weak mappings, authentication templates without the SID extension, CAs outside
/// NTAuth, DC LDAPS – probed only on its button –, the KDC indicator and the KDC events row of P5), the thresholds, the
/// navigation, copy and export, a forest without AD CS, a failed account pass and the language switch.
/// </summary>
public sealed class PkiReadinessTests
{
    private static readonly PkiDocumentRequest Request = new(PkiDocumentKind.Readiness);

    private static Task<PkiReadinessDocumentViewModel> OpenAsync(PkiHarness h) => h.OpenAsync<PkiReadinessDocumentViewModel>(Request);

    private static PkiReadinessGroupViewModel Area(PkiReadinessDocumentViewModel document, PkiReadinessArea area) =>
        document.Groups.Single(g => g.Area == area);

    private static string Rows(PkiReadinessGroupViewModel group) =>
        string.Join(Environment.NewLine, group.AllRows.Select(r => r.Status + " | " + r.Title + " | " + r.Detail));

    [Fact]
    public async Task Opening_ShowsEveryArea_WithTheRowsOfTheSample_AndProbesNothing()
    {
        var h = await PkiHarness.CreateAsync();
        using var readiness = await OpenAsync(h);

        Assert.Null(readiness.Error);
        Assert.Equal("Gotowość na silne mapowanie i NTAuth", readiness.Title);
        Assert.Equal(Enum.GetValues<PkiReadinessArea>(), readiness.Groups.Select(g => g.Area));
        Assert.Equal(
            [
                "Jawne mapowania certyfikatów (altSecurityIdentities)", "Szablony uwierzytelniania a rozszerzenie SID",
                "Urzędy przedsiębiorstwa a magazyn NTAuth", "Certyfikaty LDAPS kontrolerów domeny",
                "Certyfikaty KDC (opublikowane na obiektach kontrolerów domeny)", "Zdarzenia KDC",
            ],
            readiness.Groups.Select(g => g.Title));
        Assert.Equal("OK: 4 · Ostrzeżenia: 3 · Problemy: 3 · Nie sprawdzono: 2", readiness.SummaryText);
        Assert.Equal(readiness.SummaryText, readiness.StatusText);
        Assert.StartsWith("Odczytano: ", readiness.LastRunText, StringComparison.Ordinal);
        Assert.EndsWith(" na dc01.contoso.test", readiness.LastRunText, StringComparison.Ordinal);

        // Decision 1: no TLS probe without the operator's button.
        Assert.Empty(h.TlsProbe.Calls);

        // Weak mappings: the Tier 0 account first (a problem), the other weak one a warning, the strong ones counted; the
        // Kerberos mapping is no certificate mapping.
        var mappings = Area(readiness, PkiReadinessArea.Mappings);
        Assert.Equal(PkiReadinessStatus.Problem, mappings.Status);
        Assert.Equal(
            "Problem | Administrator | Słabe mapowania: Wystawca i podmiot. Konto Tier 0: kto uzyska certyfikat o pasujących nazwach, zaloguje się jako ono (ESC14)."
            + Environment.NewLine + "Warning | Łukasz Wiśniewski | Słabe mapowania: Adres e-mail (RFC822)."
            + Environment.NewLine + "Ok | Konta tylko z silnymi mapowaniami: 1 | Gotowe na pełne wymuszanie.",
            Rows(mappings));
        Assert.StartsWith("Co zrobić: zastąp je mapowaniem wystawcy i numeru seryjnego", mappings.AllRows[0].RemedyText, StringComparison.Ordinal);
        Assert.DoesNotContain(mappings.AllRows, r => r.Title.Contains("Lewandowski", StringComparison.Ordinal));
        Assert.Equal("Otwórz konto", mappings.AllRows[0].OpenText);

        // Authentication templates: ESC9's template turns the SID extension off, the enrollee-supplied ones carry no SID.
        var templates = Area(readiness, PkiReadinessArea.AuthenticationTemplates);
        Assert.Equal(PkiReadinessStatus.Problem, templates.Status);
        Assert.Equal(
            [
                "Problem:" + PublicKeyTemplates.Esc9, "Warning:" + PublicKeyTemplates.Esc1Safe, "Warning:" + PublicKeyTemplates.Esc1,
                "Ok:Szablony z rozszerzeniem SID: 14",
            ],
            templates.AllRows.Select(r => r.Status + ":" + r.Title));
        Assert.DoesNotContain(templates.AllRows, r => r.Title == PublicKeyTemplates.Esc1NotPublished);

        // NTAuth: the root CA publishes the smart-card logon template but is not in NTAuth.
        var ntAuth = Area(readiness, PkiReadinessArea.NtAuth);
        Assert.Equal(
            "Problem | Kontoso Główny CA | Nie ma go w magazynie NTAuth, a wystawia szablony logowania: Logowanie kartą inteligentną. Ich certyfikatów nie można użyć do logowania."
            + Environment.NewLine + "Ok | Kontoso Wystawiający CA | W magazynie NTAuth.",
            Rows(ntAuth));
        Assert.Contains("certutil -dspublish -f <plik> NTAuthCA", ntAuth.AllRows[0].RemedyText, StringComparison.Ordinal);

        var ldaps = Area(readiness, PkiReadinessArea.DomainControllerLdaps);
        Assert.Equal("NotChecked | Nie sprawdzono | Naciśnij „Sprawdź certyfikaty LDAPS” – raport łączy się z kontrolerami domeny tylko po naciśnięciu tego przycisku.", Rows(ldaps));
        var kdc = Area(readiness, PkiReadinessArea.KdcCertificates);
        var dc = Assert.Single(kdc.AllRows);
        Assert.Equal((PkiReadinessStatus.Ok, "dc01.contoso.test"), (dc.Status, dc.Title));
        Assert.StartsWith("Gotowy (z certyfikatów opublikowanych w AD). Certyfikat od: Kontoso Wystawiający CA, ważny do ", dc.Detail, StringComparison.Ordinal);
        var events = Assert.Single(Area(readiness, PkiReadinessArea.KdcEvents).AllRows);
        Assert.Equal(("Zdarzenia 39, 40, 41, 45 i 21 kontrolerów domeny", PkiReadinessStatus.NotChecked), (events.Title, events.Status));
        Assert.Contains("etapie P5", events.Detail, StringComparison.Ordinal);

        // Read-only: nothing was written; value-free for diagnostics.
        Assert.DoesNotContain(h.Session.Operations, o => o.IsWrite);
        Assert.Equal("NtAuth: Problem (2 rows)", ntAuth.ToString());
        Assert.DoesNotContain("Administrator", mappings.AllRows[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRows_Navigate_ToTheAccount_TheTemplate_AndTheCa()
    {
        var h = await PkiHarness.CreateAsync();
        using var readiness = await OpenAsync(h);
        var requests = new List<PkiDocumentRequest>();
        h.Services.DocumentRequested += (_, r) => requests.Add(r);

        var administrator = Area(readiness, PkiReadinessArea.Mappings).AllRows[0];
        Assert.True(administrator.HasOpen);
        administrator.OpenCommand!.Execute(null);
        await B2Harness.WaitUntilAsync(() => h.B2.Dialogs.PropertiesShown.Count == 1);
        Assert.True(DistinguishedName.AreEqual(h.Sample.WeakTier0Account, h.B2.Dialogs.PropertiesShown[0]));

        var template = Area(readiness, PkiReadinessArea.AuthenticationTemplates).AllRows[0];
        Assert.Equal("Otwórz szablon", template.OpenText);
        template.OpenCommand!.Execute(null);
        var ca = Area(readiness, PkiReadinessArea.NtAuth).AllRows[0];
        Assert.Equal("Otwórz urząd", ca.OpenText);
        ca.OpenCommand!.Execute(null);
        Assert.Equal([PkiDocumentKind.Templates, PkiDocumentKind.Overview], requests.Select(r => r.Kind));
        Assert.Equal(PublicKeyTemplates.Esc9, requests[0].TemplateCn);
        Assert.True(DistinguishedName.AreEqual(h.Sample.RootCa, requests[1].CaDn));
        Assert.False(Area(readiness, PkiReadinessArea.KdcEvents).AllRows[0].HasOpen);
    }

    [Fact]
    public async Task TheLdapsButton_ProbesTheDomainControllers_AndTheRowFollowsTheExpiryThreshold()
    {
        var h = await PkiHarness.CreateAsync();
        h.TlsProbe.Set("dc01.contoso.test", 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        using var readiness = await OpenAsync(h);

        Assert.True(readiness.CanProbe);
        Assert.True(readiness.CheckLdapsCommand.CanExecute(null));
        await readiness.CheckLdapsCommand.ExecuteAsync(null);

        Assert.Equal([("dc01.contoso.test", 636)], h.TlsProbe.Calls.Select(c => (c.Host, c.Port)));
        Assert.False(readiness.IsCheckingLdaps);
        var row = Assert.Single(Area(readiness, PkiReadinessArea.DomainControllerLdaps).AllRows);
        Assert.Equal((PkiReadinessStatus.Ok, "dc01.contoso.test (port 636)"), (row.Status, row.Title));
        Assert.StartsWith("Wystawca: Kontoso Wystawiający CA, ważny do ", row.Detail, StringComparison.Ordinal);
        Assert.EndsWith("(pozostało dni: 88).", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasRemedy);

        // A higher threshold turns it into a warning when shown – nothing is probed again.
        await h.B2.Settings.UpdateAsync(s => s.Pki.CertificateExpiryWarningDays = 100, TestContext.Current.CancellationToken);
        row = Assert.Single(Area(readiness, PkiReadinessArea.DomainControllerLdaps).AllRows);
        Assert.Equal((PkiReadinessStatus.Warning, "Wygasa za 88 dni."), (row.Status, row.Detail));
        Assert.Single(h.TlsProbe.Calls);
        Assert.Equal(PkiReadinessStatus.Ok, Area(readiness, PkiReadinessArea.KdcCertificates).Status);

        // A second press probes again; a reload shows the window's cached checks without probing.
        await readiness.CheckLdapsCommand.ExecuteAsync(null);
        Assert.Equal(2, h.TlsProbe.Calls.Count);
        await readiness.LoadAsync();
        Assert.Equal(2, h.TlsProbe.Calls.Count);
        Assert.Equal(PkiReadinessStatus.Warning, Area(readiness, PkiReadinessArea.DomainControllerLdaps).Status);
    }

    [Fact]
    public async Task ProbeFailures_AndWrongCertificates_AreProblems_WithWhatToDo()
    {
        var h = await PkiHarness.CreateAsync();
        using var readiness = await OpenAsync(h);

        h.TlsProbe.Set("dc01.contoso.test", 636, TlsProbeOutcome.HandshakeFailed);
        await readiness.CheckLdapsCommand.ExecuteAsync(null);
        var row = Assert.Single(Area(readiness, PkiReadinessArea.DomainControllerLdaps).AllRows);
        Assert.Equal((PkiReadinessStatus.Problem, "DC bez certyfikatu LDAPS (uzgadnianie TLS nie powiodło się)"), (row.Status, row.Detail));
        Assert.StartsWith("Co zrobić: zarejestruj lub odnów certyfikat kontrolera domeny", row.RemedyText, StringComparison.Ordinal);

        h.TlsProbe.Set("dc01.contoso.test", 636, TlsProbeOutcome.Timeout);
        await readiness.CheckLdapsCommand.ExecuteAsync(null);
        row = Assert.Single(Area(readiness, PkiReadinessArea.DomainControllerLdaps).AllRows);
        Assert.Equal((PkiReadinessStatus.Warning, "Brak odpowiedzi w ciągu 5 s"), (row.Status, row.Detail));

        using var other = PkiSamples.ServerCertificate("inny.contoso.test");
        h.TlsProbe.Set("dc01.contoso.test", 636, TlsProbeOutcome.Ok, other.RawData, PkiSamples.IssuingCaDer);
        await readiness.CheckLdapsCommand.ExecuteAsync(null);
        row = Assert.Single(Area(readiness, PkiReadinessArea.DomainControllerLdaps).AllRows);
        Assert.Equal(PkiReadinessStatus.Problem, row.Status);
        Assert.Equal("Jego nazwy nie obejmują nazwy hosta kontrolera domeny. Jego nazwy nie obejmują nazwy domeny (LDAPS do nazwy domeny się nie powiedzie).", row.Detail);

        using var selfSigned = PkiSamples.ServerCertificate("dc01.contoso.test", selfSigned: true);
        h.TlsProbe.Set("dc01.contoso.test", 636, TlsProbeOutcome.Ok, selfSigned.RawData);
        await readiness.CheckLdapsCommand.ExecuteAsync(null);
        row = Assert.Single(Area(readiness, PkiReadinessArea.DomainControllerLdaps).AllRows);
        Assert.Equal(PkiReadinessStatus.Warning, row.Status);
        Assert.StartsWith("Nie prowadzi do urzędu głównego opublikowanego w Active Directory.", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATlsProbe_TheLdapsAreaSaysSo_AndTheButtonWaits()
    {
        var h = await PkiHarness.CreateAsync();
        using var readiness = new PkiReadinessDocumentViewModel(new PkiServices(h.B2.Services, new CsvCodec(), h.Snapshot));
        await readiness.LoadAsync();

        Assert.False(readiness.CanProbe);
        Assert.False(readiness.CheckLdapsCommand.CanExecute(null));
        Assert.Equal("NotChecked | Sprawdzanie LDAPS niedostępne | To okno nie ma sondy TLS.", Rows(Area(readiness, PkiReadinessArea.DomainControllerLdaps)));
    }

    [Fact]
    public async Task TheKdcIndicator_WarnsOfAV1Certificate_AndCannotJudgeADcWithoutOne()
    {
        var h = await PkiHarness.CreateAsync();
        h.Session.SetValues(h.Builder.DomainControllerDn, "userCertificate", PkiSamples.DcLdapsDer());
        using var readiness = await OpenAsync(h);

        var row = Assert.Single(Area(readiness, PkiReadinessArea.KdcCertificates).AllRows);
        Assert.Equal(PkiReadinessStatus.Warning, row.Status);
        Assert.StartsWith("Brak EKU uwierzytelniania KDC. Certyfikat z szablonu Kontroler domeny w wersji 1: PKINIT działa", row.Detail, StringComparison.Ordinal);
        Assert.StartsWith("Co zrobić: zarejestruj kontroler domeny w szablonie Uwierzytelnianie Kerberos", row.RemedyText, StringComparison.Ordinal);

        h.Session.SetValues(h.Builder.DomainControllerDn, "userCertificate");
        await readiness.RefreshCommand.ExecuteAsync(null);
        row = Assert.Single(Area(readiness, PkiReadinessArea.KdcCertificates).AllRows);
        Assert.Equal((PkiReadinessStatus.NotChecked, "Nieznany – brak certyfikatu opublikowanego w AD"), (row.Status, row.Detail));
    }

    [Fact]
    public async Task AFailedAccountPass_LeavesTheMappingsUnchecked_AndTheOtherAreasShown()
    {
        var h = await PkiHarness.CreateAsync();
        var snapshot = new HookedPkiSnapshotService(h.Snapshot)
        {
            ExpansionFailure = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "Server down."),
        };
        using var readiness = new PkiReadinessDocumentViewModel(new PkiServices(h.B2.Services, new CsvCodec(), snapshot, h.TlsProbe));
        await readiness.LoadAsync();

        Assert.Null(readiness.Error);
        var row = Assert.Single(Area(readiness, PkiReadinessArea.Mappings).AllRows);
        Assert.Equal(PkiReadinessStatus.NotChecked, row.Status);
        Assert.StartsWith("Nie można odczytać: ", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Server down.", row.Detail, StringComparison.Ordinal);
        Assert.Equal(PkiReadinessStatus.Problem, Area(readiness, PkiReadinessArea.NtAuth).Status);
    }

    [Fact]
    public async Task AForestWithoutAdCs_SaysSo_InsteadOfJudgingTemplatesAndCas()
    {
        var b2 = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var snapshot = new DirectPkiSnapshotService(b2.Services, null, null);
        using var readiness = new PkiReadinessDocumentViewModel(new PkiServices(b2.Services, new CsvCodec(), snapshot));
        await readiness.LoadAsync();

        Assert.Null(readiness.Error);
        const string NoPki = "W tym lesie nie ma AD CS albo nie można odczytać kontenera Usługi kluczy publicznych.";
        Assert.Equal("NotChecked | " + NoPki + " | ", Rows(Area(readiness, PkiReadinessArea.AuthenticationTemplates)));
        Assert.Equal("NotChecked | " + NoPki + " | ", Rows(Area(readiness, PkiReadinessArea.NtAuth)));
        Assert.Equal("Ok | Brak jawnych mapowań certyfikatów | Żadne konto nie ma mapowania certyfikatu w altSecurityIdentities.",
            Rows(Area(readiness, PkiReadinessArea.Mappings)));
    }

    [Fact]
    public async Task ALongArea_ShowsTheFirstHundredRows_ButCopyAndExportHoldThemAll()
    {
        var h = await PkiHarness.CreateAsync();
        for (var i = 0; i < 105; i++)
        {
            h.Builder.AddUser("Użytkownik" + i.ToString("D3", CultureInfo.InvariantCulture), "Mapowany", h.Company.EmployeesOu,
                new Dictionary<string, object?> { ["altSecurityIdentities"] = "X509:<RFC822>mapowany" + i.ToString(CultureInfo.InvariantCulture) + "@contoso.test" });
        }

        using var readiness = await OpenAsync(h);
        var mappings = Area(readiness, PkiReadinessArea.Mappings);
        Assert.Equal(108, mappings.AllRows.Count);
        Assert.Equal(PkiReadinessDocumentViewModel.RowsShown + 1, mappings.Rows.Count);
        Assert.Equal(("… i jeszcze 8", "Skopiuj lub wyeksportuj raport, aby zobaczyć wszystkie wiersze."), (mappings.Rows[^1].Title, mappings.Rows[^1].Detail));

        readiness.CopyCommand.Execute(null);
        var text = h.B2.Clipboard.Text!;
        Assert.StartsWith("Gotowość na silne mapowanie i NTAuth" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("Jawne mapowania certyfikatów (altSecurityIdentities) – Problem", text, StringComparison.Ordinal);
        Assert.Contains("[Ostrzeżenie] Użytkownik104 Mapowany: Słabe mapowania: Adres e-mail (RFC822).", text, StringComparison.Ordinal);
        Assert.Contains(h.B2.Notifications.Items, m => m.Message == "Skopiowano raport do schowka.");

        var csv = Path.Combine(Path.GetTempPath(), "admng-p1w6-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            h.B2.Files.NextSavePath = csv;
            await readiness.ExportCsvCommand.ExecuteAsync(null);
            Assert.StartsWith("Gotowość na silne mapowanie i NTAuth – contoso.test_", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(csv, TestContext.Current.CancellationToken);
            Assert.Equal("Obszar;Stan;Element;Wynik;Co zrobić", lines[0]);
            Assert.Equal(readiness.Groups.Sum(g => g.AllRows.Count) + 1, lines.Length);
            Assert.Contains(lines, l => l.Contains("Użytkownik104 Mapowany", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task ALanguageSwitch_RebuildsTheTexts_WithoutReadingAgain()
    {
        var h = await PkiHarness.CreateAsync();
        using var readiness = await OpenAsync(h);
        var reads = h.Snapshot.Reads;

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal(reads, h.Snapshot.Reads);
        Assert.Equal("Strong mapping and NTAuth readiness", readiness.Title);
        Assert.Equal("OK: 4 · Warnings: 3 · Problems: 3 · Not checked: 2", readiness.SummaryText);
        Assert.Equal("Enterprise CAs and the NTAuth store", Area(readiness, PkiReadinessArea.NtAuth).Title);
        Assert.Equal("Problem", Area(readiness, PkiReadinessArea.NtAuth).StatusText);
        Assert.Equal("Not checked", Area(readiness, PkiReadinessArea.KdcEvents).StatusText);
    }

    [Fact]
    public void EveryStatus_HasItsTextInBothLanguages()
    {
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("pl-PL") })
        {
            var localizer = new ResxLocalizer(culture);
            Assert.All(PkiReadinessDocumentViewModel.AllKeys(), key => Assert.DoesNotContain("[", localizer[key], StringComparison.Ordinal));
        }

        Assert.Equal(Enum.GetValues<PkiReadinessStatus>().Length, PkiReadinessDocumentViewModel.AllKeys().Count());
        Assert.Equal([3, 2, 1, 0], new[] { PkiReadinessStatus.Problem, PkiReadinessStatus.Warning, PkiReadinessStatus.NotChecked, PkiReadinessStatus.Ok }
            .Select(PkiReadinessDocumentViewModel.Rank));
    }
}
