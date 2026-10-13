using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Shell;

public class ShellViewModelTests
{
    [Fact]
    public async Task Initialize_ShowsHeader_TreeAndDomainTab()
    {
        var (shell, _) = await CreateShellAsync();

        Assert.Equal("Active Directory Users and Computers – contoso.test", shell.WindowTitle);
        Assert.Equal("dc01.contoso.test", shell.ServerText);
        Assert.Equal(@"CONTOSO\helpdesk", shell.IdentityText);
        Assert.Equal("Sign and seal (Kerberos, port 389)", shell.SecurityText);
        var document = Assert.Single(shell.Documents);
        Assert.Same(document, shell.SelectedDocument);
        Assert.Equal("contoso.test", document.Title);
        Assert.Equal("contoso.test", shell.AddressText);
        Assert.NotNull(shell.Tree.DomainNode);
        Assert.False(shell.IsPrivilegedIdentity);
    }

    [Fact]
    public async Task PrivilegedIdentity_IsDetectedFromTokenGroups()
    {
        var harness = await TestHarness.ConnectedAsync();
        var domainAdmins = AdConvert.SidToBytes("S-1-5-21-1004336348-1177238915-682003330-512");
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Helpdesk)!, Attr.TokenGroups, domainAdmins));
        var shell = harness.CreateShell();

        await shell.InitializeAsync();

        await ObjectListViewModelTests.WaitUntilAsync(() => shell.IsPrivilegedIdentity);
        Assert.True(PrivilegeCheck.IsPrivilegedSid("S-1-5-32-544"));
        Assert.False(PrivilegeCheck.IsPrivilegedSid("S-1-5-21-1-2-3-513"));
    }

    [Fact]
    public async Task QuickSearch_IsDebounced_AndOnlyTheLastQueryRuns()
    {
        var (shell, harness) = await CreateShellAsync();
        var before = harness.Directory.Searches.Count;

        shell.SearchText = "ł";
        shell.SearchText = "łu";
        harness.Time.Advance(TimeSpan.FromMilliseconds(100));
        shell.SearchText = "łuk";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await shell.PendingSearch;

        // Only the last text reached the directory (other background reads, e.g. the privilege check, are ignored).
        var searches = harness.Directory.Searches.Skip(before).Where(s => s.Filter.Contains("(anr=", StringComparison.Ordinal)).ToList();
        var query = Assert.Single(searches);
        Assert.Equal(ShellViewModel.SuggestionLimit, query.SizeLimit);
        Assert.Equal(SearchScope.Subtree, query.Scope);
        Assert.StartsWith("(&(anr=łuk)", query.Filter, StringComparison.Ordinal);
        var suggestion = Assert.Single(shell.SearchSuggestions);
        Assert.Equal("Łukasz Żółć", suggestion.Name);
        Assert.Equal("contoso.test/Firma/IT", suggestion.Location);
        Assert.True(shell.IsSuggestionsOpen);
    }

    [Fact]
    public async Task QuickSearch_Enter_OpensSuggestion_ShowAll_OpensUnlimitedSearchTab()
    {
        var (shell, harness) = await CreateShellAsync();
        shell.SearchText = "ewa";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await shell.PendingSearch;

        await shell.SubmitSearchCommand.ExecuteAsync(null);
        Assert.Equal(It, shell.SelectedDocument!.Location!.BaseDn, ignoreCase: true);
        Assert.Equal(Ewa, shell.SelectedDocument.SelectedItem?.Dn);

        await shell.ShowAllSearchResultsCommand.ExecuteAsync(null);
        Assert.Equal(2, shell.Documents.Count);
        var results = shell.SelectedDocument!;
        Assert.Equal("Search: ewa", results.Title);
        Assert.Equal(0, results.LastSearch!.SizeLimit);
        Assert.Equal(["Ewa Kowalska"], results.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task Disable_GoesThroughPlannerAndExecutor_AndRefreshesOnlyTheChangedRow()
    {
        var (shell, harness) = await CreateShellAsync(It);
        var row = Select(shell, "Łukasz Żółć");
        harness.Executor.OnExecute = _ =>
            harness.Directory.Put(WithAttribute(harness.Directory.Find(Lukasz)!, Attr.UserAccountControl, "514"));
        Assert.True(shell.DisableCommand.CanExecute(null));
        Assert.False(shell.EnableCommand.CanExecute(null));

        await shell.DisableCommand.ExecuteAsync(null);
        await ObjectListViewModelTests.WaitUntilAsync(() => row.IsDisabled);

        var request = Assert.Single(harness.Planner.Requests);
        Assert.Equal([Lukasz], request.TargetDns);
        Assert.Equal(new SetAccountEnabledOperation(false), Assert.Single(request.Operations));
        Assert.Single(harness.Executor.Plans);
        Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
        Assert.Same(row, shell.SelectedDocument!.Items.Single(r => r.Dn == Lukasz));
        Assert.Single(harness.Activities.Activities);
    }

    [Fact]
    public async Task Unlock_IsEnabledOnlyForLockedAccounts()
    {
        var (shell, harness) = await CreateShellAsync(It);

        Select(shell, "Łukasz Żółć");
        Assert.False(shell.UnlockCommand.CanExecute(null));

        Select(shell, "Jan Nowak");
        Assert.True(shell.UnlockCommand.CanExecute(null));
        await shell.UnlockCommand.ExecuteAsync(null);

        Assert.IsType<UnlockAccountOperation>(Assert.Single(Assert.Single(harness.Planner.Requests).Operations));
    }

    [Fact]
    public async Task LargeSelection_OpensBatchEditorPreview_InsteadOfExecuting()
    {
        var (shell, harness) = await CreateShellAsync(It);
        await harness.Settings.UpdateAsync(s => s.ConfirmationThreshold = 2, TestContext.Current.CancellationToken);
        SelectMany(shell, "Łukasz Żółć", "Jan Nowak", "Stary Zamek");

        await shell.DisableCommand.ExecuteAsync(null);

        var request = Assert.Single(harness.Dialogs.BatchEditorRequests);
        Assert.True(request.OpenPreview);
        Assert.Equal(3, request.TargetDns.Count);
        Assert.IsType<SetAccountEnabledOperation>(Assert.Single(request.Operations));
        Assert.Empty(harness.Planner.Requests);
        Assert.Empty(harness.Executor.Plans);
    }

    [Fact]
    public async Task PlanErrors_AreShownLocalized_AndNothingIsExecuted()
    {
        var (shell, harness) = await CreateShellAsync(It);
        harness.Planner.PlanFactory = r => new ChangePlan
        {
            Objects = [RecordingChangePlanner.WithIssue(r.TargetDns[0], IssueSeverity.Error, IssueCodes.NotWritable, Attr.Department)],
        };
        Select(shell, "Łukasz Żółć");

        await shell.DisableCommand.ExecuteAsync(null);

        var error = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Disable Account", error.Title);
        Assert.Contains("Łukasz Żółć: You do not have permission to change 'Department'.", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Executor.Plans);
    }

    [Fact]
    public async Task Delete_AsksForConfirmation_AndDeletesThroughThePipeline()
    {
        var (shell, harness) = await CreateShellAsync(It);
        Select(shell, "Ewa Kowalska");
        harness.Executor.OnExecute = _ => harness.Directory.Remove(Ewa);

        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await shell.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(harness.Planner.Requests);

        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await shell.DeleteCommand.ExecuteAsync(null);

        Assert.Contains(harness.Dialogs.Shown, s => s.Contains("delete the User named 'Ewa Kowalska'", StringComparison.Ordinal));
        Assert.Equal(new DeleteOperation(false), Assert.Single(Assert.Single(harness.Planner.Requests).Operations));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.SelectedDocument!.Items.All(r => r.Dn != Ewa));
    }

    [Fact]
    public async Task Delete_ContainerWithChildren_RequiresTypedConfirmation_AndSubtreeDelete()
    {
        var (shell, harness) = await CreateShellAsync(Firma);
        Select(shell, "IT");
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);

        await shell.DeleteCommand.ExecuteAsync(null);

        var typed = Assert.Single(harness.Dialogs.TypedConfirmations);
        Assert.Equal("IT", typed.Expected);
        Assert.Contains("deletes everything inside it", typed.Message, StringComparison.Ordinal);
        Assert.Equal(new DeleteOperation(true), Assert.Single(Assert.Single(harness.Planner.Requests).Operations));
    }

    [Fact]
    public async Task Move_UsesContainerPicker_AndRejectsInvalidTargets()
    {
        var (shell, harness) = await CreateShellAsync(Firma);
        Select(shell, "IT");

        harness.Dialogs.PickedContainers.Enqueue("OU=Sub,OU=IT,OU=Firma,DC=contoso,DC=test");
        await shell.MoveCommand.ExecuteAsync(null);
        Assert.Single(harness.Dialogs.Errors);
        Assert.Empty(harness.Planner.Requests);

        harness.Dialogs.PickedContainers.Enqueue(Ksiegowosc);
        await shell.MoveCommand.ExecuteAsync(null);
        Assert.Equal(new MoveOperation(Ksiegowosc), Assert.Single(Assert.Single(harness.Planner.Requests).Operations));
    }

    [Fact]
    public async Task DragDropMove_ConfirmsAndMoves()
    {
        var (shell, harness) = await CreateShellAsync(It);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);

        Assert.False(shell.CanDrop([It], It));
        Assert.False(shell.CanDrop([Firma], It));
        Assert.False(shell.CanDrop([Lukasz], It));
        Assert.True(shell.CanDrop([Lukasz], Ksiegowosc));
        var outcome = await shell.MoveAsync([Lukasz, Ewa], Ksiegowosc);

        Assert.Equal(ChangeOutcome.Executed, outcome);
        Assert.Contains(harness.Dialogs.Shown, s => s.Contains("Move 2 object(s) to contoso.test/Firma/Księgowość?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_EscapesTemplateBraces_AndUpdatesDisplayNameForUsers()
    {
        var (shell, harness) = await CreateShellAsync(It);
        var row = Select(shell, "Łukasz Żółć");
        ObjectRowViewModel? requested = null;
        shell.RenameRequested += (_, r) => requested = r;

        shell.BeginRenameCommand.Execute(null);
        var ok = await shell.RenameAsync(row, " Łukasz {Admin} Żółć ");

        Assert.Same(row, requested);
        Assert.True(ok);
        Assert.Equal(new RenameOperation("Łukasz {{Admin}} Żółć", AlsoSetDisplayName: true), Assert.Single(Assert.Single(harness.Planner.Requests).Operations));
        Assert.False(await shell.RenameAsync(row, "   "));
        Assert.False(await shell.RenameAsync(row, new string('x', 65)));
    }

    [Fact]
    public async Task AddToGroup_UsesGroupPicker()
    {
        var (shell, harness) = await CreateShellAsync(It);
        SelectMany(shell, "Łukasz Żółć", "WS-001");
        harness.Dialogs.PickedObjects.Enqueue([ItGroup]);

        await shell.AddToGroupCommand.ExecuteAsync(null);

        var request = Assert.Single(harness.Planner.Requests);
        Assert.Equal([Lukasz, Workstation], request.TargetDns);
        Assert.Equal([ItGroup], Assert.IsType<AddToGroupsOperation>(Assert.Single(request.Operations)).GroupDns);
    }

    [Fact]
    public async Task Properties_OpensSheetForOne_AndBatchEditorForMany()
    {
        var (shell, harness) = await CreateShellAsync(It);
        Select(shell, "Ewa Kowalska");
        await shell.PropertiesCommand.ExecuteAsync(null);
        Assert.Equal([Ewa], harness.Dialogs.PropertiesShown);

        SelectMany(shell, "Ewa Kowalska", "Jan Nowak");
        await shell.PropertiesCommand.ExecuteAsync(null);
        Assert.Equal(2, Assert.Single(harness.Dialogs.BatchEditorRequests).TargetDns.Count);

        Select(shell, "Jan Nowak");
        await shell.ResetPasswordCommand.ExecuteAsync(null);
        Assert.Equal([Jan], Assert.Single(harness.Dialogs.ResetPasswordRequests));
    }

    [Fact]
    public async Task AddressBar_AcceptsCanonicalPathsAndDns()
    {
        var (shell, _) = await CreateShellAsync();

        shell.AddressText = "contoso.test/Firma/Księgowość";
        await shell.NavigateToAddressCommand.ExecuteAsync(null);
        Assert.Equal(Ksiegowosc, shell.SelectedDocument!.Location!.BaseDn, ignoreCase: true);
        Assert.Equal("contoso.test/Firma/Księgowość", shell.AddressText);

        shell.AddressText = Ewa;
        await shell.NavigateToAddressCommand.ExecuteAsync(null);
        Assert.Equal(It, shell.SelectedDocument.Location!.BaseDn, ignoreCase: true);
        Assert.Equal(Ewa, shell.SelectedDocument.SelectedItem?.Dn);

        shell.AddressText = "contoso.test/Nie ma takiego";
        await shell.NavigateToAddressCommand.ExecuteAsync(null);
        Assert.Equal("The address was not found in the directory.", shell.AddressError);
    }

    [Fact]
    public async Task TreeSelection_NavigatesTheCurrentTab_AndListNavigationSyncsTheTree()
    {
        var (shell, _) = await CreateShellAsync();
        var users = shell.Tree.DomainNode!.Children.Single(c => c.Name == "Users");

        users.IsSelected = true;
        await shell.SelectedDocument!.CurrentLoad;
        Assert.Equal(Users, shell.SelectedDocument.Location!.BaseDn);

        await shell.NavigateAsync(ListLocation.ForContainer(It));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.Tree.SelectedNode is ContainerNodeViewModel { Name: "IT" });
        Assert.Single(shell.Documents);
    }

    [Fact]
    public async Task ViewNode_OpensBuiltInView()
    {
        var (shell, _) = await CreateShellAsync();
        var quick = (FolderNodeViewModel)shell.Tree.ViewsNode.Children[0];
        var locked = quick.Children.OfType<ViewNodeViewModel>().Single(v => v.BuiltIn!.Kind == BuiltInViewKind.LockedUsers);

        locked.IsSelected = true;
        await shell.SelectedDocument!.CurrentLoad;

        Assert.Equal("Locked out users", shell.SelectedDocument.Title);
        Assert.Equal(["Jan Nowak"], shell.SelectedDocument.Items.Select(r => r.Name));
        Assert.Equal("Locked out users", shell.AddressText);
    }

    [Fact]
    public async Task ExportList_WritesVisibleRowsAsCsv_WithFormulaProtection()
    {
        var (shell, harness) = await CreateShellAsync(It);
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Lukasz)!, Attr.Description, "=HYPERLINK(\"x\")"));
        await shell.SelectedDocument!.LoadAsync();
        shell.SelectedDocument.QuickFilterText = "łukasz";
        var path = Path.Combine(Path.GetTempPath(), $"admng-export-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        try
        {
            await shell.ExportListCommand.ExecuteAsync(null);

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("Name,Type,Description", lines[0]);
            Assert.Equal("Łukasz Żółć,User,\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("IT_", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
            Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CopyDn_PutsSelectedDnsOnTheClipboard()
    {
        var (shell, harness) = await CreateShellAsync(It);
        SelectMany(shell, "Ewa Kowalska", "Jan Nowak");

        shell.CopyDnCommand.Execute(null);

        Assert.Equal($"{Ewa}{Environment.NewLine}{Jan}", harness.Clipboard.Text);
    }

    [Fact]
    public async Task SaveAsView_StoresAPortableView_WithColumnsAndChips()
    {
        var (shell, harness) = await CreateShellAsync(It);
        shell.SelectedDocument!.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).IsActive = true;
        await shell.SelectedDocument.CurrentLoad;
        harness.Dialogs.ViewEditorHandler = editor =>
        {
            editor.Name = "IT – wyłączeni";
            editor.SaveCommand.Execute(null);
            return true;
        };

        await shell.SaveAsViewCommand.ExecuteAsync(null);

        var view = Assert.Single(harness.Views.SavedViews);
        Assert.Equal("IT – wyłączeni", view.Name);
        Assert.Equal("OU=IT,OU=Firma,{domain}", view.BaseDn);
        Assert.Equal(SearchScope.OneLevel, view.Scope);
        Assert.Equal([QuickFilterKind.Disabled], view.QuickFilters);
        Assert.Equal(["name", "objectClass", "description"], view.Columns);
        Assert.Contains(shell.Tree.ViewsNode.Children, n => n.Name == "IT – wyłączeni");
    }

    [Fact]
    public async Task PinContainer_AddsItToViews_Once()
    {
        var (shell, harness) = await CreateShellAsync(It);

        await shell.PinContainerCommand.ExecuteAsync(null);

        Assert.Equal(It, Assert.Single(harness.Views.PinnedContainers).DistinguishedName);
        Assert.False(shell.PinContainerCommand.CanExecute(null));
    }

    [Fact]
    public async Task DetailsPane_ReadsTheSelectedObjectLive_AfterTheSelectionSettles()
    {
        var (shell, harness) = await CreateShellAsync(It);
        var reads = harness.Directory.GetCount;

        Select(shell, "Jan Nowak");
        Assert.Null(shell.SelectedObject);
        harness.Time.Advance(TimeSpan.FromMilliseconds(150));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.SelectedObject is not null);

        var summary = shell.SelectedObject!;
        Assert.Equal("Jan Nowak", summary.Name);
        Assert.True(summary.IsLocked);
        Assert.Contains(summary.Fields, f => f.Label == "Status" && f.Value == "Locked out");
        Assert.Equal(reads + 1, harness.Directory.GetCount);

        SelectMany(shell, "Jan Nowak", "WS-001");
        Assert.Equal("2 objects selected", shell.SelectedObject!.Name);
    }

    [Fact]
    public async Task CommandPalette_FiltersFuzzily_AndOffersOnlyExecutableCommands()
    {
        var (shell, _) = await CreateShellAsync(It);
        var palette = shell.CommandPalette;

        palette.OpenCommand.Execute(null);
        palette.FilterText = "rfrsh";
        Assert.Equal("Refresh", palette.Results.First().Title);
        Assert.Equal("F5", palette.Results.First().Shortcut);

        palette.FilterText = "unlock";
        Assert.DoesNotContain(palette.Results, r => r.Descriptor.Id == "object.unlock");

        Select(shell, "Jan Nowak");
        palette.Refresh();
        Assert.Equal("object.unlock", palette.Results.First().Descriptor.Id);
        Assert.Equal(shell.CommandDescriptors.Count, shell.CommandDescriptors.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public async Task ToggleAdvancedFeatures_ReloadsTreeAndContainerTabs()
    {
        var (shell, _) = await CreateShellAsync();
        Assert.DoesNotContain(shell.SelectedDocument!.Items, r => r.Name == "System");

        await shell.ToggleAdvancedFeaturesCommand.ExecuteAsync(null);
        await shell.SelectedDocument.CurrentLoad;

        Assert.True(shell.ShowAdvancedFeatures);
        Assert.Contains(shell.SelectedDocument.Items, r => r.Name == "System");
        Assert.Contains(shell.Tree.DomainNode!.Children, c => c.Name == "System");
    }

    [Fact]
    public async Task NewObject_UsesCurrentContainer_AndSelectsTheCreatedObject()
    {
        var (shell, harness) = await CreateShellAsync(It);
        const string created = "CN=Anna Nowa,OU=IT,OU=Firma,DC=contoso,DC=test";
        harness.Dialogs.NewObjectResult = created;

        await shell.NewUserCommand.ExecuteAsync(null);
        harness.Directory.Put(User(created, "anna.nowa"));
        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Created, created, ObjectKind: ObjectKind.User)]);

        Assert.Contains($"new:User:{It}", harness.Dialogs.Shown);
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.SelectedDocument!.SelectedItem?.Dn == created);
    }

    [Fact]
    public async Task Tabs_OpenAndClose()
    {
        var (shell, _) = await CreateShellAsync(Firma);
        Select(shell, "IT");

        await shell.OpenInNewTabCommand.ExecuteAsync(null);
        Assert.Equal(2, shell.Documents.Count);
        Assert.Equal("IT", shell.SelectedDocument!.Title);

        shell.CloseTabCommand.Execute(null);
        Assert.Single(shell.Documents);
        Assert.Equal("Firma", shell.SelectedDocument!.Title);
    }

    [Fact]
    public async Task Disconnect_ClosesTheSession_AndShowsTheConnectWindow()
    {
        var (shell, harness) = await CreateShellAsync();

        await shell.DisconnectCommand.ExecuteAsync(null);

        Assert.True(harness.Directory.Disposed);
        Assert.Empty(shell.Documents);
        Assert.Contains("connect", harness.Dialogs.Shown);
        Assert.False(shell.IsConnected);

        // The connect window reconnects: the shell rebuilds itself for the new session.
        await harness.Sessions.ConnectAsync(new ConnectionOptions(), null, TestContext.Current.CancellationToken);
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.Documents.Count == 1 && shell.Tree.DomainNode is not null);
        Assert.True(shell.IsConnected);
    }

    [Fact]
    public async Task Polish_Ui_UsesPolishTitlesAndStatus()
    {
        var harness = await TestHarness.ConnectedAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));
        var shell = harness.CreateShell();
        await shell.InitializeAsync();

        Assert.Equal("Użytkownicy i komputery usługi Active Directory – contoso.test", shell.WindowTitle);
        Assert.Equal(["Widoki", "contoso.test"], shell.Tree.Roots.Select(r => r.Name));
        Assert.StartsWith("Obiektów: 5", shell.StatusText, StringComparison.Ordinal);
    }

    private static async Task<(ShellViewModel Shell, TestHarness Harness)> CreateShellAsync(string? container = null)
    {
        var harness = await TestHarness.ConnectedAsync();
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        if (container is not null)
        {
            await shell.NavigateAsync(ListLocation.ForContainer(container));
        }

        return (shell, harness);
    }

    private static ObjectRowViewModel Select(ShellViewModel shell, string name)
    {
        var document = shell.SelectedDocument!;
        document.SelectedItems = null;
        var row = document.Items.Single(r => r.Name == name);
        document.SelectedItem = row;
        return row;
    }

    private static void SelectMany(ShellViewModel shell, params string[] names)
    {
        var document = shell.SelectedDocument!;
        var rows = names.Select(n => document.Items.Single(r => r.Name == n)).ToList();
        document.SelectedItem = rows[0];
        document.SelectedItems = rows;
    }
}
