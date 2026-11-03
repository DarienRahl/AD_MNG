using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// Group Policy in the main window (design note docs/dev/k-computers-and-gpo.md 10): the tree nodes and their menus, the
/// documents, the GPO windows and the change plan preview with SYSVOL rows, on the fake directory and <see cref="FakeSysvol"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyWindowTests
{
    [Fact]
    public Task AppHost_RegistersTheGroupPolicyWindows_AndTheyRender_Disconnected() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var windows = context.Services.GetRequiredService<IWindowService>();
        foreach (var type in new[]
        {
            typeof(ChangePlanPreviewViewModel), typeof(NewGpoViewModel), typeof(GpoPickerViewModel), typeof(GpoBackupViewModel),
            typeof(GpoComparisonViewModel), typeof(WmiFilterEditorViewModel), typeof(CentralStoreUpdateViewModel),
        })
        {
            Assert.True(windows.IsRegistered(type), type.Name);
        }

        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        Assert.Same(context.Services.GetRequiredService<IDialogService>(), context.Services.GetRequiredService<IGroupPolicyDialogs>());
        Assert.NotNull(gp.Writer);

        var newGpo = (NewGpoWindow)windows.CreateWindow(new NewGpoViewModel(gp.Ui, NewGpoMode.New, "Nowy obiekt zasad grupy", "Komputery"));
        UiTestSupport.ShowOffScreen(newGpo);
        Assert.Equal("Nowy obiekt zasad grupy", newGpo.NameBox.Text);
        Assert.Equal(Visibility.Visible, newGpo.LinkTargetTextBlock.Visibility);
        Assert.False(newGpo.DefaultPermissionsRadio.IsVisible);
        Assert.True(newGpo.OkButton.IsEnabled);
        newGpo.ViewModel.Name = "  ";
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(newGpo.OkButton.IsEnabled);
        Assert.Equal(Visibility.Visible, newGpo.NameErrorText.Visibility);

        var copy = (NewGpoWindow)windows.CreateWindow(new NewGpoViewModel(gp.Ui, NewGpoMode.Copy, "Kopia – Zasady haseł", null, "Stacje robocze – Zasady haseł"));
        UiTestSupport.ShowOffScreen(copy);
        Assert.True(copy.DefaultPermissionsRadio.IsVisible);
        Assert.Equal(Visibility.Visible, copy.SourceTextBlock.Visibility);
        Assert.Equal(Visibility.Collapsed, copy.LinkTargetTextBlock.Visibility);

        foreach (var mode in Enum.GetValues<GpoBackupMode>())
        {
            var backup = (GpoBackupWindow)windows.CreateWindow(new GpoBackupViewModel(gp, mode, mode == GpoBackupMode.Restore
                ? []
                : [GroupPolicySamples.Gpo(GroupPolicySamples.PasswordsId, "Stacje robocze – Zasady haseł")]));
            UiTestSupport.ShowOffScreen(backup);
            await UiWait.InitializedAsync(backup.Initialization);
            Assert.Equal(backup.ViewModel.ListsBackups, backup.BackupList.IsVisible);
            Assert.Equal(backup.ViewModel.IsBeforeDelete ? Visibility.Visible : Visibility.Collapsed, backup.BackupFirstBox.Visibility);
        }

        var editor = (WmiFilterEditorWindow)windows.CreateWindow(new WmiFilterEditorViewModel(gp.Ui, null));
        UiTestSupport.ShowOffScreen(editor);
        Assert.False(editor.OkButton.IsEnabled);
        editor.ViewModel.Name = "Tylko serwery";
        editor.ViewModel.Queries[0].Query = "SELECT * FROM Win32_OperatingSystem WHERE ProductType <> 1";
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(editor.OkButton.IsEnabled);
        editor.AddQueryButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, editor.QueriesList.Items.Count);

        var edit = (WmiFilterEditorWindow)windows.CreateWindow(new WmiFilterEditorViewModel(gp.Ui, GroupPolicySamples.Filter()));
        UiTestSupport.ShowOffScreen(edit);
        Assert.Equal("Tylko stacje robocze", edit.NameBox.Text);

        // K3: "Zaktualizuj szablony ADMX" without a connection: the sources are offered, nothing can be written.
        var factory = context.Services.GetRequiredService<GroupPolicyViewModelFactory>();
        Assert.True(factory.Loader.CanExtractInstallers);
        var update = (CentralStoreUpdateWindow)windows.CreateWindow(new CentralStoreUpdateViewModel(gp, factory.Definitions, factory.Loader));
        UiTestSupport.ShowOffScreen(update);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(update.OpenFileButton.IsEnabled);
        Assert.True(update.EdgeButton.IsEnabled);
        Assert.False(update.UpdateButton.IsEnabled);
        Assert.Equal(Visibility.Collapsed, update.ErrorBar.Visibility);
        await UiTestSupport.WaitForIdleAsync();
        context.AssertHealthy();
    });

    [Fact]
    public Task CentralStoreUpdate_ComparesOneTemplateFile_BacksUpTheStore_AndUpdatesItThroughThePreviewWindow() => WpfTestHost.RunAsync(async () =>
    {
        var files = new PreparedSaveDialog();
        await using var context = await UiTestContext.CreateAsync(language: "pl", configure: s => s.AddSingleton<IFileDialogService>(files));
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        // An unpacked folder with both sample templates and their Polish files; the operator picks contoso.admx: the package
        // is that template with the language files beside it (en-US, pl-PL) – windows.admx of the same folder is not part of it.
        var package = System.IO.Directory.CreateTempSubdirectory("admng-k3-package-");
        var backups = System.IO.Directory.CreateTempSubdirectory("admng-k3-backup-");
        try
        {
            foreach (var (path, content) in SampleAdministrativeTemplates.Files())
            {
                var file = Path.Combine([package.FullName, .. path.Split('/')]);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, content);
            }

            files.NextOpenPath = Path.Combine(package.FullName, "contoso.admx");
            await context.ConnectAsync();
            var main = context.Services.GetRequiredService<MainWindow>();
            UiTestSupport.ShowOffScreen(main);
            var shell = main.Shell;
            await shell.InitializeAsync();

            var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
            await UiWait.UntilAsync(() => !objects.IsLoading && objects.UpdateTemplatesCommand.CanExecute(null), "the GPO list");
            main.UpdateLayout();
            await UiTestSupport.WaitForIdleAsync(rounds: 3);
            Assert.True(Assert.Single(UiWait.Descendants<GroupPolicyObjectsView>(main)).UpdateTemplatesButton.IsEnabled);

            var drive = UiWait.DriveModalAsync<CentralStoreUpdateWindow>(async w =>
            {
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                Assert.EndsWith(@"\Policies\PolicyDefinitions", w.TargetText.Text, StringComparison.Ordinal);
                Assert.False(w.UpdateButton.IsEnabled);

                await w.ViewModel.OpenFileCommand.ExecuteAsync(null);
                await UiWait.UntilAsync(() => !w.ViewModel.IsWorking && w.ViewModel.Rows.Count == 3, "the comparison");
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                Assert.Null(w.ViewModel.Error);
                Assert.Equal(["contoso.admx"], w.ViewModel.Package!.AdmxFiles);
                Assert.Equal(["contoso.admx", "en-US/contoso.adml", "pl-PL/contoso.adml"], w.ViewModel.Rows.Select(r => r.Path));
                Assert.Equal(3, w.FileList.Items.Count);
                Assert.Equal(2, w.CultureList.Items.Count);
                Assert.Equal("Do zapisania: 1, bez zmian: 2, pominięte: 0", w.SummaryTextBlock.Text);
                Assert.False(w.PackageIssuesExpander.IsVisible); // the package skips nothing
                Assert.False(w.WorkingProgress.IsVisible);

                // The store exists: a backup folder is required before the update (the box is checked by default).
                Assert.True(w.MakeBackupBox.IsChecked);
                Assert.False(w.UpdateButton.IsEnabled);
                w.ViewModel.BackupFolder = backups.FullName;
                await UiTestSupport.WaitForIdleAsync();
                Assert.True(w.UpdateButton.IsEnabled);

                var preview = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async p =>
                {
                    await UiTestSupport.WaitForIdleAsync(rounds: 2);
                    Assert.Contains("PolicyDefinitions", p.ViewModel.PowerShellScript, StringComparison.Ordinal);
                    await p.ViewModel.ExecuteCommand.ExecuteAsync(null);
                    await UiWait.UntilAsync(() => p.ViewModel.Step == PlanPreviewStep.Results, "the results");
                    Assert.False(p.ViewModel.ResultHasFailures);
                    p.ViewModel.CloseCommand.Execute(null);
                });
                await w.ViewModel.UpdateCommand.ExecuteAsync(null);
                await preview;

                await UiWait.UntilAsync(() => !w.ViewModel.IsWorking && w.ViewModel.Rows.Count == 3
                    && w.ViewModel.Rows.All(r => r.Status == Core.GroupPolicy.Management.CentralStoreFileStatus.Unchanged), "the new comparison");
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                Assert.Equal(Visibility.Visible, w.ResultBar.Visibility);
                Assert.True(w.ViewModel.ChangedStore);
                Assert.True(w.CloseButton.IsEnabled);
                w.ViewModel.CloseCommand.Execute(null);
            });
            await objects.UpdateTemplatesCommand.ExecuteAsync(null);
            await drive;

            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl), context.Sysvol.GetPolicyDefinition("pl-PL/contoso.adml"));
            Assert.Null(context.Sysvol.GetPolicyDefinition("pl-PL/windows.adml")); // only the template the operator picked
            var backup = Assert.Single(System.IO.Directory.GetDirectories(backups.FullName));
            Assert.True(File.Exists(Path.Combine(backup, "en-US", "contoso.adml")));
            context.AssertHealthy();
        }
        finally
        {
            package.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    });

    [Fact]
    public Task Tree_ShowsTheGroupPolicyNodes_WithTheirMenus_AndOpensTheirDocuments() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();
        var domain = shell.Tree.DomainNode!;
        await domain.EnsureChildrenLoadedAsync();
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(["Obiekty zasad grupy", "Filtry WMI", "Lokacje"], domain.Children.Take(3).Select(c => c.Name));
        var gpoRoot = shell.Tree.GroupPolicyObjectsNode!;
        gpoRoot.IsExpanded = true;
        await gpoRoot.EnsureChildrenLoadedAsync();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(7, gpoRoot.Children.OfType<GpoNodeViewModel>().Count());

        // Menus: the GPO node (delete disabled for a default GPO), the root, the OU (Group Policy items on the OU).
        var views = context.Services.GetRequiredService<ViewLibrary>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var defaultPolicy = gpoRoot.Children.OfType<GpoNodeViewModel>().Single(n => n.GpoId == sample.DefaultDomainPolicy);
        // Open, Stan GPO | Kopia zapasowa…, Kopiuj, Zmień nazwę, Usuń | Porównaj z…, Historia w Watchdogu.
        const int DeleteItem = 5;
        var menu = TreeMenu.Create(defaultPolicy, shell, views, dialogs).OfType<MenuItem>().ToList();
        Assert.Equal(8, menu.Count);
        Assert.False(menu[DeleteItem].IsEnabled);
        var passwords = gpoRoot.Children.OfType<GpoNodeViewModel>().Single(n => n.GpoId == sample.WorkstationPasswords);
        Assert.True(TreeMenu.Create(passwords, shell, views, dialogs).OfType<MenuItem>().ElementAt(DeleteItem).IsEnabled);
        Assert.NotEmpty(TreeMenu.Create(gpoRoot, shell, views, dialogs));
        var wmiNode = domain.Children.OfType<WmiFiltersNodeViewModel>().Single();
        Assert.NotEmpty(TreeMenu.Create(wmiNode, shell, views, dialogs));
        var containerMenu = TreeMenu.Create(domain, shell, views, dialogs).OfType<MenuItem>().ToList();
        Assert.Equal(4, containerMenu.Count(i => Equals(i.CommandParameter, domain.Dn) && i.Command is not null && i.Command.CanExecute(domain.Dn)));

        // Selecting the GPO node opens its document in a tab: five tabs, the Scope tab first.
        passwords.IsSelected = true;
        await UiWait.UntilAsync(() => shell.SelectedTab is GroupPolicyObjectDocumentViewModel { IsLoading: false, LoadedText.Length: > 0 }, "the GPO document");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var gpoView = Assert.Single(UiWait.Descendants<GroupPolicyObjectView>(main));
        Assert.Equal(5, gpoView.GpoTabs.Items.Count);
        Assert.Same(gpoView.ScopeTab, gpoView.GpoTabs.SelectedItem);
        Assert.Equal("Stacje robocze – Zasady haseł", gpoView.TitleText.Text);
        Assert.Single(gpoView.LinksList.Items);
        Assert.True(gpoView.DeleteButton.IsEnabled);
        Assert.True(gpoView.RenameButton.IsEnabled);
        // The button sits in the orphaned-folder bar, which is collapsed for a GPO with its folder.
        Assert.False(gpoView.DeleteOrphanButton.IsVisible);

        // The settings of the security template and the status tab.
        gpoView.GpoTabs.SelectedItem = gpoView.SettingsTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.NotEmpty(gpoView.SettingsList.Items);
        gpoView.GpoTabs.SelectedItem = gpoView.StatusTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.NotEmpty(gpoView.ChecksList.Items);

        // The site opens its "Zasady grupy" document; the objects and WMI filter nodes open theirs.
        var sites = domain.Children.OfType<SitesNodeViewModel>().Single();
        await sites.EnsureChildrenLoadedAsync();
        var site = Assert.Single(sites.Children.OfType<SiteNodeViewModel>());
        Assert.NotEmpty(TreeMenu.Create(site, shell, views, dialogs));
        site.IsSelected = true;
        await UiWait.UntilAsync(() => shell.SelectedTab is GroupPolicyScopeDocumentViewModel { IsLoading: false, Links.Count: 1 }, "the site's links");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var siteView = Assert.Single(UiWait.Descendants<GroupPolicyScopeView>(main));
        // A site cannot block inheritance: the box's row (not the box itself) is collapsed.
        Assert.False(siteView.BlockInheritanceBox.IsVisible);

        gpoRoot.IsSelected = true;
        await UiWait.UntilAsync(() => shell.SelectedTab is GroupPolicyObjectsDocumentViewModel { IsLoading: false, Rows.Count: 7 }, "the GPO list");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var objectsView = Assert.Single(UiWait.Descendants<GroupPolicyObjectsView>(main));
        Assert.Equal(7, objectsView.GpoList.Items.Count);
        var objects = (GroupPolicyObjectsDocumentViewModel)shell.SelectedTab!;
        objectsView.GpoList.SelectedItems.Add(objects.Rows[0]);
        objectsView.GpoList.SelectedItems.Add(objects.Rows[1]);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, objects.Selection.Count);
        Assert.True(objectsView.CompareButton.IsEnabled);
        Assert.False(objectsView.CopyButton.IsEnabled);
        objectsView.GpoList.SelectAll();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(7, objects.Selection.Count);
        Assert.False(objectsView.CompareButton.IsEnabled);
        Assert.True(objectsView.BackupButton.IsEnabled);

        wmiNode.IsSelected = true;
        await UiWait.UntilAsync(() => shell.SelectedTab is WmiFiltersDocumentViewModel { IsLoading: false, Rows.Count: 1 }, "the WMI filters");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Single(Assert.Single(UiWait.Descendants<WmiFiltersView>(main)).FiltersList.Items);
        context.AssertHealthy();
    });

    [Fact]
    public Task AdministrativeTemplatesEditor_EnablesAPolicy_AndSavesItThroughThePreviewWindow() => WpfTestHost.RunAsync(async () =>
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
        Assert.Equal(2, view.CategoryTree.Items.Count);
        Assert.Contains("magazyn centralny", view.DefinitionsText.Text, StringComparison.Ordinal);
        Assert.False(view.SaveButton.IsEnabled);

        editor.SelectedNode = editor.Roots[0].Children.Single(c => c.Name == "Składniki systemu Windows").Children.Single();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(2, view.PolicyList.Items.Count);
        editor.SelectedPolicy = editor.Policies.Single(p => p.Name == "Konfiguruj aktualizacje automatyczne");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        editor.Editor!.IsEnabled = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var options = Assert.Single(UiWait.Descendants<ItemsControl>(view), i => i.Name == "OptionsList");
        Assert.Equal(4, options.Items.Count);
        Assert.True(view.SaveButton.IsEnabled);

        // The search narrows the list – to another policy, so the selected one is no longer listed and loses its selection.
        editor.SearchText = "wuserver";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(view.PolicyList.Items);
        Assert.Null(editor.Editor);
        editor.SearchText = string.Empty;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        editor.SelectedPolicy = editor.Policies.Single(p => p.Name == "Konfiguruj aktualizacje automatyczne");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(editor.Editor!.IsEnabled); // the edit stays in the document

        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async w =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Contains("Set-GPRegistryValue", w.ViewModel.PowerShellScript, StringComparison.Ordinal);
            await w.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => w.ViewModel.Step == PlanPreviewStep.Results, "the results");
            Assert.False(w.ViewModel.ResultHasFailures);
            w.ViewModel.CloseCommand.Execute(null);
        });
        await editor.SaveCommand.ExecuteAsync(null);
        await drive;

        Assert.NotNull(context.Sysvol.Get(sample.SitePrinters, "Machine/Registry.pol"));
        await UiWait.UntilAsync(() => !editor.IsLoading && !editor.HasChanges, "the saved templates");

        // After the reload the selected policy can still be edited, and its state radios belong to this editor only.
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.True(editor.Editor!.CanEdit);
        var radios = UiWait.Descendants<RadioButton>(view).ToList();
        Assert.Equal(3, radios.Count);
        Assert.All(radios, r => Assert.True(r.IsEnabled));
        Assert.All(radios, r => Assert.True(string.IsNullOrEmpty(r.GroupName)));
        Assert.True(Assert.Single(radios, r => r.Name == "EnabledRadio").IsChecked);
        context.AssertHealthy();
    });

    [Fact]
    public Task SecuritySettingsEditor_ShowsEveryPage_AndSavesARightThroughThePreviewWindow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        context.Sysvol.Put(sample.SitePrinters, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 12", "NieznanyKlucz = 1",
            "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544",
            "[Group Membership]", "*S-1-5-32-544__Members = *S-1-5-32-544",
            "[Service General Setting]", "\"Spooler\",4,\"\"")));
        context.Sysvol.PutText(sample.SitePrinters, "Machine/Microsoft/Windows NT/Audit/audit.csv",
            "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\n,System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success,,1\r\n");
        context.Sysvol.PutText(sample.SitePrinters, GppGroupsParser.RelativePath, FakeDirectoryBuilder.Groups(
            FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", [("Łukasz", null, "ADD")], targeted: true)));

        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var editor = Assert.IsType<SecuritySettingsEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" }));
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.UserRights.Count > 0, "the security settings");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<SecuritySettingsEditorView>(main));
        Assert.Equal(5, view.Pages.Items.Count);
        Assert.False(view.SaveButton.IsEnabled);
        Assert.Single(view.RestrictedGroupList.Items);
        Assert.Single(view.GppGroupList.Items);

        // Every page renders (the binding listener checks each one).
        foreach (var page in new[] { view.RightsTab, view.OptionsTab, view.AuditTab, view.ServicesTab, view.GroupsTab })
        {
            view.Pages.SelectedItem = page;
            await UiTestSupport.WaitForIdleAsync(rounds: 3);
        }

        editor.SelectedRestrictedGroup = editor.RestrictedGroups[0];
        editor.SelectedGppGroup = editor.GppGroups[0];
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        view.Pages.SelectedItem = view.OptionsTab;
        editor.SelectedOptionGroup = editor.OptionGroups[0];
        editor.SelectedOption = editor.Options.Single(o => o.Definition.Id == "MinimumPasswordLength");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains("Minimalna długość hasła", UiWait.Descendants<TextBlock>(view).Select(t => t.Text));
        editor.SelectedOptionGroup = editor.OptionGroups[^1];
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(view.RawList.Items);
        Assert.Equal(9, view.LegacyAuditList.Items.Count);
        Assert.Single(view.ServiceList.Items);

        view.Pages.SelectedItem = view.RightsTab;
        editor.SelectedRight = editor.UserRights.Single(r => r.Constant == "SeShutdownPrivilege");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        editor.SelectedRight!.IsDefined = true;
        editor.NewRightHolderText = "Administratorzy";
        editor.AddRightHolderByNameCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Single(UiWait.Descendants<ListBox>(view).Single(l => l.Name == "RightHoldersList").Items);
        Assert.True(view.SaveButton.IsEnabled);

        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async w =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Contains("Get-GPO", w.ViewModel.PowerShellScript, StringComparison.Ordinal);
            await w.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => w.ViewModel.Step == PlanPreviewStep.Results, "the results");
            Assert.False(w.ViewModel.ResultHasFailures);
            w.ViewModel.CloseCommand.Execute(null);
        });
        await editor.SaveCommand.ExecuteAsync(null);
        await drive;

        await UiWait.UntilAsync(() => !editor.IsLoading && !editor.HasChanges, "the saved security settings");
        var written = SecurityTemplateDocument.Parse(context.Sysvol.Get(sample.SitePrinters, SecurityTemplate.RelativePath));
        Assert.Equal(["S-1-5-32-544"], written.GetPrivilegeRights()["SeShutdownPrivilege"]);
        Assert.Equal("1", written.Get(SecurityTemplateDocument.SystemAccess, "NieznanyKlucz"));
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        context.AssertHealthy();
    });

    [Fact]
    public Task ModelingDocument_ShowsWhatAComputerGets_WhyASettingWins_AndRecalculatesWithTheWmiAssumption() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        context.Directory.Session.ComputesTokenGroups = true;
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // K5: "Co dostanie komputer…" (Akcja → Komputer, the list's menu, palette gpo.model) for WAW-PC001.
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "gpo.model");
        Assert.True(shell.ModelComputerGroupPolicyCommand.CanExecute(context.Company.Computer1));
        shell.ModelComputerGroupPolicyCommand.Execute(context.Company.Computer1);
        var model = Assert.IsType<GroupPolicyModelingDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !model.IsLoading && model.HasModel, "the model of WAW-PC001");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GroupPolicyModelingView>(main));
        Assert.Equal("Co dostanie komputer – WAW-PC001", view.TitleText.Text);
        Assert.Same(view.ComputerTab, view.PartTabs.SelectedItem);
        Assert.Equal(Visibility.Collapsed, view.StaleText.Visibility);
        var gpos = Assert.Single(UiWait.Descendants<ListView>(view), l => l.Name == "GposList" && l.IsVisible);
        Assert.Equal(model.ComputerPart.Gpos.Count, gpos.Items.Count);
        Assert.Equal(3, model.ComputerPart.Gpos.Count(g => g.Applies));

        // "Dlaczego" of the minimum password length: the workstations' GPO wins over the Default Domain Policy.
        var settings = Assert.Single(UiWait.Descendants<ListView>(view), l => l.Name == "SettingsList" && l.IsVisible);
        settings.SelectedItem = model.ComputerPart.Settings.Single(s => s.Name == "Minimalna długość hasła");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var details = Assert.Single(UiWait.Descendants<TextBox>(view), t => t.Name == "DetailsText" && t.IsVisible);
        Assert.StartsWith("Wygrywa: Stacje robocze – Zasady haseł (pierwszeństwo 2)", details.Text, StringComparison.Ordinal);
        Assert.Contains("• Default Domain Policy (pierwszeństwo 3): 7 znaków", details.Text, StringComparison.Ordinal);
        Assert.True(Assert.Single(UiWait.Descendants<Button>(view), b => b.Name == "EditModelSettingButton" && b.IsVisible).IsEnabled); // "Edytuj to ustawienie" (K6)

        // WMI filters assumed false: "Przelicz" drops the workstations' GPO.
        view.WmiBox.SelectedValue = Core.GroupPolicy.Modeling.WmiFilterAssumption.AssumeFalse;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(model.IsStale);
        Assert.Equal(Visibility.Visible, view.StaleText.Visibility);
        Assert.True(view.RecalculateButton.IsEnabled);
        await model.RecalculateCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !model.IsLoading && !model.IsStale && model.HasModel, "the recalculated model");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(2, model.ComputerPart.Gpos.Count(g => g.Applies));
        Assert.Contains(model.ComputerPart.Gpos, g => g.OutcomeText == "Filtr WMI „Tylko stacje robocze” przyjęty jako niespełniony");
        Assert.True(view.ExportCsvButton.IsEnabled);

        // The user part of a computer-only model says so.
        view.UserTab.IsSelected = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains(UiWait.Descendants<TextBlock>(view), t => t.Name == "UnavailableText" && t.IsVisible && t.Text == "Model nie obejmuje użytkownika.");
        context.AssertHealthy();
    });

    [Fact]
    public Task ScopeDocument_EnforcesALink_ThroughThePreviewWindow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // "Zasady grupy" of OU=Komputery (Akcja → Zasady grupy, the list toolbar and the OU's menu use the same command).
        Assert.True(shell.OpenContainerPoliciesCommand.CanExecute(context.Company.ComputersOu));
        shell.OpenContainerPoliciesCommand.Execute(context.Company.ComputersOu);
        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !scope.IsLoading && scope.Links.Count == 2, "the links of OU=Komputery");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GroupPolicyScopeView>(main));
        Assert.Equal(2, view.LinksList.Items.Count);
        Assert.True(view.BlockInheritanceBox.IsVisible);
        Assert.False(view.BlockInheritanceBox.IsChecked);
        Assert.NotEmpty(view.InheritanceList.Items);
        Assert.False(view.ToggleEnforcedButton.IsEnabled);

        view.LinksList.SelectedItem = scope.Links.Single(l => l.Link.GpoId == sample.WorkstationPasswords);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(view.ToggleEnforcedButton.IsEnabled);

        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async w =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.NotEmpty(w.RowsList.Items);
            Assert.Contains("Set-GPLink", w.ViewModel.PowerShellScript, StringComparison.Ordinal);
            Assert.Equal(w.ViewModel.PowerShellScript, w.PowerShellBox.Text);
            Assert.True(w.ExecuteButton.IsEnabled);
            Assert.False(w.UndoButton.IsVisible && w.UndoButton.IsEnabled);
            await w.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => w.ViewModel.Step == PlanPreviewStep.Results, "the results");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Same(w.ResultsTab, w.PreviewTabs.SelectedItem);
            Assert.NotEmpty(w.ResultsList.Items);
            Assert.False(w.ViewModel.ResultHasFailures);
            Assert.True(w.UndoButton.IsEnabled);
            w.ViewModel.CloseCommand.Execute(null);
        });
        await scope.ToggleEnforcedCommand.ExecuteAsync(null);
        await drive;

        var link = GpLinkValue.Parse(context.Directory.Session.GetValues(context.Company.ComputersOu, Attr.GpLink).Single(), "DC=contoso,DC=test")
            .Find(sample.WorkstationPasswords)!;
        Assert.True(link.Enforced);
        await UiWait.UntilAsync(() => scope.Links.Any(l => l.Link.GpoId == sample.WorkstationPasswords && l.Enforced), "the refreshed links");
        Assert.Empty(context.Sysvol.Writes);
        context.AssertHealthy();
    });

    [Fact]
    public Task NewLinkedGpo_ThroughTheDialogs_CreatesTheGpoAndItsSysvolFolder() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();

        const string Name = "Stacje robocze – Zapora „Łódź”";
        var name = UiWait.DriveModalAsync<NewGpoWindow>(async w =>
        {
            Assert.Equal(Visibility.Visible, w.LinkTargetTextBlock.Visibility);
            w.ViewModel.Name = Name;
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(Name, w.NameBox.Text);
            w.ViewModel.OkCommand.Execute(null);
        });
        var preview = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async w =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.NotEmpty(w.RowsList.Items);
            Assert.Contains("New-GPO", w.PowerShellBox.Text, StringComparison.Ordinal);
            await w.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => w.ViewModel.Step == PlanPreviewStep.Results, "the results");
            Assert.False(w.ViewModel.ResultHasFailures, w.ViewModel.ResultText);
            w.ViewModel.CloseCommand.Execute(null);
        });
        await main.Shell.NewLinkedGpoCommand.ExecuteAsync(context.Company.ComputersOu);
        await name;
        await preview;

        var reader = context.Services.GetRequiredService<GroupPolicyServices>().Reader;
        var created = (await reader.ReadPoliciesAsync(context.Directory.Session, CancellationToken.None)).Single(g => g.DisplayName == Name);
        Assert.True(context.Sysvol.FolderExists(created.Id));
        Assert.NotNull(context.Sysvol.GetText(created.Id, "GPT.INI"));
        Assert.NotNull(GpLinkValue.Parse(context.Directory.Session.GetValues(context.Company.ComputersOu, Attr.GpLink).Single(), "DC=contoso,DC=test").Find(created.Id));
        context.AssertHealthy();
    });

    [Fact]
    public Task BackupWindow_WritesToTheChosenFolder_AndTheComparisonWindowShowsTheDifferences() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var session = context.Directory.Session;
        var passwords = (await gp.Reader.ReadPolicyAsync(session, sample.WorkstationPasswords, CancellationToken.None))!;
        var remoteDesktop = (await gp.Reader.ReadPolicyAsync(session, sample.WorkstationRemoteDesktop, CancellationToken.None))!;

        // A test-only folder: backups are written only where the operator points the window.
        var folder = Path.Combine(Path.GetTempPath(), "admng-k2-ui-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            var backup = (GpoBackupWindow)windows.CreateWindow(new GpoBackupViewModel(gp, GpoBackupMode.Backup, [passwords]));
            UiTestSupport.ShowOffScreen(backup);
            await UiWait.InitializedAsync(backup.Initialization);
            backup.ViewModel.Folder = folder;
            backup.ViewModel.Comment = "Przed zmianą haseł – Łukasz";
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(folder, backup.FolderBox.Text);
            Assert.Contains("Backup-GPO", backup.PowerShellBox.Text, StringComparison.Ordinal);
            Assert.True(backup.RunButton.IsEnabled);
            await backup.ViewModel.RunCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => !backup.ViewModel.IsBusy, "the backup");
            Assert.Null(backup.ViewModel.Error);
            var entry = Assert.Single(GpmcBackupFormat.List(folder));
            Assert.Equal(sample.WorkstationPasswords, entry.GpoId);
            Assert.Equal("Przed zmianą haseł – Łukasz", entry.Comment);
            backup.Close();

            var restore = (GpoBackupWindow)windows.CreateWindow(new GpoBackupViewModel(gp, GpoBackupMode.Restore, []));
            UiTestSupport.ShowOffScreen(restore);
            await UiWait.InitializedAsync(restore.Initialization);
            restore.ViewModel.Folder = folder;
            await restore.ViewModel.LoadBackupsCommand.ExecuteAsync(null);
            await UiTestSupport.WaitForIdleAsync();
            Assert.Single(restore.BackupList.Items);
            restore.Close();
        }
        finally
        {
            System.IO.Directory.Delete(folder, recursive: true);
        }

        // The comparison window is modeless: the two GPOs side by side, only the differences by default.
        await gp.Dialogs.ShowComparisonAsync(new GpoComparisonViewModel(gp, passwords, remoteDesktop));
        var comparison = Assert.Single(Application.Current.Windows.OfType<GpoComparisonWindow>());
        await UiWait.InitializedAsync(comparison.Initialization);
        await UiWait.UntilAsync(() => !comparison.ViewModel.IsLoading && comparison.ViewModel.Rows.Count > 0, "the comparison");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Stacje robocze – Zasady haseł", comparison.ColumnA.Header);
        Assert.Equal("Stacje robocze – Pulpit zdalny", comparison.ColumnB.Header);
        Assert.True(comparison.OnlyDifferencesBox.IsChecked);
        Assert.All(comparison.ViewModel.Rows, r => Assert.True(r.IsDifferent));
        var differences = comparison.ComparisonList.Items.Count;
        comparison.ViewModel.OnlyDifferences = false;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(comparison.ComparisonList.Items.Count >= differences);
        Assert.Equal(comparison.ViewModel.SummaryText, comparison.SummaryText.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task PickerWindow_ListsTheDomainsGpos_WithoutTheExcludedOnes_FiltersAndReturnsTheChosenOne() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var all = await gp.Reader.ReadPoliciesAsync(context.Directory.Session, CancellationToken.None);

        // "Połącz istniejący GPO…" offers every GPO of the domain except those already linked to the target.
        var picker = (GpoPickerWindow)windows.CreateWindow(new GpoPickerViewModel(
            gp.Ui, gp.Reader, "Połącz istniejący GPO z Komputery", multiSelect: false, [sample.WorkstationPasswords]));
        UiTestSupport.ShowOffScreen(picker);
        await UiWait.InitializedAsync(picker.Initialization);
        await UiWait.UntilAsync(() => !picker.ViewModel.IsLoading, "the GPOs of the domain");
        await UiTestSupport.WaitForIdleAsync();
        // Every window of a connection names its domain in the title (several connections at once).
        Assert.Equal("Połącz istniejący GPO z Komputery – contoso.test", picker.Title);
        Assert.Equal(Visibility.Collapsed, picker.ErrorBar.Visibility);
        Assert.Equal(all.Count - 1, picker.GpoList.Items.Count);
        Assert.DoesNotContain(picker.GpoList.Items.Cast<GpoPickerItem>(), i => i.Gpo.Id == sample.WorkstationPasswords);
        Assert.False(picker.OkButton.IsEnabled);

        // The filter narrows the list by name (case-insensitive, Polish letters included).
        picker.ViewModel.FilterText = "PULPIT ZDALNY";
        await UiTestSupport.WaitForIdleAsync();
        var item = Assert.Single(picker.GpoList.Items.Cast<GpoPickerItem>());
        Assert.Equal("Stacje robocze – Pulpit zdalny", item.Name);

        picker.GpoList.SelectedItem = item;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(picker.OkButton.IsEnabled);
        picker.OkButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(picker.Result);
        Assert.False(picker.IsVisible);
        Assert.Equal(sample.WorkstationRemoteDesktop, Assert.Single(picker.ViewModel.Result).Id);
        context.AssertHealthy();
    });

    [Fact]
    public Task PreviewWindow_ShowsTheSysvolRows_AndTheDiffOfAFile() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var preview = new ChangePlanPreviewViewModel(gp.Ui, gp.Engines,
            new ChangePlanPreviewRequest(GroupPolicySamples.Plan("dc01.contoso.test"), "Import ustawień")
            {
                Notes = ["Zmiany GPO trafią na dc01.contoso.test; emulatorem PDC jest dc02.contoso.test."],
            });
        var window = (ChangePlanPreviewWindow)windows.CreateWindow(preview);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Single(window.NotesList.Items);
        Assert.True(window.RowsList.Items.Count >= 3);
        Assert.Same(window.ChangesTab, window.PreviewTabs.SelectedItem);
        preview.SelectedRow = preview.Rows.First(r => r.Attribute.Contains("GptTmpl.inf", StringComparison.OrdinalIgnoreCase));
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotEmpty(window.DiffList.Items);
        Assert.Equal(preview.DiffHeader, window.DiffHeaderText.Text);
        window.PreviewTabs.SelectedItem = window.PowerShellTab;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(preview.PowerShellScript, window.PowerShellBox.Text);
        Assert.Contains("GptTmpl.inf", window.PowerShellBox.Text, StringComparison.Ordinal);
        context.AssertHealthy();
    });
}
