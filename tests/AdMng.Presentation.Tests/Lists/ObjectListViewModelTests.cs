using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;
using SortKey = AdMng.Core.Directory.SortKey;

namespace AdMng.Presentation.Tests.Lists;

public class ObjectListViewModelTests
{
    [Fact]
    public async Task Container_LoadsChildren_WithAducColumns_AndRequestsOnlyNeededAttributes()
    {
        var harness = await TestHarness.ConnectedAsync();

        var list = await harness.OpenContainerAsync(It);

        Assert.Equal(["name", "objectClass", "description"], list.Columns.Select(c => c.Attribute));
        Assert.Equal(["Name", "Type", "Description"], list.Columns.Select(c => c.Header));
        Assert.Equal(6, list.Items.Count);
        Assert.True(list.IsFullyLoaded);
        Assert.Equal("IT", list.Title);
        var search = list.LastSearch!;
        Assert.Equal(SearchScope.OneLevel, search.Scope);
        Assert.Contains(Attr.UserAccountControl, search.Attributes);
        Assert.Contains(Attr.LockoutTime, search.Attributes);
        Assert.DoesNotContain("*", search.Attributes);
        Assert.Contains("showInAdvancedViewOnly", search.Filter, StringComparison.Ordinal);
        var time = TestHarness.Now.ToString("T", CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal($"6 objects · 0.00 s · as of {time}", list.StatusText);
    }

    [Fact]
    public async Task Rows_ShowLocalizedTypeAndState()
    {
        var harness = await TestHarness.ConnectedAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));

        var list = await harness.OpenContainerAsync(It);
        var rows = list.Items.ToDictionary(r => r.Name);

        Assert.Equal("Użytkownik", rows["Łukasz Żółć"]["objectClass"]);
        Assert.Equal("Grupa zabezpieczeń - globalna", rows["GG-IT"]["objectClass"]);
        Assert.Equal("Grupa zabezpieczeń - globalna", rows["GG-IT"][Attr.GroupType]);
        Assert.Equal("Komputer", rows["WS-001"].TypeText);
        Assert.True(rows["Ewa Kowalska"].IsDisabled);
        Assert.True(rows["Jan Nowak"].IsLocked);
        Assert.False(rows["Stary Zamek"].IsLocked);
        Assert.Equal("contoso.test/Firma/IT", rows["WS-001"][ColumnSets.LocationColumn]);
        Assert.Equal(string.Empty, rows["WS-001"][Attr.Department]);
    }

    [Fact]
    public async Task LargeContainer_StreamsInBatches_OneDispatcherCallPerBatch()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 1200);
        var before = harness.Dispatcher.InvokeCount;

        var list = await harness.OpenContainerAsync("OU=Masowe,DC=contoso,DC=test");

        var flushes = harness.Dispatcher.InvokeCount - before;
        Assert.Equal(1200, list.Items.Count);
        Assert.Equal(1200, list.LoadedCount);
        Assert.InRange(flushes, 3, 10);
    }

    [Fact]
    public async Task NewLoad_CancelsThePreviousOne_AndStaleResultsAreIgnored()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = s => s.BaseDn.StartsWith("OU=Masowe", StringComparison.Ordinal) ? gate.Task : null;

        var first = list.NavigateAsync(ListLocation.ForContainer("OU=Masowe,DC=contoso,DC=test"));
        Assert.True(list.IsLoading);
        await list.NavigateAsync(ListLocation.ForContainer(It));
        gate.SetResult();
        await first;

        Assert.Equal("IT", list.Title);
        Assert.Equal(6, list.Items.Count);
        Assert.All(list.Items, r => Assert.EndsWith(It, r.Dn, StringComparison.OrdinalIgnoreCase));
        Assert.False(list.Partial);
    }

    [Fact]
    public async Task CancelLoad_KeepsLoadedRows_AndMarksTheListPartial()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;

        var load = list.NavigateAsync(ListLocation.ForContainer("OU=Masowe,DC=contoso,DC=test"));
        list.CancelLoadCommand.Execute(null);
        await load;

        Assert.True(list.Partial);
        Assert.False(list.IsLoading);
        Assert.False(list.IsFullyLoaded);
        Assert.StartsWith("Stopped", list.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickFilter_IsCaseAndDiacriticsInsensitive_OverLoadedRows()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var searches = harness.Directory.Searches.Count;

        list.QuickFilterText = "LUKASZ zolc";
        Assert.Equal(["Łukasz Żółć"], list.Items.Select(r => r.Name));
        Assert.Equal("1 of 6 objects match", list.QuickFilterStatus);

        list.QuickFilterText = "ewa.kowal";
        Assert.Equal(["Ewa Kowalska"], list.Items.Select(r => r.Name));

        list.ClearQuickFilterCommand.Execute(null);
        Assert.Equal(6, list.Items.Count);
        Assert.Equal(searches, harness.Directory.Searches.Count);
    }

    [Fact]
    public async Task FilterChips_RequeryTheServer_CombinedWithAnd()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);

        list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).IsActive = true;
        await list.CurrentLoad;

        Assert.Equal(["Ewa Kowalska"], list.Items.Select(r => r.Name));
        Assert.Contains("userAccountControl:1.2.840.113556.1.4.803:=2", list.LastSearch!.Filter, StringComparison.Ordinal);
        Assert.StartsWith("(&", list.LastSearch.Filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockedChip_DropsAccountsWhoseLockoutExpired()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);

        list.FilterChips.Single(c => c.Kind == QuickFilterKind.Locked).IsActive = true;
        await list.CurrentLoad;

        Assert.Equal(["Jan Nowak"], list.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task LocalSort_UsesPolishCollation_AndIsStable()
    {
        var harness = await TestHarness.ConnectedAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));
        const string ou = "OU=Sort,DC=contoso,DC=test";
        harness.Directory.Put(Ou(ou));
        foreach (var name in new[] { "Żaneta", "Zenon", "Łucja", "Lech", "Ćma", "Cyna" })
        {
            harness.Directory.Put(User($"CN={name},{ou}", name.ToLowerInvariant(), department: "same"));
        }

        var list = await harness.OpenContainerAsync(ou);
        await list.SortCommand.ExecuteAsync(list.Columns.First(c => c.Attribute == Attr.Name));

        Assert.Equal(["Cyna", "Ćma", "Lech", "Łucja", "Zenon", "Żaneta"], list.Items.Select(r => r.Name));
        Assert.Null(list.ServerSort);
        Assert.Equal(SortDirection.Ascending, list.Columns.First(c => c.Attribute == Attr.Name).SortDirection);

        await list.ApplyColumnsAsync(["name", "department"]);
        var before = list.Items.Select(r => r.Name).ToList();
        await list.SortCommand.ExecuteAsync(list.Columns.First(c => c.Attribute == Attr.Department));
        Assert.Equal(before, list.Items.Select(r => r.Name));

        await list.SortCommand.ExecuteAsync(list.Columns.First(c => c.Attribute == Attr.Name));
        await list.SortCommand.ExecuteAsync(list.Columns.First(c => c.Attribute == Attr.Name));
        Assert.Equal("Żaneta", list.Items[0].Name);
    }

    [Fact]
    public async Task Sort_WhileLoading_RequeriesWithServerSort()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 100);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = s => s.Sort is null && s.BaseDn.StartsWith("OU=Masowe", StringComparison.Ordinal) ? gate.Task : null;
        var load = list.NavigateAsync(ListLocation.ForContainer("OU=Masowe,DC=contoso,DC=test"));

        await list.SortCommand.ExecuteAsync(list.Columns.First(c => c.Attribute == Attr.Name));
        await load;

        Assert.Equal(new SortKey(Attr.Name, false), list.LastSearch!.Sort);
        Assert.Equal("Użytkownik 0000", list.Items[0].Name);
        Assert.Equal(100, list.Items.Count);
    }

    [Fact]
    public async Task ServerSortUnsupported_FallsBackToLocalSort()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.FailSearch = s => s.Sort is null ? null : new DirectoryException(DirectoryErrorKind.UnsupportedControl, "no sort");
        var list = harness.Lists.CreateList();

        await list.NavigateAsync(ListLocation.ForContainer(It) with { Sort = new SortKey(Attr.Name, Descending: true) });

        Assert.Null(list.Error);
        Assert.Null(list.LastSearch!.Sort);
        Assert.Equal("WS-001", list.Items[0].Name);
    }

    [Fact]
    public async Task RefreshObjects_ReplacesChangedRows_AndRemovesVanishedOnes()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var ewa = list.Items.Single(r => r.Dn == Ewa);
        var changed = new List<string?>();
        ewa.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        harness.Directory.Put(WithAttribute(harness.Directory.Find(Ewa)!, Attr.UserAccountControl, "512"));
        harness.Directory.Remove(Jan);
        var searches = harness.Directory.Searches.Count;
        await list.RefreshObjectsAsync([Ewa, Jan], TestContext.Current.CancellationToken);

        Assert.False(ewa.IsDisabled);
        Assert.Contains(string.Empty, changed);
        Assert.DoesNotContain(list.Items, r => r.Dn == Jan);
        Assert.Equal(5, list.Items.Count);
        Assert.Equal(searches + 2, harness.Directory.Searches.Count);
        Assert.All(harness.Directory.Searches.Skip(searches), s => Assert.Equal(SearchScope.Base, s.Scope));
    }

    [Fact]
    public async Task PublishedChanges_RefreshOnlyAffectedRows_AndHandleMovesAndCreates()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        const string created = "CN=Nowa Osoba,OU=IT,OU=Firma,DC=contoso,DC=test";
        harness.Directory.Put(User(created, "nowa.osoba"));
        harness.Directory.Remove(Stary);
        harness.Directory.Put(User("CN=Stary Zamek,OU=Księgowość,OU=Firma,DC=contoso,DC=test", "stary.zamek"));

        await list.HandleChangesAsync(
        [
            new DirectoryChange(DirectoryChangeKind.Created, created),
            new DirectoryChange(DirectoryChangeKind.Moved, Stary, "CN=Stary Zamek,OU=Księgowość,OU=Firma,DC=contoso,DC=test"),
            new DirectoryChange(DirectoryChangeKind.Modified, "CN=Somebody,OU=Elsewhere,DC=contoso,DC=test"),
        ]);

        Assert.Contains(list.Items, r => r.Name == "Nowa Osoba");
        Assert.DoesNotContain(list.Items, r => r.Name == "Stary Zamek");
        Assert.Equal(6, list.Items.Count);
    }

    [Fact]
    public async Task DeletedLocation_ShowsErrorInsteadOfStaleRows()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);

        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Deleted, It)]);

        Assert.Empty(list.Items);
        Assert.Equal("This container no longer exists.", list.Error);
    }

    [Fact]
    public async Task History_SupportsBackForwardAndUp()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(Firma);
        await list.NavigateAsync(ListLocation.ForContainer(It));
        Assert.True(list.GoBackCommand.CanExecute(null));
        Assert.False(list.GoForwardCommand.CanExecute(null));

        await list.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(Firma, list.Location!.BaseDn);
        Assert.True(list.GoForwardCommand.CanExecute(null));

        await list.GoForwardCommand.ExecuteAsync(null);
        Assert.Equal(It, list.Location!.BaseDn);

        await list.GoUpCommand.ExecuteAsync(null);
        Assert.Equal(Firma, list.Location!.BaseDn, ignoreCase: true);
        await list.GoUpCommand.ExecuteAsync(null);
        Assert.Equal(Domain, list.Location!.BaseDn, ignoreCase: true);
        Assert.Equal("contoso.test", list.Title);
        Assert.False(list.GoUpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Columns_ArePersistedPerLayoutKey_AndNewAttributesTriggerReload()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var searches = harness.Directory.Searches.Count;

        var chooser = list.CreateColumnChooser();
        chooser.AddCommand.Execute(chooser.Available.First(c => c.Attribute == Attr.Department));
        await list.ApplyColumnsAsync(chooser.Result);

        Assert.Equal(["name", "objectClass", "description", "department"], harness.Settings.Current.ColumnLayouts[ColumnSets.ContainerLayoutKey].Select(c => c.Attribute));
        Assert.Equal(searches + 1, harness.Directory.Searches.Count);
        Assert.Contains(Attr.Department, list.LastSearch!.Attributes);
        Assert.Equal("IT", list.Items.Single(r => r.Name == "Łukasz Żółć")[Attr.Department]);

        var other = await harness.OpenContainerAsync(Firma);
        Assert.Equal(4, other.Columns.Count);
        Assert.Equal("Department", other.Columns[3].Header);
    }

    [Fact]
    public async Task ShowingAHiddenColumnAgain_LoadsItsAttribute()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        await list.ApplyColumnsAsync(["name", "objectClass", "department"]);
        var department = list.Columns.Single(c => c.Attribute == Attr.Department);
        department.IsVisible = false;
        await list.LoadAsync();
        Assert.DoesNotContain(Attr.Department, list.LastSearch!.Attributes);

        department.IsVisible = true;
        await list.CurrentLoad;

        Assert.Contains(Attr.Department, list.LastSearch!.Attributes);
        Assert.Equal("IT", list.Items.Single(r => r.Dn == Lukasz)[Attr.Department]);
    }

    [Fact]
    public async Task ColumnChooser_OffersSchemaAttributes_WithoutBinaryOnes()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);

        var chooser = list.CreateColumnChooser();
        chooser.FilterText = "depart";

        Assert.Equal([Attr.Department], chooser.Available.Select(c => c.Attribute));
        chooser.FilterText = string.Empty;
        Assert.Contains(chooser.Available, c => c.Attribute == Attr.OperatingSystem);
        Assert.DoesNotContain(chooser.Available, c => c.Attribute == Attr.ThumbnailPhoto);
        Assert.Contains(chooser.Available, c => c.Attribute == ColumnSets.LocationColumn);
        Assert.Equal(["name", "objectClass", "description"], chooser.Selected.Select(c => c.Attribute));

        chooser.SelectedColumn = chooser.Selected[2];
        chooser.MoveUpCommand.Execute(null);
        Assert.Equal(["name", "description", "objectClass"], chooser.Result);
        chooser.ResetToDefaultCommand.Execute(null);
        Assert.Equal(["name", "objectClass", "description"], chooser.Result);
    }

    [Fact]
    public async Task LiveUpdates_RefreshRowsReportedByTheServer_AndStopOnNavigation()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.SupportsWatch = true;
        var list = await harness.OpenContainerAsync(It);
        Assert.Equal(1, harness.Directory.WatcherCount);

        const string created = "CN=Ktoś Nowy,OU=IT,OU=Firma,DC=contoso,DC=test";
        harness.Directory.Put(User(created, "ktos.nowy"));
        harness.Directory.RaiseChange(created);
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitUntilAsync(() => list.Items.Any(r => r.Dn == created));

        await list.NavigateAsync(ListLocation.ForContainer(Firma));
        await WaitUntilAsync(() => harness.Directory.WatcherCount == 1);
        list.Dispose();
        await WaitUntilAsync(() => harness.Directory.WatcherCount == 0);
    }

    [Fact]
    public async Task ExportSnapshot_ContainsVisibleColumnsAndFilteredRows()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        list.QuickFilterText = "kowalska";

        var snapshot = list.CreateExportSnapshot();
        var rows = snapshot.FormatRows().ToList();

        Assert.Equal(["Name", "Type", "Description"], snapshot.Columns.Select(c => c.Header));
        Assert.Equal(["Ewa Kowalska", "User", string.Empty], rows.Single());
        Assert.Equal("IT_20261013-1200.csv", ListExportSnapshot.SuggestFileName("IT", TestHarness.Now));
        Assert.Equal("a_b_20261013-1200.csv", ListExportSnapshot.SuggestFileName("a/b", TestHarness.Now));
    }

    [Fact]
    public async Task SelectAfterLoad_SelectsTheObjectWhenItArrives()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();
        ObjectRowViewModel? scrolled = null;
        list.ScrollIntoViewRequested += (_, row) => scrolled = row;

        list.SelectAfterLoad(Ewa);
        await list.NavigateAsync(ListLocation.ForContainer(It));

        Assert.Equal(Ewa, list.SelectedItem?.Dn);
        Assert.Same(list.SelectedItem, scrolled);
        Assert.Equal("1 selected", list.SelectionText);
    }

    [Fact]
    public async Task Selection_TracksTheViewsMultiSelection()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var selected = new System.Collections.ObjectModel.ObservableCollection<object>();
        var changes = 0;
        list.SelectionChanged += (_, _) => changes++;
        list.SelectedItems = selected;

        selected.Add(list.Items[0]);
        selected.Add(list.Items[1]);

        Assert.Equal(2, list.Selection.Count);
        Assert.Equal(3, changes);
        Assert.Equal("2 selected", list.SelectionText);
    }

    [Fact]
    public async Task CultureChange_RelocalizesHeadersRowsAndChips()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var row = list.Items.Single(r => r.Name == "WS-001");
        Assert.Equal("Computer", row["objectClass"]);

        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Komputer", row["objectClass"]);
        Assert.Equal("Typ", list.Columns[1].Header);
        Assert.Equal("Wyłączone", list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).Title);
        Assert.Equal("Nieaktywne 90 dni", list.FilterChips.Single(c => c.Kind == QuickFilterKind.Inactive).Title);
    }

    [Fact]
    public async Task NavigationCommands_StayExecutable_WhileALongLoadRuns()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = await harness.OpenContainerAsync(It);
        await list.NavigateAsync(ListLocation.ForContainer("OU=Masowe,DC=contoso,DC=test"));
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = s => s.BaseDn.StartsWith("OU=Masowe", StringComparison.Ordinal) ? gate.Task : null;

        var refresh = list.RefreshCommand.ExecuteAsync(null);
        Assert.True(list.IsLoading);

        // F5, Back and column clicks must not wait for the running load - they cancel it.
        Assert.True(list.RefreshCommand.CanExecute(null));
        Assert.True(list.GoBackCommand.CanExecute(null));
        Assert.True(list.SortCommand.CanExecute(list.Columns[0]));
        await list.GoBackCommand.ExecuteAsync(null);
        await refresh;
        gate.SetResult();

        Assert.Equal(It, list.Location!.BaseDn);
        Assert.Equal(6, list.Items.Count);
        Assert.True(list.IsFullyLoaded);
    }

    [Fact]
    public async Task LiveUpdates_ANewNotification_DoesNotDropARefreshInFlight()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.SupportsWatch = true;
        var list = await harness.OpenContainerAsync(It);
        const string first = "CN=Pierwsza Osoba,OU=IT,OU=Firma,DC=contoso,DC=test";
        const string second = "CN=Druga Osoba,OU=IT,OU=Firma,DC=contoso,DC=test";
        harness.Directory.Put(User(first, "pierwsza"));
        harness.Directory.Put(User(second, "druga"));
        var gate = new TaskCompletionSource();
        harness.Directory.HoldBeforeResults = s => s.Scope == SearchScope.Base ? gate.Task : null;

        harness.Directory.RaiseChange(first);
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitUntilAsync(() => harness.Directory.Searches.Any(s => s.BaseDn == first));

        // The second notification arrives while the first refresh still waits for the DC.
        harness.Directory.RaiseChange(second);
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitUntilAsync(() => harness.Directory.Searches.Any(s => s.BaseDn == second));
        gate.SetResult();

        await WaitUntilAsync(() => list.Items.Any(r => r.Dn == first) && list.Items.Any(r => r.Dn == second));
        list.Dispose();
    }

    [Fact]
    public async Task RenamedSelectedObject_StaysSelected_UnderItsNewDn()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        list.SelectedItem = list.Items.Single(r => r.Dn == Ewa);
        const string renamed = "CN=Ewa Nowak,OU=IT,OU=Firma,DC=contoso,DC=test";
        harness.Directory.Remove(Ewa);
        harness.Directory.Put(User(renamed, "ewa.kowalska", disabled: true));

        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Moved, Ewa, renamed, ObjectKind.User)]);

        Assert.Equal(renamed, list.SelectedItem?.Dn);
        Assert.DoesNotContain(list.Items, r => r.Dn == Ewa);
        Assert.Equal(6, list.Items.Count);
    }

    [Fact]
    public async Task ModifiedObject_ThatNowMatchesTheFilter_IsAdded_AndOneThatNoLongerMatches_IsRemoved()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).IsActive = true;
        await list.CurrentLoad;
        Assert.Equal(["Ewa Kowalska"], list.Items.Select(r => r.Name));
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Lukasz)!, Attr.UserAccountControl, "514"));
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Ewa)!, Attr.UserAccountControl, "512"));

        await list.HandleChangesAsync(
        [
            new DirectoryChange(DirectoryChangeKind.Modified, Lukasz, ObjectKind: ObjectKind.User),
            new DirectoryChange(DirectoryChangeKind.Modified, Ewa, ObjectKind: ObjectKind.User),
        ]);

        Assert.Equal(["Łukasz Żółć"], list.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task NotConnected_ShowsError()
    {
        var harness = TestHarness.Disconnected();
        var list = harness.Lists.CreateList();

        await list.NavigateAsync(ListLocation.ForContainer(It));

        Assert.Equal("Not connected to a domain.", list.Error);
        Assert.False(list.IsLoading);
    }

    /// <summary>
    /// Polls a condition. Tests have no single UI thread (the test dispatcher runs work inline on pool threads), so a
    /// collection may change while the condition enumerates it - that read simply counts as "not yet".
    /// </summary>
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !Evaluate(condition); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(Evaluate(condition));
    }

    private static bool Evaluate(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
