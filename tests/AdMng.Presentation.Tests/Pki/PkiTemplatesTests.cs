using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.Pki;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// <i>Szablony certyfikatów</i> on <c>AddPublicKeySample</c> (docs/dev/p-certificate-services.md 2.5, 2.10): the README columns,
/// flags as sentences, periods, issuing CAs, unpublished templates, the trustee level and then the expanded counts, navigation
/// to accounts and groups, who can change a template, the compare pane and the exports.
/// </summary>
public sealed class PkiTemplatesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PkiHarness H, PkiTemplatesDocumentViewModel Document)> OpenAsync(PkiDocumentRequest? request = null)
    {
        var h = await PkiHarness.CreateAsync();
        var document = await h.OpenAsync<PkiTemplatesDocumentViewModel>(request ?? new PkiDocumentRequest(PkiDocumentKind.Templates));
        return (h, document);
    }

    private static PkiTemplateRowViewModel Row(PkiTemplatesDocumentViewModel document, string cn) => Assert.Single(document.Rows, r => r.Cn == cn);

    [Fact]
    public async Task EveryTemplate_IsListed_WithTheReadmeColumns()
    {
        var (_, document) = await OpenAsync();
        using var __ = document;

        Assert.Null(document.Error);
        Assert.Equal(PublicKeyTemplates.All.Order(StringComparer.Ordinal), document.Rows.Select(r => r.Cn).Order(StringComparer.Ordinal));
        Assert.Equal($"Szablony: {PublicKeyTemplates.All.Count}, niewystawiane przez żaden urząd: 1", document.SummaryText);
        Assert.Equal("Szablony certyfikatów", document.Title);

        var esc1 = Row(document, PublicKeyTemplates.Esc1);
        Assert.Equal("2", esc1.SchemaText);
        Assert.Equal("100.3", esc1.VersionText);
        Assert.Equal("Client Authentication", esc1.PurposeText);
        Assert.Equal("1 rok", esc1.ValidityText);
        Assert.Equal("6 tygodni", esc1.RenewalText);
        Assert.StartsWith("2048 bitów", esc1.KeyText, StringComparison.Ordinal);
        Assert.Equal("Dostarcz w żądaniu", esc1.SubjectText);
        Assert.Equal("Nie", esc1.ApprovalText);
        Assert.Equal("0", esc1.SignaturesText);
        Assert.Equal(PkiSamples.IssuingCaName, esc1.CasText);
        Assert.Equal("Tak", Row(document, PublicKeyTemplates.Esc1Safe).ApprovalText);
        Assert.Equal("Kompiluj na podstawie informacji usługi Active Directory", Row(document, PublicKeyTemplates.Esc9).SubjectText);
        Assert.Equal("1", Row(document, PublicKeyTemplates.Esc15).SchemaText);
        Assert.Equal(PkiDisplay.Purpose(PkiSamples.AnyPurpose), Row(document, PublicKeyTemplates.Esc2).PurposeText);
        Assert.Equal("Polityka wysokiej pewności", Row(document, PublicKeyTemplates.Esc13).IssuancePoliciesText);

        var unpublished = Row(document, PublicKeyTemplates.Esc1NotPublished);
        Assert.False(unpublished.IsPublished);
        Assert.Equal("niewystawiany przez żaden urząd", unpublished.CasText);
        Assert.Equal(PkiSamples.RootCaName, Row(document, PublicKeyTemplates.SmartCardLogon).CasText);
    }

    [Fact]
    public async Task TheFlags_AreSentences_WithTheTechnicalNameInTheTooltip()
    {
        var (_, document) = await OpenAsync();
        using var __ = document;

        document.SelectedRow = Row(document, PublicKeyTemplates.Esc1);
        var name = Assert.Single(document.FlagGroups, g => g.Header.StartsWith("Nazwa podmiotu", StringComparison.Ordinal));
        var item = Assert.Single(name.Items);
        Assert.Equal("Wnioskujący podaje nazwę podmiotu w żądaniu", item.Text);
        Assert.Equal("CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT (0x1)", item.TechnicalName);
        var privateKey = Assert.Single(document.FlagGroups, g => g.Header.StartsWith("Klucz prywatny", StringComparison.Ordinal));
        Assert.Contains(privateKey.Items, i => i.Text == "Minimalna wersja urzędu: 1");
        var general = Assert.Single(document.FlagGroups, g => g.Header.StartsWith("Ogólne", StringComparison.Ordinal));
        Assert.Contains(general.Items, i => i.TechnicalName.StartsWith("CT_FLAG_IS_MODIFIED", StringComparison.Ordinal));

        document.SelectedRow = Row(document, PublicKeyTemplates.Esc9);
        var enrollment = Assert.Single(document.FlagGroups, g => g.Header.StartsWith("Rejestracja", StringComparison.Ordinal));
        Assert.Contains(enrollment.Items, i => i.TechnicalName == "CT_FLAG_NO_SECURITY_EXTENSION (0x80000)");
        Assert.Contains(document.Details, d => d.Label == "OID szablonu" && d.Value.StartsWith(PkiSamples.ForestOid, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFilters_ShowTheUnpublishedTemplates_AndSearchNamesAndTrustees()
    {
        var (_, document) = await OpenAsync();
        using var __ = document;

        document.UnpublishedOnly = true;
        Assert.Equal([PublicKeyTemplates.Esc1NotPublished], document.Rows.Select(r => r.Cn));
        Assert.True(document.IsFiltered);
        Assert.Equal($"Pokazane szablony: 1 z {PublicKeyTemplates.All.Count}", document.FilterText);

        document.UnpublishedOnly = false;
        document.SearchText = "bezpieczn";
        Assert.All(document.Rows, r => Assert.Contains("Bezpieczn", r.Cn, StringComparison.Ordinal));
        Assert.Equal(PublicKeyTemplates.Safe.Count, document.Rows.Count);

        document.SearchText = "GG_IT";
        Assert.Contains(document.Rows, r => r.Cn == PublicKeyTemplates.Esc2);
        Assert.DoesNotContain(document.Rows, r => r.Cn == PublicKeyTemplates.Esc1);
    }

    [Fact]
    public async Task WhoCanEnroll_TheTrusteesAndThenTheExpandedCounts()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;

        Assert.True(document.IsExpanded);
        Assert.False(document.IsCounting);
        Assert.StartsWith("Policzone konta:", document.CountStatusText, StringComparison.Ordinal);

        var esc1 = Row(document, PublicKeyTemplates.Esc1);
        Assert.True(esc1.IsBroadEnroll);
        Assert.Contains("Szeroka grupa", esc1.EnrollText, StringComparison.Ordinal);
        document.SelectedRow = esc1;
        var broad = Assert.Single(document.EnrollTrustees, t => t.Right == TemplateRight.Enroll && t.IsBroad);
        Assert.False(broad.CanOpen);
        Assert.Empty(broad.FirstAccounts);
        Assert.StartsWith("kont: ", broad.CountText, StringComparison.Ordinal);
        var total = Assert.Single(document.EnrollTotals, t => t.Total.Right == TemplateRight.Enroll);
        Assert.True(total.Total.AccountCount > 0);
        Assert.True(total.IsBroad);

        // A group trustee opens the group; its first accounts open their own sheets.
        document.SelectedRow = Row(document, PublicKeyTemplates.Esc2);
        var it = Assert.Single(document.EnrollTrustees, t => t.Right == TemplateRight.Enroll && !t.IsTier0);
        Assert.True(it.IsGroup);
        Assert.True(it.CanOpen);
        Assert.Equal("Konto lub grupa spoza Tier 0", it.ClassText);
        await document.OpenTrusteeCommand.ExecuteAsync(it);
        Assert.Equal(h.Company.GroupIt, Assert.Single(h.B2.Dialogs.PropertiesShown));
        var account = Assert.IsType<PkiAccountLinkViewModel>(it.FirstAccounts.First());
        await document.OpenAccountCommand.ExecuteAsync(account);
        Assert.Equal(account.Dn, h.B2.Dialogs.PropertiesShown[^1]);
        Assert.Equal(h.Company.GroupIt, h.B2.Dialogs.PropertiesShown[0]);
    }

    [Fact]
    public async Task WhoCanChange_ListsTheWritersOfASensitiveAttribute()
    {
        var (_, document) = await OpenAsync(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = PublicKeyTemplates.Esc4 });
        using var __ = document;

        Assert.Equal(PublicKeyTemplates.Esc4, document.SelectedRow?.Cn);
        var writer = Assert.Single(document.ModifyTrustees, t => t.Right == TemplateRight.WriteSensitiveProperty && t.IsBroad);
        Assert.Contains("msPKI-Certificate-Name-Flag", writer.RightText, StringComparison.Ordinal);
        Assert.Contains(document.ModifyTrustees, t => t.Right == TemplateRight.Owner);
        Assert.Contains(document.ModifyTotals, t => t.Total.Right == TemplateRight.WriteSensitiveProperty);
        Assert.False(document.IsNotEvaluated);
    }

    [Fact]
    public async Task TheTrusteeLevel_IsShownWhileTheAccountsAreCounted()
    {
        var h = await PkiHarness.CreateAsync();
        using var document = new PkiTemplatesDocumentViewModel(h.Services);
        using var gate = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        h.Session.BeforeOperation = operation =>
        {
            // The account pass (PrivilegedAccessScanner.AccountFilter) waits until the trustee level was checked.
            if (operation.Kind == FakeOperationKind.Search && operation.Search?.Attributes.Contains("altSecurityIdentities", StringComparer.OrdinalIgnoreCase) == true)
            {
                started.Release();
                gate.Wait(TimeSpan.FromSeconds(20));
            }
        };

        // Off the test thread: the reads may complete synchronously up to the held search.
        var load = Task.Run(document.LoadAsync, Ct);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await B2Harness.WaitUntilAsync(() => document.Rows.Count > 0);
        Assert.True(document.IsCounting);
        Assert.False(document.IsExpanded);
        document.SelectedRow = Row(document, PublicKeyTemplates.Esc1);
        Assert.Contains(document.EnrollTrustees, t => t.IsBroad && t.CountText == "liczenie…");

        gate.Set();
        await load;
        Assert.True(document.IsExpanded);
        Assert.StartsWith("kont: ", Assert.Single(document.EnrollTrustees, t => t.IsBroad && t.Right == TemplateRight.Enroll).CountText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheComparePane_PutsTwoTemplatesSideBySide_AndMarksTheDifferences()
    {
        var (_, document) = await OpenAsync();
        using var __ = document;
        var left = Row(document, PublicKeyTemplates.Esc1);
        var right = Row(document, PublicKeyTemplates.Esc1Safe);

        document.SetSelection([left, right]);
        document.SelectedRow = left;
        document.CompareCommand.Execute(null);

        Assert.True(document.IsComparing);
        Assert.Same(left, document.CompareLeft);
        Assert.Same(right, document.CompareRight);
        var approval = Assert.Single(document.CompareRows, r => r.Property == "Zatwierdzenie menedżera certyfikatów");
        Assert.True(approval.IsDifferent);
        Assert.Equal(("Nie", "Tak"), (approval.Left, approval.Right));
        Assert.False(Assert.Single(document.CompareRows, r => r.Property == "Okres ważności").IsDifferent);
        Assert.StartsWith("Różnice: ", document.CompareSummary, StringComparison.Ordinal);

        document.CompareOnlyDifferences = true;
        Assert.All(document.CompareRows, r => Assert.True(r.IsDifferent));
        Assert.Contains(document.CompareRows, r => r.Property.StartsWith("Rejestracja", StringComparison.Ordinal));

        document.CompareRight = Row(document, PublicKeyTemplates.Esc1);
        Assert.Empty(document.CompareRows);
        Assert.Equal("Szablony nie różnią się w porównywanych właściwościach.", document.CompareSummary);

        document.CloseCompareCommand.Execute(null);
        Assert.False(document.IsComparing);
        Assert.Empty(document.CompareRows);
    }

    [Fact]
    public async Task Export_WritesTheShownTemplatesToTheChosenFiles_EscapedAndWithACsp()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        using var folder = new TempFolder();
        var csv = Path.Combine(folder.Path, "szablony.csv");
        var html = Path.Combine(folder.Path, "szablony.html");
        document.UnpublishedOnly = true;

        h.B2.Files.NextSavePath = csv;
        await document.ExportCsvCommand.ExecuteAsync(null);
        h.B2.Files.NextSavePath = html;
        await document.ExportHtmlCommand.ExecuteAsync(null);

        var csvText = await File.ReadAllTextAsync(csv, Ct);
        Assert.Contains(PublicKeyTemplates.Esc1NotPublished, csvText, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicKeyTemplates.Esc1Safe, csvText, StringComparison.Ordinal);
        Assert.Contains("Kto może się zarejestrować", csvText, StringComparison.Ordinal);
        var htmlText = await File.ReadAllTextAsync(html, Ct);
        Assert.Contains("<title>Szablony certyfikatów – contoso.test</title>", htmlText, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", htmlText, StringComparison.Ordinal);
        Assert.Contains($"Z filtrem: 1 z {PublicKeyTemplates.All.Count} szablonów.", htmlText, StringComparison.Ordinal);
        Assert.Contains(h.B2.Localizer[L.PkiTemplates.ReportPrivacy], htmlText, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", htmlText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, h.B2.Notifications.Items.Count(m => m.Message.Contains("szablony", StringComparison.Ordinal)));

        Assert.Equal("&lt;img src=x onerror=&#39;a&#39;&gt; &amp; &quot;", PkiTemplatesDocumentViewModel.E("<img src=x onerror='a'> & \""));
    }

    [Fact]
    public async Task CopyDetails_PutsTheTemplateOnTheClipboard()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        document.SelectedRow = Row(document, PublicKeyTemplates.Esc1);

        document.CopyDetailsCommand.Execute(null);

        Assert.StartsWith(PublicKeyTemplates.Esc1, h.B2.Clipboard.Text, StringComparison.Ordinal);
        Assert.Contains("Wnioskujący podaje nazwę podmiotu w żądaniu", h.B2.Clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnTheRealCache_TheExpansionIsReused_AndF5CountsAgain()
    {
        var h = await PkiHarness.CreateAsync();
        using var cache = new PkiSnapshotService(h.B2.Services, h.TlsProbe, h.HealthProbe);
        var services = new PkiServices(h.B2.Services, new CsvCodec(), cache, h.TlsProbe, h.HealthProbe);
        using var first = new PkiTemplatesDocumentViewModel(services);
        await first.LoadAsync();
        var expansion = cache.Expansion;
        Assert.NotNull(expansion);

        using var second = new PkiTemplatesDocumentViewModel(services);
        await second.LoadAsync();
        Assert.Same(expansion, cache.Expansion);
        Assert.True(second.IsExpanded);

        await second.RefreshAsync();
        Assert.NotSame(expansion, cache.Expansion);
        Assert.True(second.IsExpanded);
    }

    [Fact]
    public async Task EveryDynamicKey_OfTheTemplates_ExistsInBothLanguages_AndTheRowsPrintNoValue()
    {
        var (h, document) = await OpenAsync();
        using var _ = document;
        foreach (var culture in new[] { "pl-PL", "en-US" })
        {
            h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo(culture));
            foreach (var key in L.PkiTemplates.AllDynamicKeys())
            {
                Assert.True(h.B2.Localizer.Contains(key), $"{culture}: {key}");
            }
        }

        document.SelectedRow = Row(document, PublicKeyTemplates.Esc2);
        object[] rows = [.. document.Rows, .. document.FlagGroups, .. document.FlagGroups.SelectMany(g => g.Items), .. document.EnrollTrustees, .. document.ModifyTrustees, .. document.EnrollTotals];
        foreach (var row in rows)
        {
            var text = row.ToString()!;
            Assert.DoesNotContain("Podatny", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GG_", text, StringComparison.Ordinal);
            Assert.DoesNotContain("S-1-", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CT_FLAG", text, StringComparison.Ordinal);
        }
    }
}
