using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
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
    public async Task QuickSearch_Enter_OpensTheHighlightedSuggestion_OtherwiseAnUnlimitedSearchTab()
    {
        var (shell, harness) = await CreateShellAsync();
        shell.SearchText = "ewa";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await shell.PendingSearch;

        // Nothing is pre-highlighted (spec: Enter opens the results tab); the operator arrows down to a suggestion.
        Assert.Null(shell.SelectedSuggestion);
        shell.SelectedSuggestion = Assert.Single(shell.SearchSuggestions);
        await shell.SubmitSearchCommand.ExecuteAsync(null);
        Assert.Equal(It, shell.SelectedDocument!.Location!.BaseDn, ignoreCase: true);
        Assert.Equal(Ewa, shell.SelectedDocument.SelectedItem?.Dn);
        Assert.Single(shell.Documents);
        Assert.False(shell.IsSuggestionsOpen);

        shell.SearchText = "ewa.";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await shell.PendingSearch;
        Assert.True(shell.IsSuggestionsOpen);
        await shell.SubmitSearchCommand.ExecuteAsync(null);

        Assert.Equal(2, shell.Documents.Count);
        var results = shell.SelectedDocument!;
        Assert.Equal("Search: ewa.", results.Title);
        Assert.Equal(0, results.LastSearch!.SizeLimit);
        Assert.Equal(["Ewa Kowalska"], results.Items.Select(r => r.Name));
        Assert.False(shell.IsSuggestionsOpen);
        Assert.False(shell.IsSearching);
    }

    [Fact]
    public async Task QuickSearch_SizeLimitExceeded_ShowsTheEntriesReturnedSoFar()
    {
        var (shell, harness) = await CreateShellAsync();
        shell.SearchText = "łuk";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await shell.PendingSearch;
        Assert.Equal("Łukasz Żółć", Assert.Single(shell.SearchSuggestions).Name);

        // A broad query: the DC returns the first entries, then ends the search with "size limit exceeded".
        harness.Directory.FailAfterResults = s => s.Filter.Contains("(anr=", StringComparison.Ordinal)
            ? new DirectoryException(DirectoryErrorKind.SizeLimitExceeded, "size limit") { LdapResultCode = 4 }
            : null;
        shell.SearchText = "ewa";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await shell.PendingSearch;

        Assert.Equal("Ewa Kowalska", Assert.Single(shell.SearchSuggestions).Name);
        Assert.True(shell.IsSuggestionsOpen);
        Assert.DoesNotContain(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Warning);
    }

    [Fact]
    public async Task SearchAndAddressShortcuts_MoveTheFocus_InsteadOfRunningASearch()
    {
        var (shell, _) = await CreateShellAsync();
        var requests = new List<ShellFocusTarget>();
        shell.FocusRequested += (_, target) => requests.Add(target);
        shell.SearchText = "ewa";
        var documents = shell.Documents.Count;

        shell.CommandPalette.Find("nav.search")!.Command.Execute(null);
        shell.CommandPalette.Find("nav.address")!.Command.Execute(null);

        Assert.Equal([ShellFocusTarget.Search, ShellFocusTarget.Address], requests);
        Assert.Equal(documents, shell.Documents.Count);
        Assert.Equal("Ctrl+E", shell.CommandPalette.Find("nav.search")!.Shortcut);
    }

    [Fact]
    public async Task PinnedContainers_CanBeReordered()
    {
        var (shell, harness) = await CreateShellAsync();
        await harness.Views.PinAsync(It, "IT");
        await harness.Views.PinAsync(Firma, "Firma");
        var it = shell.Tree.ViewsNode.Children.OfType<ContainerNodeViewModel>().Single(n => n.Dn == It);

        await shell.MovePinnedDownCommand.ExecuteAsync(it);

        Assert.Equal([Firma, It], harness.Views.PinnedContainers.Select(p => p.DistinguishedName));
        Assert.Equal(["Firma", "IT"], shell.Tree.ViewsNode.Children.OfType<ContainerNodeViewModel>().Select(n => n.Name));
    }

    [Fact]
    public async Task ShowAllResults_WhileASuggestionQueryRuns_DoesNotLeaveTheSearchBusy()
    {
        var (shell, harness) = await CreateShellAsync();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = s => s.Filter.Contains("(anr=", StringComparison.Ordinal) && s.SizeLimit > 0 ? gate.Task : null;
        shell.SearchText = "ewa";
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.IsSearching);

        await shell.ShowAllSearchResultsCommand.ExecuteAsync(null);
        await shell.PendingSearch;

        Assert.False(shell.IsSearching);
        Assert.Equal("Search: ewa", shell.SelectedDocument!.Title);
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
    public async Task Disable_ReevaluatesCommandsAndDetails_WhenTheSelectedRowChanges()
    {
        var (shell, harness) = await CreateShellAsync(It);
        var row = Select(shell, "Łukasz Żółć");
        harness.Executor.OnExecute = _ =>
            harness.Directory.Put(WithAttribute(harness.Directory.Find(Lukasz)!, Attr.UserAccountControl, "514"));
        var enableNotified = 0;
        shell.EnableCommand.CanExecuteChanged += (_, _) => enableNotified++;

        await shell.DisableCommand.ExecuteAsync(null);
        await ObjectListViewModelTests.WaitUntilAsync(() => row.IsDisabled && enableNotified > 0);

        // The toolbar learns that "Enable" is now possible (CanExecuteChanged), and the details pane re-reads the object.
        Assert.True(shell.EnableCommand.CanExecute(null));
        Assert.False(shell.DisableCommand.CanExecute(null));
        await ObjectListViewModelTests.WaitUntilAsync(() =>
        {
            harness.Time.Advance(TimeSpan.FromMilliseconds(150));
            return shell.SelectedObject is { IsDisabled: true };
        });
    }

    [Fact]
    public async Task ExportList_CanExecute_FollowsTheShownRows()
    {
        var (shell, _) = await CreateShellAsync(It);
        var notified = 0;
        shell.ExportListCommand.CanExecuteChanged += (_, _) => notified++;

        shell.SelectedDocument!.QuickFilterText = "nikogo takiego";
        Assert.True(notified > 0);
        Assert.False(shell.ExportListCommand.CanExecute(null));

        notified = 0;
        shell.SelectedDocument.QuickFilterText = string.Empty;
        Assert.True(notified > 0);
        Assert.True(shell.ExportListCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_WhenTheChildCheckFails_ShowsTheErrorAndDeletesNothing()
    {
        var (shell, harness) = await CreateShellAsync(Firma);
        Select(shell, "IT");
        harness.Directory.FailSearch = s => s.Scope == SearchScope.OneLevel && s.SizeLimit == 1
            ? new DirectoryException(DirectoryErrorKind.AccessDenied, "denied")
            : null;

        await shell.DeleteCommand.ExecuteAsync(null);

        var error = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Access is denied.", error.Message);
        Assert.Empty(harness.Dialogs.TypedConfirmations);
        Assert.Empty(harness.Planner.Requests);
    }

    [Fact]
    public async Task ShellNavigation_StaysExecutable_WhileALongLoadRuns()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = s => s.Scope == SearchScope.OneLevel && s.BaseDn.StartsWith("OU=Masowe", StringComparison.Ordinal) ? gate.Task : null;

        shell.AddressText = "OU=Masowe,DC=contoso,DC=test";
        var navigation = shell.NavigateToAddressCommand.ExecuteAsync(null);
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.SelectedDocument!.IsLoading && shell.SelectedDocument.Title == "Masowe");

        Assert.True(shell.NavigateToAddressCommand.CanExecute(null));
        Assert.True(shell.GoBackCommand.CanExecute(null));
        Assert.True(shell.RefreshCommand.CanExecute(null));
        await shell.GoBackCommand.ExecuteAsync(null);
        await navigation;
        gate.SetResult();

        Assert.Equal("contoso.test", shell.SelectedDocument!.Title);
        Assert.True(shell.SelectedDocument.IsFullyLoaded);
    }

    [Fact]
    public async Task Initialize_JoinsTheInitializationStartedByTheConnect()
    {
        var harness = TestHarness.Disconnected();
        var shell = harness.CreateShell();

        // The shell exists before the first connect: SessionChanged starts the initialization, then the app calls it too.
        await harness.Sessions.ConnectAsync(new ConnectionOptions(), null, TestContext.Current.CancellationToken);
        await shell.InitializeAsync();

        await shell.SelectedDocument!.CurrentLoad;
        Assert.Single(shell.Documents);
        Assert.Equal("contoso.test", shell.SelectedDocument.Title);
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
    public async Task Delete_ComputerWithBitLockerRecoveryInformation_AsksForTypedConfirmation_AndDeletesTheTree()
    {
        // Real planner and executor over the fake DC: a plain delete of an object with children is refused there.
        var harness = await B2Harness.CreateAsync();
        var laptop = harness.Company.Laptop;
        var recovery = harness.Session.Seed(
            $"CN=2026-10-01T08:00:00-00:00{{6E1B0C8F-7A43-4C3E-9B54-0D7C3F1E2A10}},{laptop}",
            new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "msFVE-RecoveryInformation" } });
        using var shell = CreateShell(harness);
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(harness.Company.ComputersOu));
        Select(shell, "KRK-LT001");
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);

        await shell.DeleteCommand.ExecuteAsync(null);

        var typed = Assert.Single(harness.Dialogs.TypedConfirmations);
        Assert.Equal("KRK-LT001", typed.Expected);
        Assert.Contains("contains other objects", typed.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.False(harness.Session.Exists(recovery));
        Assert.False(harness.Session.Exists(laptop));
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
        Assert.Equal(
            new RenameOperation("Łukasz {{Admin}} Żółć", AlsoSetDisplayName: true) { KeepCustomDisplayName = true },
            Assert.Single(Assert.Single(harness.Planner.Requests).Operations));
        Assert.False(await shell.RenameAsync(row, "   "));
        Assert.False(await shell.RenameAsync(row, new string('x', 65)));
    }

    [Fact]
    public async Task Rename_KeepsADisplayNameMaintainedSeparately_AndUpdatesOneThatIsTheOldName()
    {
        // Real planner and executor: F2 executes without a preview, so it must not overwrite a GAL display name.
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.DisplayName, "Kowalski Jan (IT)");
        using var shell = CreateShell(harness);
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(harness.Company.ItOu));

        Assert.True(await shell.RenameAsync(Select(shell, "Jan Kowalski"), "Jan Kowalsky"));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.SelectedDocument!.Items.Any(r => r.Name == "Jan Kowalsky"));
        Assert.True(await shell.RenameAsync(Select(shell, "Łukasz Wiśniewski"), "Łukasz Wiśniewski-Nowak"));

        Assert.Equal("Kowalski Jan (IT)", harness.Value($"CN=Jan Kowalsky,{harness.Company.ItOu}", Attr.DisplayName));
        Assert.Equal("Łukasz Wiśniewski-Nowak", harness.Value($"CN=Łukasz Wiśniewski-Nowak,{harness.Company.ItOu}", Attr.DisplayName));
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
        var (shell, harness) = await CreateShellAsync();

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

        // Two children with the same name (OU=IT and CN=IT): no "size limit exceeded", the first match is used.
        harness.Directory.SizeLimitBehavior = SizeLimitBehavior.ThrowBeforeEntries;
        harness.Directory.Put(Container("CN=IT,OU=Firma,DC=contoso,DC=test"));
        shell.AddressText = "contoso.test/Firma/IT";
        await shell.NavigateToAddressCommand.ExecuteAsync(null);
        Assert.Null(shell.AddressError);
        Assert.Equal("IT", shell.SelectedDocument!.Title);
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
    public async Task SwitchingTabs_SyncsTheTree_SoAClickOnTheOtherTabsContainerOpensIt()
    {
        var (shell, _) = await CreateShellAsync(Ksiegowosc);
        var accounting = shell.SelectedDocument!;
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(It));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.Tree.SelectedNode is ContainerNodeViewModel { Name: "IT" });

        shell.SelectedDocument = accounting;
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.Tree.SelectedNode is ContainerNodeViewModel { Name: "Księgowość" });

        // The operator clicks IT in the tree: the active tab opens it.
        shell.Tree.FindNode(It)!.IsSelected = true;
        await accounting.CurrentLoad;
        Assert.Equal(It, accounting.Location!.BaseDn);
        Assert.Equal(2, shell.Documents.Count);
    }

    [Fact]
    public async Task ClickOnTheSelectedNode_OpensIt_WhenTheTabShowsSomethingElse_AndIsIgnoredOtherwise()
    {
        var (shell, _) = await CreateShellAsync(It);
        var document = shell.SelectedDocument!;
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.Tree.SelectedNode is ContainerNodeViewModel { Name: "IT" });

        // The tab moved on to search results; IT stays highlighted in the tree.
        await shell.NavigateAsync(ListLocation.ForSearch("kowal", Domain, "kowal", "(objectClass=user)"));
        Assert.Same(shell.Tree.FindNode(It), shell.Tree.SelectedNode);

        shell.Tree.ActivateSelected();
        await document.CurrentLoad;
        Assert.Equal(It, document.Location!.BaseDn);

        // Clicking it again: the tab shows IT already - no reload, no history entry.
        var load = document.CurrentLoad;
        shell.Tree.ActivateSelected();
        Assert.Same(load, document.CurrentLoad);
        await document.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(ListLocationKind.Search, document.Location!.Kind);
    }

    [Fact]
    public async Task NewFromTheTreeContextMenu_CreatesInTheClickedContainer_NotInTheTabsContainer()
    {
        var (shell, harness) = await CreateShellAsync(Ksiegowosc);

        await shell.NewUserCommand.ExecuteAsync(It);
        await shell.NewGroupCommand.ExecuteAsync(null);

        Assert.Equal([$"new:User:{It}", $"new:Group:{Ksiegowosc}"], harness.Dialogs.Shown.Where(s => s.StartsWith("new:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NewCommands_FollowTheCreatePermissionsOfTheTargetContainer()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.Permissions = dn => Core.Ldap.DistinguishedName.AreEqual(dn, It)
            ? new EffectivePermissions
            {
                IsKnown = true,
                CreatableChildClasses = new HashSet<string>(["user", "contact"], StringComparer.OrdinalIgnoreCase),
            }
            : null;
        var shell = harness.CreateShell();
        await shell.InitializeAsync();

        await shell.NavigateAsync(ListLocation.ForContainer(It));
        await ObjectListViewModelTests.WaitUntilAsync(() => !shell.NewGroupCommand.CanExecute(null));

        Assert.True(shell.NewUserCommand.CanExecute(null));
        Assert.True(shell.NewContactCommand.CanExecute(null));
        Assert.False(shell.NewOrganizationalUnitCommand.CanExecute(null));
        Assert.False(shell.NewComputerCommand.CanExecute(null));
        Assert.Equal("You do not have permission to create objects of type 'Group' in this container.", shell.CreateBlockedReason(ObjectKind.Group));
        Assert.Equal(shell.CreateBlockedReason(ObjectKind.Group), shell.CommandPalette.Find("new.group")!.DisabledReason!(null));
        Assert.Null(shell.CreateBlockedReason(ObjectKind.User));
        shell.CommandPalette.Refresh();
        Assert.DoesNotContain(shell.CommandPalette.Results, r => r.Descriptor.Id == "new.group");
        Assert.Contains(shell.CommandPalette.Results, r => r.Descriptor.Id == "new.user");

        // Unknown permissions (Księgowość): everything stays enabled and the DC decides.
        Assert.True(shell.NewGroupCommand.CanExecute(Ksiegowosc));
        await shell.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        await ObjectListViewModelTests.WaitUntilAsync(() => shell.NewGroupCommand.CanExecute(null));
        Assert.Null(shell.CreateBlockedReason(ObjectKind.Group));

        // The tree's context menu on IT while the tab shows Księgowość: the clicked container's permissions count.
        await shell.LoadCreatePermissionsAsync(It);
        Assert.False(shell.NewGroupCommand.CanExecute(It));
        Assert.True(shell.NewUserCommand.CanExecute(It));
        Assert.True(shell.NewGroupCommand.CanExecute(null));
        Assert.Contains(It, harness.Directory.PermissionReads);
    }

    [Fact]
    public async Task EditingTheShownSavedView_RunsItsNewDefinition()
    {
        var (shell, harness) = await CreateShellAsync();
        await harness.Views.SaveViewAsync(new SavedView { Id = "wylaczeni", Name = "Wyłączeni", Filter = "(objectClass=user)" });
        var node = shell.Tree.ViewsNode.Children.OfType<ViewNodeViewModel>().Single(v => v.ViewId == "wylaczeni");
        node.IsSelected = true;
        var document = shell.SelectedDocument!;
        await document.CurrentLoad;
        Assert.Equal("(objectClass=user)", document.Location!.Filter);
        harness.Dialogs.ViewEditorHandler = editor =>
        {
            editor.Filter = "(&(objectClass=user)(userAccountControl:1.2.840.113556.1.4.803:=2))";
            editor.SaveCommand.Execute(null);
            return true;
        };

        await shell.EditViewCommand.ExecuteAsync(node);

        await ObjectListViewModelTests.WaitUntilAsync(() => document.Location!.Filter.Contains("803", StringComparison.Ordinal));
        await document.CurrentLoad;
        Assert.Contains("803", document.LastSearch!.Filter, StringComparison.Ordinal);
        Assert.Equal(["Ewa Kowalska"], document.Items.Select(r => r.Name));

        // The new definition replaced the old one in place: Back returns to the domain, not to the outdated view.
        await document.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(ListLocationKind.Container, document.Location!.Kind);
    }

    [Fact]
    public async Task SaveAsView_FromADateBasedBuiltInView_StoresItsChip_NotTodaysDates()
    {
        var (shell, harness) = await CreateShellAsync();
        harness.Dialogs.ViewEditorHandler = editor =>
        {
            editor.SaveCommand.Execute(null);
            return true;
        };
        var quick = (FolderNodeViewModel)shell.Tree.ViewsNode.Children[0];
        foreach (var kind in new[] { BuiltInViewKind.ExpiringAccounts, BuiltInViewKind.LockedUsers, BuiltInViewKind.InactiveComputers })
        {
            quick.Children.OfType<ViewNodeViewModel>().Single(v => v.BuiltIn!.Kind == kind).IsSelected = true;
            await shell.SelectedDocument!.CurrentLoad;
            await shell.SaveAsViewCommand.ExecuteAsync(null);
        }

        var saved = harness.Views.SavedViews;
        Assert.Equal(3, saved.Count);
        Assert.Equal(QuickFilters.Users, saved[0].Filter);
        Assert.Equal([QuickFilterKind.Expiring], saved[0].QuickFilters);
        Assert.Equal(QuickFilters.Users, saved[1].Filter);
        Assert.Equal([QuickFilterKind.Locked], saved[1].QuickFilters);
        Assert.Equal(QuickFilters.Computers, saved[2].Filter);
        Assert.Equal([QuickFilterKind.Inactive], saved[2].QuickFilters);
        Assert.All(saved, v => Assert.DoesNotContain(Attr.AccountExpires, v.Filter, StringComparison.OrdinalIgnoreCase));

        // Opened later, the saved "locked out" view keeps the exact lockout check (Stary Zamek's lockout expired).
        await shell.NavigateAsync(saved[1].ToLocation(Domain));
        Assert.Equal(["Jan Nowak"], shell.SelectedDocument!.Items.Select(r => r.Name));
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
    public async Task ExportList_WritesSecretColumnsOnlyAfterTheExportPasswordsConfirmation()
    {
        const string LapsPassword = "Laps!Sekret#2026";
        var (shell, harness) = await CreateShellAsync(It);
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Lukasz)!, "ms-Mcs-AdmPwd", LapsPassword));
        await shell.SelectedDocument!.ApplyColumnsAsync([Attr.Name, "ms-Mcs-AdmPwd", Attr.Description]);
        shell.SelectedDocument.QuickFilterText = "łukasz";
        var path = Path.Combine(Path.GetTempPath(), $"admng-export-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        try
        {
            // Declined: the list is exported without the secret column.
            harness.Dialogs.ConfirmAnswers.Enqueue(false);
            await shell.ExportListCommand.ExecuteAsync(null);

            var declined = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(LapsPassword, declined, StringComparison.Ordinal);
            Assert.StartsWith("Name,Description", declined, StringComparison.Ordinal);
            var confirmation = Assert.Single(harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));
            Assert.Contains("ms-Mcs-AdmPwd", confirmation, StringComparison.Ordinal);

            // Confirmed: the operator chose to write the values.
            harness.Dialogs.ConfirmAnswers.Enqueue(true);
            await shell.ExportListCommand.ExecuteAsync(null);

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines[0].Split(',').Length);
            Assert.Contains(LapsPassword, lines[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportList_ASavedViewWithASecretColumn_FollowsTheSameRule()
    {
        const string LapsPassword = "Laps!Sekret#2026";
        var (shell, harness) = await CreateShellAsync();
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Lukasz)!, "msLAPS-Password", LapsPassword));
        await harness.Views.SaveViewAsync(new SavedView
        {
            Id = "laps",
            Name = "Hasła LAPS",
            Filter = "(objectClass=user)",
            Columns = [Attr.Name, "msLAPS-Password"],
        });
        shell.Tree.ViewsNode.Children.OfType<ViewNodeViewModel>().Single(v => v.ViewId == "laps").IsSelected = true;
        await shell.SelectedDocument!.CurrentLoad;
        Assert.Contains(shell.SelectedDocument.Items, r => r.Name == "Łukasz Żółć");
        var path = Path.Combine(Path.GetTempPath(), $"admng-export-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        try
        {
            // No answer scripted: the confirmation is declined.
            await shell.ExportListCommand.ExecuteAsync(null);

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("Name", lines[0]);
            Assert.DoesNotContain(lines, l => l.Contains(LapsPassword, StringComparison.Ordinal));
            Assert.Single(harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ListExportSnapshot_TreatsConfidentialSchemaAttributesAsSecret()
    {
        var schema = new SchemaCatalog(
            [new AttributeDefinition { LdapName = "contoso-Pin", Syntax = AttributeSyntax.UnicodeString, IsConfidential = true }], []);
        var snapshot = new ListExportSnapshot(
            [(Attr.Name, "Name"), ("contoso-Pin", "PIN"), ("msLAPS-Password", "LAPS"), (Attr.Description, "Description")], []);

        var secret = snapshot.SecretColumns(schema);

        Assert.Equal(["contoso-Pin", "msLAPS-Password"], secret.Select(c => c.Attribute));
        Assert.Equal(["msLAPS-Password"], snapshot.SecretColumns(null).Select(c => c.Attribute));
        Assert.Equal(["Name", "Description"], snapshot.WithoutColumns(secret.Select(c => c.Attribute)).Columns.Select(c => c.Header));
    }

    [Fact]
    public async Task ExportList_UsesTheRegionalListSeparator_NotTheUiLanguage()
    {
        // English UI on Polish Windows: Polish Excel splits columns at ';'.
        var harness = await TestHarness.ConnectedAsync(regional: CultureInfo.GetCultureInfo("pl-PL"));
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(It));
        shell.SelectedDocument!.QuickFilterText = "łukasz";
        var path = Path.Combine(Path.GetTempPath(), $"admng-export-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        try
        {
            await shell.ExportListCommand.ExecuteAsync(null);

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("Name;Type;Description", lines[0]);
            Assert.StartsWith("Łukasz Żółć;User", lines[1], StringComparison.Ordinal);
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
    public async Task PinContainer_AddsItToViews_Once_AndUnpinReenablesIt()
    {
        var (shell, harness) = await CreateShellAsync(It);

        await shell.PinContainerCommand.ExecuteAsync(null);

        Assert.Equal(It, Assert.Single(harness.Views.PinnedContainers).DistinguishedName);
        Assert.False(shell.PinContainerCommand.CanExecute(null));

        var notified = 0;
        shell.PinContainerCommand.CanExecuteChanged += (_, _) => notified++;
        await harness.Views.UnpinAsync(It);
        Assert.True(notified > 0);
        Assert.True(shell.PinContainerCommand.CanExecute(null));
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
    public async Task RecycleBin_IsOfferedWhileConnected_InThePaletteAndAsAShortcut()
    {
        var (shell, harness) = await CreateShellAsync();
        var descriptor = shell.CommandPalette.Find("tools.recycleBin");

        Assert.NotNull(descriptor);
        Assert.Equal("Ctrl+Shift+R", descriptor.Shortcut);
        Assert.Same(shell.ShowRecycleBinCommand, descriptor.Command);
        shell.CommandPalette.OpenCommand.Execute(null);
        shell.CommandPalette.FilterText = "recycle";
        Assert.Equal("tools.recycleBin", shell.CommandPalette.Results.First().Descriptor.Id);
        Assert.Equal("AD Recycle Bin...", shell.CommandPalette.Results.First().Title);

        await shell.ShowRecycleBinCommand.ExecuteAsync(null);
        Assert.Contains("recycleBin", harness.Dialogs.Shown);

        await shell.DisconnectCommand.ExecuteAsync(null);
        Assert.False(shell.ShowRecycleBinCommand.CanExecute(null));
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
        Assert.Null(shell.Tree.DomainNode);
        Assert.Equal([shell.Tree.ViewsNode], shell.Tree.Roots);

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

    [Fact]
    public async Task Dispose_IsIdempotent_TheSecondCallLeavesBoundCollectionsAlone()
    {
        // The main window disposes the shell when it closes; the DI container disposes it again at exit, on another
        // thread - where a collection notification would fail in a bound WPF collection view.
        var (shell, _) = await CreateShellAsync();
        shell.Dispose();
        var notifications = 0;
        shell.Documents.CollectionChanged += (_, _) => notifications++;

        shell.Dispose();

        Assert.Equal(0, notifications);
        Assert.Empty(shell.Documents);
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

    /// <summary>A shell over the real Core engines and the fake DC with the sample company.</summary>
    private static ShellViewModel CreateShell(B2Harness harness)
    {
        var tree = new NavigationTreeViewModel(harness.Sessions, harness.Settings, harness.Localizer, harness.Dispatcher, harness.Errors, harness.Views, harness.Changes);
        return new ShellViewModel(
            harness.Lists, tree, harness.Activities, harness.Coordinator, harness.Views, harness.Dialogs, harness.Files, harness.Clipboard,
            harness.Notifications, new CsvCodec(), harness.Regional);
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
