using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>
/// What the object list says instead of rows (track V, stage V2b-5b, <c>EmptyState</c> and <c>Skeleton</c>): an empty list says
/// so in one sentence with its next step - Refresh, or "Clear the filter" when a filter hides everything - a list that is read
/// for the first time shows placeholder rows, and a list that is read, stopped or could not be read has no empty state at all
/// (the status line says why).
/// </summary>
public class ObjectListEmptyStateTests
{
    private const string Bulk = "OU=Masowe,DC=contoso,DC=test";

    [Fact]
    public async Task AnEmptyContainer_SaysSo_AndOffersRefresh()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();

        // Nothing is shown yet: no sentence, no placeholder rows.
        Assert.Same(EmptyListState.None, list.EmptyState);
        Assert.False(list.EmptyState.HasMessage);
        Assert.False(list.ShowsSkeleton);

        await list.NavigateAsync(ListLocation.ForContainer(TestDirectory.System));
        Assert.Empty(list.Rows);
        var empty = list.EmptyState;
        Assert.True(empty.HasMessage);
        Assert.Equal("There are no items to show in this view.", empty.Message);
        Assert.Equal(list.Icon, empty.Icon);
        Assert.Equal("Refresh", empty.ActionText);
        Assert.Same(list.RefreshCommand, empty.ActionCommand);
        Assert.False(list.HasActiveFilter);
        Assert.False(list.ShowsSkeleton);

        // The action reads the list again and the sentence waits for the answer.
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var reload = list.RefreshAsync();
        await ObjectListViewModelTests.WaitUntilAsync(() => list.IsLoading);
        Assert.Same(EmptyListState.None, list.EmptyState);
        gate.SetResult();
        await reload;
        Assert.Equal("There are no items to show in this view.", list.EmptyState.Message);
    }

    [Fact]
    public async Task AListWithRows_HasNoEmptyState()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);

        Assert.NotEmpty(list.Rows);
        Assert.Same(EmptyListState.None, list.EmptyState);
        Assert.False(list.ShowsSkeleton);
    }

    [Fact]
    public async Task AFilterThatHidesEveryRow_SaysSo_AndOffersToClearIt()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var all = list.Items.Count;
        var seen = new List<string?>();
        list.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        list.QuickFilterText = "zzz-no-such-object";
        Assert.Empty(list.Rows);
        Assert.Contains(nameof(ObjectListViewModel.EmptyState), seen);
        var empty = list.EmptyState;
        Assert.Equal("No object matches the filter.", empty.Message);
        Assert.Equal(ObjectIcon.Query, empty.Icon);
        Assert.Equal("Clear the filter", empty.ActionText);
        Assert.Same(list.ClearFiltersCommand, empty.ActionCommand);
        Assert.True(list.HasActiveFilter);

        // The action: the text is gone and the rows are back, and so is the silence of the list.
        empty.ActionCommand!.Execute(empty.ActionParameter);
        Assert.Equal(string.Empty, list.QuickFilterText);
        Assert.Equal(all, list.Items.Count);
        Assert.Same(EmptyListState.None, list.EmptyState);
        Assert.False(list.HasActiveFilter);
    }

    [Fact]
    public async Task ClearTheFilter_AlsoSwitchesTheChipsOff_AndReadsOnce()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var all = list.Items.Count;
        var chip = list.FilterChips.Single(c => c.Kind == QuickFilterKind.Enabled);

        chip.IsActive = true;
        await list.CurrentLoad;
        list.QuickFilterText = "zzz-no-such-object";
        Assert.True(list.HasActiveFilter);
        Assert.Equal("No object matches the filter.", list.EmptyState.Message);

        var searches = harness.Directory.Searches;
        list.ClearFiltersCommand.Execute(null);
        await list.CurrentLoad;

        Assert.False(chip.IsActive);
        Assert.All(list.FilterChips, c => Assert.False(c.IsActive));
        Assert.Equal(string.Empty, list.QuickFilterText);
        Assert.False(list.HasActiveFilter);
        Assert.Equal(all, list.Items.Count);
        Assert.Same(EmptyListState.None, list.EmptyState);

        // One read, not one per chip.
        Assert.Equal(1, harness.Directory.Searches.Count(s => s.BaseDn == It) - searches.Count(s => s.BaseDn == It));
    }

    [Fact]
    public async Task TheFirstRead_ShowsPlaceholderRows_UntilRowsArrive_AndAReloadDoesNot()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var changes = new List<(string? Name, bool Skeleton)>();
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ObjectListViewModel.ShowsSkeleton))
            {
                changes.Add((e.PropertyName, list.ShowsSkeleton));
            }
        };

        // The first read of a container: placeholder rows, and no sentence about an empty list.
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var first = list.NavigateAsync(ListLocation.ForContainer(Bulk));
        Assert.True(list.IsLoading);
        Assert.True(list.ShowsSkeleton);
        Assert.Same(EmptyListState.None, list.EmptyState);
        gate.SetResult();
        await first;

        // Rows arrived: the placeholder rows are gone for good.
        Assert.Equal(300, list.Items.Count);
        Assert.False(list.ShowsSkeleton);
        Assert.Contains(changes, change => change.Skeleton);
        Assert.False(changes.Last().Skeleton);

        // A reload of the same list - F5, a chip, a sort - brings its rows back at once: no placeholder rows.
        var again = new TaskCompletionSource();
        harness.Directory.Hold = _ => again.Task;
        changes.Clear();
        var reload = list.RefreshAsync();
        await ObjectListViewModelTests.WaitUntilAsync(() => list.IsLoading);
        Assert.False(list.ShowsSkeleton);
        Assert.DoesNotContain(changes, change => change.Skeleton);
        Assert.Same(EmptyListState.None, list.EmptyState);
        again.SetResult();
        await reload;
        Assert.Equal(300, list.Items.Count);
        Assert.False(list.ShowsSkeleton);

        // Another container is a first read again.
        var other = new TaskCompletionSource();
        harness.Directory.Hold = _ => other.Task;
        var next = list.NavigateAsync(ListLocation.ForContainer(It));
        await ObjectListViewModelTests.WaitUntilAsync(() => list.IsLoading);
        Assert.True(list.ShowsSkeleton);
        other.SetResult();
        await next;
        Assert.False(list.ShowsSkeleton);
        Assert.Equal(6, list.Items.Count);
    }

    [Fact]
    public async Task ARedefinedLocation_IsNotAFirstRead_ButAnotherSearchIs()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;

        // The same container with a new title (the edited definition of a view): the rows are read again, not for the first time.
        var redefined = ListLocation.ForContainer(It, "IT (nowa nazwa)");
        var replace = list.ReplaceLocationAsync(redefined);
        await ObjectListViewModelTests.WaitUntilAsync(() => list.IsLoading);
        Assert.False(list.ShowsSkeleton);
        gate.SetResult();
        await replace;

        // A search for something else is a list that was not shown.
        var other = new TaskCompletionSource();
        harness.Directory.Hold = _ => other.Task;
        var search = list.NavigateAsync(ListLocation.ForSearch("kowalska", Domain, "kowalska", "(anr=kowalska)"));
        await ObjectListViewModelTests.WaitUntilAsync(() => list.IsLoading);
        Assert.True(list.ShowsSkeleton);
        other.SetResult();
        await search;
        Assert.False(list.ShowsSkeleton);
    }

    [Fact]
    public async Task AStoppedOrFailedFirstRead_HasNeitherPlaceholderRowsNorAnEmptyState()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();

        // Stopped by the operator before a row arrived: the line says so; the list is not "empty".
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var load = list.NavigateAsync(ListLocation.ForContainer(Bulk));
        Assert.True(list.ShowsSkeleton);
        list.Status.Operation!.Cancel();
        await load;
        Assert.True(list.Partial);
        Assert.False(list.ShowsSkeleton);
        Assert.Same(EmptyListState.None, list.EmptyState);

        // Could not be read: an error in the line, never an empty list.
        harness.Directory.Hold = null;
        harness.Directory.FailSearch = search => search.BaseDn == It
            ? new DirectoryException(DirectoryErrorKind.ServerUnavailable, "81: Server Down") { LdapResultCode = 81 }
            : null;
        await list.NavigateAsync(ListLocation.ForContainer(It));
        Assert.NotNull(list.Error);
        Assert.False(list.ShowsSkeleton);
        Assert.Same(EmptyListState.None, list.EmptyState);
    }

    [Fact]
    public async Task NotConnected_HasNoEmptyState()
    {
        var harness = TestHarness.Disconnected();
        var list = harness.Lists.CreateList();

        await list.NavigateAsync(ListLocation.ForContainer(It));

        Assert.NotNull(list.Error);
        Assert.False(list.ShowsSkeleton);
        Assert.Same(EmptyListState.None, list.EmptyState);
    }

    [Fact]
    public async Task ThePolishWords_AreTheSameShape()
    {
        var polish = CultureInfo.GetCultureInfo("pl-PL");
        var harness = await TestHarness.ConnectedAsync(culture: polish);
        var list = await harness.OpenContainerAsync(TestDirectory.System);

        Assert.Equal("Brak elementów do pokazania w tym widoku.", list.EmptyState.Message);
        Assert.Equal("Odśwież", list.EmptyState.ActionText);

        var rows = await harness.OpenContainerAsync(It);
        rows.QuickFilterText = "zzz-no-such-object";
        Assert.Equal("Żaden obiekt nie pasuje do filtra.", rows.EmptyState.Message);
        Assert.Equal("Wyczyść filtr", rows.EmptyState.ActionText);
    }

    [Fact]
    public async Task TheEmptyState_ComesAndGoesWithTheRows_AndSaysItEachTime()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var seen = new List<string?>();
        list.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        // A filter that matches nothing and then one that matches again: the sentence comes and goes with the rows.
        list.QuickFilterText = "zzz-no-such-object";
        Assert.True(list.EmptyState.HasMessage);
        list.QuickFilterText = string.Empty;
        Assert.False(list.EmptyState.HasMessage);
        Assert.True(seen.Count(name => name == nameof(ObjectListViewModel.EmptyState)) >= 2, "the empty state was not announced to the view");
    }
}
