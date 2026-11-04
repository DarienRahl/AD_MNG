using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// "Diagnostyka zasad grupy" (K6, design note docs/dev/k-computers-and-gpo.md 20.2) on the Polish GPO management sample:
/// the quick check of the opening, the full check with progress, cancel, the chips and filters, the details and actions, the
/// repairs through the preview with the check that follows, the notes of what was not checked, the exports and the stale
/// mark.
/// </summary>
public sealed class GroupPolicyDiagnosticsDocumentTests
{
    private const string LengthKey = "Computer/Security/System Access/MinimumPasswordLength";
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    internal static Task<GroupPolicyDiagnosticsDocumentViewModel> OpenAsync(GroupPolicyHarness h) =>
        h.OpenAsync<GroupPolicyDiagnosticsDocumentViewModel>(GroupPolicyDiagnosticsEntry.Request);

    [Fact]
    public async Task Opening_RunsAQuickCheck_WithReadableFindings_AndSaysWhatAQuickCheckSkips()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);

        Assert.Null(document.Error);
        Assert.Equal("Diagnostyka zasad grupy", document.Title);
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);
        Assert.StartsWith("Ostatnie sprawdzenie: szybkie, ", document.LastRunText, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation == "List");

        var mismatch = Assert.Single(document.Rows, r => r.Code == GpoFindingCodes.VersionMismatch);
        Assert.Equal("Użytkownicy – Mapowanie dysków", mismatch.GpoText);
        Assert.Equal(("Średnia", "Wersje"), (mismatch.SeverityText, mismatch.AreaText));
        Assert.Equal("Wersje w AD i SYSVOL różnią się", mismatch.Title);
        Assert.Equal("Wersja w Active Directory (użytkownik 2, komputer 0) różni się od wersji w pliku GPT.INI (użytkownik 1, komputer 0).", mismatch.Message);
        Assert.Equal("Naprawa: wyrównaj wersje", mismatch.FixText);
        Assert.Contains("DFSR", mismatch.Recommendation, StringComparison.Ordinal);
        var disabled = Assert.Single(document.Rows, r => r.Code == GpoFindingCodes.GpoLinksAllDisabled);
        Assert.Equal("contoso.test/Firma/Komputery", disabled.WhereText);
        Assert.Equal("Wszystkie połączenia obiektu GPO są wyłączone (liczba połączeń: 1), więc nigdzie nie działa.", disabled.Message);

        // Most severe first; one chip per severity whose counts add up.
        Assert.Equal(document.Rows.OrderByDescending(r => r.Severity).Select(r => r.Severity), document.Rows.Select(r => r.Severity));
        Assert.Equal([GpoFindingSeverity.Critical, GpoFindingSeverity.High, GpoFindingSeverity.Medium, GpoFindingSeverity.Low, GpoFindingSeverity.Info],
            document.Chips.Select(c => c.Severity));
        Assert.Equal(document.TotalCount, document.Chips.Sum(c => c.Count));
        Assert.Contains(document.Chips, c => c.Text == "Średnie: " + c.Count && c.Severity == GpoFindingSeverity.Medium);

        // What a quick check does not cover, in the operator's words.
        var note = Assert.Single(document.Notes);
        Assert.Equal("Szybkie sprawdzenie nie obejmuje: foldery SYSVOL i ustawienia obiektów GPO, użytkownicy i komputery pod połączeniami, "
            + "identyfikatory SID w uprawnieniach i ustawieniach, konflikty między obiektami GPO. Uruchom pełne sprawdzenie, aby je uwzględnić – "
            + "odczytuje ono także foldery SYSVOL i może potrwać dłużej.", note);
        Assert.EndsWith(document.TotalCount % 10 is >= 2 and <= 4 && document.TotalCount % 100 is < 12 or > 14 ? "uwagi" : "uwag", document.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullCheck_ReadsTheFolders_ReportsItsProgress_AndFindsTheSettingsProblems()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        var progress = new ConcurrentQueue<string>();
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(document.ProgressText) && document.ProgressText.Length > 0)
            {
                progress.Enqueue(document.ProgressText);
            }
        };

        await document.FullCheckCommand.ExecuteAsync(null);

        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.Empty(document.Notes);
        Assert.Contains("Odczyt folderów SYSVOL: 7 z 7", progress);
        Assert.Contains("Odczyt obiektów GPO, połączeń i filtrów WMI…", progress);
        Assert.Equal(string.Empty, document.ProgressText);

        var account = Assert.Single(document.Rows, r => r.Code == GpoFindingCodes.AccountPolicyBelowDomain);
        Assert.Equal(("Stacje robocze – Zasady haseł", "Minimalna długość hasła"), (account.GpoText, account.WhereText));
        Assert.Contains("Klucz: " + LengthKey, account.AffectedLines);
        Assert.True(account.CanEditSetting);
        Assert.Equal(h.Company.ComputersOu, account.ModelContainerDn);

        var weak = Assert.Single(document.Rows, r => r.Code == GpoFindingCodes.WeakDomainPasswordPolicy);
        Assert.Equal("Zasady haseł domeny są słabe: ustawienie „Minimalna długość hasła” ma wartość 7.", weak.Message);

        var conflict = Assert.Single(document.Rows, r => r.Code == GpoFindingCodes.ConflictingSetting && r.Finding.SettingKey == LengthKey);
        Assert.Equal("„Stacje robocze – Zasady haseł” ustawia 12, a „Default Domain Policy” – 7; obowiązuje „Stacje robocze – Zasady haseł”.", conflict.Message);
        Assert.Contains("Kontener: contoso.test/Firma/Komputery", conflict.AffectedLines);
        Assert.Contains("Pozostałe obiekty GPO: Default Domain Policy", conflict.AffectedLines);
        Assert.Contains(document.Rows, r => r.Code == GpoFindingCodes.ExtensionNamesMissing && r.FixText == "Naprawa: dodaj brakujące nazwy rozszerzeń");

        // Every finding's arguments are as many as its message takes.
        Assert.All(document.Rows, r => Assert.Equal(GpoFindingText.ArgumentCounts[r.Code], r.Finding.Arguments.Count));
    }

    [Fact]
    public async Task Cancel_StopsTheFullCheck_AndKeepsTheResultsShown()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        var shown = document.Rows;
        h.Sysvol.BeforeCall = (operation, _, _) =>
        {
            if (operation == "List")
            {
                Assert.True(document.CancelCommand.CanExecute(null));
                document.Cancel();
            }
        };

        await document.FullCheckCommand.ExecuteAsync(null);
        h.Sysvol.BeforeCall = null;

        Assert.False(document.IsLoading);
        Assert.Null(document.Error);
        Assert.Equal("Sprawdzanie anulowano.", document.StatusText);
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);
        Assert.Same(shown, document.Rows);
        Assert.False(document.CancelCommand.CanExecute(null));
        Assert.True(document.FullCheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChipsAndFilters_NarrowTheList_AndTheSearchIgnoresCaseAndDiacritics()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        await document.FullCheckCommand.ExecuteAsync(null);
        var total = document.TotalCount;

        var medium = document.Chips.Single(c => c.Severity == GpoFindingSeverity.Medium);
        medium.IsSelected = true;
        Assert.All(document.Rows, r => Assert.Equal(GpoFindingSeverity.Medium, r.Severity));
        Assert.Equal(medium.Count, document.Rows.Count);
        Assert.Equal(GpoFindingSeverity.Medium, document.SelectedSeverity?.Value);
        Assert.Equal($"Pokazano uwagi: {medium.Count} z {total}", document.StatusText);
        Assert.True(document.IsFiltered);

        // The severity box moves the chips along.
        document.SelectedSeverity = document.SeverityOptions.Single(o => o.Value == GpoFindingSeverity.Info);
        Assert.False(document.Chips.Single(c => c.Severity == GpoFindingSeverity.Medium).IsSelected);
        Assert.True(document.Chips.Single(c => c.Severity == GpoFindingSeverity.Info).IsSelected);

        // Area: Bezpieczeństwo is a filter of its own.
        document.SelectedSeverity = document.SeverityOptions[0];
        document.SelectedArea = document.AreaOptions.Single(o => o.Value == GpoFindingArea.Security);
        Assert.Equal("Bezpieczeństwo", document.SelectedArea.Text);
        Assert.NotEmpty(document.Rows);
        Assert.All(document.Rows, r => Assert.Equal(GpoFindingArea.Security, r.Area));

        // A severity with nothing in the area: the list says so.
        document.SelectedSeverity = document.SeverityOptions.Single(o => o.Value == GpoFindingSeverity.Critical);
        Assert.Empty(document.Rows);
        Assert.Equal("Żadna uwaga nie pasuje do filtrów.", document.ListNote);

        document.ClearFiltersCommand.Execute(null);
        Assert.Equal(total, document.Rows.Count);
        Assert.False(document.IsFiltered);
        Assert.All(document.Chips, c => Assert.False(c.IsSelected));

        // GPO (the winner or one of the other GPOs of a finding).
        var drives = document.GpoOptions.Single(o => o.Text == "Użytkownicy – Mapowanie dysków");
        document.SelectedGpo = drives;
        Assert.NotEmpty(document.Rows);
        Assert.All(document.Rows, r => Assert.True(r.Finding.GpoId == drives.Value || r.Finding.RelatedGpoIds.Contains(drives.Value!.Value)));

        // Text, case and diacritics ignored; Esc clears it.
        document.ClearFiltersCommand.Execute(null);
        document.SearchText = "MAPOWANIE DYSKOW";
        Assert.NotEmpty(document.Rows);
        Assert.All(document.Rows, r => Assert.Contains("Mapowanie dysków", r.SearchText, StringComparison.Ordinal));
        document.ClearSearchCommand.Execute(null);
        Assert.Equal(total, document.Rows.Count);

        var focused = false;
        document.SearchFocusRequested += (_, _) => focused = true;
        document.FocusSearch();
        Assert.True(focused);
    }

    [Fact]
    public async Task WithoutSysvol_TheNotesSayWhatWasNotChecked_AndWhy()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        using var document = await OpenAsync(h);

        Assert.NotNull(document.SysvolWarning);
        Assert.Equal(2, document.Notes.Count);
        Assert.Equal("Nie sprawdzono z powodu niedostępności SYSVOL: wersje GPT.INI. " + document.SysvolWarning, document.Notes[0]);
        Assert.StartsWith("Szybkie sprawdzenie nie obejmuje: foldery SYSVOL", document.Notes[1], StringComparison.Ordinal);
        Assert.DoesNotContain(document.Rows, r => r.Code == GpoFindingCodes.VersionMismatch);

        await document.FullCheckCommand.ExecuteAsync(null);

        var note = Assert.Single(document.Notes);
        Assert.StartsWith("Nie sprawdzono z powodu niedostępności SYSVOL: wersje GPT.INI, foldery SYSVOL i ustawienia obiektów GPO, konflikty między obiektami GPO.",
            note, StringComparison.Ordinal);
        Assert.Contains(document.Rows, r => r.Code == GpoFindingCodes.GpoLinksAllDisabled);
    }

    [Fact]
    public async Task Fix_GoesThroughThePreview_ChecksAgainAtTheSameDepth_AndKeepsTheSelectionNearby()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        await document.FullCheckCommand.ExecuteAsync(null);
        var row = document.Rows.Single(r => r.Code == GpoFindingCodes.VersionMismatch);
        var index = document.Rows.ToList().IndexOf(row);
        document.SelectedRow = row;
        Assert.True(document.FixCommand.CanExecute(null));

        await document.FixCommand.ExecuteAsync(null);

        var preview = Assert.Single(h.Windows.Previews);
        Assert.Equal("Wyrównanie wersji „Użytkownicy – Mapowanie dysków”", preview.Title);
        Assert.Equal(0, preview.Result!.FailedCount);
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.DoesNotContain(document.Rows, r => r.Code == GpoFindingCodes.VersionMismatch);
        Assert.Same(document.Rows[Math.Min(index, document.Rows.Count - 1)], document.SelectedRow);
        Assert.False(document.IsStale);

        // A repair the operator closes without executing changes nothing and checks nothing again.
        h.Windows.ExecutePreviews = false;
        document.SelectedRow = document.Rows.Single(r => r.Code == GpoFindingCodes.GpoLinksAllDisabled);
        var rows = document.Rows;
        await document.FixCommand.ExecuteAsync(null);
        Assert.Same(rows, document.Rows);
        Assert.Equal(GpoFindingCodes.GpoLinksAllDisabled, document.SelectedRow?.Code);
    }

    [Fact]
    public async Task Actions_OpenTheGpo_EditTheSetting_ShowTheModel_AndCopyTheDetails()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var requests = new List<GroupPolicyDocumentRequest>();
        h.Services.DocumentRequested += (_, r) => requests.Add(r);
        using var document = await OpenAsync(h);
        await document.FullCheckCommand.ExecuteAsync(null);

        document.SelectedRow = document.Rows.Single(r => r.Code == GpoFindingCodes.AccountPolicyBelowDomain);
        document.EditSettingCommand.Execute(null);
        document.OpenGpoCommand.Execute(null);
        document.ShowInModelCommand.Execute(null);

        Assert.Equal([GroupPolicyDocumentKind.SecuritySettings, GroupPolicyDocumentKind.Object, GroupPolicyDocumentKind.Modeling], requests.Select(r => r.Kind));
        Assert.Equal((h.Sample.WorkstationPasswords, LengthKey), (requests[0].GpoId!.Value, requests[0].Focus?.SettingKey));
        Assert.Equal((h.Sample.WorkstationPasswords, GpoDocumentTab.Settings), (requests[1].GpoId!.Value, requests[1].Tab!.Value));
        Assert.Equal((h.Company.ComputersOu, "contoso.test/Firma/Komputery"), (requests[2].ContainerDn, requests[2].Name));

        // A finding without a setting: no editor; the model of its GPO's link; the Stan tab of the GPO.
        document.SelectedRow = document.Rows.Single(r => r.Code == GpoFindingCodes.VersionMismatch);
        Assert.False(document.EditSettingCommand.CanExecute(null));
        Assert.True(document.ShowInModelCommand.CanExecute(null));
        document.OpenGpoCommand.Execute(null);
        Assert.Equal(GpoDocumentTab.Status, requests[^1].Tab);

        document.CopyCommand.Execute(null);
        var text = h.B2.Clipboard.Text!;
        Assert.StartsWith("Średnia · Wersje · Wersje w AD i SYSVOL różnią się", text, StringComparison.Ordinal);
        Assert.Contains("Dlaczego to ważne", text, StringComparison.Ordinal);
        Assert.Contains("Co zrobić", text, StringComparison.Ordinal);
        Assert.Contains("Obiekt GPO: Użytkownicy – Mapowanie dysków " + GroupPolicyText.Guid(h.Sample.DriveMappings), text, StringComparison.Ordinal);

        // Nothing selected: the actions wait.
        document.SelectedRow = null;
        Assert.False(document.FixCommand.CanExecute(null));
        Assert.False(document.OpenGpoCommand.CanExecute(null));
        Assert.False(document.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_WritesCsvAndHtml_ToTheChosenFile_WithEveryValueEscaped()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        const string Name = "<script>alert('x')</script> & \"Żółw\"";
        h.B2.Builder.AddManagedGroupPolicy(h.Sysvol, Name, 0, 0);
        using var document = await OpenAsync(h);
        Assert.Contains(document.Rows, r => r.Code == GpoFindingCodes.GpoNotLinked && r.GpoText == Name);
        var csv = Path.Combine(Path.GetTempPath(), "admng-k6-" + Guid.NewGuid().ToString("N") + ".csv");
        var html = Path.ChangeExtension(csv, ".html");
        try
        {
            h.B2.Files.NextSavePath = csv;
            await document.ExportCsvCommand.ExecuteAsync(null);
            Assert.StartsWith("Diagnostyka zasad grupy – contoso.test_", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            Assert.EndsWith(".csv", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(csv, TestContext.Current.CancellationToken);
            Assert.StartsWith("Ważność;Obszar;Uwaga;Obiekt zasad grupy;Kontener lub ustawienie", lines[0], StringComparison.Ordinal);
            Assert.Equal(document.Rows.Count + 1, lines.Length);
            Assert.Contains(lines, l => l.Contains("\"<script>alert('x')</script> & \"\"Żółw\"\"\"", StringComparison.Ordinal));

            h.B2.Files.NextSavePath = html;
            await document.ExportHtmlCommand.ExecuteAsync(null);
            Assert.EndsWith(".html", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var page = await File.ReadAllTextAsync(html, TestContext.Current.CancellationToken);
            Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", page, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;Żółw&quot;", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", page, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<h1>Diagnostyka zasad grupy – contoso.test</h1>", page, StringComparison.Ordinal);
            Assert.Contains("Wersje w AD i SYSVOL różnią się", page, StringComparison.Ordinal);
            Assert.Contains(h.B2.Notifications.Items, m => m.Message.Contains(Path.GetFileName(html), StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(csv);
            File.Delete(html);
        }
    }

    [Fact]
    public async Task ChangesElsewhere_MarkTheResultsStale_UntilCheckedAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        var rows = document.Rows;
        var reads = h.Sysvol.Calls.Count(c => c.Operation != "Open");

        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading);

        Assert.Same(rows, document.Rows);
        Assert.Equal(reads, h.Sysvol.Calls.Count(c => c.Operation != "Open"));
        await document.CheckAgainCommand.ExecuteAsync(null);
        Assert.False(document.IsStale);
        Assert.NotSame(rows, document.Rows);
    }

    [Fact]
    public async Task WithoutResults_AReload_RunsAQuickCheck_AndAFirstCheckCancelledLeavesNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var document = (GroupPolicyDiagnosticsDocumentViewModel)h.Factory.Create(GroupPolicyDiagnosticsEntry.Request);
        using (document)
        {
            h.Sysvol.BeforeCall = (operation, _, _) =>
            {
                if (operation == "Read")
                {
                    document.Cancel();
                }
            };
            await document.LoadAsync();
            h.Sysvol.BeforeCall = null;

            Assert.Null(document.LastDepth);
            Assert.Empty(document.Rows);
            Assert.Null(document.ListNote);
            Assert.Equal("Sprawdzanie anulowano.", document.StatusText);
            Assert.False(document.ExportCsvCommand.CanExecute(null));

            // Nothing shown: the next reload (here a change of Group Policy) checks quickly again instead of marking stale.
            h.Services.NotifyChanged();
            await B2Harness.WaitUntilAsync(() => document.LastDepth == GpoDiagnosticsDepth.Quick && !document.IsLoading);
            Assert.False(document.IsStale);
            Assert.NotEmpty(document.Rows);
        }
    }

    [Fact]
    public async Task LanguageSwitch_RebuildsTheTexts_WithoutReadingAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        var calls = h.Sysvol.Calls.Count(c => c.Operation != "Open");

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => document.Title == "Group Policy diagnostics");

        var mismatch = document.Rows.Single(r => r.Code == GpoFindingCodes.VersionMismatch);
        Assert.Equal("The version in Active Directory (user 2, computer 0) differs from the version in GPT.INI (user 1, computer 0).", mismatch.Message);
        Assert.Equal("Fix: align the versions", mismatch.FixText);
        Assert.Equal("Any severity", document.SeverityOptions[0].Text);
        Assert.Equal(calls, h.Sysvol.Calls.Count(c => c.Operation != "Open"));
    }

    [Fact]
    public async Task LanguageSwitch_KeepsTheStaleMark_OfResultsNotCheckedAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await OpenAsync(h);
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading);

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => document.Title == "Group Policy diagnostics");

        Assert.True(document.IsStale);
        Assert.StartsWith("Last check: quick, ", document.LastRunText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASupersededCheckArrivingLate_LeavesTheRunningCheckItsProgressAndCancel()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var logon = new HeldSysvolLogon(h.Sysvol);
        var services = new GroupPolicyServices(h.Ui, h.B2.Engines, h.Planner, h.Windows, logon);
        using var document = new GroupPolicyDiagnosticsDocumentViewModel(services, new GroupPolicyActions(services));
        var reading = h.Sysvol.HoldNext("Read");

        try
        {
            var first = document.CheckAsync(GpoDiagnosticsDepth.Quick); // its SYSVOL logon is slow
            var second = document.CheckAsync(GpoDiagnosticsDepth.Full); // supersedes it and starts reading
            await reading.Reached.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The first logon ends now, its load cancelled: the running check keeps its progress and "Anuluj".
            logon.Release();
            await first;
            Assert.True(document.IsLoading);
            Assert.True(document.CancelCommand.CanExecute(null));
            Assert.NotEqual(string.Empty, document.ProgressText);

            reading.Release();
            await second;
            Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
            Assert.Equal(string.Empty, document.ProgressText);
        }
        finally
        {
            reading.Release();
            logon.Release();
        }
    }

    [Fact]
    public void AnUnknownSidWithOnlyInheritedEntries_IsNotSentToFix_ButToTheParentObject()
    {
        var localizer = new ResxLocalizer(Polish);
        var snapshot = new GpoDiagnosticsSnapshot
        {
            Depth = GpoDiagnosticsDepth.Full,
            DomainDn = "DC=contoso,DC=test",
            DomainDnsName = "contoso.test",
            Gpos = [],
            Containers = [],
        };
        var rows = new GpoFindingRows(localizer, snapshot, new Dictionary<string, string>());
        var definition = GpoFindingCodes.All.Single(d => d.Code == GpoFindingCodes.OrphanedTrustee);
        var gpo = Guid.NewGuid();
        const string Sid = "S-1-5-21-1004336348-1177238915-682003330-4321";
        GpoFinding Finding(GpoQuickFix? fix) => new(definition.Code, definition.Severity, definition.Area, gpo, null, null, [Sid], fix);

        var inherited = rows.Row(Finding(null));
        var explicitEntries = rows.Row(Finding(new GpoQuickFix.RemoveTrustee(gpo, Sid)));

        Assert.False(inherited.HasFix);
        Assert.Equal("Brak automatycznej naprawy – postępuj zgodnie z zaleceniem.", inherited.FixText);
        Assert.Contains("dziedziczone", inherited.Recommendation, StringComparison.Ordinal);
        Assert.Contains("CN=Policies,CN=System", inherited.Recommendation, StringComparison.Ordinal);
        Assert.DoesNotContain("Napraw", inherited.Recommendation, StringComparison.Ordinal);
        Assert.True(explicitEntries.HasFix);
        Assert.Contains("„Napraw…”", explicitEntries.Recommendation, StringComparison.Ordinal);
    }

    /// <summary>A SYSVOL writer whose first open waits for <see cref="Release"/> and then ignores the cancellation, like the SMB logon of alternate credentials.</summary>
    private sealed class HeldSysvolLogon(ISysvolFileWriter inner) : ISysvolFileWriter
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public void Release() => _released.TrySetResult();

        public async Task<ISysvolWriteConnection> OpenForWriteAsync(IDirectorySession session, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _held, 1) == 0)
            {
                await _released.Task.ConfigureAwait(false);
            }

            return await inner.OpenForWriteAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task EntryPoints_OpenOneTabPerWindow_FromTheShellThePaletteTheGpoListAndF5ChecksAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        var descriptor = Assert.Single(shell.CommandDescriptors, d => d.Id == "gpo.diagnose");
        Assert.Equal("Diagnostyka zasad grupy…", h.B2.Localizer[descriptor.TitleKey]);
        Assert.True(descriptor.Command.CanExecute(null));
        descriptor.Command.Execute(null);
        var document = Assert.IsType<GroupPolicyDiagnosticsDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => document.LastDepth is not null && !document.IsLoading);

        // The GPO list's toolbar and the actions select the same tab.
        var objects = shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));
        Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.SelectedTab);
        ((GroupPolicyObjectsDocumentViewModel)objects!).DiagnoseCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(shell.SelectedTab, document));
        shell.OpenGroupPolicyDiagnosticsCommand.Execute(null);
        Assert.Same(document, shell.SelectedTab);
        Assert.Single(shell.Documents.OfType<GroupPolicyDiagnosticsDocumentViewModel>());

        // F5 checks again at the depth shown.
        await document.FullCheckCommand.ExecuteAsync(null);
        var shown = document.Rows;
        await shell.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.NotSame(shown, document.Rows);
    }
}
