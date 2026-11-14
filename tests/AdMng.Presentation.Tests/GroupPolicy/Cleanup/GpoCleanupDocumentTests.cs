using System.Resources;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy.Copy;

namespace AdMng.Presentation.Tests.GroupPolicy.Cleanup;

/// <summary>
/// "Porządki w GPO" (K7, docs/dev/k-computers-and-gpo.md 21.6) on the cleanup sample: the proposals with their texts,
/// chips, filters and notes; every action kind through the change plan (scripted windows); the refresh after an action;
/// a foreign change marks the analysis stale; the entry points (palette <c>gpo.cleanup</c>, the GPO list, one tab).
/// </summary>
public sealed class GpoCleanupDocumentTests
{
    private static GpoCleanupRowViewModel Row(GpoCleanupDocumentViewModel document, GpoCleanupKind kind, Guid gpo) =>
        Assert.Single(document.Rows, r => r.Kind == kind && r.GpoId == gpo);

    [Fact]
    public async Task Proposals_OfTheSample_WithTextsChipsNotesAndActions()
    {
        var k = await K7Harness.CreateAsync();
        using var document = await k.CleanupAsync();

        Assert.Equal("Porządki w GPO", document.Title);
        Assert.Null(document.Error);
        Assert.True(document.HasResult);
        Assert.False(document.IsStale);
        Assert.Empty(document.Result!.NotEvaluated);

        var empty = Row(document, GpoCleanupKind.EmptyGpo, k.Cleanup.EmptyGpo);
        Assert.Equal("Pusty obiekt GPO", empty.Title);
        Assert.Equal("Porządki – Pusty GPO nie zawiera żadnych ustawień.", empty.Message);
        Assert.Equal("Połączony z: Firma", empty.LinksText);
        Assert.Equal("Usuń GPO…, Odłącz wszędzie…", empty.ActionsText);
        Assert.Equal(["Obiekt GPO jest połączony: usunięcie usunie też jego połączenia."], empty.NoteTexts);

        var duplicate = Row(document, GpoCleanupKind.DuplicateSetting, k.Cleanup.TemplatesBranch);
        Assert.Equal("Porządki – Szablony", duplicate.RelatedText);
        Assert.Equal("Wyłącz okienko podglądu", duplicate.SettingName);
        Assert.StartsWith("Konfiguracja komputera", duplicate.SettingPath, StringComparison.Ordinal);
        Assert.Equal("Usuń duplikat z Porządki – Szablony (oddział) – wartość nadal ustawia Porządki – Szablony.", duplicate.Recommendation);

        var merge = Row(document, GpoCleanupKind.MergeCandidate, k.Cleanup.RemoteDesktop);
        Assert.Equal("Porządki – Inspekcja", merge.RelatedText);
        Assert.Equal("Scal z…", merge.ActionsText);

        var single = Row(document, GpoCleanupKind.SingleSettingGpo, k.Cleanup.RemoteDesktop);
        Assert.Equal("Kopiuj do innego GPO…, Wyłącz GPO, Scal z…", single.ActionsText);
        Assert.NotNull(single.SettingName);

        // Proposals sorted by kind; one chip per kind with its count.
        Assert.Equal(document.Rows.Select(r => r.Kind).Order(), document.Rows.Select(r => r.Kind));
        Assert.Equal(Enum.GetValues<GpoCleanupKind>().Length, document.Chips.Count);
        var chip = document.Chips.Single(c => c.Kind == GpoCleanupKind.DuplicateSetting);
        Assert.Equal("Zduplikowane ustawienie (1)", chip.Text);

        // Without the definition set the note says so; with it, none.
        Assert.DoesNotContain(document.Notes, n => n.Contains("Szablony administracyjne nie są wczytane", StringComparison.Ordinal));
        Assert.Contains("propozyc", document.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Porządki", document.Result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Porządki", empty.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selection_ShowsOnlyTheOfferedActions_AndAnInformationalProposalOffersNone()
    {
        // The branch GPO turns the policy off: the two values differ (an informational proposal, no action).
        var k = await K7Harness.CreateAsync(arrange: h => h.H.Sysvol.Put(h.Cleanup.TemplatesBranch, "Machine/Registry.pol", RegistryPolFile.Write(
            [new RegistryPolEntry(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(0))])));
        using var document = await k.CleanupAsync();
        Assert.DoesNotContain(document.Rows, r => r.Kind == GpoCleanupKind.DuplicateSetting);
        var different = Assert.Single(document.Rows, r => r.Kind == GpoCleanupKind.DifferentValues
            && (r.GpoId == k.Cleanup.TemplatesBranch || r.Proposal.RelatedGpoIds.Contains(k.Cleanup.TemplatesBranch)));
        Assert.Equal("Różne wartości jednego ustawienia", different.Title);
        Assert.Empty(different.Proposal.Actions);
        Assert.Equal(string.Empty, different.ActionsText);
        document.SelectedRow = different;
        Assert.True(document.ShowsNoActions);
        Assert.False(document.ShowsDelete || document.ShowsUnlink || document.ShowsDisable || document.ShowsRemoveSetting || document.ShowsCopyTo || document.ShowsMerge);
        Assert.True(document.CompareCommand.CanExecute(null));
        Assert.True(document.CopyCommand.CanExecute(null));

        document.SelectedRow = Row(document, GpoCleanupKind.EmptyGpo, k.Cleanup.EmptyGpo);
        Assert.True(document.ShowsDelete);
        Assert.True(document.ShowsUnlink);
        Assert.False(document.ShowsDisable);
        Assert.False(document.ShowsMerge);
        Assert.False(document.ShowsNoActions);
        Assert.True(document.DeleteCommand.CanExecute(null));
        Assert.False(document.MergeCommand.CanExecute(null));
        Assert.False(document.ShowInSearchCommand.CanExecute(null));
        Assert.Equal("Pusty obiekt GPO – Porządki – Pusty GPO", document.SelectionText);

        document.SelectedRow = Row(document, GpoCleanupKind.SingleSettingGpo, k.Cleanup.RemoteDesktop);
        Assert.True(document.ShowsCopyTo);
        Assert.True(document.ShowsDisable);
        Assert.True(document.ShowsMerge);
        Assert.True(document.ShowInSearchCommand.CanExecute(null));
        Assert.True(document.CompareCommand.CanExecute(null));

        // Filters: a chip, the GPO, the text (diacritics ignored); "Wyczyść filtry".
        var all = document.Rows.Count;
        document.Chips.Single(c => c.Kind == GpoCleanupKind.EmptyGpo).IsSelected = true;
        Assert.All(document.Rows, r => Assert.Equal(GpoCleanupKind.EmptyGpo, r.Kind));
        Assert.True(document.IsFiltered);
        document.ClearFiltersCommand.Execute(null);
        Assert.Equal(all, document.Rows.Count);
        document.SearchText = "pulpit zdalny";
        Assert.All(document.Rows, r => Assert.Contains("Pulpit zdalny", r.SearchText, StringComparison.Ordinal));
        document.SearchText = "Inspekcja";
        Assert.Contains(document.Rows, r => r.Kind == GpoCleanupKind.MergeCandidate);
        document.ClearFiltersCommand.Execute(null);
        document.SelectedGpo = document.GpoOptions.Single(o => o.Value == k.Cleanup.Disabled);
        Assert.Equal([GpoCleanupKind.AllLinksDisabled, GpoCleanupKind.GpoDisabled, GpoCleanupKind.SingleSettingGpo], document.Rows.Select(r => r.Kind));
        Assert.Equal("Pokazano propozycji: 3 z " + all, document.StatusText);
    }

    [Fact]
    public async Task RemoveDuplicate_RunsThroughThePlan_ThenTheDocumentReadsTheGposAgain()
    {
        var k = await K7Harness.CreateAsync();
        using var document = await k.CleanupAsync();
        document.SelectedRow = Row(document, GpoCleanupKind.DuplicateSetting, k.Cleanup.TemplatesBranch);
        var original = k.H.Sysvol.Get(k.Cleanup.TemplatesBranch, "Machine/Registry.pol");
        k.H.Sysvol.ClearCalls();

        await document.RemoveSettingCommand.ExecuteAsync(null);

        var preview = Assert.Single(k.H.Windows.Previews);
        Assert.Equal("Usuwanie ustawień z Porządki – Szablony (oddział)", preview.Title);
        Assert.Equal(0, preview.Result!.FailedCount);
        Assert.NotEqual(original, k.H.Sysvol.Get(k.Cleanup.TemplatesBranch, "Machine/Registry.pol"));
        Assert.DoesNotContain(document.Rows, r => r.Kind == GpoCleanupKind.DuplicateSetting);
        Assert.False(document.IsStale);

        // Incremental refresh: only the changed GPO's folder was listed again.
        var lists = k.H.Sysvol.Calls.Where(c => c.Operation == "List").Select(c => c.GpoId).Distinct().ToList();
        Assert.Contains(k.Cleanup.TemplatesBranch, lists);
        Assert.DoesNotContain(k.Cleanup.Audit, lists);
    }

    [Fact]
    public async Task Unlink_Disable_AndDelete_EachRunThroughThePlan()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var document = await k.CleanupAsync();

        document.SelectedRow = Row(document, GpoCleanupKind.EmptyGpo, k.Cleanup.EmptyGpo);
        await document.UnlinkCommand.ExecuteAsync(null);
        Assert.Equal("Odłączanie Porządki – Pusty GPO wszędzie", k.H.Windows.Previews[^1].Title);
        Assert.DoesNotContain(k.Cleanup.EmptyGpo.ToString("D"), k.H.Value(k.H.Company.CompanyOu, Attr.GpLink) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        document.SelectedRow = Row(document, GpoCleanupKind.UnlinkedGpo, k.Cleanup.Unlinked);
        await document.DisableCommand.ExecuteAsync(null);
        Assert.Equal("3", k.H.Value(k.H.GpoDn(k.Cleanup.Unlinked), "flags"));
        Assert.Contains(document.Rows, r => r.Kind == GpoCleanupKind.GpoDisabled && r.GpoId == k.Cleanup.Unlinked);

        // The empty GPO is now unlinked too: delete it (backup offer declined to write, typed confirmation).
        k.H.Windows.BackupHandler = window =>
        {
            window.BackupFirst = false;
            return Task.FromResult(true);
        };
        k.H.B2.Dialogs.TypedConfirmAnswers.Enqueue(true);
        document.SelectedRow = Row(document, GpoCleanupKind.EmptyGpo, k.Cleanup.EmptyGpo);
        await document.DeleteCommand.ExecuteAsync(null);
        Assert.False(k.H.Session.Exists(k.H.GpoDn(k.Cleanup.EmptyGpo)));
        Assert.DoesNotContain(document.Rows, r => r.GpoId == k.Cleanup.EmptyGpo);
        Assert.Empty(k.H.B2.Dialogs.Errors);
    }

    [Fact]
    public async Task Delete_OfAGpoChangedElsewhere_IsRefused_AndTheAnalysisIsReadAgain()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var document = await k.CleanupAsync();
        document.SelectedRow = Row(document, GpoCleanupKind.UnlinkedGpo, k.Cleanup.Unlinked);

        // Another administrator links the GPO after the analysis.
        k.H.B2.Builder.AddGroupPolicyLink(k.H.Company.SalesOu, k.Cleanup.Unlinked);
        await document.DeleteCommand.ExecuteAsync(null);

        Assert.StartsWith("Porządki – Bez powiązań zmienił się", Assert.Single(k.H.B2.Dialogs.Errors).Message, StringComparison.Ordinal);
        Assert.Empty(k.H.Windows.Previews);
        Assert.True(k.H.Session.Exists(k.H.GpoDn(k.Cleanup.Unlinked)));
        Assert.DoesNotContain(document.Rows, r => r.Kind == GpoCleanupKind.UnlinkedGpo && r.GpoId == k.Cleanup.Unlinked);
    }

    [Fact]
    public async Task CopyTo_AndMerge_OfTheSingleSettingGpo()
    {
        var k = await K7Harness.CreateAsync();
        using var document = await k.CleanupAsync();
        document.SelectedRow = Row(document, GpoCleanupKind.SingleSettingGpo, k.Cleanup.RemoteDesktop);
        k.H.Windows.PickGpo = "Porządki – Pusty GPO";

        await document.CopyToCommand.ExecuteAsync(null);

        Assert.Equal("Kopiowanie ustawień do Porządki – Pusty GPO", k.H.Windows.Previews[^1].Title);
        Assert.NotNull(k.H.Sysvol.Get(k.Cleanup.EmptyGpo, SecurityTemplate.RelativePath));
        Assert.DoesNotContain(document.Rows, r => r.Kind == GpoCleanupKind.EmptyGpo && r.GpoId == k.Cleanup.EmptyGpo);

        document.SelectedRow = Row(document, GpoCleanupKind.MergeCandidate, k.Cleanup.RemoteDesktop);
        await document.MergeCommand.ExecuteAsync(null);

        var window = k.H.Windows.CopyWindows[^1];
        Assert.True(window.IsMerge);
        Assert.Equal("Scalanie Porządki – Pulpit zdalny z Porządki – Inspekcja", k.H.Windows.Previews[^1].Title);
        Assert.Equal("3", k.H.Value(k.H.GpoDn(k.Cleanup.RemoteDesktop), "flags"));
        Assert.Contains(document.Rows, r => r.Kind == GpoCleanupKind.GpoDisabled && r.GpoId == k.Cleanup.RemoteDesktop);
        Assert.DoesNotContain(document.Rows, r => r.Kind == GpoCleanupKind.MergeCandidate);
    }

    [Fact]
    public async Task AStaleAnalysis_DisablesTheContentActions_UntilItIsReadAgain()
    {
        // The keeper of a duplicate may have lost the setting since the analysis: "Usuń duplikat" must not run on old rows.
        var k = await K7Harness.CreateAsync();
        using var document = await k.CleanupAsync();
        document.SelectedRow = Row(document, GpoCleanupKind.DuplicateSetting, k.Cleanup.TemplatesBranch);
        Assert.True(document.RemoveSettingCommand.CanExecute(null));

        k.H.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading);

        Assert.False(document.RemoveSettingCommand.CanExecute(null));
        document.SelectedRow = Row(document, GpoCleanupKind.SingleSettingGpo, k.Cleanup.RemoteDesktop);
        Assert.False(document.CopyToCommand.CanExecute(null));
        Assert.False(document.DisableCommand.CanExecute(null));
        document.SelectedRow = Row(document, GpoCleanupKind.MergeCandidate, k.Cleanup.RemoteDesktop);
        Assert.False(document.MergeCommand.CanExecute(null));
        document.SelectedRow = Row(document, GpoCleanupKind.UnlinkedGpo, k.Cleanup.Unlinked);
        Assert.True(document.DeleteCommand.CanExecute(null)); // compares the GPO with the analysis itself

        await ((Presentation.Shell.IShellDocument)document).RefreshAsync();
        Assert.False(document.IsStale);
        document.SelectedRow = Row(document, GpoCleanupKind.DuplicateSetting, k.Cleanup.TemplatesBranch);
        Assert.True(document.RemoveSettingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AChangeElsewhere_MarksTheAnalysisStale_AndF5ReadsAgain()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var document = await k.CleanupAsync();
        var shown = document.Rows;

        k.H.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale);
        Assert.Same(shown, document.Rows);

        await ((Presentation.Shell.IShellDocument)document).RefreshAsync();
        Assert.False(document.IsStale);
        Assert.NotSame(shown, document.Rows);
    }

    [Fact]
    public async Task AnotherDomainController_DropsTheProposalsOfTheOldSession_AndAnalysesAgain()
    {
        var k = await K7Harness.CreateOnDomainControllerAsync();
        using var document = await k.CleanupAsync();
        var first = document.View!;
        Assert.Equal("dc01.contoso.test", first.Index.Server);

        // 21.7: the proposals are dropped on SessionChanged – never shown (and acted on) as if they were of the new session.
        await k.H.B2.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, K7Harness.Ct);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.View is { } view && !ReferenceEquals(view, first));

        Assert.Equal("dc02.contoso.test", document.View!.Index.Server);
        Assert.Null(document.Error);
        Assert.True(document.HasResult);
        Assert.False(document.IsStale);
        Assert.Contains(document.Rows, r => r.Kind == GpoCleanupKind.EmptyGpo && r.GpoId == k.Cleanup.EmptyGpo);
    }

    [Fact]
    public async Task EntryPoints_OpenOneTab_FromThePaletteTheGpoListAndTheActions()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var shell = k.H.CreateShell();
        await shell.InitializeAsync();

        var descriptor = Assert.Single(shell.CommandDescriptors, d => d.Id == "gpo.cleanup");
        Assert.Equal("Porządki w GPO…", k.H.B2.Localizer[descriptor.TitleKey]);
        descriptor.Command.Execute(null);
        var document = Assert.IsType<GpoCleanupDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => document.HasResult && !document.IsLoading);

        var objects = (GroupPolicyObjectsDocumentViewModel)shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects))!;
        objects.CleanupCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(shell.SelectedTab, document));
        Assert.True(k.H.Actions.OpenCleanup());
        Assert.Same(document, shell.SelectedTab);
        Assert.Single(shell.Documents.OfType<GpoCleanupDocumentViewModel>());

        // The palette's "Edytuj …" commands work on the selected proposal's GPO.
        document.SelectedRow = Row(document, GpoCleanupKind.EmptyGpo, k.Cleanup.EmptyGpo);
        Assert.Equal((k.Cleanup.EmptyGpo, "Porządki – Pusty GPO"), shell.ActiveGpo());
    }

    [Fact]
    public async Task Export_WritesTheShownProposalsToTheChosenFiles()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var document = await k.CleanupAsync();
        using var folder = new TempFolder();
        var csv = Path.Combine(folder.Path, "porządki.csv");
        var html = Path.Combine(folder.Path, "porządki.html");
        k.H.B2.Files.NextSavePath = csv;
        await document.ExportCsvCommand.ExecuteAsync(null);
        k.H.B2.Files.NextSavePath = html;
        await document.ExportHtmlCommand.ExecuteAsync(null);

        var csvText = await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken);
        Assert.Contains("Pusty obiekt GPO", csvText, StringComparison.Ordinal);
        Assert.Contains("Porządki – Pusty GPO", csvText, StringComparison.Ordinal);
        var htmlText = await File.ReadAllTextAsync(html, TestContext.Current.CancellationToken);
        Assert.Contains("<title>Porządki w GPO – contoso.test</title>", htmlText, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", htmlText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CultureChange_RebuildsTheTexts_WithoutReadingTheFoldersAgain()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var document = await k.CleanupAsync();
        k.H.Sysvol.ClearCalls();

        k.H.B2.Localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => document.Rows.Any(r => r.Title == "Empty GPO") && !document.IsLoading);

        Assert.Equal("GPO cleanup", document.Title);
        Assert.DoesNotContain(k.H.Sysvol.Calls, c => c.Operation is "List" or "Read" && c.GpoId != Guid.Empty);
    }

    [Fact]
    public void Localization_TheCleanupFamiliesExistInBothLanguages()
    {
        var resources = new ResourceManager(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);
        var en = resources.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false)!;
        var pl = resources.GetResourceSet(System.Globalization.CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = L.GpoCleanup.AllKeys().ToList();

        Assert.Equal(Enum.GetValues<GpoCleanupKind>().Length * 3 + Enum.GetValues<GpoCleanupAction>().Length + Enum.GetValues<GpoCleanupNote>().Length,
            keys.Count);
        Assert.Equal([], keys.Where(key => en.GetString(key) is null));
        Assert.Equal([], keys.Where(key => pl.GetString(key) is null));
    }
}
