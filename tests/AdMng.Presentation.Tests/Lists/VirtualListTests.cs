using System.Collections.Specialized;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>Very large containers as server sorted virtual lists (VLV) - docs/dev/phase1-navigation.md.</summary>
public class VirtualListTests
{
    private const string Masowe = "OU=Masowe,DC=contoso,DC=test";
    private const int Users = 120;

    private static string UserDn(int i) => $"CN=Użytkownik {i:0000},{Masowe}";

    private static string UserName(int i) => $"Użytkownik {i:0000}";

    [Fact]
    public async Task LargeContainer_SwitchesToTheVirtualList_AtTheThreshold()
    {
        var (harness, list) = await OpenAsync();

        Assert.True(list.IsVirtual);
        var rows = Assert.IsType<VirtualRowList>(list.Rows);
        Assert.Same(rows, list.VirtualRows);
        Assert.Equal(Users, rows.Count);
        Assert.Empty(list.Items);
        Assert.Equal(Users, list.LoadedCount);
        Assert.False(list.IsFullyLoaded);
        Assert.False(list.IsLoading);

        var (search, window) = Assert.Single(harness.Directory.VirtualListRequests);
        Assert.Equal(Masowe, search.BaseDn);
        Assert.Equal(SearchScope.OneLevel, search.Scope);
        Assert.Equal(new SortKey(Attr.Name), search.Sort);
        Assert.Equal(0, window.Offset);
        Assert.Equal(10, window.Count);
        Assert.Equal(0, window.EstimatedCount);
        Assert.Equal(SortDirection.Ascending, list.Columns.Single(c => c.Attribute == Attr.Name).SortDirection);
        Assert.Equal(UserName(0), rows[0].Name);
        Assert.Equal(UserName(9), rows[9].Name);
        Assert.StartsWith("120 objects · virtual list (VLV), sorted on the server by Name · as of 12:00:00", list.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmallContainers_SearchResultsAndViews_KeepStreaming()
    {
        var (harness, list) = await OpenAsync(It);
        Assert.False(list.IsVirtual);
        Assert.Same(list.Items, list.Rows);
        Assert.NotEmpty(list.Items);

        // A subtree search over all 120 users never becomes virtual (VLV is for one container).
        await list.NavigateAsync(ListLocation.ForSearch("u", Domain, "Search", "(objectClass=user)"));
        Assert.False(list.IsVirtual);
        Assert.True(list.Items.Count > Users);
        Assert.Empty(harness.Directory.VirtualListRequests);
    }

    [Fact]
    public async Task DcRefusingVlv_KeepsTheStream_AndDoesNotAskAgain()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        harness.Directory.FailVirtualList = (_, _) => new DirectoryException(DirectoryErrorKind.UnsupportedControl, "VLV not supported");
        var list = CreateList(harness);

        await list.NavigateAsync(ListLocation.ForContainer(Masowe));
        await list.RefreshAsync();

        Assert.False(list.IsVirtual);
        Assert.Equal(Users, list.Items.Count);
        Assert.Single(harness.Directory.VirtualListRequests);
        Assert.Null(list.Error);
    }

    [Fact]
    public async Task Pages_AreReadOnDemand_PlaceholdersAreReplaced()
    {
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        var events = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, e) => events.Add(e);
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = _ => gate.Task;

        var placeholder = rows[55];
        Assert.True(placeholder.IsPlaceholder);
        Assert.Equal("Loading…", placeholder.Name);
        Assert.Equal(string.Empty, placeholder[Attr.Description]);
        Assert.Same(placeholder, rows[55]);
        Assert.Equal(55, rows.IndexOf(placeholder));
        gate.SetResult();
        await TestWait.UntilAsync(() => events.Count > 0);
        await rows.Idle;

        var loaded = rows[55];
        Assert.False(loaded.IsPlaceholder);
        Assert.Equal(UserName(55), loaded.Name);
        Assert.Equal(55, rows.IndexOf(loaded));
        Assert.Equal(-1, rows.IndexOf(placeholder));
        var replace = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Replace, replace.Action);
        Assert.Same(placeholder, replace.OldItems![0]);
        Assert.Same(loaded, replace.NewItems![0]);
        Assert.Equal(55, replace.NewStartingIndex);

        var window = harness.Directory.VirtualListRequests.Last().Window;
        Assert.Equal(50, window.Offset);
        Assert.NotNull(window.ContextId);
    }

    [Fact]
    public async Task Memory_IsBounded_PendingPagesAreCappedAndOldPagesEvicted()
    {
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = w => w.Offset == 10 ? gate.Task : null;

        _ = rows[10];
        await TestWait.UntilAsync(() => harness.Directory.VirtualListRequests.Any(r => r.Window.Offset == 10));
        for (var i = 2; i < 12; i++)
        {
            _ = rows[i * 10];
        }

        // An enumeration (UI Automation, LINQ) reads nothing.
        Assert.Equal(Users, rows.Count());

        Assert.Equal([8, 9, 10, 11], rows.PendingPages);
        gate.SetResult();
        await TestWait.UntilAsync(() => rows.Idle.IsCompleted && rows.PendingPages.Count == 0);
        await rows.Idle;

        Assert.True(rows.CachedPageCount <= 3, $"{rows.CachedPageCount} pages cached");
        Assert.Equal(UserName(80), rows.GetLoaded(80)?.Name);
        Assert.Null(rows.GetLoaded(110));
        Assert.True(rows.PlaceholderCount <= 11, $"{rows.PlaceholderCount} placeholders");
        Assert.DoesNotContain(harness.Directory.VirtualListRequests, r => r.Window.Offset is 20 or 30 or 70);
    }

    [Fact]
    public async Task Memory_IsBounded_PlaceholdersOfPagesScrolledPastAreForgotten()
    {
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = _ => gate.Task;
        var events = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, e) => events.Add(e);

        // Dragging the scroll thumb over the whole list while the DC is slow: every row the view realizes is a placeholder.
        var early = rows[20];
        var shown = new ObjectRowViewModel[Users];
        for (var i = 10; i < Users; i++)
        {
            shown[i] = rows[i];
            Assert.True(rows.PlaceholderCount <= rows.MaxPlaceholders, $"{rows.PlaceholderCount} placeholders");
        }

        // The placeholders the view may still show (recently shown and pending pages) keep their identity.
        Assert.Equal(-1, rows.IndexOf(early));
        Assert.Same(shown[115], rows[115]);
        Assert.Equal(115, rows.IndexOf(shown[115]));

        gate.SetResult();
        await TestWait.UntilAsync(() => rows.Idle.IsCompleted && rows.PendingPages.Count == 0);
        await rows.Idle;

        // The shown page replaced its placeholders in place; forgotten placeholders got no notification.
        var replaced = Assert.Single(events, e => e.Action == NotifyCollectionChangedAction.Replace && ReferenceEquals(e.OldItems![0], shown[115]));
        Assert.Equal(UserName(115), Assert.IsType<ObjectRowViewModel>(replaced.NewItems![0]).Name);
        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Replace && ReferenceEquals(e.OldItems![0], early));
        Assert.True(rows.PlaceholderCount <= rows.MaxPlaceholders);
    }

    [Fact]
    public async Task HeaderClick_SortsOnTheServer_TypeIsNotSortable()
    {
        var (harness, list) = await OpenAsync();
        var name = list.Columns.Single(c => c.Attribute == Attr.Name);

        await list.SortAsync(name);

        Assert.True(list.IsVirtual);
        Assert.Equal(new SortKey(Attr.Name, Descending: true), harness.Directory.VirtualListRequests.Last().Search.Sort);
        Assert.Equal(UserName(119), list.VirtualRows![0].Name);
        Assert.Equal(SortDirection.Descending, name.SortDirection);

        var requests = harness.Directory.VirtualListRequests.Count;
        await list.SortAsync(list.Columns.Single(c => c.Attribute == ColumnSets.TypeColumn));
        Assert.Equal(requests, harness.Directory.VirtualListRequests.Count);
        Assert.Equal(SortDirection.Descending, name.SortDirection);
    }

    [Fact]
    public async Task RefusedSort_FallsBackToTheStream()
    {
        var (harness, list) = await OpenAsync();
        harness.Directory.FailVirtualList = (search, _) => search.Sort?.Attribute == Attr.Description
            ? new DirectoryException(DirectoryErrorKind.UnwillingToPerform, "sort not supported")
            : null;

        await list.SortAsync(list.Columns.Single(c => c.Attribute == Attr.Description));

        Assert.False(list.IsVirtual);
        Assert.Equal(Users, list.Items.Count);
        Assert.Null(list.Error);
        Assert.Equal(new SortKey(Attr.Description), list.LastSearch!.Sort);
    }

    [Fact]
    public async Task TypeToFind_SeeksOnTheSortColumn_InBothDirections()
    {
        var (harness, list) = await OpenAsync();

        Assert.True(await list.FindByPrefixAsync("użytkownik 0077"));
        Assert.Equal(UserDn(77), list.SelectedItem!.Dn);
        Assert.Equal("użytkownik 0077", harness.Directory.VirtualListRequests.Last(r => r.Window.SeekValue is not null).Window.SeekValue);
        Assert.False(await list.FindByPrefixAsync("Zenon"));
        Assert.Equal(UserDn(77), list.SelectedItem!.Dn);

        // Descending: "Użytkownik 004" seeks below all "Użytkownik 004x" - the block before the target is walked back.
        await list.SortAsync(list.Columns.Single(c => c.Attribute == Attr.Name));
        Assert.True(await list.FindByPrefixAsync("Użytkownik 004"));
        Assert.Equal(UserDn(49), list.SelectedItem!.Dn);
        Assert.False(await list.FindByPrefixAsync("Ala"));
    }

    [Fact]
    public async Task TypeToFind_AnOlderPrefixAnsweredLate_DoesNotOverrideTheNewerOne()
    {
        // Typing "Użytkownik 0012" quickly: the seek for the shorter prefix comes back last (another pooled connection).
        var (harness, list) = await OpenAsync();
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = w => w.SeekValue == "Użytkownik 001" ? gate.Task : null;

        var older = list.FindByPrefixAsync("Użytkownik 001");
        await TestWait.UntilAsync(() => harness.Directory.VirtualListRequests.Any(r => r.Window.SeekValue == "Użytkownik 001"));
        Assert.True(await list.FindByPrefixAsync("Użytkownik 0012"));
        Assert.Equal(UserDn(12), list.SelectedItem!.Dn);

        gate.SetResult();
        Assert.False(await older);
        Assert.Equal(UserDn(12), list.SelectedItem!.Dn);
    }

    [Fact]
    public async Task TypeToFind_WalksBack_WhenTheServerSeeksPastTheMatches()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        harness.Directory.SambaSeek = true;
        foreach (var name in new[] { "Zając Łukasz", "Zając Żaneta", "Zenon Abacki" })
        {
            harness.Directory.Put(User($"CN={name},{Masowe}", name.Replace(' ', '.')));
        }

        var list = CreateList(harness);
        await list.NavigateAsync(ListLocation.ForContainer(Masowe));

        Assert.True(await list.FindByPrefixAsync("Zając"));
        Assert.Equal("Zając Łukasz", list.SelectedItem!.Name);
        Assert.Equal("zając", harness.Directory.VirtualListRequests.Last(r => r.Window.SeekValue is not null).Window.SeekValue![..5].ToLowerInvariant());
    }

    [Fact]
    public async Task Locate_FindsAnObjectThatIsNotLoaded()
    {
        var (_, list) = await OpenAsync();
        ObjectRowViewModel? scrolled = null;
        list.ScrollIntoViewRequested += (_, row) => scrolled = row;

        list.SelectAfterLoad(UserDn(93));

        await TestWait.UntilAsync(() => list.SelectedItem?.Dn == UserDn(93) && scrolled is not null);
        Assert.Same(list.SelectedItem, scrolled);
        Assert.Equal(93, list.VirtualRows!.IndexOf(list.SelectedItem));
    }

    [Fact]
    public async Task LiveChange_RereadsTheShownPages_AndKeepsTheSelectionByDn()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        harness.Directory.SupportsWatch = true;
        var list = CreateList(harness);
        await list.NavigateAsync(ListLocation.ForContainer(Masowe));
        var rows = list.VirtualRows!;
        var selected = rows[5];
        list.SelectedItems = new List<object> { selected };
        list.SelectedItem = selected;
        var resets = 0;
        rows.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        var added = $"CN=Użytkownik 0000a,{Masowe}";
        harness.Directory.Put(User(added, "user0000a"));
        harness.Directory.RaiseChange(added);
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));

        await TestWait.UntilAsync(() => rows.Count == Users + 1);
        Assert.Equal(1, resets);
        Assert.Equal(Users + 1, list.LoadedCount);
        Assert.Equal("Użytkownik 0000a", rows[1].Name);
        Assert.Same(selected, rows[6]);
        Assert.Same(selected, list.SelectedItem);
        Assert.Equal([selected], list.Selection);
        Assert.Null(harness.Directory.VirtualListRequests.Last().Window.ContextId);
    }

    [Fact]
    public async Task LiveChange_OfAnAttribute_UpdatesTheRowsInPlace_WithoutAReset()
    {
        // The common live update (an attribute of a listed object): no Reset - a Reset regenerates every row container
        // and takes the keyboard focus out of the list on a busy container.
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        harness.Directory.SupportsWatch = true;
        var list = CreateList(harness);
        await list.NavigateAsync(ListLocation.ForContainer(Masowe));
        var rows = list.VirtualRows!;
        var selected = rows[5];
        list.SelectedItems = new List<object> { selected };
        list.SelectedItem = selected;
        // Only the first read of page 6 is held (the pump's); the refresh reads it again meanwhile.
        var gate = new TaskCompletionSource();
        var held = 0;
        harness.Directory.HoldVirtualList = w => w.Offset == 60 && w.SeekValue is null && Interlocked.Increment(ref held) == 1 ? gate.Task : null;
        var placeholder = rows[65];
        await TestWait.UntilAsync(() => held == 1);
        var events = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, e) => events.Add(e);
        var reloaded = 0;
        rows.Reloaded += (_, _) => reloaded++;
        Assert.Equal(string.Empty, selected[Attr.Description]);

        harness.Directory.Put(WithAttribute(harness.Directory.Find(UserDn(5))!, Attr.Description, "Nowy opis – księgowość"));
        harness.Directory.RaiseChange(UserDn(5));
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => selected[Attr.Description] == "Nowy opis – księgowość");

        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Reset);
        Assert.Equal(0, reloaded);
        Assert.Same(selected, rows[5]);
        Assert.Same(selected, list.SelectedItem);
        Assert.Equal([selected], list.Selection);
        Assert.Equal(Users, rows.Count);

        // The page that was still being read when the refresh started: its placeholders are replaced in place.
        gate.SetResult();
        await TestWait.UntilAsync(() => rows.GetLoaded(65) is not null);
        Assert.Equal(UserName(65), rows[65].Name);
        Assert.Contains(events, e => e.Action == NotifyCollectionChangedAction.Replace && ReferenceEquals(e.OldItems![0], placeholder));
        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task DeletedSelection_IsSoughtOnce_NotOnEveryLaterRefresh()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        harness.Directory.SupportsWatch = true;
        var list = CreateList(harness);
        await list.NavigateAsync(ListLocation.ForContainer(Masowe));
        var rows = list.VirtualRows!;
        var selected = rows[5];
        list.SelectedItems = new List<object> { selected };
        list.SelectedItem = selected;
        int Seeks() => harness.Directory.VirtualListRequests.Count(r => r.Window.SeekValue is not null);

        // The selected object is deleted: after the Reset the list looks for it (it may only have moved) - in vain.
        harness.Directory.Remove(UserDn(5));
        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Deleted, UserDn(5), ObjectKind: ObjectKind.User)]);
        await TestWait.UntilAsync(() => rows.Count == Users - 1 && Seeks() > 0);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // WPF drops the selection of a row that is gone after the Reset.
        list.SelectedItems = new List<object>();
        list.SelectedItem = null;
        var seeks = Seeks();

        // Later live updates of other objects do not look for the deleted object again.
        foreach (var (dn, index) in new[] { (UserDn(7), 6), (UserDn(8), 7) })
        {
            harness.Directory.Put(WithAttribute(harness.Directory.Find(dn)!, Attr.Description, "Zmieniony opis"));
            harness.Directory.RaiseChange(dn);
            harness.Time.Advance(TimeSpan.FromMilliseconds(500));
            await TestWait.UntilAsync(() => rows.GetLoaded(index)?[Attr.Description] == "Zmieniony opis");
        }

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(seeks, Seeks());
        Assert.Null(list.SelectedItem);
    }

    [Fact]
    public async Task InPlaceRefresh_DropsTheCachedPagesItDidNotReread()
    {
        // A rename far from the shown rows moves objects only there (same count): a cached page that was not re-read
        // must not keep showing the old order when the operator scrolls back to it.
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        var list = CreateList(harness);
        list.VirtualMaxCachedPages = 6;
        await list.NavigateAsync(ListLocation.ForContainer(Masowe));
        var rows = list.VirtualRows!;
        foreach (var i in new[] { 95, 15, 25, 5 })
        {
            _ = rows[i];
            await TestWait.UntilAsync(() => rows.GetLoaded(i) is not null);
        }

        var shown = rows[5];
        var events = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, e) => events.Add(e);
        harness.Directory.Remove(UserDn(95));
        harness.Directory.Put(User($"CN=Użytkownik 0098a,{Masowe}", "user0098a"));

        await rows.RefreshAsync();

        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Reset);
        Assert.Same(shown, rows[5]);
        Assert.Null(rows.GetLoaded(95));
        _ = rows[95];
        await TestWait.UntilAsync(() => rows.GetLoaded(95) is not null);
        Assert.Equal([UserName(96), UserName(97), UserName(98), "Użytkownik 0098a", UserName(99)], Enumerable.Range(95, 5).Select(i => rows[i].Name));
    }

    [Fact]
    public async Task FailedRefresh_AsksAgainForThePagesItsStartDropped()
    {
        // A page is being read when a refresh starts (its result is dropped) and the refresh itself fails: the rows that
        // waited for that page must not stay "Loading…" for good.
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        var gate = new TaskCompletionSource();
        var held = 0;
        harness.Directory.HoldVirtualList = w => w.Offset == 60 && Interlocked.Increment(ref held) == 1 ? gate.Task : null;
        var placeholder = rows[65];
        await TestWait.UntilAsync(() => held == 1);
        // The refresh re-reads the shown page without the server context - and the DC is busy.
        harness.Directory.FailVirtualList = (_, w) => w.Offset == 60 && w.ContextId is null
            ? new DirectoryException(DirectoryErrorKind.Busy, "busy")
            : null;

        await Assert.ThrowsAsync<DirectoryException>(rows.RefreshAsync);
        harness.Directory.FailVirtualList = null;
        gate.SetResult();

        await TestWait.UntilAsync(() => rows.GetLoaded(65) is not null);
        Assert.Equal(UserName(65), rows[65].Name);
        Assert.Equal(-1, rows.IndexOf(placeholder));
    }

    [Fact]
    public async Task Refresh_RereadsTheShownPages_EvenWhenTheyWereNotCached()
    {
        var (_, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        foreach (var i in new[] { 30, 60, 90, 110 })
        {
            _ = rows[i];
            await TestWait.UntilAsync(() => rows.GetLoaded(i) is not null);
        }

        Assert.Null(rows.GetLoaded(1));
        _ = rows[1];
        await rows.RefreshAsync();

        Assert.Equal(UserName(1), rows.GetLoaded(1)?.Name);
        Assert.Equal(UserName(110), rows.GetLoaded(110)?.Name);
        Assert.True(rows.CachedPageCount <= 3);
    }

    [Fact]
    public async Task OwnChanges_RefreshTheVirtualList()
    {
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;

        harness.Directory.Remove(UserDn(3));
        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Deleted, UserDn(3), ObjectKind: ObjectKind.User)]);

        await TestWait.UntilAsync(() => rows.Count == Users - 1);
        Assert.Equal(UserName(4), rows[3].Name);
        Assert.True(list.IsVirtual);
    }

    [Fact]
    public async Task QuickFilter_OnALargeContainer_FiltersOnTheServer()
    {
        var (harness, list) = await OpenAsync();

        list.QuickFilterText = "0042";
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => list.VirtualRows?.Count == 1);
        await list.CurrentLoad;

        Assert.True(list.IsVirtual);
        Assert.Contains("(name=*0042*)", harness.Directory.VirtualListRequests.Last().Search.Filter, StringComparison.Ordinal);
        Assert.Equal(UserName(42), list.VirtualRows![0].Name);
        Assert.Equal("1 objects match (filtered on the server)", list.QuickFilterStatus);

        list.QuickFilterText = string.Empty;
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => list.VirtualRows?.Count == Users);
    }

    [Fact]
    public async Task FilterChips_OnALargeContainer_UseVlvWithTheCombinedFilter_ExceptLocked()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        foreach (var i in new[] { 3, 40, 77 })
        {
            harness.Directory.Put(User(UserDn(i), $"user{i:0000}", disabled: true));
        }

        var list = CreateList(harness);
        await list.NavigateAsync(ListLocation.ForContainer(Masowe));
        Assert.True(list.IsVirtual);

        // A server-side chip: still a virtual list (the location is known to be large), with the chip in the filter.
        var disabled = list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled);
        disabled.IsActive = true;
        await list.CurrentLoad;

        Assert.True(list.IsVirtual);
        Assert.Equal(3, list.VirtualRows!.Count);
        Assert.Contains("userAccountControl:1.2.840.113556.1.4.803:=2", harness.Directory.VirtualListRequests.Last().Search.Filter, StringComparison.Ordinal);
        Assert.Equal([UserName(3), UserName(40), UserName(77)], list.VirtualRows.Select(r => r.Name));

        // "Locked" drops rows on the client (expired lockouts): it streams the whole container instead.
        disabled.IsActive = false;
        await list.CurrentLoad;
        var requests = harness.Directory.VirtualListRequests.Count;
        list.FilterChips.Single(c => c.Kind == QuickFilterKind.Locked).IsActive = true;
        await list.CurrentLoad;

        Assert.False(list.IsVirtual);
        Assert.Same(list.Items, list.Rows);
        Assert.Equal(requests, harness.Directory.VirtualListRequests.Count);
        Assert.Null(list.Error);
    }

    [Fact]
    public async Task NavigatingAway_LeavesTheVirtualMode_AndBackReturnsToIt()
    {
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;

        await list.NavigateAsync(ListLocation.ForContainer(It));
        Assert.False(list.IsVirtual);
        Assert.Same(list.Items, list.Rows);
        Assert.Null(list.VirtualRows);

        // The old list is disposed: it reads nothing any more.
        var requests = harness.Directory.VirtualListRequests.Count;
        _ = rows[70];
        await rows.Idle;
        Assert.Equal(requests, harness.Directory.VirtualListRequests.Count);

        await list.GoBackCommand.ExecuteAsync(null);
        Assert.True(list.IsVirtual);
    }

    [Fact]
    public async Task SelectedPlaceholder_IsReplacedBySelectedRow_WhenItsPageArrives()
    {
        // End (or PageDown) in a large list selects a "Loading…" row; WPF's Selector unselects an item that a Replace
        // notification swaps out - the operator would be left without a selection once the page arrived.
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = _ => gate.Task;
        var last = rows[Users - 1];
        var other = rows[Users - 2];
        Assert.True(last.IsPlaceholder);
        var selection = new System.Collections.ObjectModel.ObservableCollection<object> { last };
        list.SelectedItems = selection;
        list.SelectedItem = last;

        // Like Selector.OnItemsChanged(Replace): the replaced item leaves the selection (subscribed after the list).
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Replace && selection.Remove(e.OldItems![0]!) && ReferenceEquals(list.SelectedItem, e.OldItems[0]))
            {
                list.SelectedItem = null;
            }
        };

        gate.SetResult();
        await TestWait.UntilAsync(() => rows.GetLoaded(Users - 1) is not null);
        await rows.Idle;

        var loaded = rows[Users - 1];
        Assert.Equal(UserName(Users - 1), loaded.Name);
        Assert.Same(loaded, list.SelectedItem);
        Assert.Equal([loaded], selection);
        Assert.Equal([loaded], list.Selection);
        Assert.DoesNotContain(rows[Users - 2], selection);
        Assert.True(other.IsPlaceholder);
    }

    [Fact]
    public async Task Placeholders_AreNeverPartOfTheSelection()
    {
        var (harness, list) = await OpenAsync();
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = _ => gate.Task;
        var placeholder = list.VirtualRows![99];

        list.SelectedItem = placeholder;
        Assert.Empty(list.Selection);
        list.SelectedItems = new List<object> { placeholder, list.VirtualRows[0] };
        Assert.Equal([list.VirtualRows[0]], list.Selection);
        Assert.Equal("1 selected", list.SelectionText);
        gate.SetResult();
    }

    [Fact]
    public async Task Shell_ShowsTheVirtualListBadge_ForTheSelectedTabOnly_AndDisablesExport()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var document = shell.SelectedDocument!;
        document.VirtualThreshold = 50;
        var changes = new List<string?>();
        shell.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await shell.NavigateAsync(ListLocation.ForContainer(Masowe));

        Assert.True(shell.IsVirtualListShown);
        Assert.Contains(nameof(shell.IsVirtualListShown), changes);
        Assert.StartsWith("120 objects · virtual list (VLV)", shell.StatusText, StringComparison.Ordinal);
        Assert.False(shell.ExportListCommand.CanExecute(null));

        await shell.NewTabCommand.ExecuteAsync(null);
        await shell.NavigateAsync(ListLocation.ForContainer(It));
        Assert.False(shell.IsVirtualListShown);
        shell.SelectedDocument = document;
        Assert.True(shell.IsVirtualListShown);
    }

    [Fact]
    public async Task Shell_OpenItem_OnAPlaceholderRow_DoesNothing()
    {
        // Double click / Enter on a "Loading…" row: it has no DN - no properties of "" (the RootDSE), no navigation.
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var document = shell.SelectedDocument!;
        document.VirtualThreshold = 50;
        document.VirtualPageSize = 10;
        await shell.NavigateAsync(ListLocation.ForContainer(Masowe));
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = _ => gate.Task;
        var placeholder = document.VirtualRows![99];
        Assert.True(placeholder.IsPlaceholder);

        Assert.False(shell.OpenItemCommand.CanExecute(placeholder));
        await shell.OpenItemCommand.ExecuteAsync(placeholder);
        document.SelectedItem = placeholder;
        await shell.OpenItemCommand.ExecuteAsync(null);

        Assert.Empty(harness.Dialogs.PropertiesShown);
        Assert.Equal(Masowe, document.Location!.BaseDn);
        gate.SetResult();
        await document.VirtualRows.Idle;
        await shell.OpenItemCommand.ExecuteAsync(document.VirtualRows[99]);
        Assert.Equal([UserDn(99)], harness.Dialogs.PropertiesShown);
    }

    [Fact]
    public async Task LanguageSwitch_RelocalizesThePlaceholdersTheViewShows()
    {
        // A page that cannot be read (slow or failing DC) keeps its "Loading…" rows on screen: they follow the language.
        var (harness, list) = await OpenAsync();
        var rows = list.VirtualRows!;
        var gate = new TaskCompletionSource();
        harness.Directory.HoldVirtualList = _ => gate.Task;
        var placeholder = rows[55];
        Assert.Equal("Loading…", placeholder.Name);
        var changed = new List<string?>();
        placeholder.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        harness.Localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Ładowanie…", placeholder.Name);
        Assert.Equal("Ładowanie…", placeholder[Attr.Name]);
        Assert.Equal(string.Empty, placeholder[Attr.Description]);
        Assert.True(placeholder.IsPlaceholder);
        Assert.NotEmpty(changed);
        Assert.Equal("Ładowanie…", rows[56].Name);
        gate.SetResult();
    }

    [Fact]
    public async Task LanguageSwitch_ReformatsTheStatus()
    {
        var (harness, list) = await OpenAsync();

        harness.Localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Obiektów: 120 · lista wirtualna (VLV), sortowanie na serwerze: Nazwa · stan na 12:00:00", list.StatusText);
    }

    private static ObjectListViewModel CreateList(TestHarness harness)
    {
        var list = harness.Lists.CreateList();
        list.VirtualThreshold = 50;
        list.VirtualPageSize = 10;
        list.VirtualMaxCachedPages = 3;
        return list;
    }

    private static async Task<(TestHarness Harness, ObjectListViewModel List)> OpenAsync(string container = Masowe)
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: Users);
        var list = CreateList(harness);
        await list.NavigateAsync(ListLocation.ForContainer(container));
        return (harness, list);
    }
}

/// <summary>Polling wait for asynchronous view model work in tests.</summary>
internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Condition not met in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
