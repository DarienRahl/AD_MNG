using System.Resources;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.GroupPolicy.Copy;

/// <summary>
/// Copy, remove, merge, unlink-all and the guarded delete of K7 (docs/dev/k-computers-and-gpo.md 21.5) end to end on the
/// fake directory and SYSVOL: the "Kopiuj ustawienia" window, the change plan preview with "Zmiany ustawień", execution,
/// undo, the refusals and the "nothing would change" dialog (never RunAsync's toast).
/// </summary>
public sealed class GpoSettingsActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Copy_PicksTheTarget_ShowsTheWindowAndThePreviewWithSettingChanges_AndWritesTheTarget()
    {
        var k = await K7Harness.CreateAsync();
        var items = await k.ItemsAsync(k.Cleanup.Templates);
        var item = Assert.Single(items);
        IReadOnlyList<GroupPolicySettingChange> changes = [];
        k.H.Windows.PickGpo = "Porządki – Pulpit zdalny";
        k.H.Windows.BeforePreview = preview =>
        {
            changes = preview.SettingChanges;
            return Task.CompletedTask;
        };

        var copied = await k.H.Actions.CopySettingsAsync(items, definitions: (await k.IndexAsync()).Index.Definitions);

        Assert.True(copied);
        var picker = Assert.Single(k.H.Windows.Pickers);
        Assert.DoesNotContain(picker.Items, i => i.Gpo.Id == k.Cleanup.Templates);
        var window = Assert.Single(k.H.Windows.CopyWindows);
        Assert.Equal("Kopiuj ustawienia do Porządki – Pulpit zdalny", window.WindowTitle);
        Assert.Equal(GpoCopyConflictMode.Replace, window.SelectedConflict!.Mode);
        Assert.Equal("Zaznaczono: 1 z 1", window.CountText);
        Assert.Equal("Wyłącz okienko podglądu", window.Items[0].Name);
        Assert.False(window.HasRefused);

        var preview = Assert.Single(k.H.Windows.Previews);
        Assert.Equal("Kopiowanie ustawień do Porządki – Pulpit zdalny", preview.Title);
        Assert.Equal(0, preview.Result!.FailedCount);
        var change = Assert.Single(changes);
        Assert.Equal("Wyłącz okienko podglądu", change.Setting);
        Assert.Equal("Nie skonfigurowano", change.Before);
        Assert.NotEqual(change.Before, change.After);
        Assert.StartsWith("Porządki – Pulpit zdalny › Konfiguracja komputera", change.Group, StringComparison.Ordinal);
        Assert.NotNull(k.H.Sysvol.Get(k.Cleanup.RemoteDesktop, "Machine/Registry.pol"));
        Assert.Contains(GpoExtensionNames.Registry.ToString("B"), k.H.Value(k.H.GpoDn(k.Cleanup.RemoteDesktop), "gPCMachineExtensionNames") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(item.Unit.GpoId.ToString(), item.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Copy_Undo_RestoresTheTarget()
    {
        var k = await K7Harness.CreateAsync();
        var items = await k.ItemsAsync(k.Cleanup.RemoteDesktop);
        var target = await k.GpoAsync(k.Cleanup.Audit);
        var before = k.H.Value(k.H.GpoDn(k.Cleanup.Audit), "versionNumber");
        k.H.Windows.UndoAfterExecute = true;

        var copied = await k.H.Actions.CopySettingsAsync(items, target);

        Assert.True(copied);
        var preview = Assert.Single(k.H.Windows.Previews);
        Assert.Equal(0, preview.Result!.FailedCount);
        Assert.Null(k.H.Sysvol.Get(k.Cleanup.Audit, SecurityTemplate.RelativePath));
        Assert.NotEqual(before, k.H.Value(k.H.GpoDn(k.Cleanup.Audit), "versionNumber")); // undo bumps the version again
    }

    [Fact]
    public async Task Copy_OfTheSameValue_ShowsWhyNothingWouldChange_InsteadOfAPreviewOrToast()
    {
        var k = await K7Harness.CreateAsync();
        var items = await k.ItemsAsync(k.Cleanup.Templates);
        var target = await k.GpoAsync(k.Cleanup.TemplatesBranch);

        var copied = await k.H.Actions.CopySettingsAsync(items, target, (await k.IndexAsync()).Index.Definitions);

        Assert.False(copied);
        Assert.Empty(k.H.Windows.Previews);
        Assert.Empty(k.H.B2.Notifications.Items);
        var error = Assert.Single(k.H.B2.Dialogs.Errors);
        Assert.Equal("Kopiowanie ustawień do Porządki – Szablony (oddział)", error.Title);
        Assert.StartsWith("Nic nie zmieniłoby się w Porządki – Szablony (oddział):", error.Message, StringComparison.Ordinal);
        Assert.Contains("Wyłącz okienko podglądu (Porządki – Szablony) – docelowy obiekt GPO ma już tę samą wartość", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_ASecret_IsShownRefusedWithTheReason_AndCannotBeConfirmed()
    {
        var k = await K7Harness.CreateAsync(arrange: h => h.PutGppPassword(h.Cleanup.RemoteDesktop));
        var items = await k.ItemsAsync(k.Cleanup.RemoteDesktop);
        var secret = Assert.Single(items, i => i.Refusal == GpoUnitRefusal.Secret);
        var target = await k.GpoAsync(k.Cleanup.Audit);
        k.H.Windows.CopySettingsHandler = window =>
        {
            var row = window.Items.Single(i => i.Item == secret);
            Assert.False(row.CanCopy);
            Assert.False(row.IsChecked);
            row.IsChecked = true;
            Assert.False(row.IsChecked);
            Assert.Contains("MS14-025", row.RefusalText, StringComparison.Ordinal);
            Assert.Equal("Ustawień, których nie można skopiować: 1 – powód jest podany przy każdym z nich.", window.RefusedText);
            window.CheckNoneCommand.Execute(null);
            Assert.False(window.OkCommand.CanExecute(null));
            return true;
        };

        Assert.False(await k.H.Actions.CopySettingsAsync([secret], target));
        Assert.Single(k.H.Windows.CopyWindows);
        Assert.Empty(k.H.Windows.Previews);
    }

    [Fact]
    public async Task NothingText_NamesTheMs14025Refusal_AndListsThePlansIssues()
    {
        var gpo = Guid.Parse("4f1c2b3a-5d6e-4f70-8192-a3b4c5d6e7f8");
        var selection = new GpoUnitSelection(gpo, "I|Computer|Groups|User|serwis_łódź");
        var item = new GpoCopyItem(selection, "Porządki – Pulpit zdalny", "serwis_łódź", "Konfiguracja komputera", null, null);
        var plan = new ChangePlan
        {
            Objects = [new ObjectPlan
            {
                DistinguishedName = "CN={0}",
                DisplayName = "Porządki – Inspekcja",
                Issues = [new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoSettingSecret, "secret") { Arguments = ["Porządki – Pulpit zdalny", "serwis_łódź"] }],
            }],
        };
        var planned = new GpoSettingsPlan(plan, [new GpoUnitOutcome(selection, GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingSecret)]);
        var services = (await GroupPolicyHarness.CreateAsync(K7Harness.Polish)).Services;

        var text = GpoSettingsActions.NothingText(services, planned, [item], plan, "Nic nie zmieniłoby się w Porządki – Inspekcja:");

        Assert.Contains("serwis_łódź (Porządki – Pulpit zdalny) – Zawiera wartość tajną", text, StringComparison.Ordinal);
        Assert.Contains("serwis_łódź w Porządki – Pulpit zdalny zawiera wartość tajną", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Window_RefusesMoreThanTenSourceGpos_AndOffersTheConflictModes()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        var items = Enumerable.Range(1, 11).Select(i => new GpoCopyItem(new GpoUnitSelection(Guid.NewGuid(), "K|Computer|x" + i), "Źródło " + i, "Ustawienie " + i, null, "1", null)).ToList();
        using var window = new GpoCopySettingsViewModel(k.H.Ui, items, "Porządki – Inspekcja");

        Assert.Equal(["Zastąp", "Pomiń", "Połącz listy"], window.ConflictOptions.Select(o => o.Text));
        Assert.Equal("Zaznaczono ustawienia z 11 obiektów GPO; jedno kopiowanie obejmuje najwyżej 10 źródłowych obiektów GPO.", window.TooManySourcesText);
        Assert.False(window.OkCommand.CanExecute(null));
        window.Items[0].IsChecked = false;
        Assert.False(window.HasTooManySources);
        Assert.True(window.OkCommand.CanExecute(null));
        window.SelectedConflict = window.ConflictOptions[2];
        window.OkCommand.Execute(null);
        Assert.Equal(GpoCopyConflictMode.Union, window.Conflict);
        Assert.Equal(10, window.SelectedItems.Count);
        Assert.Contains("najwyżej 10", k.H.B2.Localizer.Format(L.GpoCopy.TooManySources, 11, 10), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_TakesTheDuplicateOutOfOneGpo_AndUndoPutsItBack()
    {
        var k = await K7Harness.CreateAsync();
        var items = await k.ItemsAsync(k.Cleanup.TemplatesBranch);
        var gpo = await k.GpoAsync(k.Cleanup.TemplatesBranch);
        var original = k.H.Sysvol.Get(k.Cleanup.TemplatesBranch, "Machine/Registry.pol");

        Assert.True(await k.H.Actions.RemoveSettingsAsync(gpo, items, (await k.IndexAsync()).Index.Definitions));

        var preview = Assert.Single(k.H.Windows.Previews);
        Assert.Equal("Usuwanie ustawień z Porządki – Szablony (oddział)", preview.Title);
        Assert.NotEqual(original, k.H.Sysvol.Get(k.Cleanup.TemplatesBranch, "Machine/Registry.pol"));

        // Undo of the same plan restores the file.
        k.H.Windows.UndoAfterExecute = true;
        var again = await k.ItemsAsync(k.Cleanup.Templates);
        Assert.True(await k.H.Actions.RemoveSettingsAsync(await k.GpoAsync(k.Cleanup.Templates), again, (await k.IndexAsync()).Index.Definitions));
        Assert.Equal(original, k.H.Sysvol.Get(k.Cleanup.Templates, "Machine/Registry.pol"));
    }

    [Fact]
    public async Task Merge_ShowsTheDerivedMode_RelinksAndDisablesTheSource()
    {
        var k = await K7Harness.CreateAsync();
        var source = await k.GpoAsync(k.Cleanup.RemoteDesktop);
        var target = await k.GpoAsync(k.Cleanup.Audit);
        var items = await k.ItemsAsync(k.Cleanup.RemoteDesktop);
        GpoCopySettingsViewModel? shown = null;
        k.H.Windows.CopySettingsHandler = window =>
        {
            shown = window;
            return true;
        };

        Assert.True(await k.H.Actions.MergeAsync(source, target, items));

        Assert.NotNull(shown);
        Assert.True(shown.IsMerge);
        Assert.Equal("Scal Porządki – Pulpit zdalny z Porządki – Inspekcja", shown.WindowTitle);
        Assert.Equal("Porządki – Inspekcja ma wyższe pierwszeństwo połączenia wszędzie tam, gdzie oba są połączone: jego wartości zostają.", shown.OrderText);
        Assert.Null(shown.SelectedConflict!.Mode);
        Assert.Equal("Według kolejności połączeń: Pomiń", shown.SelectedConflict.Text);
        Assert.Empty(shown.Warnings);
        Assert.False(shown.HasBlocking);
        Assert.Equal("Scal", shown.OkText);
        Assert.Null(shown.Conflict);

        var preview = Assert.Single(k.H.Windows.Previews);
        Assert.Equal("Scalanie Porządki – Pulpit zdalny z Porządki – Inspekcja", preview.Title);
        Assert.Equal(0, preview.Result!.FailedCount);
        Assert.Equal("3", k.H.Value(k.H.GpoDn(k.Cleanup.RemoteDesktop), "flags"));
        Assert.DoesNotContain(k.Cleanup.RemoteDesktop.ToString("D"), k.H.Value(k.H.Company.ItOu, Attr.GpLink) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SeRemoteInteractiveLogonRight", System.Text.Encoding.Unicode.GetString(k.H.Sysvol.Get(k.Cleanup.Audit, SecurityTemplate.RelativePath)!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_WithAMixedOrder_NeedsTheOperatorsChoice()
    {
        var k = await K7Harness.CreateAsync();
        k.H.B2.Builder.AddGroupPolicyLink(k.H.Company.SalesOu, k.Cleanup.RemoteDesktop);
        k.H.B2.Builder.AddGroupPolicyLink(k.H.Company.SalesOu, k.Cleanup.Audit);
        var source = await k.GpoAsync(k.Cleanup.RemoteDesktop);
        var target = await k.GpoAsync(k.Cleanup.Audit);
        k.H.Windows.CopySettingsHandler = window =>
        {
            Assert.True(window.NeedsConflictChoice);
            Assert.Null(window.SelectedConflict);
            Assert.False(window.OkCommand.CanExecute(null));
            Assert.Equal(3, window.ConflictOptions.Count);
            Assert.Equal("Porządki – Pulpit zdalny jest przed Porządki – Inspekcja w jednych miejscach, a po nim w innych: wybierz, które wartości mają wygrać.",
                window.OrderText);
            return false;
        };

        Assert.False(await k.H.Actions.MergeAsync(source, target, await k.ItemsAsync(k.Cleanup.RemoteDesktop)));
        Assert.Single(k.H.Windows.CopyWindows);
        Assert.Empty(k.H.Windows.Previews);
    }

    [Fact]
    public async Task UnlinkAll_RemovesEveryLinkOfTheGpo()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        var gpo = await k.GpoAsync(k.Cleanup.EmptyGpo);

        Assert.True(await k.H.Actions.UnlinkAllAsync(gpo));

        Assert.Equal("Odłączanie Porządki – Pusty GPO wszędzie", Assert.Single(k.H.Windows.Previews).Title);
        Assert.DoesNotContain(k.Cleanup.EmptyGpo.ToString("D"), k.H.Value(k.H.Company.CompanyOu, Attr.GpLink) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteChecked_IsRefusedWhenTheGpoChangedSinceTheAnalysis()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        var view = await k.IndexAsync();
        var entry = view.Index.Find(k.Cleanup.Unlinked)!;
        var expected = new GpoExpectedState(entry.Gpo.DirectoryVersion, []);
        k.H.B2.Builder.AddGroupPolicyLink(k.H.Company.SalesOu, k.Cleanup.Unlinked);

        Assert.False(await k.H.Actions.DeleteCheckedAsync(entry.Gpo, expected));

        var error = Assert.Single(k.H.B2.Dialogs.Errors);
        Assert.StartsWith("Porządki – Bez powiązań zmienił się (ustawienia lub połączenia) od analizy", error.Message, StringComparison.Ordinal);
        Assert.Empty(k.H.Windows.BackupWindows);
        Assert.True(k.H.Session.Exists(k.H.GpoDn(k.Cleanup.Unlinked)));
    }

    [Fact]
    public async Task DeleteChecked_UnchangedGpo_GoesThroughTheBackupOfferAndTheGuardedPlan()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        var view = await k.IndexAsync();
        var entry = view.Index.Find(k.Cleanup.Unlinked)!;
        k.H.Windows.BackupHandler = window =>
        {
            window.BackupFirst = false;
            return Task.FromResult(true);
        };
        k.H.B2.Dialogs.TypedConfirmAnswers.Enqueue(true);

        Assert.True(await k.H.Actions.DeleteCheckedAsync(entry.Gpo, new GpoExpectedState(entry.Gpo.DirectoryVersion, [])));

        Assert.Single(k.H.Windows.BackupWindows);
        Assert.Contains("Remove-GPO", Assert.Single(k.H.Windows.Previews).PowerShell, StringComparison.Ordinal);
        Assert.False(k.H.Session.Exists(k.H.GpoDn(k.Cleanup.Unlinked)));
    }

    [Fact]
    public async Task SettingChangesBuilder_DescribesRemovedAndChangedUnits_OfEveryWrittenFile()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        var oldTemplate = k.H.Sysvol.Get(k.Cleanup.RemoteDesktop, SecurityTemplate.RelativePath)!;
        var newTemplate = System.Text.Encoding.Unicode.GetBytes("﻿[Unicode]\r\nUnicode=yes\r\n[Privilege Rights]\r\nSeShutdownPrivilege = *S-1-5-32-544\r\n[Version]\r\nsignature=\"$CHICAGO$\"\r\nRevision=1\r\n");
        var plan = new ChangePlan
        {
            Objects = [new ObjectPlan
            {
                DistinguishedName = k.H.GpoDn(k.Cleanup.RemoteDesktop),
                DisplayName = "Porządki – Pulpit zdalny",
                Changes = [new PlannedChange
                {
                    Action = ChangeAction.WriteGpoFile,
                    Sysvol = new SysvolChange { GpoId = k.Cleanup.RemoteDesktop, RelativePath = SecurityTemplate.RelativePath, OldContent = oldTemplate, NewContent = newTemplate },
                }],
            }],
        };

        var rows = await GpoSettingChangesBuilder.BuildAsync(plan, k.H.Session, k.H.B2.Localizer, null, Ct);

        Assert.Equal(2, rows.Count);
        var removed = Assert.Single(rows, r => r.After == "Nie skonfigurowano");
        Assert.Contains("Administratorzy", removed.Before, StringComparison.Ordinal);
        var added = Assert.Single(rows, r => r.Before == "Nie skonfigurowano");
        Assert.All(rows, r => Assert.StartsWith("Porządki – Pulpit zdalny › Konfiguracja komputera › ", r.Group, StringComparison.Ordinal));
        Assert.NotEqual(removed.Setting, added.Setting);
    }

    [Fact]
    public async Task Copy_AnOpenSecuritySettingsEditorOfTheTarget_ReloadsWithoutAConflictNote()
    {
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var editor = await k.H.SecurityAsync(k.Cleanup.Audit);
        Assert.Equal("Nie zdefiniowano", editor.UserRights.Single(r => r.Constant == "SeRemoteInteractiveLogonRight").HoldersText);

        Assert.True(await k.H.Actions.CopySettingsAsync(await k.ItemsAsync(k.Cleanup.RemoteDesktop), await k.GpoAsync(k.Cleanup.Audit)));

        await Dialogs.B2Harness.WaitUntilAsync(() => !editor.IsLoading
            && editor.UserRights.Single(r => r.Constant == "SeRemoteInteractiveLogonRight").HoldersText != "Nie zdefiniowano");
        Assert.Null(editor.ConflictNote);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task UnavailableGroupPolicyDialogs_AnswersTheCopyWindowAsCancelled()
    {
        var type = typeof(Presentation.GroupPolicy.GroupPolicyServiceCollectionExtensions).GetNestedType("UnavailableGroupPolicyDialogs", System.Reflection.BindingFlags.NonPublic)!;
        var dialogs = (Presentation.GroupPolicy.IGroupPolicyDialogs)Activator.CreateInstance(type, nonPublic: true)!;
        var k = await K7Harness.CreateAsync(centralStore: false);
        using var window = new GpoCopySettingsViewModel(k.H.Ui, await k.ItemsAsync(k.Cleanup.RemoteDesktop), "Porządki – Inspekcja");

        Assert.False(await dialogs.ShowCopySettingsAsync(window));
        Assert.False(window.Confirmed);
    }

    [Fact]
    public void Localization_TheCopyOutcomeFamilyExistsInBothLanguages()
    {
        var resources = new ResourceManager(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);
        var en = resources.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false)!;
        var pl = resources.GetResourceSet(System.Globalization.CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = L.GpoCopy.AllKeys().ToList();

        Assert.Equal(Enum.GetValues<GpoUnitOutcomeKind>().Length, keys.Count);
        Assert.Equal([], keys.Where(key => en.GetString(key) is null));
        Assert.Equal([], keys.Where(key => pl.GetString(key) is null));
    }

    [Fact]
    public void ToString_OfTheNewTypes_CarriesNoDirectoryValues()
    {
        var info = new GpoMergeInfo("Dyski (Księgowość)", "Dyski (Kadry)", GpoCopyConflictMode.Skip, true, false, ["Tylko Dyski (Kadry) jest połączony z: Kadry"]);

        Assert.DoesNotContain("Księgowość", info.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Kadry", info.ToString(), StringComparison.Ordinal);
    }
}
