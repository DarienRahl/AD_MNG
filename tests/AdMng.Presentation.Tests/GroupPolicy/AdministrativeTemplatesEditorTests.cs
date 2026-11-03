using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Lists;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

public class AdministrativeTemplatesEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(GroupPolicyHarness Harness, AdministrativeTemplatesEditorViewModel Editor)> OpenAsync(CultureInfo? culture = null, bool centralStore = true)
    {
        var harness = await GroupPolicyHarness.CreateAsync(culture);
        if (centralStore)
        {
            harness.AddCentralStore();
        }

        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);
        return (harness, editor);
    }

    private static PolicyTreeNodeViewModel Node(AdministrativeTemplatesEditorViewModel editor, PolicyClass scope, params string[] path)
    {
        var node = editor.Roots.Single(r => r.Scope == scope);
        foreach (var name in path)
        {
            node = node.Children.Single(c => c.Name == name);
        }

        return node;
    }

    private static PolicyRowViewModel Select(AdministrativeTemplatesEditorViewModel editor, PolicyClass scope, string policyName, params string[] path)
    {
        editor.SelectedNode = Node(editor, scope, path);
        var row = editor.Policies.Single(p => p.Name == policyName);
        editor.SelectedPolicy = row;
        return row;
    }

    [Fact]
    public async Task Opens_WithTheCentralStore_InThePolishTree()
    {
        var (_, editor) = await OpenAsync(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Null(editor.Error);
        Assert.Equal(["Konfiguracja komputera", "Konfiguracja użytkownika"], editor.Roots.Select(r => r.Name));
        Assert.Equal(["Składniki systemu Windows", "System", "Wszystkie ustawienia", "Ustawienia rejestru spoza szablonów (0)"],
            editor.Roots[0].Children.Select(c => c.Name));
        Assert.Contains("magazyn centralny", editor.DefinitionsNote, StringComparison.Ordinal);
        Assert.False(editor.IsCentralStoreMissing);
        Assert.Empty(editor.DefinitionIssues);

        editor.SelectedNode = Node(editor, PolicyClass.Machine, "Składniki systemu Windows", "Aktualizacje Contoso");
        Assert.Equal(["Konfiguruj aktualizacje automatyczne", "Określ lokalizację usługi aktualizacji w intranecie"], editor.Policies.Select(p => p.Name));
        Assert.All(editor.Policies, p => Assert.Equal("Nie skonfigurowano", p.StateText));
        Assert.False(editor.ShowsCategoryColumn);
    }

    [Fact]
    public async Task EnablingAPolicy_ChangesTheDocument_AndSavingWritesThePlan()
    {
        var (harness, editor) = await OpenAsync();
        var row = Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        var panel = editor.Editor!;
        Assert.Equal("At least Windows 10", panel.Definition.SupportedOnText);
        Assert.False(panel.Elements.OfType<DropDownEditorViewModel>().Single().IsEditable);

        panel.IsEnabled = true;
        var mode = panel.Elements.OfType<DropDownEditorViewModel>().Single();
        Assert.True(mode.IsEditable);
        Assert.Equal(1, mode.SelectedIndex); // the presentation's default item
        mode.SelectedIndex = 2;
        panel.Elements.OfType<NumberEditorViewModel>().Single().Text = "3";

        Assert.Equal(PolicyState.Enabled, row.State);
        Assert.True(row.IsChanged);
        Assert.Equal(1, editor.ChangedCount);
        Assert.True(editor.HasChanges);
        Assert.True(editor.SaveCommand.CanExecute(null));

        await editor.SaveCommand.ExecuteAsync(null);
        await editor.LoadAsync();

        var preview = Assert.Single(harness.Windows.Previews);
        Assert.Contains("Set-GPRegistryValue", preview.PowerShell, StringComparison.Ordinal);
        var written = RegistryPolicyDocument.Parse(harness.Sysvol.Get(harness.Sample.SitePrinters, "Machine/Registry.pol"));
        var setting = PolicyEngine.Read(row.Definition.Policy, written.View);
        Assert.Equal(PolicyState.Enabled, setting.State);
        Assert.Equal(new EnumElementValue(2), setting.ValueOf("Tryb"));
        Assert.Equal(new NumberElementValue(3), setting.ValueOf("Dzien"));
        Assert.Contains("{35378EAC-683F-11D2-A89A-00C04FBBCFA2}", harness.Value(harness.GpoDn(harness.Sample.SitePrinters), "gPCMachineExtensionNames"), StringComparison.Ordinal);
        Assert.False(editor.HasChanges);
        Assert.Equal(0, editor.ChangedCount);
        Assert.Equal(PolicyState.Enabled, editor.Policies.Single(p => p.Name == "Configure automatic updates").State);
        Assert.False(editor.Policies.Single(p => p.Name == "Configure automatic updates").IsChanged);
    }

    [Fact]
    public async Task InvalidOptions_AreListed_AndLeaveTheDocumentAlone()
    {
        var (_, editor) = await OpenAsync();
        Select(editor, PolicyClass.Machine, "Specify intranet update service location", "Windows Components", "Contoso Update");
        var panel = editor.Editor!;

        panel.IsEnabled = true; // the default "https://" is valid: written
        Assert.True(editor.HasChanges);
        panel.Elements.OfType<TextEditorViewModel>().First().Text = string.Empty;

        Assert.Equal(["Update server: a value is required."], panel.Problems);
        Assert.Equal(PolicyState.Enabled, editor.SelectedPolicy!.State); // the last valid setting stays in the document
    }

    [Fact]
    public async Task Search_IgnoresCaseAndDiacritics_AndFindsRegistryValues()
    {
        var (_, editor) = await OpenAsync(CultureInfo.GetCultureInfo("pl-PL"));
        editor.SelectedNode = editor.Roots[0];

        editor.SearchText = "okreSLA";
        Assert.Equal(["Konfiguruj aktualizacje automatyczne"], editor.Policies.Select(p => p.Name)); // "Określa, czy …" in its help
        Assert.True(editor.ShowsCategoryColumn);
        Assert.Equal("Składniki systemu Windows / Aktualizacje Contoso", editor.Policies[0].CategoryPath);

        editor.SearchText = "wuserver";
        Assert.Equal(["Określ lokalizację usługi aktualizacji w intranecie"], editor.Policies.Select(p => p.Name));

        editor.SearchText = "nie ma takiego";
        Assert.Empty(editor.Policies);
        Assert.Equal("Brak pasujących ustawień.", editor.ListNote);
    }

    [Fact]
    public async Task Filters_ShowConfiguredAndChangedPoliciesOfEveryCategoryBelow()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        var existing = new RegistryPolicyDocument();
        PolicyEngine.Write(SampleAdministrativeTemplates.LoadPolish().Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Disabled, existing);
        harness.Sysvol.Put(harness.Sample.SitePrinters, "Machine/Registry.pol", existing.ToBytes());
        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);
        editor.SelectedNode = editor.Roots[0];

        editor.OnlyConfigured = true;
        Assert.Equal(["Turn off the preview pane"], editor.Policies.Select(p => p.Name));
        Assert.Equal("Disabled", editor.Policies[0].StateText);

        editor.OnlyConfigured = false;
        editor.OnlyChanged = true;
        Assert.Empty(editor.Policies);
        editor.OnlyChanged = false;
        Select(editor, PolicyClass.Machine, "Work mode", "System");
        editor.Editor!.IsDisabled = true;
        editor.SelectedNode = editor.Roots[0];
        editor.OnlyChanged = true;
        Assert.Equal(["Work mode"], editor.Policies.Select(p => p.Name));
    }

    [Fact]
    public async Task ExtraRegistrySettings_AreListed_AndCanBeDeleted()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        harness.Sysvol.Put(harness.Sample.SitePrinters, "Machine/Registry.pol", RegistryPolFile.Write(
        [
            new RegistryPolEntry(@"Software\Policies\Inny\Program", "Tryb", RegistryFormat.RegSz, RegistryPolFile.StringData("łączność")),
            new RegistryPolEntry(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(1)),
        ]));
        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);
        var extra = Node(editor, PolicyClass.Machine, "Registry settings outside the templates (1)");

        editor.SelectedNode = extra;
        var entry = Assert.Single(editor.ExtraEntries);
        Assert.Equal(@"HKLM\Software\Policies\Inny\Program", entry.KeyText);
        Assert.Equal("łączność", entry.DataText);
        Assert.True(editor.IsExtraSelected);

        editor.SelectedExtraEntry = entry;
        editor.DeleteExtraEntryCommand.Execute(null);

        Assert.Empty(editor.ExtraEntries);
        Assert.Equal("Registry settings outside the templates (0)", extra.Name);
        Assert.True(editor.HasChanges);
        Assert.Equal(1, editor.ChangedCount);
    }

    [Fact]
    public async Task WithoutACentralStore_UsesTheTemplatesOfThisComputer_OrSaysThereAreNone()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var none = await harness.TemplatesAsync(harness.Sample.SitePrinters);
        Assert.True(none.IsCentralStoreMissing);
        Assert.Contains("No ADMX templates", none.DefinitionsNote, StringComparison.Ordinal);
        Assert.Equal(["All settings", "Registry settings outside the templates (0)"], none.Roots[0].Children.Select(c => c.Name));

        harness.Local.Folder = @"C:\Windows\PolicyDefinitions";
        harness.Local.Source = SampleAdministrativeTemplates.Source();
        harness.Definitions.Invalidate();
        await none.LoadAsync();

        Assert.True(none.IsCentralStoreMissing);
        Assert.Contains(@"C:\Windows\PolicyDefinitions", none.DefinitionsNote, StringComparison.Ordinal);
        Assert.Contains("has no central store", none.DefinitionsNote, StringComparison.Ordinal);
        Assert.Equal(["System", "Windows Components", "All settings", "Registry settings outside the templates (0)"], none.Roots[0].Children.Select(c => c.Name));
    }

    [Fact]
    public async Task AFileChangedBehindTheEdits_KeepsThem_WithAConflictNote_AndDiscardShowsTheNewFile()
    {
        var (harness, editor) = await OpenAsync();
        var row = Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        editor.Editor!.IsEnabled = true;
        var other = new RegistryPolicyDocument();
        PolicyEngine.Write(row.Definition.Policy, PolicySetting.Disabled, other);
        harness.Sysvol.Put(harness.Sample.SitePrinters, "Machine/Registry.pol", other.ToBytes()); // another administrator saved

        await editor.LoadAsync();

        Assert.NotNull(editor.ConflictNote);
        Assert.True(editor.HasChanges);
        Assert.Equal(PolicyState.Enabled, editor.Policies.Single(p => p.Name == "Turn off the preview pane").State);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.DiscardCommand.ExecuteAsync(null);

        Assert.Null(editor.ConflictNote);
        Assert.False(editor.HasChanges);
        Assert.Equal(PolicyState.Disabled, editor.Policies.Single(p => p.Name == "Turn off the preview pane").State);
    }

    [Fact]
    public async Task AnUnreadableRegistryPol_MakesItsHalfReadOnly()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        harness.Sysvol.Put(harness.Sample.SitePrinters, "User/Registry.pol", [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);

        Assert.Contains("User/Registry.pol", editor.FileNote, StringComparison.Ordinal);
        var row = Select(editor, PolicyClass.User, "Logon message", "Windows Components", "Contoso Update", "Desktop");
        Assert.Equal("–", row.StateText);
        Assert.False(editor.Editor!.CanEdit);
        Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        Assert.True(editor.Editor!.CanEdit);
    }

    [Fact]
    public async Task ListsAndLines_AreEditedAsEntries()
    {
        var (_, editor) = await OpenAsync();
        Select(editor, PolicyClass.User, "Drive mappings", "Windows Components", "Contoso Update", "Desktop");
        var panel = editor.Editor!;
        panel.IsEnabled = true;
        var list = panel.Elements.OfType<ListEditorViewModel>().Single();
        Assert.True(list.IsExplicit);

        list.AddCommand.Execute(null);
        list.Entries[0].Name = "P";
        list.Entries[0].Value = @"\\pliki\Wydział Prawa";
        list.AddCommand.Execute(null);
        list.Entries[1].Name = "p";
        list.Entries[1].Value = @"\\pliki\Księgowość";

        Assert.Equal(["Drive letter and path: entries repeat."], panel.Problems);
        list.Entries[1].Name = "Q";
        Assert.Empty(panel.Problems);
        var setting = panel.CurrentSetting()!;
        Assert.Equal(["P", "Q"], ((ListElementValue)setting.ValueOf("Mapy")!).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task NumberEditor_SpinsWithinTheRange_AndReportsText()
    {
        var (_, editor) = await OpenAsync();
        Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        var panel = editor.Editor!;
        panel.IsEnabled = true;
        var day = panel.Elements.OfType<NumberEditorViewModel>().Single();

        day.Text = "6";
        day.UpCommand.Execute(null);
        day.UpCommand.Execute(null);
        Assert.Equal("7", day.Text);
        day.Text = "abc";
        Assert.Equal(["Scheduled install day: not a whole number."], panel.Problems);
        day.Text = "8";
        Assert.Equal(["Scheduled install day: enter a number from 0 to 7."], panel.Problems);
    }

    [Fact]
    public async Task TheGpoDocument_OpensTheEditor_InTheShell()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        using var shell = harness.CreateShell();
        var gpo = await harness.GpoAsync(harness.Sample.SitePrinters);

        gpo.EditTemplatesCommand.Execute(null);
        for (var i = 0; i < 200 && !shell.Documents.OfType<AdministrativeTemplatesEditorViewModel>().Any(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var editor = Assert.Single(shell.Documents.OfType<AdministrativeTemplatesEditorViewModel>());
        Assert.Equal(harness.Sample.SitePrinters, editor.GpoId);
        Assert.Same(editor, shell.SelectedTab);
    }

    [Fact]
    public async Task AfterAReload_TheSelectedPolicyCanStillBeEdited()
    {
        var (_, editor) = await OpenAsync();
        var row = Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        Assert.True(editor.Editor!.CanEdit);

        await editor.LoadAsync(); // F5, a change elsewhere in the window, the reload after a save: the row stays selected

        Assert.Same(row, editor.SelectedPolicy);
        var panel = editor.Editor!;
        Assert.True(panel.CanEdit);
        panel.IsEnabled = true;
        Assert.Equal(PolicyState.Enabled, row.State);
        Assert.True(panel.Elements.OfType<DropDownEditorViewModel>().Single().IsEditable);
        Assert.True(editor.HasChanges);
    }

    [Fact]
    public async Task InvalidOptions_BlockTheSave_UntilTheyAreCorrected()
    {
        var (harness, editor) = await OpenAsync();
        Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        var panel = editor.Editor!;
        panel.IsEnabled = true;
        var day = panel.Elements.OfType<NumberEditorViewModel>().Single();
        day.Text = "3";
        Assert.True(editor.SaveCommand.CanExecute(null));

        day.Text = "8"; // shown, but invalid: the document still holds 3
        Assert.Equal(["Scheduled install day: enter a number from 0 to 7."], panel.Problems);
        Assert.False(editor.SaveCommand.CanExecute(null));

        await editor.SaveCommand.ExecuteAsync(null); // started another way: still nothing saved
        Assert.Empty(harness.Windows.Previews);
        Assert.Contains(harness.B2.Notifications.Items,
            n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("“Configure automatic updates”", StringComparison.Ordinal));

        day.Text = "4";
        Assert.Empty(panel.Problems);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileSaving_TheEditorIsReadOnly_AndEditableAgainAfterwards()
    {
        var (harness, editor) = await OpenAsync();
        var row = Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        editor.Editor!.IsEnabled = true;
        var seen = new List<(bool Saving, bool CanEdit, bool Save, bool Discard)>();
        harness.Windows.BeforePreview = _ =>
        {
            var panel = editor.Editor!;
            seen.Add((editor.IsSaving, panel.CanEdit, editor.SaveCommand.CanExecute(null), editor.DiscardCommand.CanExecute(null)));
            panel.IsDisabled = true; // a click while the plan is shown: refused, never half in and half out of the plan
            return Task.CompletedTask;
        };

        await editor.SaveCommand.ExecuteAsync(null);
        await editor.LoadAsync();

        Assert.Equal([(true, false, false, false)], seen);
        var written = RegistryPolicyDocument.Parse(harness.Sysvol.Get(harness.Sample.SitePrinters, "Machine/Registry.pol"));
        Assert.Equal(PolicyState.Enabled, PolicyEngine.Read(row.Definition.Policy, written.View).State);
        Assert.False(editor.IsSaving);
        Assert.False(editor.HasChanges);
        Assert.Null(editor.ConflictNote);
        Assert.True(editor.Editor!.CanEdit);
        Assert.True(editor.Editor.IsEnabled);
    }

    [Fact]
    public async Task EditsAfterASave_WhoseReloadCouldNotReadTheFile_StayWithoutAFalseConflict()
    {
        var (harness, editor) = await OpenAsync();
        var preview = Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        editor.Editor!.IsEnabled = true;

        // The reload that follows the save cannot read Machine/Registry.pol: the tab still holds the edits as unsaved.
        harness.Windows.AfterPreview = _ => harness.Sysvol.Fail("Read", SysvolWriteError.Failed, "Machine/Registry.pol");
        await editor.SaveCommand.ExecuteAsync(null);
        await ObjectListViewModelTests.WaitUntilAsync(() => !editor.IsLoading);
        harness.Windows.AfterPreview = null;
        Assert.True(editor.HasChanges);

        var workMode = Select(editor, PolicyClass.Machine, "Work mode", "System");
        editor.Editor!.IsDisabled = true;
        await editor.LoadAsync(); // now the file reads: it holds what this tab saved – not someone else's change

        Assert.Null(editor.ConflictNote);
        Assert.False(preview.IsChanged);
        Assert.True(workMode.IsChanged);
        Assert.Equal(1, editor.ChangedCount);

        await editor.SaveCommand.ExecuteAsync(null); // the compare-and-swap accepts it
        await editor.LoadAsync();

        var written = RegistryPolicyDocument.Parse(harness.Sysvol.Get(harness.Sample.SitePrinters, "Machine/Registry.pol"));
        Assert.Equal(PolicyState.Enabled, PolicyEngine.Read(preview.Definition.Policy, written.View).State);
        Assert.Equal(PolicyState.Disabled, PolicyEngine.Read(workMode.Definition.Policy, written.View).State);
        Assert.False(editor.HasChanges);
        Assert.Null(editor.ConflictNote);
    }

    [Fact]
    public async Task ClosingTheTab_WithUnsavedChanges_AsksFirst()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        using var shell = harness.CreateShell();
        var editor = Assert.IsType<AdministrativeTemplatesEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = harness.Sample.SitePrinters }));
        await editor.LoadAsync();
        Assert.False(editor.HasUnsavedChanges);
        Assert.True(await editor.CanCloseAsync()); // nothing to lose: no question

        Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        editor.Editor!.IsEnabled = true;
        Assert.True(editor.HasUnsavedChanges);
        Assert.True(shell.HasUnsavedDocuments);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(false);
        await shell.CloseTabCommand.ExecuteAsync(editor);
        Assert.Contains(editor, shell.Documents); // kept, with its edits
        Assert.True(editor.HasChanges);
        Assert.Contains("confirm:Setting changes not saved yet: 1. Close the tab and discard them?", harness.B2.Dialogs.Shown);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await shell.CloseTabCommand.ExecuteAsync(editor);
        Assert.DoesNotContain(editor, shell.Documents);
    }

    [Fact]
    public async Task ReloadingTheTemplates_SupersededByAnotherLoad_StillReadsTheStore()
    {
        var (harness, editor) = await OpenAsync();
        harness.Sysvol.PutPolicyDefinition("en-US/contoso.adml", SampleAdministrativeTemplates.Utf8(
            SampleAdministrativeTemplates.ContosoAdmlEn.Replace("Configure automatic updates", "Configure automatic updates (2026)", StringComparison.Ordinal)));
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var lists = 0;
        harness.Sysvol.BeforeCall = (operation, _, _) =>
        {
            if (operation == "ListDefinitions" && Interlocked.Increment(ref lists) == 1)
            {
                entered.Release();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var reload = editor.ReloadDefinitionsCommand.ExecuteAsync(null); // reads the store…
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var refresh = editor.LoadAsync(); // …and F5 (or a change elsewhere) supersedes it
        release.Set();
        await Task.WhenAll(reload, refresh);
        harness.Sysvol.BeforeCall = null;

        Select(editor, PolicyClass.Machine, "Configure automatic updates (2026)", "Windows Components", "Contoso Update");
    }

    [Fact]
    public async Task UnreadableTemplates_AreReportedInTheUiLanguage_AndAFailedReadIsNotCached()
    {
        var harness = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        harness.AddCentralStore();
        harness.Sysvol.PutPolicyDefinition("uszkodzony.admx", SampleAdministrativeTemplates.Utf8("<policyDefinitions><niedomknięty>"));
        harness.Sysvol.Fail("ReadDefinitions", SysvolWriteError.Failed, "contoso.admx"); // one SMB read fails

        var first = await harness.TemplatesAsync(harness.Sample.SitePrinters);

        Assert.Equal(["contoso.admx: nie można odczytać pliku – brak jego ustawień (błąd odczytu)",
            "uszkodzony.admx: nie można odczytać pliku – brak jego ustawień (plik jest uszkodzony)"], first.DefinitionIssues);
        first.SelectedNode = first.Roots[0].Children.Single(c => c.Name == "Wszystkie ustawienia");
        Assert.DoesNotContain(first.Policies, p => p.Name == "Konfiguruj aktualizacje automatyczne");

        // The set without contoso.admx was not kept: the next tab of the window reads the store again.
        var second = await harness.TemplatesAsync(harness.Sample.WorkstationPasswords);
        Assert.Equal(["uszkodzony.admx: nie można odczytać pliku – brak jego ustawień (plik jest uszkodzony)"], second.DefinitionIssues);
        second.SelectedNode = second.Roots[0].Children.Single(c => c.Name == "Wszystkie ustawienia");
        Assert.Contains(second.Policies, p => p.Name == "Konfiguruj aktualizacje automatyczne");
    }

    [Fact]
    public async Task ATemplateTooLargeToRead_IsReportedAsSuch()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var files = SampleAdministrativeTemplates.Files();
        harness.Local.Folder = @"C:\Windows\PolicyDefinitions";
        harness.Local.Source = new ScriptedPolicyDefinitionSource(files) { Lengths = { ["contoso.admx"] = PolicyDefinitionLoader.MaxFileBytes + 1 } };

        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);

        Assert.Equal(["contoso.admx: the file cannot be read – its settings are missing (the file is too large)"], editor.DefinitionIssues);
    }

    // ---- K6: editing comfort (docs/dev/k-computers-and-gpo.md 20.3) ----------------------------------------------------

    [Fact]
    public async Task TheConfiguredCounter_FollowsTheEdits()
    {
        var (_, editor) = await OpenAsync(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.EndsWith("skonfigurowane: 0", editor.LoadedText, StringComparison.Ordinal);

        Select(editor, PolicyClass.Machine, "Wyłącz okienko podglądu", "System");
        editor.Editor!.IsEnabled = true;
        Assert.EndsWith("skonfigurowane: 1", editor.LoadedText, StringComparison.Ordinal);
        Assert.Equal(editor.LoadedText, editor.StatusText);

        Select(editor, PolicyClass.Machine, "Tryb pracy", "System");
        editor.Editor!.IsDisabled = true;
        Assert.EndsWith("skonfigurowane: 2", editor.LoadedText, StringComparison.Ordinal);

        editor.Editor.IsNotConfigured = true;
        Assert.EndsWith("skonfigurowane: 1", editor.LoadedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingAPolicyWithInvalidOptions_KeepsWhatWasTyped_WarnsAndMarksTheRow()
    {
        var (harness, editor) = await OpenAsync();
        var updates = Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        var panel = editor.Editor!;
        panel.IsEnabled = true;
        var day = panel.Elements.OfType<NumberEditorViewModel>().Single();
        day.Text = "3";
        day.Text = "8"; // invalid: the document keeps 3

        Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");

        Assert.Contains(harness.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Warning
            && n.Message.Contains("“Configure automatic updates”", StringComparison.Ordinal) && n.Message.Contains("not valid", StringComparison.Ordinal));
        Assert.True(updates.HasProblem);
        Assert.False(editor.SaveCommand.CanExecute(null)); // the save stays blocked while a typed value is invalid
        editor.SelectedNode = editor.Roots[0];
        editor.OnlyChanged = true;
        Assert.Contains(updates, editor.Policies);

        // Back to the policy: what was typed is shown again, to be corrected.
        editor.OnlyChanged = false;
        Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        Assert.Same(panel, editor.Editor);
        Assert.Equal("8", day.Text);
        Assert.Equal(["Scheduled install day: enter a number from 0 to 7."], editor.Editor!.Problems);

        day.Text = "4";
        Assert.False(updates.HasProblem);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.Equal(new NumberElementValue(4), panel.CurrentSetting()!.ValueOf("Dzien"));
        Assert.True(updates.IsChanged);
    }

    [Fact]
    public async Task DiscardingTheEdits_DropsInvalidOptionsKeptInAnotherPolicy()
    {
        var (harness, editor) = await OpenAsync();
        var updates = Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        editor.Editor!.IsEnabled = true;
        editor.Editor.Elements.OfType<NumberEditorViewModel>().Single().Text = "abc";
        Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        Assert.True(updates.HasProblem);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.DiscardCommand.ExecuteAsync(null);

        Assert.False(editor.HasChanges);
        Assert.False(updates.HasProblem);
        Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        Assert.Empty(editor.Editor!.Problems);
        Assert.True(editor.Editor.IsNotConfigured);
    }

    [Fact]
    public async Task OptionsTypedInvalidAndKept_AreUnsavedEdits_ThatClosingAndDiscardCount()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        using var shell = harness.CreateShell();
        var editor = Assert.IsType<AdministrativeTemplatesEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = harness.Sample.SitePrinters }));
        await editor.LoadAsync();
        Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        editor.Editor!.IsEnabled = true;
        await editor.SaveCommand.ExecuteAsync(null);
        await editor.LoadAsync();
        Assert.False(editor.HasUnsavedChanges);

        // Only an option of the configured policy typed invalid, then another policy selected: nothing changed in the GPO.
        var updates = Select(editor, PolicyClass.Machine, "Configure automatic updates", "Windows Components", "Contoso Update");
        editor.Editor!.Elements.OfType<NumberEditorViewModel>().Single().Text = "zażółć";
        Select(editor, PolicyClass.Machine, "Turn off the preview pane", "System");
        Assert.True(updates.HasProblem);
        Assert.False(editor.HasChanges);
        Assert.True(editor.HasUnsavedChanges);
        Assert.True(shell.HasUnsavedDocuments);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(false);
        await shell.CloseTabCommand.ExecuteAsync(editor);
        Assert.Contains(editor, shell.Documents); // asked, and kept
        Assert.Contains("confirm:Setting changes not saved yet: 1. Close the tab and discard them?", harness.B2.Dialogs.Shown);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.DiscardCommand.ExecuteAsync(null);
        Assert.Contains("confirm:Discard the changes of 1 settings that are not saved yet?", harness.B2.Dialogs.Shown);
        Assert.False(editor.HasUnsavedChanges);
        Assert.False(updates.HasProblem);
    }

    [Fact]
    public async Task FocusSetting_SelectsThePolicy_ByItsIdOrByARegistryValueItOwns()
    {
        var (_, editor) = await OpenAsync();
        editor.SearchText = "nie ma takiego";
        editor.OnlyConfigured = true;

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.Updates)));

        Assert.Equal(string.Empty, editor.SearchText);
        Assert.False(editor.OnlyConfigured);
        Assert.Equal("Contoso Update", editor.SelectedNode!.Name);
        Assert.True(editor.SelectedNode.IsSelected);
        Assert.True(Node(editor, PolicyClass.Machine, "Windows Components").IsExpanded);
        Assert.Equal("Configure automatic updates", editor.SelectedPolicy!.Name);
        Assert.Equal("Configure automatic updates", editor.Editor!.Name);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting(@"Computer/Registry/HKLM\Software\Policies\Contoso\Eksplorator!NoPreview")));
        Assert.Equal("System", editor.SelectedNode!.Name);
        Assert.Equal("Turn off the preview pane", editor.SelectedPolicy!.Name);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting(@"User/Registry/HKCU\Software\Policies\Contoso\Komunikat!Lines")));
        Assert.Equal(PolicyClass.User, editor.SelectedPolicy!.Scope);
        Assert.Equal("Logon message", editor.SelectedPolicy.Name);

        // A list key is owned by its policy whatever the value is named.
        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting(@"User/Registry/HKCU\Software\Policies\Contoso\Dyski\Mapy!P")));
        Assert.Equal("Drive mappings", editor.SelectedPolicy!.Name);
    }

    [Fact]
    public async Task FocusSetting_OfAValueNoTemplateDescribes_SelectsTheExtraEntry_AndAnUnknownPolicyIsReported()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();
        harness.Sysvol.Put(harness.Sample.SitePrinters, "Machine/Registry.pol", RegistryPolFile.Write(
        [
            new RegistryPolEntry(@"Software\Policies\Inny\Program", "Tryb", RegistryFormat.RegSz, RegistryPolFile.StringData("łączność")),
        ]));
        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting(@"Computer/Registry/HKLM\Software\Policies\Inny\Program!Tryb")));
        Assert.True(editor.IsExtraSelected);
        Assert.Equal("Tryb", editor.SelectedExtraEntry!.ValueName);

        Assert.False(editor.FocusSetting(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, "Contoso.Policies.Aktualizacje:Nieznana")));
        Assert.Contains(harness.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Info && n.Message.Contains("Nieznana", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFocusRequestedBeforeTheLoad_IsAppliedWhenItEnds()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        harness.AddCentralStore();

        var editor = await harness.OpenAsync<AdministrativeTemplatesEditorViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates)
        {
            GpoId = harness.Sample.SitePrinters,
            Focus = GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.WorkMode),
        });

        Assert.Equal("Work mode", editor.SelectedPolicy!.Name);
        Assert.Equal("System", editor.SelectedNode!.Name);
    }

    [Fact]
    public async Task TheRegistryLocation_IsShownAsTheKeyAndTheValue()
    {
        var (_, editor) = await OpenAsync(CultureInfo.GetCultureInfo("pl-PL"));
        Select(editor, PolicyClass.Machine, "Konfiguruj aktualizacje automatyczne", "Składniki systemu Windows", "Aktualizacje Contoso");
        Assert.Equal(@"HKLM\" + SampleAdministrativeTemplates.UpdatesKey, editor.Editor!.RegistryKeyText);
        Assert.Equal("Wartość: NoAutoUpdate", editor.Editor.RegistryValueText);

        Select(editor, PolicyClass.Machine, "Określ lokalizację usługi aktualizacji w intranecie", "Składniki systemu Windows", "Aktualizacje Contoso");
        Assert.Equal(@"HKLM\Software\Policies\Contoso\Aktualizacje", editor.Editor!.RegistryKeyText);
        Assert.Null(editor.Editor.RegistryValueText); // only options: no value of its own
    }

    [Fact]
    public async Task Saving_ShowsTheSettingChanges_InTheOperatorsLanguage()
    {
        var harness = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        harness.AddCentralStore();
        var existing = new RegistryPolicyDocument();
        PolicyEngine.Write(SampleAdministrativeTemplates.LoadPolish().Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Disabled, existing);
        existing.Set(@"Software\Policies\Inny\Program", "Tryb", RegistryFormat.RegSz, RegistryPolFile.StringData("łączność"));
        harness.Sysvol.Put(harness.Sample.SitePrinters, "Machine/Registry.pol", existing.ToBytes());
        var editor = await harness.TemplatesAsync(harness.Sample.SitePrinters);

        Select(editor, PolicyClass.Machine, "Konfiguruj aktualizacje automatyczne", "Składniki systemu Windows", "Aktualizacje Contoso");
        editor.Editor!.IsEnabled = true;
        editor.Editor.Elements.OfType<NumberEditorViewModel>().Single().Text = "3";
        Select(editor, PolicyClass.Machine, "Wyłącz okienko podglądu", "System");
        editor.Editor!.IsEnabled = true;
        Select(editor, PolicyClass.Machine, "Tryb pracy", "System");
        editor.Editor!.IsEnabled = true;
        editor.Editor.IsNotConfigured = true; // touched, but back as read: not a change
        editor.SelectedNode = Node(editor, PolicyClass.Machine, "Ustawienia rejestru spoza szablonów (1)");
        editor.SelectedExtraEntry = editor.ExtraEntries.Single();
        editor.DeleteExtraEntryCommand.Execute(null);
        IReadOnlyList<GroupPolicySettingChange> shown = [];
        string? title = null;
        harness.Windows.BeforePreview = preview =>
        {
            shown = preview.SettingChanges;
            title = preview.HasSettingChanges ? preview.SettingChangesTitle : null;
            return Task.CompletedTask;
        };

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Zmiany ustawień (3)", title);
        Assert.Equal(
        [
            new GroupPolicySettingChange(@"HKLM\Software\Policies\Inny\Program, wartość Tryb", "łączność", "—", "Konfiguracja komputera"),
            new GroupPolicySettingChange("Wyłącz okienko podglądu", "Wyłączone", "Włączone", "Konfiguracja komputera / System"),
        ], shown.Where(c => c.Setting != "Konfiguruj aktualizacje automatyczne"));
        var updates = Assert.Single(shown, c => c.Setting == "Konfiguruj aktualizacje automatyczne");
        Assert.Equal("Nie skonfigurowano", updates.Before);
        Assert.StartsWith("Włączone; ", updates.After, StringComparison.Ordinal);
        Assert.Contains("3", updates.After, StringComparison.Ordinal);
        Assert.Equal("Konfiguracja komputera / Składniki systemu Windows / Aktualizacje Contoso", updates.Group);
    }
}
