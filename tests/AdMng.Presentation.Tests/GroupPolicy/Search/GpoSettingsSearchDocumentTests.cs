using System.Globalization;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Search;

/// <summary>
/// "Wyszukiwarka ustawień GPO" (K7, docs/dev/k-computers-and-gpo.md 21.4) on the Polish GPO management sample (and the
/// cleanup sample): the summary of an empty query, the search by name, value, key and account, the groups with the agreement
/// of their values, the facets, the notes, masked values, the stale mark, cancel, the row actions and the exports.
/// </summary>
public sealed class GpoSettingsSearchDocumentTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task<GpoSettingsSearchDocumentViewModel> OpenAsync(GroupPolicyHarness h, string? searchText = null)
    {
        var document = await h.OpenAsync<GpoSettingsSearchDocumentViewModel>(GpoSettingsSearchEntry.Request with { SearchText = searchText });
        Assert.Null(document.Error);
        return document;
    }

    internal static async Task SearchAsync(GpoSettingsSearchDocumentViewModel document, string text, GpoSearchField field = GpoSearchField.All)
    {
        document.SelectedField = document.FieldOptions.Single(o => o.Value == field);
        document.SearchText = text;
        await document.SearchCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Opening_ReadsTheIndexOnce_AndAnEmptyQueryShowsOnlyTheSummary()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);

        Assert.Equal("Wyszukiwarka ustawień GPO", document.Title);
        Assert.True(document.HasIndex);
        Assert.True(document.IsSummaryShown);
        Assert.Empty(document.Rows);
        Assert.Empty(document.Groups);
        Assert.StartsWith("Przeszukiwane obiekty GPO: 7 · odczyt ", document.IndexInfoText, StringComparison.Ordinal);
        Assert.Contains(document.SummaryLines, l => l.StartsWith("Ustawienia zabezpieczeń – ustawienia: ", StringComparison.Ordinal));
        Assert.Contains(document.SummaryLines, l => l.StartsWith("Preferencje – ustawienia: 1, wystąpienia: 1, obiekty GPO: 1", StringComparison.Ordinal));
        Assert.StartsWith("Razem – ustawienia: ", document.SummaryLines[^1], StringComparison.Ordinal);
        Assert.Equal(document.IndexInfoText, document.StatusText);
        Assert.False(document.IsStale);

        // One character: still the summary, never every row.
        await SearchAsync(document, "k");
        Assert.True(document.IsSummaryShown);
        Assert.Empty(document.Rows);
        Assert.Equal(string.Empty, document.ResultText);
    }

    [Fact]
    public async Task MinimumPasswordLength_FindsBothGpos_AsOneSettingWithDifferentValues()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);

        await SearchAsync(document, "minimalna długość hasła");

        Assert.False(document.IsSummaryShown);
        var group = Assert.Single(document.Groups);
        Assert.Equal("Minimalna długość hasła", group.Name);
        Assert.EndsWith("Zasady konta › Zasady haseł", group.GroupPath, StringComparison.Ordinal);
        Assert.Equal(GpoValueAgreement.Different, group.Agreement);
        Assert.Equal("ustawione w 2 GPO – wartości różne", group.SummaryText);
        Assert.Equal(GpoSearchArea.Security, group.Area);
        Assert.Equal(["Default Domain Policy", "Stacje robocze – Zasady haseł"], group.Rows.Select(r => r.GpoName));
        Assert.Contains("7", group.Rows[0].ValueText, StringComparison.Ordinal);
        Assert.Contains("12", group.Rows[1].ValueText, StringComparison.Ordinal);
        Assert.Equal("Komputery", group.Rows[1].LinksText);
        Assert.Equal("Znalezione ustawienia: 1 (wystąpienia: 2)", document.ResultText);
        Assert.Equal(document.ResultText, document.StatusText);

        // Without Polish letters, any case: the same group.
        await SearchAsync(document, "MINIMALNA DLUGOSC");
        Assert.Equal("Minimalna długość hasła", Assert.Single(document.Groups).Name);

        // By value: "12" finds the setting and shows the other GPO's value too.
        await SearchAsync(document, "12", GpoSearchField.Value);
        var byValue = document.Groups.Single(g => g.Name == "Minimalna długość hasła");
        Assert.Equal(2, byValue.Rows.Count);

        // By technical key.
        await SearchAsync(document, "MinimumPasswordLength", GpoSearchField.Technical);
        Assert.Equal("Minimalna długość hasła", Assert.Single(document.Groups).Name);
        await SearchAsync(document, "MinimumPasswordLength", GpoSearchField.Name);
        Assert.Empty(document.Groups);
        Assert.Equal("Żadne ustawienie nie pasuje do wyszukiwania.", document.ResultText);
    }

    [Fact]
    public async Task DriveK_InThreeGpos_IsOneSetting_AndOnlyDifferentValuesKeepsIt()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var cleanup = h.B2.Builder.AddGroupPolicyCleanupSample(h.Company, h.Sysvol);
        using var document = await OpenAsync(h);

        await SearchAsync(document, "K:");

        var drive = Assert.Single(document.Groups, g => g.Area == GpoSearchArea.Preferences);
        Assert.Equal(["Porządki – Dyski (Kadry)", "Porządki – Dyski (Księgowość)", "Użytkownicy – Mapowanie dysków"], drive.Rows.Select(r => r.GpoName));
        Assert.Equal(3, drive.GpoCount);
        Assert.Equal(GpoValueAgreement.Different, drive.Agreement);
        Assert.All(drive.Rows, r => Assert.Equal(GpoScope.User, r.Unit.Scope));
        Assert.Contains(drive.Rows, r => r.GpoId == cleanup.DrivesHr);

        document.OnlyDifferent = true;
        await document.SearchCommand.ExecuteAsync(null);
        Assert.Contains(document.Groups, g => ReferenceEquals(g.Source, drive.Source));
        Assert.All(document.Groups, g => Assert.Equal(GpoValueAgreement.Different, g.Agreement));
        Assert.True(document.IsFiltered);

        // The shared path matches the value of the two cleanup GPOs only, but the group shows every GPO that maps K:.
        document.ClearFiltersCommand.Execute(null);
        await document.SearchCommand.ExecuteAsync(null);
        Assert.False(document.IsFiltered);
        await SearchAsync(document, "Wspólne", GpoSearchField.Value);
        var shared = Assert.Single(document.Groups);
        Assert.Same(drive.Source, shared.Source);
        Assert.Equal(3, shared.Rows.Count); // every GPO that sets K:, also the one with another path
    }

    [Fact]
    public async Task AccountSearch_ByNameAndBySid_FindsTheSettingsNamingTheGroup()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.B2.Builder.AddGroupPolicyCleanupSample(h.Company, h.Sysvol);
        using var document = await OpenAsync(h);
        var sid = h.B2.Builder.SidOf(h.Company.GroupIt);

        await SearchAsync(document, @"CONTOSO\GG_IT", GpoSearchField.Account);
        var byName = document.Rows.Select(r => r.GpoName).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Contains("Stacje robocze – Pulpit zdalny", byName);
        Assert.Contains("Porządki – Pulpit zdalny", byName);
        Assert.Contains("Porządki – Dyski (Kadry)", byName);
        Assert.All(document.Groups, g => Assert.Contains(g.Rows, r => r.Unit.PrincipalSids.Contains(sid, StringComparer.OrdinalIgnoreCase)));

        await SearchAsync(document, "*" + sid, GpoSearchField.Account);
        Assert.Equal(byName, document.Rows.Select(r => r.GpoName).Distinct().Order(StringComparer.Ordinal));

        // A part of a resolved name matches too ("gg_it" in CONTOSO\GG_IT).
        await SearchAsync(document, "gg_it", GpoSearchField.Account);
        Assert.Equal(byName, document.Rows.Select(r => r.GpoName).Distinct().Order(StringComparer.Ordinal));

        // Built-in Administrators by its Polish name.
        await SearchAsync(document, "Administratorzy", GpoSearchField.Account);
        Assert.Contains(document.Rows, r => r.GpoName == "Default Domain Controllers Policy");
    }

    [Fact]
    public async Task AMaskedValue_IsNeverMatched_OnlyItsNameAndKey()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.Put(h.Sample.ServerSecurity, "Machine/Registry.pol", RegistryPolFile.Write(
        [
            new RegistryPolEntry(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword", RegistryFormat.RegSz, RegistryPolFile.StringData("Tajne-Hasło-2026")),
        ]));
        using var document = await OpenAsync(h);

        Assert.Contains(document.Notes, n => n.Text.StartsWith("Wartości ukryte (hasła, community SNMP …; wystąpienia: 1)", StringComparison.Ordinal));
        await SearchAsync(document, "Tajne-Hasło");
        Assert.Empty(document.Groups);
        await SearchAsync(document, "Tajne", GpoSearchField.Value);
        Assert.Empty(document.Groups);

        await SearchAsync(document, "DefaultPassword", GpoSearchField.Technical);
        var row = Assert.Single(Assert.Single(document.Groups).Rows);
        Assert.True(row.IsMasked);
        Assert.Contains("wartość ukryta", row.Marks);
        Assert.DoesNotContain("Tajne", row.ValueText, StringComparison.Ordinal);
        Assert.DoesNotContain("Tajne", row.CopyText, StringComparison.Ordinal);
        Assert.Equal("Nie można skopiować: " + h.B2.Localizer["GpoCopy.Refusal.Secret"], "Nie można skopiować: " + row.RefusalText);
    }

    [Fact]
    public async Task Facets_NarrowTheScopeTheAreaAndThePreferenceType()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.B2.Builder.AddGroupPolicyCleanupSample(h.Company, h.Sysvol);
        using var document = await OpenAsync(h);

        Assert.Equal(["Wszystko", "Nazwa", "Wartość", "Klucz techniczny", "Konto"], document.FieldOptions.Select(o => o.Text));
        Assert.Equal(["Komputera i użytkownika", "Komputera", "Użytkownika"], document.ScopeOptions.Select(o => o.Text));
        Assert.Contains(document.AreaOptions, o => o.Value is { Area: GpoSearchArea.Preferences, PreferenceType: "Drives" } && o.Text.StartsWith("Preferencje – ", StringComparison.Ordinal));
        Assert.Contains(document.AreaOptions, o => o.Value is { Area: GpoSearchArea.Preferences, PreferenceType: "Groups" });

        // "K:" and "Wspólne" are user settings, the password policy a computer setting.
        await SearchAsync(document, "wsp");
        Assert.NotEmpty(document.Groups);
        document.SelectedScope = document.ScopeOptions.Single(o => o.Value == GpoScope.User);
        await document.SearchCommand.ExecuteAsync(null);
        Assert.NotEmpty(document.Rows);
        Assert.All(document.Rows, r => Assert.Equal(GpoScope.User, r.Unit.Scope));
        document.SelectedScope = document.ScopeOptions.Single(o => o.Value == GpoScope.Computer);
        await SearchAsync(document, "minimalna długość");
        Assert.NotEmpty(document.Rows);
        Assert.All(document.Rows, r => Assert.Equal(GpoScope.Computer, r.Unit.Scope));
        document.SelectedScope = document.ScopeOptions.Single(o => o.Value == GpoScope.User);
        await document.SearchCommand.ExecuteAsync(null);
        Assert.Empty(document.Groups);

        document.SelectedScope = document.ScopeOptions[0];
        document.SelectedArea = document.AreaOptions.Single(o => o.Value is { Area: GpoSearchArea.Audit, PreferenceType: null });
        await SearchAsync(document, "logowani");
        Assert.NotEmpty(document.Groups);
        Assert.All(document.Groups, g => Assert.Equal(GpoSearchArea.Audit, g.Area));
        Assert.Contains(document.Rows, r => r.GpoName == "Porządki – Inspekcja");

        document.SelectedArea = document.AreaOptions.Single(o => o.Value is { PreferenceType: "Drives" });
        await SearchAsync(document, "K:");
        Assert.All(document.Rows, r => Assert.Equal("Drives", r.Unit.PreferenceType));
        Assert.Equal(3, document.Rows.Count);

        document.ClearFiltersCommand.Execute(null);
        Assert.Null(document.SelectedArea!.Value);
        Assert.Null(document.SelectedScope!.Value);
    }

    [Fact]
    public async Task Notes_SayWhichGposWereNotRead_AndWhenSysvolIsMissing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        await using (var connection = await h.Sysvol.OpenForWriteAsync(h.Session, Ct))
        {
            await connection.DeleteGpoFolderAsync(h.Sample.SitePrinters, Ct);
        }

        using var document = await OpenAsync(h);

        var notRead = Assert.Single(document.Notes, n => n.Text.StartsWith("Nie odczytano folderów: 1 ", StringComparison.Ordinal));
        Assert.Equal("Lokacja Warszawa – Drukarki (Nie znaleziono folderu w SYSVOL)", notRead.Detail);

        var bare = await GroupPolicyHarness.CreateAsync(Polish, withWriter: false);
        using var withoutSysvol = await OpenAsync(bare);
        var note = Assert.Single(withoutSysvol.Notes);
        Assert.StartsWith("SYSVOL jest niedostępny (", note.Text, StringComparison.Ordinal);
        Assert.Empty(withoutSysvol.Corpus!.Records);
    }

    [Fact]
    public async Task ChangesElsewhere_MarkTheResultsStale_AndRefreshReadsOnlyTheChangedGpos()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h, "hasła");
        var view = document.View;
        h.Sysvol.ClearCalls();

        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading);
        Assert.Same(view, document.View);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation == "List");

        await document.RefreshIndexCommand.ExecuteAsync(null);
        Assert.False(document.IsStale);
        Assert.NotSame(view, document.View);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation == "List"); // nothing changed: every GPO reused
        Assert.NotEmpty(document.Groups); // the query ran again

        h.Sysvol.ClearCalls();
        await document.ReadAllCommand.ExecuteAsync(null);
        Assert.Contains(h.Sysvol.Calls, c => c.Operation == "List");
    }

    [Fact]
    public async Task Cancel_StopsTheRead_AndKeepsTheShownResults()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h, "hasła");
        var rows = document.Rows;
        Assert.NotEmpty(rows);
        h.Sysvol.BeforeCall = (operation, _, _) =>
        {
            if (operation == "List")
            {
                Assert.True(document.CancelCommand.CanExecute(null));
                document.Cancel();
            }
        };

        await document.ReadAllCommand.ExecuteAsync(null);
        h.Sysvol.BeforeCall = null;

        Assert.False(document.IsLoading);
        Assert.Null(document.Error);
        Assert.Equal("Odczyt ustawień anulowano.", document.StatusText);
        Assert.Same(rows, document.Rows);
        Assert.False(document.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task RowActions_OpenTheGpo_EditTheSetting_CompareAndCopy()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        var requests = new List<GroupPolicyDocumentRequest>();
        h.Services.DocumentRequested += (_, r) => requests.Add(r);
        await SearchAsync(document, "minimalna długość hasła");
        var group = Assert.Single(document.Groups);
        Assert.False(document.OpenGpoCommand.CanExecute(null));

        document.SelectedRow = group.Rows[1];
        Assert.Equal("Stacje robocze – Zasady haseł – Minimalna długość hasła", document.SelectionText);
        document.OpenGpoCommand.Execute(null);
        var open = Assert.Single(requests);
        Assert.Equal((GroupPolicyDocumentKind.Object, h.Sample.WorkstationPasswords, GpoDocumentTab.Settings), (open.Kind, open.GpoId!.Value, open.Tab!.Value));

        Assert.True(document.EditSettingCommand.CanExecute(null));
        document.EditSettingCommand.Execute(null);
        var edit = requests[^1];
        Assert.Equal(GroupPolicyDocumentKind.SecuritySettings, edit.Kind);
        Assert.Equal(h.Sample.WorkstationPasswords, edit.GpoId);
        Assert.NotNull(edit.Focus);

        // Two rows: their GPOs are compared.
        document.SelectedRows = [.. group.Rows];
        Assert.True(document.CompareCommand.CanExecute(null));
        await document.CompareCommand.ExecuteAsync(null);
        var comparison = Assert.Single(h.Windows.Comparisons);
        comparison.Dispose();

        // "Kopiuj": the occurrences as text; "Kopiuj do innego GPO…" hands the items to the copy actions (W5).
        document.CopyCommand.Execute(null);
        Assert.Contains("Default Domain Policy: Minimalna długość hasła = ", h.B2.Clipboard.Text, StringComparison.Ordinal);
        Assert.Contains("Stacje robocze – Zasady haseł: Minimalna długość hasła = ", h.B2.Clipboard.Text, StringComparison.Ordinal);
        Assert.True(document.CopyToCommand.CanExecute(null));
        var item = group.Rows[0].CopyItem;
        Assert.Equal(h.Sample.DefaultDomainPolicy, item.Unit.GpoId);
        Assert.Equal(group.Rows[0].Unit.Id, item.Unit.UnitId);
        Assert.Equal(group.Rows[0].Unit.ValueFingerprint, item.Unit.ValueFingerprint);
        Assert.Null(item.Refusal);
        await document.CopyToCommand.ExecuteAsync(null);
        Assert.False(document.IsLoading);
    }

    [Fact]
    public async Task Export_WritesTheResults_ToTheChosenFile_WithEveryValueEscaped()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        const string Name = "<script>alert('x')</script> & \"Żółw\"";
        var id = h.B2.Builder.AddManagedGroupPolicy(h.Sysvol, Name, 0, 1);
        h.Sysvol.Put(id, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 14")));
        using var document = await OpenAsync(h);
        await SearchAsync(document, "minimalna długość hasła");
        Assert.Equal(3, Assert.Single(document.Groups).Rows.Count);
        var csv = Path.Combine(Path.GetTempPath(), "admng-k7-" + Guid.NewGuid().ToString("N") + ".csv");
        var html = Path.ChangeExtension(csv, ".html");
        try
        {
            h.B2.Files.NextSavePath = csv;
            await document.ExportCsvCommand.ExecuteAsync(null);
            Assert.StartsWith("Wyszukiwanie ustawień GPO – contoso.test_", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(csv, Ct);
            Assert.StartsWith("Ustawienie;Miejsce w konsoli GPMC;Wartości;Obiekt zasad grupy;Wartość", lines[0], StringComparison.Ordinal);
            Assert.Equal(4, lines.Length);
            Assert.Contains(lines, l => l.Contains("\"<script>alert('x')</script> & \"\"Żółw\"\"\"", StringComparison.Ordinal));

            h.B2.Files.NextSavePath = html;
            await document.ExportHtmlCommand.ExecuteAsync(null);
            var page = await File.ReadAllTextAsync(html, Ct);
            Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", page, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;Żółw&quot;", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", page, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<h1>Wyszukiwanie ustawień GPO – contoso.test</h1>", page, StringComparison.Ordinal);
            Assert.Contains("ustawione w 3 GPO – wartości różne", page, StringComparison.Ordinal);
            Assert.Contains(h.B2.Notifications.Items, m => m.Message.Contains(Path.GetFileName(html), StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(csv);
            File.Delete(html);
        }
    }

    [Fact]
    public async Task LanguageSwitch_RerendersTheTexts_WithoutReadingTheGpoFolders()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h, "MinimumPasswordLength");
        Assert.Equal("Minimalna długość hasła", Assert.Single(document.Groups).Name);
        h.Sysvol.ClearCalls();

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => document.Title == "GPO settings search" && !document.IsLoading && document.View?.Culture.Name == "en-US"
            && document.Groups.Count == 1 && document.Groups[0].Name == "Minimum password length");

        Assert.Equal("set in 2 GPOs – values differ", document.Groups[0].SummaryText);
        Assert.Equal("Everything", document.FieldOptions[0].Text);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation is "List" or "Read" && c.GpoId != Guid.Empty);
    }

    [Fact]
    public async Task ToString_PrintsNoDirectoryValues()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h, "minimalna długość hasła");
        var group = Assert.Single(document.Groups);

        Assert.DoesNotContain("hasła", group.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stacje", group.Rows[1].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hasła", document.LastQuery!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Minimum", document.Corpus!.Records[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Stacje", group.Rows[1].CopyItem.ToString(), StringComparison.Ordinal);
    }
}
