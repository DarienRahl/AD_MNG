using System.Windows;
using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Lists;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The first uses of the empty state and of the placeholder rows (track V, stage V2b-5b; docs/dev/v-interface-renewal.md 6.7) in the
/// object list of the main window, through the real window over the fake directory: an empty container says so and offers Refresh,
/// a filter that matches nothing says so and offers to clear it, the first read of a container shows placeholder rows - after the
/// delay of the status line - and a reload does not. A read that is held lasts until the test lets it go; the delay of the
/// placeholder rows is a manual clock, so nothing waits for a time.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ObjectListEmptyStateUiTests
{
    [Fact]
    public Task AnEmptyContainer_SaysThereIsNothingHere_AndOffersRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var empty = context.Directory.AddOu("Pusta", context.Company.CompanyOu);
        var (_, document, view) = await OpenAsync(context, ListLocation.ForContainer(empty));
        var state = view.EmptyList;
        var action = ControlHost.Part<Button>(state, EmptyState.ActionPartName);

        // The list was read and has no rows: a picture, one sentence and one action, in the middle of the list.
        Assert.Empty(document.Items);
        Assert.True(state.IsVisible);
        Assert.Equal("There are no items to show in this view.", state.Message);
        Assert.Equal(document.Icon, state.Icon);
        Assert.True(state.HasAction);
        Assert.Equal("Refresh", action.Content);
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.InRange(CentreOf(state, view.ListControl), view.ListControl.ActualWidth / 2 - 2, view.ListControl.ActualWidth / 2 + 2);

        // The sentence is said once - the list has no text of its own for it any more - and there are no placeholder rows.
        Assert.Single(UiWait.Descendants<TextBlock>(view), text => text.IsVisible && text.Text == state.Message);
        Assert.False(document.ShowsSkeleton);
        Assert.False(view.LoadingSkeleton.IsActive);
        Assert.False(view.LoadingSkeleton.IsVisible);

        // The status line still says what it always said - the summary of what was read: the empty state hides nothing of it.
        Assert.Equal(AdMng.Presentation.Feedback.PageStatusKind.Idle, view.PageStatus.Kind);
        Assert.StartsWith(document.StatusText, view.PageStatus.Message, StringComparison.Ordinal);

        // Refresh, pressed like the operator: the container is read again.
        var reads = ReadsOf(context, empty);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => ReadsOf(context, empty) > reads, "the container is read again");
        await UiWait.UntilAsync(() => !document.IsLoading, "the reload of the empty container");
        Assert.True(state.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFilterThatMatchesNothing_SaysSo_AndTheActionClearsIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (_, document, view) = await OpenAsync(context, ListLocation.ForContainer(context.Company.ItOu));
        var state = view.EmptyList;
        var all = document.Items.Count;

        // Rows: nothing to say.
        Assert.True(all > 0);
        Assert.False(state.IsVisible);
        Assert.False(state.HasMessage);

        // A text that no object has: the list is empty because of the filter, and says so - with the way out.
        document.QuickFilterText = "zzz-no-such-object";
        await UiWait.UntilAsync(() => view.ListControl.Items.Count == 0 && state.IsVisible, "the empty state of the filter");

        // (A control that was collapsed has no template until it is laid out once: the parts are looked up after that.)
        view.UpdateLayout();
        var action = ControlHost.Part<Button>(state, EmptyState.ActionPartName);
        Assert.Equal("No object matches the filter.", state.Message);
        Assert.Equal(AdMng.Presentation.Icons.ObjectIcon.Query, state.Icon);
        Assert.Equal("Clear the filter", action.Content);
        Assert.Same(document.ClearFiltersCommand, action.Command);
        Assert.True(action.IsVisible);
        Assert.Single(UiWait.Descendants<TextBlock>(view), text => text.IsVisible && text.Text == state.Message);

        // The action: the filter is gone from the box and the rows are back - and the empty state with them.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => document.QuickFilterText.Length == 0 && view.ListControl.Items.Count == all, "the filter is cleared");
        await UiWait.UntilAsync(() => !state.IsVisible, "the empty state is gone");
        Assert.Equal(string.Empty, view.QuickFilterBox.Text);
        Assert.False(state.HasMessage);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheFirstRead_OfAContainer_ShowsPlaceholderRows_AfterTheDelay_AndAReloadDoesNot() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (_, document, view) = await OpenAsync(context, ListLocation.ForContainer(context.Company.ItOu));
        var skeleton = view.LoadingSkeleton;
        var clock = new ManualStatusScheduler();
        skeleton.Scheduler = clock;
        var session = context.Directory.Session;
        var target = context.Company.EmployeesOu;
        var delay = Assert.NotNull(Motion.TimeOf(Motion.WorkingDelayKey));

        // The first read of another container, held at the domain controller: placeholder rows - not an empty list.
        HeldSearch? held = null;
        HeldSearch? again = null;
        try
        {
            held = HoldTheReadOf(session, target);
            var load = document.NavigateAsync(ListLocation.ForContainer(target));
            await UiWait.UntilAsync(() => held.Reached.Task.IsCompleted && document.ShowsSkeleton, "the first read is held");
            Assert.True(document.IsLoading);
            Assert.Empty(document.Items);
            Assert.True(skeleton.IsActive);
            Assert.True(skeleton.IsVisible);
            Assert.False(view.EmptyList.IsVisible);

            // Not yet: a read that ends at once never flashes. After the delay of the status line the rows are there - under the
            // column headers, above nothing else, and the list does not move.
            Assert.False(skeleton.IsShown);
            Assert.Equal([delay], clock.Requested);
            clock.Advance(delay);
            view.UpdateLayout();
            Assert.True(skeleton.IsShown);
            Assert.Equal(12, skeleton.BuiltRows);
            Assert.True(TopOf(skeleton, view) >= TopOf(view.ListControl, view) + 28, "the placeholder rows are over the column headers");
            Assert.True(skeleton.ActualHeight > 0);
            Assert.False(skeleton.IsHitTestVisible);

            // The rows arrive: the placeholder rows are gone.
            held.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(30));
            await UiWait.UntilAsync(() => !document.IsLoading && view.ListControl.Items.Count > 0, "the rows of the container");
            Assert.False(document.ShowsSkeleton);
            Assert.False(skeleton.IsActive);
            Assert.False(skeleton.IsVisible);
            Assert.False(skeleton.IsShown);

            // A reload of the same container - F5 - keeps its look: no placeholder rows, no new delay.
            var requested = clock.Requested.Count;
            again = HoldTheReadOf(session, target);
            var reload = document.RefreshCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => again.Reached.Task.IsCompleted && document.IsLoading, "the reload is held");
            Assert.False(document.ShowsSkeleton);
            Assert.False(skeleton.IsActive);
            Assert.False(skeleton.IsVisible);
            Assert.Equal(requested, clock.Requested.Count);
            Assert.Equal(0, clock.Pending);
            again.Release();
            await reload.WaitAsync(TimeSpan.FromSeconds(30));
            await UiWait.UntilAsync(() => !document.IsLoading, "the reload ended");
            Assert.False(skeleton.IsVisible);
        }
        finally
        {
            held?.Release();
            again?.Release();
            session.BeforeOperationAsync = null;
        }

        context.AssertHealthy();
    });

    [Fact]
    public Task AFirstRead_ThatEndsBeforeTheDelay_ShowsNoPlaceholderRowsAtAll() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (_, document, view) = await OpenAsync(context, ListLocation.ForContainer(context.Company.ItOu));
        var clock = new ManualStatusScheduler();
        view.LoadingSkeleton.Scheduler = clock;

        // The read of the next container is as fast as the fake directory: it ends before the manual clock moved at all.
        await document.NavigateAsync(ListLocation.ForContainer(context.Company.EmployeesOu));
        await UiWait.UntilAsync(() => !document.IsLoading, "the container is read");

        Assert.False(view.LoadingSkeleton.IsShown);
        Assert.False(view.LoadingSkeleton.IsVisible);
        Assert.Equal(0, clock.Pending);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(view.LoadingSkeleton.IsShown);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFailed_ShowsNeitherPlaceholderRowsNorAnEmptyList_ThatIsTheStatusLine() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();

        // A container of its own, which nothing else reads: the refusal is for the next search of it, and nobody else takes it.
        var broken = context.Directory.AddOu("Awaria", context.Company.CompanyOu);
        var (_, document, view) = await OpenAsync(context, ListLocation.ForContainer(context.Company.ItOu));
        context.Directory.Session.FailNext(FakeOperationKind.Search, broken, AdMng.Core.Directory.DirectoryErrorKind.ServerUnavailable);

        await document.NavigateAsync(ListLocation.ForContainer(broken));
        await UiWait.UntilAsync(() => !document.IsLoading && document.Error is not null, "the read failed");

        // "Could not be read" is an error, not an empty list: the status line says it, with Retry; the empty state stays quiet.
        Assert.Equal(AdMng.Presentation.Feedback.PageStatusKind.Error, view.PageStatus.Kind);
        Assert.False(view.EmptyList.IsVisible);
        Assert.False(view.LoadingSkeleton.IsVisible);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static async Task<(MainWindow Window, ObjectListViewModel Document, ObjectListView View)> OpenAsync(UiTestContext context, ListLocation location)
    {
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(location);
        await UiTestSupport.WaitForIdleAsync();
        var document = Assert.IsType<ObjectListViewModel>(window.Shell.SelectedTab);
        var view = Assert.IsType<ObjectListView>(window.SelectedListView);
        await UiWait.UntilAsync(() => !document.IsLoading, "the first read of the container");
        Assert.Null(document.Error);
        window.UpdateLayout();
        return (window, document, view);
    }

    /// <summary>The search of the container waits (no thread is blocked) until the test lets it go.</summary>
    private static HeldSearch HoldTheReadOf(FakeDirectorySession session, string containerDn)
    {
        var held = new HeldSearch();
        session.BeforeOperationAsync = async operation =>
        {
            if (operation.Kind == FakeOperationKind.Search
                && string.Equals(operation.DistinguishedName, containerDn, StringComparison.OrdinalIgnoreCase)
                && !held.Gate.Task.IsCompleted)
            {
                held.Reached.TrySetResult();
                await held.Gate.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        };
        return held;
    }

    private static int ReadsOf(UiTestContext context, string containerDn) =>
        context.Directory.Session.Operations.Count(operation =>
            operation.Kind == FakeOperationKind.Search && string.Equals(operation.DistinguishedName, containerDn, StringComparison.OrdinalIgnoreCase));

    private static double TopOf(FrameworkElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    private static double CentreOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(element.ActualWidth / 2, 0), relativeTo).X;

    private sealed class HeldSearch
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the held search go on (and every later one: the hold is spent).</summary>
        public void Release() => Gate.TrySetResult();
    }
}
