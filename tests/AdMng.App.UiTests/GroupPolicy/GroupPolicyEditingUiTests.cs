using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// K6 editing comfort in the views (docs/dev/k-computers-and-gpo.md 20.3): trimmed headers, visible splitters, the search
/// boxes (Ctrl+F focuses them, Esc clears them), the category and group columns, "Edytuj to ustawienie" selecting a page,
/// the link menu of "Zasady grupy" and "Zmiany ustawień" in the change plan preview.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyEditingUiTests
{
    [Fact]
    public Task AdministrativeTemplatesEditor_HasATrimmedHeader_SplittersAndASearchBoxForCtrlF() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();
        var editor = Assert.IsType<AdministrativeTemplatesEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" }));
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.Roots.Count == 2, "the administrative templates");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<AdministrativeTemplatesEditorView>(main));

        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.Equal(Visibility.Collapsed, view.ChangedText.Visibility);
        Assert.Equal(2, UiWait.Descendants<PaneSplitter>(view).Count());
        Assert.Contains(view.CategoryColumn, view.PolicyGrid.Columns); // "Konfiguracja komputera" spans the categories

        // A category alone: no category column; a search spans the categories again.
        editor.FocusSetting(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.Updates));
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.DoesNotContain(view.CategoryColumn, view.PolicyGrid.Columns);
        Assert.Same(editor.SelectedPolicy, view.PolicyList.SelectedItem);
        Assert.Equal(@"HKLM\" + SampleAdministrativeTemplates.UpdatesKey, UiWait.Descendants<TextBox>(view).Single(t => t.Name == "RegistryKeyText").Text);
        editor.SearchText = "wuserver";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains(view.CategoryColumn, view.PolicyGrid.Columns);

        // Ctrl+F: the search box takes the keyboard; Esc clears it.
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));
        view.SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view.SearchBox)!, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(string.Empty, editor.SearchText);
        Assert.Equal(string.Empty, view.SearchBox.Text);

        // An edit: the changed count shows under the title, and the counter of the status bar follows it.
        editor.FocusSetting(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.HidePreview));
        editor.Editor!.IsEnabled = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(Visibility.Visible, view.ChangedText.Visibility);
        Assert.EndsWith("skonfigurowane: 1", editor.StatusText, StringComparison.Ordinal);
        context.AssertHealthy();
    });

    [Fact]
    public Task SecuritySettingsEditor_FiltersEveryPage_AndOpensThePageOfAFocusedSetting() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        context.Sysvol.Put(sample.SitePrinters, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 12",
            "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544",
            "[Service General Setting]", "\"Spooler\",4,\"\"")));
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();
        var editor = Assert.IsType<SecuritySettingsEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings)
            {
                GpoId = sample.SitePrinters,
                Name = "Lokacja Warszawa – Drukarki",
                Focus = GroupPolicySettingFocus.ForSetting("Computer/Security/Services/Spooler/StartupMode"),
            }));
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.SelectedService is not null, "the security settings at the service");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<SecuritySettingsEditorView>(main));
        Assert.Same(view.ServicesTab, view.Pages.SelectedItem);
        Assert.Same(editor.SelectedService, view.ServiceList.SelectedItem);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);

        // The splitters of the groups, rights and options pages.
        foreach (var page in new[] { view.GroupsTab, view.RightsTab, view.OptionsTab })
        {
            view.Pages.SelectedItem = page;
            await UiTestSupport.WaitForIdleAsync(rounds: 3);
            Assert.NotEmpty(UiWait.Descendants<PaneSplitter>((DependencyObject)page.Content));
        }

        editor.OnlyDefined = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.OnlyDefinedToggle.IsChecked);
        Assert.Contains(view.OptionGroupColumn, view.OptionGrid.Columns); // the options span the groups
        Assert.Contains(view.OptionList.Items.Cast<SecurityOptionRowViewModel>(), o => o.Definition.Id == "MinimumPasswordLength");
        view.Pages.SelectedItem = view.RightsTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("SeDebugPrivilege", Assert.Single(view.RightList.Items.Cast<UserRightRowViewModel>()).Constant);
        editor.OnlyDefined = false;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.DoesNotContain(view.OptionGroupColumn, view.OptionGrid.Columns);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/Event Audit/AuditLogonEvents")));
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.AuditTab, view.Pages.SelectedItem);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheGpoList_HasASearchBox_AndTheScopeLinkMenuEditsTheGpo() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        await UiWait.UntilAsync(() => !objects.IsLoading && objects.Rows.Count == 7, "the GPOs");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var list = Assert.Single(UiWait.Descendants<GroupPolicyObjectsView>(main));
        objects.SearchText = "pulpit";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(list.GpoList.Items);
        Assert.Equal(Visibility.Collapsed, list.ListNoteText.Visibility);
        objects.SearchText = "nie ma takiego";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(Visibility.Visible, list.ListNoteText.Visibility);
        objects.SearchText = string.Empty;

        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = context.Company.ComputersOu, Name = "Komputery" }));
        await UiWait.UntilAsync(() => !scope.IsLoading && scope.Links.Count == 2, "the links");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GroupPolicyScopeView>(main));
        Assert.Single(UiWait.Descendants<PaneSplitter>(view));
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        var menu = view.LinksList.ContextMenu!;
        var edit = menu.Items.OfType<MenuItem>().Single(i => i.Name == "EditGpoMenuItem");
        scope.SelectedLink = scope.Links.Single(l => l.Link.GpoId == sample.WorkstationPasswords);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(edit.Command.CanExecute(edit.CommandParameter));
        edit.Command.Execute(edit.CommandParameter);
        await UiWait.UntilAsync(() => shell.SelectedTab is GroupPolicyObjectDocumentViewModel { SelectedTab: GpoDocumentTab.Settings }, "the GPO's settings");
        context.AssertHealthy();
    });

    [Fact]
    public Task PreviewWindow_ShowsTheSettingChangesAboveTheRows() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var preview = new ChangePlanPreviewViewModel(gp.Ui, gp.Engines,
            new ChangePlanPreviewRequest(GroupPolicySamples.Plan("dc01.contoso.test"), "Ustawienia zabezpieczeń – Stacje robocze – Zasady haseł")
            {
                SettingChanges =
                [
                    new GroupPolicySettingChange("Minimalna długość hasła", "12", "14", "Zasady haseł"),
                    new GroupPolicySettingChange("Zamykanie systemu", "Nie zdefiniowano", "Administratorzy", "Prawa użytkownika"),
                ],
            });
        var window = (ChangePlanPreviewWindow)windows.CreateWindow(preview);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(Visibility.Visible, window.SettingChangesExpander.Visibility);
        Assert.True(window.SettingChangesExpander.IsExpanded);
        Assert.Equal("Zmiany ustawień (2)", window.SettingChangesExpander.Header);
        Assert.Equal(2, window.SettingChangesList.Items.Count);
        Assert.True(window.RowsList.Items.Count >= 3);
        context.AssertHealthy();
    });
}
