using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Filters;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Filters;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>
/// The "Uprawnienia" tab of the account review (README U4a) end to end: the real engines over the fake DC with the Polish
/// sample company and <see cref="FakeDirectoryBuilder.AddPrivilegedAccessSample"/>.
/// </summary>
public class PrivilegedAccessViewModelTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Run_ListsEveryPrivilegedAccount_WithLevelsPathsAndScope_InPolish()
    {
        var (harness, sample) = await CreateAsync(Polish);
        using var review = await RunAsync(harness);
        var tab = review.Privileges;

        Assert.Null(review.Error);
        Assert.Null(tab.Error);
        Assert.True(tab.HasRun);
        Assert.False(tab.IsRunning);
        Assert.Equal(17, tab.AccountCount);
        Assert.Equal([9, 1, 5, 2], tab.LevelTiles.Select(t => t.Count));
        Assert.Equal(["Tier 0", "Wysoki", "Podwyższony", "Drobny"], tab.LevelTiles.Select(t => t.Title));
        Assert.Equal("Stan na godz. 12:00 · odczytano kont: 30 · z uprawnieniami ponad użytkownika domeny: 17", tab.StatusText);
        Assert.Equal(tab.StatusText, review.FooterText);

        // Sorted by level, then name; the U4b/U4c sources are not offered while no source provides them.
        Assert.Equal(PrivilegeLevel.Tier0, tab.Items[0].Level);
        Assert.Equal(PrivilegeLevel.Minor, tab.Items[^1].Level);
        Assert.Equal(
            [PrivilegeSource.Groups, PrivilegeSource.OperatorRules, PrivilegeSource.AccountSettings, PrivilegeSource.Peers],
            tab.SourceOptions.Select(o => o.Source));

        var henryk = Row(tab, "Henryk Żuławski");
        Assert.Equal("Tier 0", henryk[PrivilegedAccessColumns.Level]);
        Assert.Equal("Użytkownik", henryk[PrivilegedAccessColumns.Type]);
        Assert.Equal("Grupy, Podobne konta", henryk[PrivilegedAccessColumns.Sources]);
        Assert.Equal("Nietypowe", henryk[PrivilegedAccessColumns.Peers]);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", henryk[PrivilegedAccessColumns.Location]);
        Assert.Equal("henryk.zulawski", henryk[PrivilegedAccessColumns.Login]);

        tab.SelectedItem = henryk;
        Assert.Equal("Uprawnienia: Henryk Żuławski", tab.DetailsTitle);
        var group = Assert.IsType<PrivilegeFindingViewModel>(tab.SelectedFinding);
        Assert.Equal("Członek grupy Operatorzy kont", group.Title);
        Assert.Equal("Henryk Żuławski → GG_Helpdesk → GG_Wsparcie_IT → Operatorzy kont", group.PathText);
        Assert.Equal("Domena contoso.test", group.ScopeText);
        Assert.Equal("Tworzy większość kont i grup i nimi zarządza; logowanie na kontrolerach domeny.", group.GrantsText);
        Assert.Equal("Tier 0", group.LevelText);
        Assert.True(group.CanRemoveFromGroup);

        // The primary group of a DC and a peer outlier.
        tab.SelectedItem = Row(tab, "DC01");
        Assert.Equal("DC01 → Kontrolery domeny (grupa podstawowa)", Assert.Single(tab.Findings).PathText);
        Assert.False(tab.SelectedFinding!.CanRemoveFromGroup);
        Assert.StartsWith("Uprawnienie wynika z grupy podstawowej", tab.RemoveFromGroupHint, StringComparison.Ordinal);
        Assert.False(tab.RemoveFromGroupCommand.CanExecute(null));

        tab.SelectedItem = Row(tab, "Dorota Łukasiewicz");
        var peer = Assert.Single(tab.Findings, f => f.Finding.Kind == PrivilegeFindingKind.PeerOutlier);
        Assert.Equal("Grupy nietypowe na tle podobnych kont", peer.Title);
        Assert.Equal("Ma grupy (2), których nie ma nikt lub prawie nikt z 6 innych kont w tej samej jednostce contoso.test/Firma/Pracownicy/Księgowość.", peer.GrantsText);
        Assert.Equal("GG_Kadry_Płace (nikt inny), GG_Serwis_RDP (nikt inny)", peer.ValuesText);
        Assert.Equal("Podobne konta: contoso.test/Firma/Pracownicy/Księgowość", peer.ScopeText);
        Assert.Equal("Podwyższony", peer.LevelText);

        // Settings findings and the built-in accounts.
        tab.SelectedItem = Row(tab, "Usługa SQL");
        Assert.Equal(["Ograniczone delegowanie", "Nazwa SPN na koncie użytkownika (Kerberoasting)"], tab.Findings.Select(f => f.Title));
        Assert.Equal("cifs/fs01.contoso.test", tab.Findings[0].ValuesText);
        tab.SelectedItem = Row(tab, "krbtgt");
        Assert.Equal("Wbudowane konto krbtgt", tab.Findings[0].Title);
        Assert.Equal("krbtgt (wyłączone)", tab.SelectedItem![PrivilegedAccessColumns.Name]);
        _ = sample;
    }

    [Fact]
    public async Task Filters_ByLevel_Source_AndPeerOutliers()
    {
        var (harness, _) = await CreateAsync();
        using var review = await RunAsync(harness);
        var tab = review.Privileges;

        tab.LevelTiles[0].IsActive = false;
        Assert.DoesNotContain(tab.Items, r => r.Level == PrivilegeLevel.Tier0);
        Assert.Equal(8, tab.Items.Count);

        tab.LevelTiles[0].IsActive = true;
        tab.SourceOptions.Single(o => o.Source == PrivilegeSource.Groups).IsActive = false;
        tab.SourceOptions.Single(o => o.Source == PrivilegeSource.Peers).IsActive = false;
        tab.SourceOptions.Single(o => o.Source == PrivilegeSource.OperatorRules).IsActive = false;
        Assert.Equal(
            ["Administrator", "Ignacy Wróblewski", "Jan Kowalski", "Jolanta Ćwik", "krbtgt", "SRV-APP01", "SRV-WEB01", "Stanisław Łęcki", "Usługa SQL", "Usługa WWW"],
            Names(tab));

        foreach (var option in tab.SourceOptions)
        {
            option.IsActive = true;
        }

        tab.OnlyPeerOutliers = true;
        Assert.Equal(["Dorota Łukasiewicz", "Ewa Błaszczyk", "Henryk Żuławski", "Jan Kowalski", "Łukasz Wiśniewski", "Wojciech Ząbek"], Names(tab));
        Assert.Equal(6, tab.SourceOptions.Single(o => o.Source == PrivilegeSource.Peers).Count);

        tab.LevelTiles[1].IsActive = false;
        tab.LevelTiles[2].IsActive = false;
        tab.LevelTiles[3].IsActive = false;
        tab.LevelTiles[0].IsActive = false;
        Assert.True(tab.IsEmpty);
    }

    [Fact]
    public async Task Actions_GoThroughTheChangePlan_AndTheRowsAreReadAgain()
    {
        var (harness, sample) = await CreateAsync();
        using var review = await RunAsync(harness);
        var tab = review.Privileges;

        // Remove from the direct group of the path: the batch editor preview with the group.
        tab.SelectedItem = Row(tab, "Henryk Żuławski");
        Assert.True(tab.RemoveFromGroupCommand.CanExecute(null));
        await tab.RemoveFromGroupCommand.ExecuteAsync(null);
        var request = Assert.Single(harness.Dialogs.BatchEditorRequests);
        Assert.Equal([sample.HenrykZulawski], request.TargetDns);
        Assert.Equal(new RemoveFromGroupsOperation([sample.Helpdesk]).GroupDns, Assert.IsType<RemoveFromGroupsOperation>(Assert.Single(request.Operations)).GroupDns);
        Assert.True(request.OpenPreview);
        Assert.Equal("Remove from GG_Helpdesk", request.Title);

        // Clear an orphaned adminCount: only for such accounts.
        tab.SelectedItem = Row(tab, "Jan Kowalski");
        Assert.False(tab.ClearAdminCountCommand.CanExecute(null));
        tab.SelectedItem = Row(tab, "Ignacy Wróblewski");
        Assert.True(tab.ClearAdminCountCommand.CanExecute(null));
        await tab.ClearAdminCountCommand.ExecuteAsync(null);
        Assert.Equal(Attr.AdminCount, Assert.IsType<ClearAttributeOperation>(Assert.Single(harness.Dialogs.BatchEditorRequests[1].Operations)).Attribute);

        // Disable runs directly (below the threshold); the row is read again and shows it.
        tab.SelectedItem = Row(tab, "Jolanta Ćwik");
        await tab.DisableCommand.ExecuteAsync(null);
        await tab.Refreshing;
        var uac = (UserAccountControl)int.Parse(harness.Value(sample.JolantaCwik, Attr.UserAccountControl)!, CultureInfo.InvariantCulture);
        Assert.True(uac.HasFlag(UserAccountControl.AccountDisable));
        Assert.True(Row(tab, "Jolanta Ćwik").IsDisabled);
        Assert.Equal("Jolanta Ćwik (disabled)", Row(tab, "Jolanta Ćwik")[PrivilegedAccessColumns.Name]);

        // A change made elsewhere (Henryk leaves GG_Helpdesk): the row is re-evaluated, Account Operators is gone.
        await harness.Coordinator.ApplyAsync(new Presentation.Changes.ChangeRequest([sample.HenrykZulawski], [new RemoveFromGroupsOperation([sample.Helpdesk])], "test"), TestContext.Current.CancellationToken);
        await tab.Refreshing;
        Assert.DoesNotContain(tab.Items, r => r.Name == "Henryk Żuławski");
        Assert.Equal(16, tab.AccountCount);

        // Properties, batch editor and CSV export of the shown list.
        tab.SelectedItem = Row(tab, "Wojciech Ząbek");
        await tab.OpenCommand.ExecuteAsync(null);
        Assert.Equal([sample.WojciechZabek], harness.Dialogs.PropertiesShown);
        await tab.BatchEditCommand.ExecuteAsync(null);
        Assert.Equal([sample.WojciechZabek], harness.Dialogs.BatchEditorRequests[^1].TargetDns);
        var path = Path.Combine(Path.GetTempPath(), $"admng-u4-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            await tab.ExportCsvCommand.ExecuteAsync(null);
            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(tab.Items.Count + 1, lines.Length);
            Assert.Contains("Logon name", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("Distinguished name", lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, l => l.Contains("Member of DnsAdmins", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Settings_MarksLevelsRulesAndPeers_AreSavedWithoutDirectoryData_AndUsedByTheNextRun()
    {
        var (harness, sample) = await CreateAsync();
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        var tab = review.Privileges;

        // Mark a group through the object picker: SID and portable DN only.
        harness.Dialogs.PickedObjects.Enqueue([sample.Payroll]);
        await tab.MarkGroupCommand.ExecuteAsync(null);
        var mark = Assert.Single(harness.Settings.Current.AccountReview.Privileges.Marks);
        Assert.Equal("CN=GG_Kadry_Płace,OU=Grupy,OU=Firma,{domain}", mark.Dn);
        Assert.StartsWith(harness.Builder.DomainSid + "-", mark.Sid, StringComparison.Ordinal);
        Assert.Equal(PrivilegeLevel.High, mark.Level);
        tab.Marks[0].Note = "Dane płacowe";
        Assert.Equal("Dane płacowe", harness.Settings.Current.AccountReview.Privileges.Marks[0].Note);

        // The level of a built-in group.
        var readers = tab.GroupLevels.Single(g => g.Group.Id == "EventLogReaders");
        readers.Level = readers.Levels.Single(l => l.Value == PrivilegeLevel.High);
        Assert.Equal([new PrivilegedGroupLevelSetting("EventLogReaders", PrivilegeLevel.High)], harness.Settings.Current.AccountReview.Privileges.LevelOverrides);

        // A rule with a saved filter (accounts with employeeType IT-Admin).
        var library = new FilterLibrary(harness.Settings);
        await library.SaveAsync(new SavedFilter
        {
            Id = "it-admin",
            Name = "Administratorzy IT",
            Definition = FilterDefinition.Of(new FilterCondition(Attr.EmployeeType, FilterOperator.Equals, "IT-Admin")),
        });
        Assert.Equal(["Administratorzy IT"], tab.FilterOptions.Select(f => f.Text));
        await tab.AddRuleCommand.ExecuteAsync(null);
        tab.Rules[0].Level = tab.Rules[0].Levels.Single(l => l.Value == PrivilegeLevel.Tier0);
        Assert.Equal("it-admin", Assert.Single(harness.Settings.Current.AccountReview.Privileges.Rules).FilterId);

        // Peers: switched off.
        tab.PeersByOu = false;
        tab.PeersByDepartment = false;
        Assert.False(harness.Settings.Current.AccountReview.Privileges.Peers.IsEnabled);

        await review.RunCommand.ExecuteAsync(null);
        Assert.Null(tab.Error);
        tab.SelectedItem = Row(tab, "Dorota Łukasiewicz");
        var payroll = Assert.Single(tab.Findings, f => f.Finding.Kind == PrivilegeFindingKind.MarkedGroup);
        Assert.Equal("High", payroll.LevelText);
        Assert.Equal("Dane płacowe", payroll.NoteText);
        Assert.Equal("Member of GG_Kadry_Płace (marked as privileged)", payroll.Title);
        Assert.Equal(PrivilegeLevel.High, Assert.Single(Row(tab, "Łukasz Wiśniewski").Facts.Findings).Level);
        Assert.Contains(Row(tab, "Wojciech Ząbek").Facts.Findings, f => f.Kind == PrivilegeFindingKind.ConditionAccount && f.Level == PrivilegeLevel.Tier0);
        Assert.DoesNotContain(tab.Items, r => r.IsPeerOutlier);

        // Nothing of the results is in the settings.
        var json = JsonSettingsStore.Serialize(harness.Settings.Current);
        Assert.DoesNotContain("Łukasz", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Operatorzy kont", json, StringComparison.Ordinal);

        // A new window loads the settings; removing the mark and the rule saves again.
        using var next = harness.Factory.CreateAccountReview();
        Assert.Equal("Dane płacowe", Assert.Single(next.Privileges.Marks).Note);
        Assert.Equal(PrivilegeLevel.High, next.Privileges.GroupLevels.Single(g => g.Group.Id == "EventLogReaders").Level.Value);
        await next.Privileges.RemoveMarkCommand.ExecuteAsync(next.Privileges.Marks[0]);
        await next.Privileges.RemoveRuleCommand.ExecuteAsync(next.Privileges.Rules[0]);
        Assert.Empty(harness.Settings.Current.AccountReview.Privileges.Marks);
        Assert.Empty(harness.Settings.Current.AccountReview.Privileges.Rules);
    }

    [Fact]
    public async Task ScopeFilter_OfTheWindow_AppliesToBothTabs_ServerAndClientParts()
    {
        var (harness, _) = await CreateAsync();
        var library = new FilterLibrary(harness.Settings);
        await library.SaveAsync(new SavedFilter
        {
            Id = "it",
            Name = "Dział IT",
            Definition = FilterDefinition.Of(
                new FilterCondition(Attr.Department, FilterOperator.Equals, "IT"),
                new FilterCondition(Attr.UserAccountControlComputed, FilterOperator.BitNotSet, "16")),
        });
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.ScopeFilter = review.ScopeFilters.Single(f => f.Text == "Dział IT");
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        await review.RunCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Ewa Błaszczyk", "Henryk Żuławski", "Ignacy Wróblewski", "Jan Kowalski", "Jolanta Ćwik", "Łukasz Wiśniewski", "Stanisław Łęcki", "Wojciech Ząbek"],
            Names(review.Privileges));
        var search = harness.Session.Operations.Last(o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("805306369", StringComparison.Ordinal)).Search!;
        Assert.Contains("(department=IT)", search.Filter, StringComparison.Ordinal);
        Assert.DoesNotContain(Attr.UserAccountControlComputed, search.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Attr.UserAccountControlComputed, search.Attributes);
        Assert.Equal("it", harness.Settings.Current.AccountReview.Scope.FilterId);

        // The checks tab with the same filter: Żaneta (locked, Sprzedaż) is out of scope.
        review.SelectedTabIndex = 0;
        await review.RunCommand.ExecuteAsync(null);
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.Locked);
        Assert.Empty(review.Items);
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.PrivilegedWithProblem);
        Assert.True(review.ShowPrivilegesLink);
        Assert.Equal("All privileged accounts: 8 (Privileges tab) →", review.PrivilegesLinkText);

        // Deleting the filter clears the scope.
        await library.DeleteAsync("it");
        Assert.Equal(string.Empty, review.ScopeFilter.Value);
        Assert.Null(harness.Settings.Current.AccountReview.Scope.FilterId);
    }

    [Fact]
    public async Task LinkFromThePrivilegedTile_OpensTheTab_AndRunsItOnce()
    {
        var (harness, _) = await CreateAsync();
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        await review.RunCommand.ExecuteAsync(null);
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.PrivilegedWithProblem);
        Assert.Equal("All privileged accounts (Privileges tab) →", review.PrivilegesLinkText);

        await review.ShowPrivilegesCommand.ExecuteAsync(null);
        await review.Running;

        Assert.True(review.IsPrivilegesTab);
        Assert.True(review.Privileges.HasRun);
        Assert.Equal(17, review.Privileges.AccountCount);
        Assert.Equal(review.Privileges.StatusText, review.FooterText);
        var searches = harness.Session.Operations.Count;
        await review.ShowPrivilegesCommand.ExecuteAsync(null);
        Assert.Equal(searches, harness.Session.Operations.Count);
    }

    [Fact]
    public async Task SessionEnded_KeepsTheResults_RefusesActions()
    {
        var (harness, _) = await CreateAsync();
        using var review = await RunAsync(harness);
        var tab = review.Privileges;
        tab.SelectedItem = Row(tab, "Jolanta Ćwik");
        Assert.True(tab.DisableCommand.CanExecute(null));
        Assert.True(tab.NewFilterCommand.CanExecute(null));
        Assert.True(tab.ManageFiltersCommand.CanExecute(null));

        await harness.Sessions.DisconnectAsync();

        Assert.Equal(17, tab.AccountCount);
        Assert.False(tab.DisableCommand.CanExecute(null));
        Assert.False(tab.OpenCommand.CanExecute(null));
        Assert.False(review.RunCommand.CanExecute(null));

        // The builder and the saved filters window would read the connection window's next session, not the review's.
        Assert.False(tab.NewFilterCommand.CanExecute(null));
        Assert.False(tab.ManageFiltersCommand.CanExecute(null));
        Assert.False(tab.HasFilters);
        var editors = harness.Dialogs.FilterEditors.Count;
        await tab.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(editors, harness.Dialogs.FilterEditors.Count);
        Assert.Empty(tab.Rules);
    }

    private static async Task<(B2Harness Harness, PrivilegedSample Sample)> CreateAsync(CultureInfo? culture = null)
    {
        var harness = await B2Harness.CreateAsync(culture);
        return (harness, harness.Builder.AddPrivilegedAccessSample(harness.Company));
    }

    private static async Task<AccountReviewViewModel> RunAsync(B2Harness harness)
    {
        var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        await review.RunCommand.ExecuteAsync(null);
        return review;
    }

    private static PrivilegedAccountRowViewModel Row(PrivilegedAccessViewModel tab, string name) => Assert.Single(tab.Items, r => r.Name == name);

    private static IReadOnlyList<string> Names(PrivilegedAccessViewModel tab) =>
        [.. tab.Items.Select(r => r.Name).Order(StringComparer.Create(Polish, ignoreCase: true))];
}
