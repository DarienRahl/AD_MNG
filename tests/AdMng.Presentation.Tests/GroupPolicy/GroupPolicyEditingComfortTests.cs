using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// K6 editing comfort (docs/dev/k-computers-and-gpo.md 20.3) across the window: "Edytuj to ustawienie" routing, Ctrl+S, Ctrl+F,
/// the palette entries, the GPO list's search, the scope view's link menu and the "Zmiany ustawień" of the preview.
/// </summary>
public sealed class GroupPolicyEditingComfortTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Theory]
    [InlineData("Computer/Security/Privilege Rights/SeDebugPrivilege", GroupPolicyDocumentKind.SecuritySettings)]
    [InlineData("Computer/Security/System Access/MinimumPasswordLength", GroupPolicyDocumentKind.SecuritySettings)]
    [InlineData(@"Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Control\Lsa!LmCompatibilityLevel", GroupPolicyDocumentKind.SecuritySettings)]
    [InlineData("Computer/Audit/{0cce9215-69ae-11d9-bed3-505054503030}", GroupPolicyDocumentKind.SecuritySettings)]
    [InlineData("computer/preferences/groups/{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}/Properties@action", GroupPolicyDocumentKind.SecuritySettings)]
    [InlineData(@"Computer/Registry/HKLM\Software\Policies\Contoso\Aktualizacje\AU!NoAutoUpdate", GroupPolicyDocumentKind.AdministrativeTemplates)]
    [InlineData(@"User/Registry/HKCU\Software\Policies\Contoso\Komunikat!Lines", GroupPolicyDocumentKind.AdministrativeTemplates)]
    [InlineData("User/Preferences/Drives/{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}/Properties@path", null)]
    [InlineData("User/Preferences/Groups/{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}/Properties@action", null)]
    [InlineData("Computer/Scripts/Startup/0/CmdLine", null)]
    public void TheEditor_IsChosenByTheSettingKey(string key, GroupPolicyDocumentKind? expected)
    {
        var focus = GroupPolicySettingFocus.ForSetting(key);

        Assert.Equal(expected, focus.Editor);
        Assert.Equal(expected is not null, GroupPolicyActions.CanEditSetting(focus));
    }

    [Fact]
    public void AnAdmxPolicy_IsEditedInTheAdministrativeTemplates_AndRegistryKeysAreRead()
    {
        Assert.Equal(GroupPolicyDocumentKind.AdministrativeTemplates, GroupPolicySettingFocus.ForPolicy(PolicyClass.User, SampleAdministrativeTemplates.Message).Editor);
        Assert.False(GroupPolicyActions.CanEditSetting(null));
        Assert.Null(new GroupPolicySettingFocus().Editor);

        Assert.True(GroupPolicySettingFocus.ForSetting(@"User/Registry/HKCU\Software\Policies\Contoso\Komunikat!Lines")
            .TryGetRegistryValue(out var scope, out var key, out var value));
        Assert.Equal((PolicyClass.User, @"Software\Policies\Contoso\Komunikat", "Lines"), (scope, key, value));
        Assert.True(GroupPolicySettingFocus.ForSetting(@"Computer/Registry/HKLM\Software\Łódź!(Default)").TryGetRegistryValue(out scope, out key, out value));
        Assert.Equal((PolicyClass.Machine, @"Software\Łódź", string.Empty), (scope, key, value));
        Assert.True(GroupPolicySettingFocus.ForSetting(@"Computer/Registry/HKLM\Software\Hasła!*").TryGetRegistryValue(out _, out _, out value));
        Assert.Null(value); // names hidden: the key only
        Assert.False(GroupPolicySettingFocus.ForSetting("Computer/Security/Privilege Rights/SeDebugPrivilege").TryGetRegistryValue(out _, out _, out _));
    }

    [Fact]
    public async Task EditSetting_OpensTheSecurityEditorAtTheRow_AndPointsAnOpenTabAtTheNextSetting()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var gpo = h.Sample.ServerSecurity;

        Assert.True(h.Actions.EditSetting(gpo, "Serwery – Zabezpieczenia", GroupPolicySettingFocus.ForSetting("Computer/Security/Privilege Rights/SeDebugPrivilege")));

        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is SecuritySettingsEditorViewModel { IsLoading: false, SelectedRight: not null });
        var editor = (SecuritySettingsEditorViewModel)shell.SelectedTab!;
        Assert.Equal(gpo, editor.GpoId);
        Assert.Equal(SecuritySettingsPage.Rights, editor.SelectedPage);
        Assert.Equal("Debugowanie programów", editor.SelectedRight!.Name);

        // Another setting of the same GPO: the open tab is activated and pointed at it, no second tab.
        shell.OpenGroupPolicyObjectsCommand.Execute(null);
        Assert.True(h.Actions.EditSetting(gpo, "Serwery – Zabezpieczenia", GroupPolicySettingFocus.ForSetting("Computer/Security/System Access/MinimumPasswordLength")));
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(shell.SelectedTab, editor));
        Assert.Single(shell.Documents.OfType<SecuritySettingsEditorViewModel>());
        Assert.Equal(SecuritySettingsPage.Options, editor.SelectedPage);
        Assert.Equal("MinimumPasswordLength", editor.SelectedOption!.Definition.Id);

        // Nothing edits a script: nothing opens.
        var tabs = shell.Documents.Count;
        Assert.False(h.Actions.EditSetting(gpo, "Serwery – Zabezpieczenia", GroupPolicySettingFocus.ForSetting("Computer/Scripts/Startup/0/CmdLine")));
        Assert.Equal(tabs, shell.Documents.Count);
    }

    [Fact]
    public async Task EditSetting_OfAnAdmxPolicy_OpensTheTemplatesEditorAtThePolicy()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.AddCentralStore();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        Assert.True(h.Actions.EditSetting(h.Sample.SitePrinters, "Lokacja Warszawa – Drukarki",
            GroupPolicySettingFocus.ForPolicy(PolicyClass.User, SampleAdministrativeTemplates.Message)));

        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is AdministrativeTemplatesEditorViewModel { IsLoading: false, SelectedPolicy: not null });
        var editor = (AdministrativeTemplatesEditorViewModel)shell.SelectedTab!;
        Assert.Equal("Komunikat przy logowaniu", editor.SelectedPolicy!.Name);
        Assert.Equal(PolicyClass.User, editor.SelectedPolicy.Scope);
        Assert.Equal("Pulpit", editor.SelectedNode!.Name);
    }

    [Fact]
    public async Task CtrlS_SavesTheActiveEditor_AndIsOffElsewhere()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.AddCentralStore();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var save = Assert.Single(shell.CommandDescriptors, d => d.Id == "document.save");
        Assert.Equal("Ctrl+S", save.Shortcut);
        Assert.Equal(L.GroupPolicyEditing.DocumentCategory, save.CategoryKey);
        Assert.False(save.Command.CanExecute(null)); // a list tab: nothing to save

        var editor = Assert.IsType<AdministrativeTemplatesEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = h.Sample.SitePrinters }));
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && editor.Roots.Count == 2);
        Assert.False(save.Command.CanExecute(null)); // nothing changed yet
        editor.FocusSetting(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.HidePreview));
        editor.Editor!.IsEnabled = true;
        Assert.True(save.Command.CanExecute(null));

        await shell.SaveDocumentCommand.ExecuteAsync(null);

        Assert.Single(h.Windows.Previews);
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && !editor.HasChanges);
        Assert.False(save.Command.CanExecute(null));
    }

    [Fact]
    public async Task CtrlF_FocusesTheSearchOfTheDocument_ElseOpensFind()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        Assert.Equal("Ctrl+F", Assert.Single(shell.CommandDescriptors, d => d.Id == "nav.find").Shortcut);

        await shell.FindCommand.ExecuteAsync(null); // the object list: the directory Find
        Assert.Contains(h.B2.Dialogs.Shown, s => s.StartsWith("find:", StringComparison.Ordinal));

        foreach (var request in new[]
        {
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects),
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = h.Sample.ServerSecurity },
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = h.Sample.ServerSecurity },
        })
        {
            var document = Assert.IsAssignableFrom<ISearchableDocument>(shell.OpenGroupPolicyDocument(request));
            var focused = 0;
            document.SearchFocusRequested += (_, _) => focused++;
            var finds = h.B2.Dialogs.Shown.Count(s => s.StartsWith("find:", StringComparison.Ordinal));

            Assert.True(shell.FindCommand.CanExecute(null));
            await shell.FindCommand.ExecuteAsync(null);

            Assert.Equal(1, focused);
            Assert.Equal(finds, h.B2.Dialogs.Shown.Count(s => s.StartsWith("find:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task ThePalette_EditsTheTemplatesAndSecurityOfTheGpoTheWindowIsOn()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var templates = Assert.Single(shell.CommandDescriptors, d => d.Id == "gpo.editTemplates");
        var security = Assert.Single(shell.CommandDescriptors, d => d.Id == "gpo.editSecurity");
        Assert.Equal(L.AdministrativeTemplates.EditCommand, templates.TitleKey);
        Assert.Equal(L.SecuritySettings.EditCommand, security.TitleKey);
        Assert.Null(shell.ActiveGpo());
        Assert.False(templates.Command.CanExecute(null)); // no GPO in sight

        var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        await B2Harness.WaitUntilAsync(() => !objects.IsLoading && objects.Rows.Count > 0);
        objects.SelectedRow = objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords);
        Assert.Equal((h.Sample.WorkstationPasswords, "Stacje robocze – Zasady haseł"), shell.ActiveGpo());

        security.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is SecuritySettingsEditorViewModel);
        var editor = (SecuritySettingsEditorViewModel)shell.SelectedTab!;
        Assert.Equal(h.Sample.WorkstationPasswords, editor.GpoId);

        // From one editor to the other of the same GPO.
        Assert.Equal(h.Sample.WorkstationPasswords, shell.ActiveGpo()!.Value.Id);
        templates.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is AdministrativeTemplatesEditorViewModel);
        Assert.Equal(h.Sample.WorkstationPasswords, ((AdministrativeTemplatesEditorViewModel)shell.SelectedTab!).GpoId);
    }

    [Fact]
    public async Task TheGpoList_SearchesNamesGuidsWmiFiltersAndLinks()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var objects = await h.ObjectsAsync();
        Assert.Equal(7, objects.Rows.Count);
        var countText = objects.LoadedText;

        objects.SearchText = "PULPIT";
        Assert.Equal(["Stacje robocze – Pulpit zdalny"], objects.Rows.Select(r => r.Name));
        Assert.Equal("Pokazano obiekty zasad grupy: 1 z 7", objects.LoadedText);

        objects.SearchText = h.Sample.ServerSecurity.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant()[..8];
        Assert.Equal([h.Sample.ServerSecurity], objects.Rows.Select(r => r.Gpo.Id));

        objects.SearchText = "tylko stacje"; // the WMI filter
        Assert.Equal([h.Sample.WorkstationPasswords], objects.Rows.Select(r => r.Gpo.Id));

        objects.SelectedRow = objects.Rows[0];
        objects.SearchText = "warszawa"; // the site the GPO is linked to
        Assert.Equal([h.Sample.SitePrinters], objects.Rows.Select(r => r.Gpo.Id));
        Assert.Null(objects.SelectedRow); // the selection keeps only rows still listed
        Assert.Empty(objects.Selection);

        objects.SearchText = "nie ma takiego";
        Assert.Empty(objects.Rows);
        Assert.Equal("Żaden GPO nie pasuje do wyszukiwania.", objects.ListNote);

        objects.ClearSearchCommand.Execute(null);
        Assert.Equal(7, objects.Rows.Count);
        Assert.Null(objects.ListNote);
        Assert.Equal(countText, objects.LoadedText);

        objects.SearchText = "dysków"; // the search survives a reload
        await objects.LoadAsync();
        Assert.Equal([h.Sample.DriveMappings], objects.Rows.Select(r => r.Gpo.Id));
    }

    [Fact]
    public async Task TheLinkMenuOfAScope_EditsTheLinkedGpo()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = h.Company.ComputersOu }));
        await B2Harness.WaitUntilAsync(() => !scope.IsLoading && scope.Links.Count == 2);
        Assert.False(scope.EditGpoCommand.CanExecute(null));
        var link = scope.Links.Single(l => l.Link.GpoId == h.Sample.WorkstationPasswords);
        scope.SelectedLink = link;
        Assert.Equal(h.Sample.WorkstationPasswords, shell.ActiveGpo()!.Value.Id);

        scope.EditGpoCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is GroupPolicyObjectDocumentViewModel);
        var gpo = (GroupPolicyObjectDocumentViewModel)shell.SelectedTab!;
        Assert.Equal(h.Sample.WorkstationPasswords, gpo.GpoId);
        Assert.Equal(GpoDocumentTab.Settings, gpo.SelectedTab);

        scope.EditTemplatesCommand.Execute(link);
        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is AdministrativeTemplatesEditorViewModel);
        scope.EditSecuritySettingsCommand.Execute(link);
        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is SecuritySettingsEditorViewModel);
        Assert.Equal(h.Sample.WorkstationPasswords, ((SecuritySettingsEditorViewModel)shell.SelectedTab!).GpoId);
    }

    [Fact]
    public async Task ThePreview_ShowsTheSettingChanges_CollapsedWhenLong_AndNotForTheUndo()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var editor = await h.SecurityAsync(h.Sample.WorkstationPasswords);
        editor.OptionGroups.SelectMany(g => g.Rows).Single(r => r.Definition.Id == "MinimumPasswordLength").Text = "14";
        ChangePlanPreviewViewModel? shown = null;
        var seen = new List<(bool Has, string Title, bool Expanded)>();
        h.Windows.UndoAfterExecute = true;
        h.Windows.BeforePreview = preview =>
        {
            shown = preview;
            seen.Add((preview.HasSettingChanges, preview.SettingChangesTitle, preview.AreSettingChangesExpanded));
            return Task.CompletedTask;
        };

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal([(true, "Zmiany ustawień (1)", true)], seen);
        Assert.Equal([new GroupPolicySettingChange("Minimalna długość hasła", "12", "14", "Zasady haseł")], shown!.SettingChanges);
        Assert.True(shown.IsRollbackPreview); // the window played "Cofnij": the plan shown is the undo…
        Assert.False(shown.HasSettingChanges); // …which the summary does not describe

        var many = Enumerable.Range(1, ChangePlanPreviewViewModel.MaxExpandedSettingChanges + 1)
            .Select(i => new GroupPolicySettingChange($"Ustawienie {i}", null, "Włączone", null)).ToList();
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(shown.Plan, "Zapis") { SettingChanges = many });
        Assert.True(preview.HasSettingChanges);
        Assert.False(preview.AreSettingChangesExpanded);
        using var none = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(shown.Plan, "Zapis"));
        Assert.False(none.HasSettingChanges);
    }
}
