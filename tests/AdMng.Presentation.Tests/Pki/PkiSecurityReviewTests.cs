using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// <i>Przegląd bezpieczeństwa AD CS</i> (P1 W6, docs/dev/p-certificate-services.md 2.5, 2.10) on the Polish PKI sample through
/// the real Core readers: every deliberately vulnerable template with its ESC number and severity and none of the look-alikes,
/// the rows grouped by object with their details, the chips and filters, the notes of what was not evaluated, the navigation
/// (no <i>Napraw</i> in P1), the copy and the CSV / HTML exports, cancel, and the language switch.
/// </summary>
public sealed class PkiSecurityReviewTests
{
    private static readonly PkiDocumentRequest Request = new(PkiDocumentKind.SecurityReview);

    private const string AssumesCaEnroll = "Zakłada, że urząd nadaje Użytkownikom uwierzytelnionym prawo „Rejestruj” (zabezpieczenia urzędu – etap P4)";

    private static Task<PkiSecurityReviewDocumentViewModel> OpenAsync(PkiHarness h, PkiDocumentRequest? request = null) =>
        h.OpenAsync<PkiSecurityReviewDocumentViewModel>(request ?? Request);

    [Fact]
    public async Task Opening_ChecksTheSample_EachVulnerableTemplateHasItsEsc_AndNoLookAlikeIsFlagged()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h);

        Assert.Null(review.Error);
        Assert.False(review.IsLoading);
        Assert.Equal("Przegląd bezpieczeństwa AD CS", review.Title);
        Assert.Equal("15 uwag", review.StatusText);
        Assert.StartsWith("Ostatnie sprawdzenie: ", review.LastRunText, StringComparison.Ordinal);
        Assert.EndsWith(" na dc01.contoso.test", review.LastRunText, StringComparison.Ordinal);
        Assert.Contains("ESC6, 7, 8, 10, 11, 16", review.ScopeNote, StringComparison.Ordinal);
        Assert.Empty(review.Notes);
        Assert.Null(review.ListNote);
        Assert.Equal(15, review.TotalCount);

        string Of(string cn) => string.Join(",", review.Rows.Where(r => r.TemplateCn == cn).Select(r => r.Esc + ":" + r.SeverityText));
        Assert.Equal("ESC1:Krytyczna", Of(PublicKeyTemplates.Esc1));
        Assert.Equal("ESC1:Wysoka", Of(PublicKeyTemplates.Esc1NotPublished));
        Assert.Equal("ESC2:Średnia,ESC3:Średnia", Of(PublicKeyTemplates.Esc2));
        Assert.Equal("ESC3:Krytyczna", Of(PublicKeyTemplates.Esc3));
        Assert.Equal("ESC3:Wysoka", Of(PublicKeyTemplates.Esc3Target));
        Assert.Equal("ESC4:Krytyczna", Of(PublicKeyTemplates.Esc4));
        Assert.Equal("ESC9:Wysoka", Of(PublicKeyTemplates.Esc9));
        Assert.Equal("ESC13:Krytyczna", Of(PublicKeyTemplates.Esc13));
        Assert.Equal("ESC15:Średnia", Of(PublicKeyTemplates.Esc15));
        Assert.Equal("ESC17:Wysoka", Of(PublicKeyTemplates.Esc17));
        Assert.All(PublicKeyTemplates.Safe.Concat(PublicKeyTemplates.Defaults), cn =>
        {
            Assert.DoesNotContain(review.Rows, r => r.TemplateCn == cn);
            Assert.DoesNotContain(review.Rows, r => r.ObjectText == cn);
        });
        Assert.Equal(["ESC5"], review.Rows.Where(r => r.Area == PkiFindingArea.PkiObject).Select(r => r.Esc));
        Assert.Equal(3, review.Rows.Count(r => r.Esc == "ESC14"));

        // One chip per severity, most serious first; the counts add up.
        Assert.Equal(PkiFindingText.Severities, review.Chips.Select(c => c.Severity));
        Assert.Equal(["Krytyczne: 4", "Wysokie: 5", "Średnie: 5", "Niskie: 1", "Informacje: 0"], review.Chips.Select(c => c.Text));
        Assert.False(review.Chips[^1].HasFindings);
        Assert.Equal(review.TotalCount, review.Chips.Sum(c => c.Count));

        // Grouped by object: the rows of one object together, the object with the most serious finding first.
        var groups = review.Rows.Select(r => r.GroupText).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(groups.Count, review.Rows.Select((r, i) => (r.GroupText, i)).GroupBy(p => p.GroupText).Count(g => g.Max(p => p.i) - g.Min(p => p.i) == g.Count() - 1));
        var worst = groups.Select(g => review.Rows.Where(r => r.GroupText == g).Max(r => r.Severity)).ToList();
        Assert.Equal(worst.OrderByDescending(s => s), worst);
        Assert.Equal(["Konto: Administrator", "Konto: Administrator"], review.Rows.Where(r => r.Esc == "ESC14" && r.OpenDn is not null).Select(r => r.GroupText));
    }

    [Fact]
    public async Task ARow_HasTheCondition_AttackPath_Principals_Notes_RelatedObjects_AndToolIds()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h);

        var esc1 = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc1);
        Assert.Equal((PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, PkiFindingSeverity.Critical, "Szablon certyfikatu"), (esc1.Code, esc1.Severity, esc1.AreaText));
        Assert.Equal("Szablon certyfikatu: Użytkownik-VPN-Podatny", esc1.GroupText);
        Assert.Equal("Wnioskujący podaje podmiot certyfikatu uwierzytelniania klienta", esc1.Title);
        Assert.Equal("Szablon Użytkownik-VPN-Podatny: podmiot dostarcza wnioskujący, a certyfikat pozwala na uwierzytelnianie klienta; "
            + "rejestrować się może: Domain Users (kont: 8).", esc1.Message);
        Assert.Contains("To ESC1", esc1.Explanation, StringComparison.Ordinal);
        Assert.Contains("Kompiluj na podstawie tych informacji usługi Active Directory", esc1.Recommendation, StringComparison.Ordinal);
        Assert.Equal("Certipy: ESC1 · Locksmith: ESC1 · MDI: –", esc1.IdsText);
        Assert.Equal("Domain Users (8)", esc1.PrincipalsText);
        Assert.Equal(["Domain Users · Szeroka grupa · kont: 8 · Rejestruj"], esc1.PrincipalLines);
        Assert.Equal([AssumesCaEnroll], esc1.NoteLines);
        Assert.Equal(["Urząd certyfikacji: Kontoso Wystawiający CA"], esc1.RelatedLines);
        Assert.True(DistinguishedName.AreEqual(h.Sample.IssuingCa, esc1.CaDn));
        Assert.Null(esc1.OpenDn);

        // The unpublished copy: one level lower, says so, no CA to open.
        var unpublished = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc1NotPublished);
        Assert.Equal("Szablon nie jest wystawiany (do wykorzystania dopiero po opublikowaniu)", unpublished.NoteLines[0]);
        Assert.Null(unpublished.CaDn);
        Assert.False(unpublished.HasRelated);

        // ESC3: the agent template names the targets it reaches; ESC13 the linked issuance policy and group.
        var agent = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc3);
        Assert.Contains("Szablon: Logowanie-W-Imieniu-Podatne", agent.RelatedLines);
        var esc13 = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc13);
        Assert.Contains("GG_Administratorzy_PKI", esc13.Message, StringComparison.Ordinal);
        Assert.Contains("Obiekt OID: Polityka wysokiej pewności", esc13.RelatedLines);
        Assert.Equal("Certipy: ESC15 · Locksmith: ESC15/EKUwu · MDI: –", review.Rows.Single(r => r.Esc == "ESC15").IdsText);

        // ESC4 names the right through its resource; ESC5 the PKI object; ESC14 its three variants in their own words.
        var esc4 = review.Rows.Single(r => r.Esc == "ESC4");
        Assert.Equal("Szablon Szablon-Zapisywalny-Podatny: może go zmienić Użytkownicy uwierzytelnieni (Zapis właściwości istotnej dla bezpieczeństwa).", esc4.Message);
        Assert.Equal(["Użytkownicy uwierzytelnieni · Szeroka grupa · kont: 14 · Zapis właściwości istotnej dla bezpieczeństwa"], esc4.PrincipalLines);
        var esc5 = review.Rows.Single(r => r.Esc == "ESC5");
        Assert.Equal(("Obiekt PKI: NTAuthCertificates", "Otwórz obiekt"), (esc5.GroupText, esc5.OpenObjectText));
        Assert.True(DistinguishedName.AreEqual(h.Sample.NtAuth, esc5.OpenDn));
        Assert.Null(esc5.CaDn);
        var mapping = review.Rows.Single(r => r.Esc == "ESC14" && r.Severity == PkiFindingSeverity.High);
        Assert.Equal("Konto Tier 0 Administrator ma słabe jawne mapowanie certyfikatu: Wystawca i podmiot.", mapping.Message);
        Assert.Equal(("Otwórz konto", "–"), (mapping.OpenObjectText, mapping.PrincipalsText));
        Assert.True(DistinguishedName.AreEqual(h.Sample.WeakTier0Account, mapping.OpenDn));
        var write = review.Rows.Single(r => r.Esc == "ESC14" && r.Severity == PkiFindingSeverity.Medium);
        Assert.StartsWith("GG_IT może zmienić altSecurityIdentities konta Tier 0 Administrator – prawa: ", write.Message, StringComparison.Ordinal);
        Assert.Equal("GG_IT (2)", write.PrincipalsText);
        var aggregated = review.Rows.Single(r => r.Esc == "ESC14" && r.Severity == PkiFindingSeverity.Low);
        Assert.Equal("Konta spoza Tier 0 (1)", aggregated.ObjectText);
        Assert.StartsWith("Konta spoza Tier 0 ze słabym jawnym mapowaniem certyfikatu: 1 (Adres e-mail (RFC822))", aggregated.Message, StringComparison.Ordinal);
        Assert.Null(aggregated.OpenDn);

        // Value-free for diagnostics.
        Assert.DoesNotContain(PublicKeyTemplates.Esc1, esc1.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Domain Users", esc1.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chips_AndFilters_NarrowTheList_KeepTheSelection_AndClearTogether()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h);
        review.SelectedRow = review.Rows.Single(r => r.Esc == "ESC9");

        // A chip filters to its severity and reflects a severity chosen in the box.
        review.Chips.Single(c => c.Severity == PkiFindingSeverity.High).IsSelected = true;
        Assert.Equal(PkiFindingSeverity.High, review.SelectedSeverity?.Value);
        Assert.All(review.Rows, r => Assert.Equal(PkiFindingSeverity.High, r.Severity));
        Assert.Equal(5, review.Rows.Count);
        Assert.True(review.IsFiltered);
        Assert.Equal("Pokazano uwagi: 5 z 15", review.StatusText);
        Assert.Equal("ESC9", review.SelectedRow?.Esc);
        review.SelectedSeverity = review.SeverityOptions.Single(o => o.Value == PkiFindingSeverity.Critical);
        Assert.True(review.Chips.Single(c => c.Severity == PkiFindingSeverity.Critical).IsSelected);
        Assert.False(review.Chips.Single(c => c.Severity == PkiFindingSeverity.High).IsSelected);
        review.Chips.Single(c => c.Severity == PkiFindingSeverity.Critical).IsSelected = false;
        Assert.Null(review.SelectedSeverity?.Value);
        Assert.Equal(15, review.Rows.Count);

        // ESC, area, template, text (case and diacritics ignored).
        Assert.Equal(["Każda technika", "ESC1", "ESC2", "ESC3", "ESC4", "ESC5", "ESC9", "ESC13", "ESC14", "ESC15", "ESC17"], review.EscOptions.Select(o => o.Text));
        review.SelectedEsc = review.EscOptions.Single(o => o.Value == "ESC3");
        Assert.Equal([PublicKeyTemplates.Esc3, PublicKeyTemplates.Esc3Target, PublicKeyTemplates.Esc2], review.Rows.Select(r => r.TemplateCn));
        review.ClearFiltersCommand.Execute(null);
        review.SelectedArea = review.AreaOptions.Single(o => o.Value == PkiFindingArea.Account);
        Assert.All(review.Rows, r => Assert.Equal("ESC14", r.Esc));
        Assert.Equal(3, review.Rows.Count);
        review.ClearFiltersCommand.Execute(null);

        var target = review.TemplateOptions.Single(o => o.Text == PublicKeyTemplates.Esc3Target);
        Assert.Equal("Każdy szablon", review.TemplateOptions[0].Text);
        Assert.DoesNotContain(review.TemplateOptions, o => PublicKeyTemplates.Safe.Contains(o.Text));
        review.SelectedTemplate = target;
        Assert.Equal(["ESC3:" + PublicKeyTemplates.Esc3, "ESC3:" + PublicKeyTemplates.Esc3Target, "ESC3:" + PublicKeyTemplates.Esc2],
            review.Rows.Select(r => r.Esc + ":" + r.TemplateCn));
        review.ClearFiltersCommand.Execute(null);

        review.SearchText = "POLITYKA WYSOKIEJ PEWNOSCI";
        Assert.Equal(["ESC13"], review.Rows.Select(r => r.Esc));
        review.SearchText = "nic takiego";
        Assert.Empty(review.Rows);
        Assert.Null(review.SelectedRow);
        Assert.Equal("Żadna uwaga nie pasuje do filtrów.", review.ListNote);
        Assert.False(review.ExportCsvCommand.CanExecute(null));

        review.ClearFiltersCommand.Execute(null);
        Assert.False(review.IsFiltered);
        Assert.Equal(15, review.Rows.Count);
        Assert.Equal("15 uwag", review.StatusText);

        // Ctrl+F asks the view to focus the search box.
        var focused = 0;
        review.SearchFocusRequested += (_, _) => focused++;
        review.FocusSearch();
        Assert.Equal(1, focused);
    }

    [Fact]
    public async Task ARequestWithAFindingCode_ShowsItsEscOnly_AndShowCodeOfAnUnknownCode_ClearsTheFilters()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h, Request with { FindingCode = PkiFindingCodes.EnrollmentAgentTargetTemplate });

        Assert.Equal("ESC3", review.SelectedEsc?.Value);
        Assert.Equal(3, review.Rows.Count);
        Assert.All(review.Rows, r => Assert.Equal("ESC3", r.Esc));

        review.SearchText = "Agent";
        review.ShowCode(PkiFindingCodes.WeakExplicitMapping);
        Assert.Equal((string.Empty, "ESC14", 3), (review.SearchText, review.SelectedEsc?.Value, review.Rows.Count));
        review.ShowCode("NieMaTakiegoKodu");
        Assert.False(review.IsFiltered);
        Assert.Equal(15, review.Rows.Count);
    }

    [Fact]
    public async Task AnOpenReview_AppliesTheFindingCodeOfALaterRequest_AndARequestWithoutOneKeepsItsFilters()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h);
        IPkiRequestTarget target = review;

        target.Apply(Request with { FindingCode = PkiFindingCodes.EnrollmentAgentTargetTemplate });
        Assert.Equal("ESC3", review.SelectedEsc?.Value);

        target.Apply(Request);
        Assert.Equal("ESC3", review.SelectedEsc?.Value);
        Assert.All(review.Rows, r => Assert.Equal("ESC3", r.Esc));
    }

    [Fact]
    public async Task TheActions_NavigateThroughTheShell_OpenTheObject_AndCopy_ButNeverRepair()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h);
        var requests = new List<PkiDocumentRequest>();
        h.Services.DocumentRequested += (_, r) => requests.Add(r);

        Assert.Null(typeof(PkiSecurityReviewDocumentViewModel).GetProperty("FixCommand"));
        Assert.False(review.OpenTemplateCommand.CanExecute(null));
        Assert.False(review.CopyCommand.CanExecute(null));

        review.SelectedRow = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc1);
        Assert.Equal(review.SelectedRow!.Title, review.SelectionText);
        Assert.False(review.OpenObjectCommand.CanExecute(null));
        review.OpenTemplateCommand.Execute(null);
        review.OpenCaCommand.Execute(null);
        Assert.Equal([PkiDocumentKind.Templates, PkiDocumentKind.Overview], requests.Select(r => r.Kind));
        Assert.Equal(PublicKeyTemplates.Esc1, requests[0].TemplateCn);
        Assert.True(DistinguishedName.AreEqual(h.Sample.IssuingCa, requests[1].CaDn));
        Assert.Equal("PkiDocumentRequest(Templates)", requests[0].ToString());

        review.CopyCommand.Execute(null);
        var text = h.B2.Clipboard.Text!;
        Assert.StartsWith("Krytyczna · ESC1 · Szablon certyfikatu · Wnioskujący podaje podmiot certyfikatu uwierzytelniania klienta", text, StringComparison.Ordinal);
        foreach (var heading in new[] { "Certipy: ESC1", "Czego dotyczy", "Warunek", "Ścieżka ataku", "Co zrobić", "Konta i grupy", "Zastrzeżenia", "Powiązane obiekty" })
        {
            Assert.Contains(heading, text, StringComparison.Ordinal);
        }

        Assert.Contains(h.B2.Notifications.Items, m => m.Message == "Skopiowano uwagę do schowka.");

        // An account opens its property sheet; a PKI object too; neither has a template or a CA.
        review.SelectedRow = review.Rows.First(r => r.Esc == "ESC14" && r.OpenDn is not null);
        Assert.False(review.OpenTemplateCommand.CanExecute(null));
        Assert.False(review.OpenCaCommand.CanExecute(null));
        await review.OpenObjectCommand.ExecuteAsync(null);
        review.SelectedRow = review.Rows.Single(r => r.Esc == "ESC5");
        await review.OpenObjectCommand.ExecuteAsync(null);
        Assert.Equal(2, h.B2.Dialogs.PropertiesShown.Count);
        Assert.True(DistinguishedName.AreEqual(h.Sample.WeakTier0Account, h.B2.Dialogs.PropertiesShown[0]));
        Assert.True(DistinguishedName.AreEqual(h.Sample.NtAuth, h.B2.Dialogs.PropertiesShown[1]));

        // Read-only: nothing was written.
        Assert.DoesNotContain(h.Session.Operations, o => o.IsWrite);
    }

    [Fact]
    public async Task Exports_WriteTheShownFindings_ToTheChosenFile_WithEveryValueEscaped_AndAPrivacyFooter()
    {
        var h = await PkiHarness.CreateAsync();
        const string Name = "<script>alert('x')</script> & \"Żółw\"";
        h.Session.SetValues(h.TemplateDn(PublicKeyTemplates.Esc1), "displayName", Name);
        using var review = await OpenAsync(h);
        Assert.Contains(review.Rows, r => r.ObjectText == Name);
        var csv = Path.Combine(Path.GetTempPath(), "admng-p1w6-" + Guid.NewGuid().ToString("N") + ".csv");
        var html = Path.ChangeExtension(csv, ".html");
        try
        {
            h.B2.Files.NextSavePath = csv;
            await review.ExportCsvCommand.ExecuteAsync(null);
            Assert.StartsWith("Przegląd bezpieczeństwa AD CS – contoso.test_", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            Assert.EndsWith(".csv", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(csv, TestContext.Current.CancellationToken);
            Assert.Equal("Ważność;ESC;Obszar;Obiekt;Uwaga;Warunek;Ścieżka ataku;Co zrobić;Konta i grupy;Zastrzeżenia;Powiązane obiekty;Certipy;Locksmith;MDI;Code", lines[0]);
            Assert.Equal(16, lines.Length);
            Assert.Contains(lines, l => l.Contains("\"<script>alert('x')</script> & \"\"Żółw\"\"\"", StringComparison.Ordinal));

            // The filters decide what is exported.
            review.SelectedEsc = review.EscOptions.Single(o => o.Value == "ESC3");
            h.B2.Files.NextSavePath = html;
            await review.ExportHtmlCommand.ExecuteAsync(null);
            Assert.EndsWith(".html", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var page = await File.ReadAllTextAsync(html, TestContext.Current.CancellationToken);
            Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
            Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'\"", page, StringComparison.Ordinal);
            Assert.Contains("<h1>Przegląd bezpieczeństwa AD CS – contoso.test</h1>", page, StringComparison.Ordinal);
            Assert.Contains("Po filtrowaniu: 3 z 15 uwag.", page, StringComparison.Ordinal);
            Assert.Contains("Raport zawiera nazwy obiektów Twojego katalogu.", page, StringComparison.Ordinal);
            Assert.Contains("Agent-Rejestracji-Podatny", page, StringComparison.Ordinal);
            Assert.DoesNotContain("Bez-Rozszerzenia-SID-Podatny", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", page, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(h.B2.Notifications.Items, m => m.Message.Contains(Path.GetFileName(html), StringComparison.Ordinal));

            review.ClearFiltersCommand.Execute(null);
            await review.ExportHtmlCommand.ExecuteAsync(null);
            page = await File.ReadAllTextAsync(html, TestContext.Current.CancellationToken);
            Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;Żółw&quot;", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(csv);
            File.Delete(html);
        }

        // A cancelled save dialog writes nothing.
        h.B2.Files.NextSavePath = null;
        var notifications = h.B2.Notifications.Items.Count;
        await review.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(notifications, h.B2.Notifications.Items.Count);
    }

    [Fact]
    public async Task TheCheck_ReportsItsStages_AndCancel_KeepsWhatWasShown()
    {
        var h = await PkiHarness.CreateAsync();
        var snapshot = new HookedPkiSnapshotService(h.Snapshot);
        var services = new PkiServices(h.B2.Services, new CsvCodec(), snapshot, h.TlsProbe, h.HealthProbe);
        using var review = new PkiSecurityReviewDocumentViewModel(services);
        var progress = new ConcurrentQueue<string>();
        review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(review.ProgressText) && review.ProgressText.Length > 0)
            {
                progress.Enqueue(review.ProgressText);
            }
        };

        // Cancelled before anything was shown: no rows, the status says so, nothing to export.
        snapshot.BeforeExpansion = () =>
        {
            Assert.True(review.CancelCommand.CanExecute(null));
            review.CancelCommand.Execute(null);
        };
        Assert.Equal("Jeszcze nie sprawdzono.", review.StatusText);
        await review.LoadAsync();
        Assert.Empty(review.Rows);
        Assert.False(review.HasResult);
        Assert.Null(review.Error);
        Assert.Equal("Sprawdzanie anulowano.", review.StatusText);
        Assert.False(review.ExportCsvCommand.CanExecute(null));
        Assert.False(review.CancelCommand.CanExecute(null));

        snapshot.BeforeExpansion = null;
        await review.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(15, review.Rows.Count);
        Assert.Contains(progress, p => p.StartsWith("Odczyt obiektów PKI (", StringComparison.Ordinal));
        Assert.Contains(progress, p => p.StartsWith("Odczyt kont (", StringComparison.Ordinal));
        Assert.Contains("Odczyt uprawnień obiektów PKI…", progress);
        Assert.Equal(string.Empty, review.ProgressText);
        Assert.True(snapshot.Refreshes > 0);

        // Cancelled while results are shown: they stay (with the selection).
        review.SelectedRow = review.Rows.Single(r => r.Esc == "ESC13");
        var rows = review.Rows;
        snapshot.BeforeExpansion = () => review.CancelCommand.Execute(null);
        await review.RefreshCommand.ExecuteAsync(null);
        Assert.Same(rows, review.Rows);
        Assert.Equal("ESC13", review.SelectedRow?.Esc);
        Assert.Equal("Sprawdzanie anulowano.", review.StatusText);
        Assert.True(review.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATemplateWithoutASecurityDescriptor_IsNamedInTheNotes_AndNotReportedSafe()
    {
        var h = await PkiHarness.CreateAsync();
        h.Session.SetValues(h.TemplateDn(PublicKeyTemplates.Esc1), "nTSecurityDescriptor");
        using var review = await OpenAsync(h);

        Assert.Null(review.Error);
        Assert.DoesNotContain(review.Rows, r => r.TemplateCn == PublicKeyTemplates.Esc1);
        var note = Assert.Single(review.Notes);
        Assert.Equal("Nie można odczytać deskryptora zabezpieczeń szablonu – jego prawa nie zostały ocenione: " + PublicKeyTemplates.Esc1, note);
    }

    [Fact]
    public async Task AForestWithoutAdCs_SaysSo_InsteadOfListingFindings()
    {
        var b2 = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var snapshot = new DirectPkiSnapshotService(b2.Services, null, null);
        using var review = new PkiSecurityReviewDocumentViewModel(new PkiServices(b2.Services, new CsvCodec(), snapshot));
        await review.LoadAsync();

        Assert.Null(review.Error);
        Assert.Empty(review.Rows);
        Assert.Contains("Nie znaleziono kontenera Usługi kluczy publicznych – w tym lesie nie ma AD CS", review.Notes);
        Assert.Equal("W tym lesie nie ma AD CS – nie ma czego sprawdzać.", review.ListNote);
        Assert.False(review.ExportHtmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADeniedPkiBase_IsShownAsNotEvaluated_OnlyTheMappingsOfTheAccountPassAreReported()
    {
        var b2 = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        b2.Builder.AddPublicKeySample(b2.Company);
        var denied = PkiSnapshot.Empty(PkiSamples.ConfigurationDn, PkiSamples.Now,
            [new PkiReadNotice(PkiReadNoticeCode.BaseAccessDenied, PkiSamples.PublicKeyServicesDn, null)]);
        var snapshot = new HookedPkiSnapshotService(new DirectPkiSnapshotService(b2.Services, null, null)) { Snapshot = denied };
        using var review = new PkiSecurityReviewDocumentViewModel(new PkiServices(b2.Services, new CsvCodec(), snapshot));
        await review.LoadAsync();

        Assert.Null(review.Error);
        Assert.Equal(["ESC14", "ESC14"], review.Rows.Select(r => r.Esc));
        Assert.Equal(
            [
                "Odmowa dostępu do kontenera Usługi kluczy publicznych – PKI nie zostało ocenione",
                "Nie oceniono: reguły rejestracji (ESC1, 2, 3, 9, 13, 15, 17); uprawnienia obiektów PKI (ESC5 i zapis altSecurityIdentities w ESC14).",
            ],
            review.Notes);

        // Without any finding the list says the PKI was not evaluated – never that it is safe.
        var bare = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var bareSnapshot = new HookedPkiSnapshotService(new DirectPkiSnapshotService(bare.Services, null, null)) { Snapshot = denied };
        using var empty = new PkiSecurityReviewDocumentViewModel(new PkiServices(bare.Services, new CsvCodec(), bareSnapshot));
        await empty.LoadAsync();
        Assert.Empty(empty.Rows);
        Assert.Equal("Nie można ocenić PKI – zobacz informacje powyżej.", empty.ListNote);
        Assert.True(empty.HasNotes);
    }

    [Fact]
    public async Task ALanguageSwitch_RebuildsTheTexts_WithoutReadingAgain_AndKeepsTheSelection()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = await OpenAsync(h);
        review.SelectedEsc = review.EscOptions.Single(o => o.Value == "ESC1");
        review.SelectedRow = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc1);
        var reads = h.Snapshot.Reads;

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal(reads, h.Snapshot.Reads);
        Assert.Equal("AD CS security review", review.Title);
        Assert.Equal("Any technique", review.EscOptions[0].Text);
        Assert.Equal("ESC1", review.SelectedEsc?.Value);
        Assert.Equal(2, review.Rows.Count);
        Assert.Equal((PublicKeyTemplates.Esc1, "Critical", "Certificate template"), (review.SelectedRow?.TemplateCn, review.SelectedRow?.SeverityText, review.SelectedRow?.AreaText));
        Assert.Equal("Findings shown: 2 of 15", review.StatusText);
        Assert.Contains("Critical: 4", review.Chips.Select(c => c.Text));
    }

    [Fact]
    public async Task TheFactory_CreatesAndMatchesOneTabPerKind()
    {
        var h = await PkiHarness.CreateAsync();
        using var review = h.Factory.Create(Request);
        using var readiness = h.Factory.Create(new PkiDocumentRequest(PkiDocumentKind.Readiness));

        Assert.IsType<PkiSecurityReviewDocumentViewModel>(review);
        Assert.IsType<PkiReadinessDocumentViewModel>(readiness);
        Assert.True(PkiViewModelFactory.Matches(review, Request with { FindingCode = PkiFindingCodes.TemplateWriteable }));
        Assert.False(PkiViewModelFactory.Matches(review, new PkiDocumentRequest(PkiDocumentKind.Readiness)));
        Assert.True(PkiViewModelFactory.Matches(readiness, new PkiDocumentRequest(PkiDocumentKind.Readiness)));
        Assert.False(PkiViewModelFactory.Matches(readiness, Request));
        Assert.False(review.HasUnsavedChanges);
        Assert.True(await review.CanCloseAsync());
    }
}

/// <summary>
/// The test's <see cref="DirectPkiSnapshotService"/> with hooks: <see cref="BeforeExpansion"/> runs before the expansion (to
/// cancel a check in the middle), <see cref="Snapshot"/> replaces the read snapshot, <see cref="ExpansionFailure"/> makes the
/// expansion fail, and <see cref="Refreshes"/> counts the calls asked to read again.
/// </summary>
internal sealed class HookedPkiSnapshotService(DirectPkiSnapshotService inner) : IPkiSnapshotService
{
    public Action? BeforeExpansion { get; set; }

    public PkiSnapshot? Snapshot { get; set; }

    public Exception? ExpansionFailure { get; set; }

    /// <summary>The DC checks the LDAPS button gets instead of probing (null: the inner service probes).</summary>
    public IReadOnlyList<DcTlsCheck>? DcChecks { get; set; }

    public int Refreshes { get; private set; }

    public PkiSnapshot? Current => Snapshot ?? inner.Current;

    public DateTimeOffset? ReadAt => Current?.ReadAt;

    public bool IsBuilding => inner.IsBuilding;

    public PkiEnrollmentExpansion? Expansion => inner.Expansion;

    public IReadOnlyList<DcTlsCheck>? DcCertificates => DcChecks is null ? inner.DcCertificates : null;

    public IReadOnlyList<PkiHttpCheckResult>? HttpResults => inner.HttpResults;

    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public async Task<PkiSnapshot> GetAsync(bool refresh, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken)
    {
        Refreshes += refresh ? 1 : 0;
        var read = await inner.GetAsync(refresh, progress, cancellationToken);
        return Snapshot ?? read;
    }

    public async Task<PkiEnrollmentExpansion> GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>? progress, CancellationToken cancellationToken)
    {
        BeforeExpansion?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (ExpansionFailure is { } failure)
        {
            throw failure;
        }

        return await inner.GetExpansionAsync(refresh, progress, cancellationToken);
    }

    public Task<IReadOnlyList<PkiDomainController>> GetDomainControllersAsync(bool refresh, CancellationToken cancellationToken) =>
        inner.GetDomainControllersAsync(refresh, cancellationToken);

    public Task<IReadOnlyList<DcTlsCheck>> GetDcCertificatesAsync(bool refresh, CancellationToken cancellationToken) =>
        DcChecks is { } checks ? Task.FromResult(checks) : inner.GetDcCertificatesAsync(refresh, cancellationToken);

    public Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpAsync(IProgress<int>? progress, CancellationToken cancellationToken) =>
        inner.CheckHttpAsync(progress, cancellationToken);

    public void Invalidate() => inner.Invalidate();
}
