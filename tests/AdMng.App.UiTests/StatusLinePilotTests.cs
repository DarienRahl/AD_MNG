using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The two pilot pages of the status line and the action button (track V, stage V2a; docs/dev/v-interface-renewal.md
/// 6.1, 6.7 and goals G2 and G3), through their real windows over the fake directory: the object list of the main
/// window (summary, a slow load with Stop, a stopped load, a failed load with Retry - and the status bar that no longer
/// repeats the list) and the Find window (what to do, search, cancel where you started, a failed search). A slow domain
/// controller is the fake session's latency (cancellable), a failing one an injected search failure.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StatusLinePilotTests
{
    /// <summary>Longer than any wait of a test: a search that is not cancelled fails the test instead of ending by itself.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromSeconds(30);

    // ---- The object list of the main window ---------------------------------------------------------------------

    [Fact]
    public Task TheObjectList_SaysItsSummaryInItsOwnLine_AndTheStatusBarDoesNotRepeatIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, document, view) = await OpenListAsync(context);
        var line = view.PageStatus;

        // One status line, named PageStatus, between the header and the rows, over the status of the page.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(view)));
        Assert.True(view.ShowStatusLine);
        Assert.True(line.IsVisible);
        Assert.Same(document.Status.Current, line.Status);
        Assert.Same(document.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, view) < TopOf(view.ListControl, view), "the status line is not above the rows");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: the summary of what is loaded, with the info picture, nothing to press.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(2, document.Items.Count);
        Assert.StartsWith("2 objects · ", line.Message, StringComparison.Ordinal);
        Assert.StartsWith(document.StatusText, line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        // The status bar of the window says nothing about a tab that has its own line - one window never says the same
        // sentence twice. The texts of the shell are unchanged for whoever else reads them.
        Assert.Equal("StatusBarText", BindingOperations.GetBinding(window.TabStatusText, TextBlock.TextProperty)?.Path.Path);
        Assert.Equal("StatusBarSelectionText", BindingOperations.GetBinding(window.TabSelectionText, TextBlock.TextProperty)?.Path.Path);
        Assert.Equal(string.Empty, window.TabStatusText.Text);
        Assert.Equal(string.Empty, window.TabSelectionText.Text);
        Assert.Equal(document.StatusText, window.Shell.StatusText);

        // A selection joins the summary - in the line, not in the status bar.
        view.ListControl.SelectedItem = document.Items[0];
        await UiWait.UntilAsync(() => line.Message.EndsWith(" · 1 selected", StringComparison.Ordinal), "the selection in the status line");
        Assert.StartsWith("2 objects · ", line.Message, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("1 selected", window.Shell.SelectionText);
        Assert.Equal(string.Empty, window.TabSelectionText.Text);
        Assert.Equal(string.Empty, window.TabStatusText.Text);

        // The old places are gone: no progress bar and no error bar in the list page.
        Assert.Empty(UiWait.Descendants<ProgressBar>(view));
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        Assert.DoesNotContain(UiWait.Descendants<Border>(view), border => ReferenceEquals(border.Style, errorBar));

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASlowLoadOfTheList_IsWorkingWithStop_AndStopReturnsControl() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, document, view) = await OpenListAsync(context);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var rowsTop = TopOf(view.ListControl, view);
        var session = context.Directory.Session;

        // A domain controller that takes its time. (The policy of the domain was read with the first load: only the
        // search waits now.)
        session.Latency = SlowDomainController;
        Task load;
        try
        {
            load = document.RefreshCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => document.IsLoading && line.IsWorking, "the list is being read");

            // Working: the action and its scope, the turning ring, Stop - and nothing to close.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Reading “{document.Title}”…", line.Message);
            Assert.True(ring.IsTurning, "the ring of the running load does not turn");
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

            // The line was there before: the rows did not move. The status bar still says nothing about the list.
            view.UpdateLayout();
            Assert.Equal(rowsTop, TopOf(view.ListControl, view));
            Assert.Equal(string.Empty, window.TabStatusText.Text);

            // Stop, pressed in the line: the load ends well before the domain controller would have answered.
            ControlHost.Press(action);
            await load.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            session.Latency = TimeSpan.Zero;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped before all results arrived: attention, what is there, Refresh as the way on. Control is back.
        Assert.False(document.IsLoading);
        Assert.True(document.Partial);
        Assert.True(document.RefreshCommand.CanExecute(null));
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal("Attention", line.Title);
        Assert.Equal(
            $"Reading “{document.Title}” was stopped before all results arrived: 0 objects loaded. Press Refresh to read the whole list.",
            line.Message);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsTurning);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        view.UpdateLayout();
        Assert.Equal(rowsTop, TopOf(view.ListControl, view));

        // Closed, the line still says that the list is incomplete - as its summary.
        ControlHost.Press(close);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Stopped – 0 objects loaded (incomplete list)", line.Message);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(Visibility.Collapsed, action.Visibility);

        // Refresh reads the whole list.
        await document.RefreshCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(document.Partial);
        Assert.Equal(2, document.Items.Count);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("2 objects · ", line.Message, StringComparison.Ordinal);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFailedLoadOfTheList_IsAnErrorInTheLine_WithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, document, view) = await OpenListAsync(context);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The domain controller refuses the next search of this container.
        context.Directory.Session.FailNext(FakeOperationKind.Search, context.Company.ItOu, DirectoryErrorKind.ServerUnavailable);
        await document.RefreshCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What we tried: why it failed. What to do next. (code) - with Retry, and nothing to close: it is the only
        // explanation of the empty list.
        var reason = Assert.IsType<string>(document.Error);
        Assert.Empty(document.Items);
        Assert.False(document.IsLoading);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            PageStatusText.Failure($"Could not read “{document.Title}”", reason, "Press Retry to try again.", ResultCode.Ldap(52)),
            line.Message);
        Assert.EndsWith(" Press Retry to try again. (LDAP 52)", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        // One place: the reason is not shown a second time in the page, and the status bar says nothing.
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(string.Empty, window.TabStatusText.Text);

        // Retry, pressed in the line, reads the list: the error is gone, the summary is back.
        ControlHost.Press(action);
        await UiWait.UntilAsync(
            () => !document.IsLoading && line.Kind == PageStatusKind.Idle && document.Items.Count == 2,
            "the list was read after Retry");
        Assert.Null(document.Error);
        Assert.StartsWith("2 objects · ", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    // ---- The Find window ----------------------------------------------------------------------------------------

    [Fact]
    public Task TheFindWindow_HasOneStatusLine_AndFindNowIsAnActionButton() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, find) = await OpenFindAsync(context);
        var line = window.PageStatus;
        var button = window.FindNowButton;

        // One status line for the window, over the status of the results; the results list shows none of its own.
        Assert.IsType<StatusLine>(line);
        Assert.True(line.IsVisible);
        Assert.Same(find.Results.Status, find.Status);
        Assert.Same(find.Status.Current, line.Status);
        Assert.Same(find.Status.CloseCommand, line.CloseCommand);
        Assert.False(window.ResultsView.ShowStatusLine);
        Assert.Equal(Visibility.Collapsed, window.ResultsView.PageStatus.Visibility);
        Assert.True(TopOf(line, window) < TopOf(window.KindBox, window), "the status line is not above the criteria");

        // Before the first search the line says what to do.
        Assert.False(find.HasSearched);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Say what to find and press Find Now.", line.Message);

        // Find Now is an action button - the default button of the window, over the commands the window had before -
        // and there is no separate Stop button any more.
        Assert.IsType<ActionButton>(button);
        Assert.True(button.IsDefault);
        Assert.Equal(ActionButtonState.Idle, button.State);
        Assert.Same(find.FindNowCommand, button.Command);
        Assert.Same(find.StopCommand, button.CancelCommand);
        Assert.Equal("Find Now", NameOf(button));
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(window),
            other => !ReferenceEquals(other, button) && ReferenceEquals(other.Command, find.StopCommand));
        Assert.Equal(window.ClearAllButton.ActualWidth, button.ActualWidth, 0.5);

        // A search: the results are shown, the line says their summary, the button is "Find Now" again.
        window.NameBox.Text = "Kowalski";
        Assert.True(find.FindNowCommand.CanExecute(null));
        Assert.True(button.IsEnabled);
        ControlHost.Press(button);
        await UiWait.UntilAsync(
            () => find.HasSearched && !find.Results.IsLoading && find.Results.Items.Count > 0 && button.State == ActionButtonState.Idle,
            "search results");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains(find.Results.Items, row => row.Name == "Jan Kowalski");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith(find.Results.StatusText, line.Message, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, line.Message);
        Assert.True(window.ResultsView.IsVisible);
        Assert.False(window.ResultsView.PageStatus.IsVisible);
        Assert.Equal("Find Now", NameOf(button));
        Assert.True(button.IsEnabled);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASearchInTheFindWindow_IsCancelledWhereItWasStarted() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, find) = await OpenFindAsync(context);
        var line = window.PageStatus;
        var button = window.FindNowButton;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var session = context.Directory.Session;

        // A first search reads the policy of the domain, so that only the search itself waits below.
        window.NameBox.Text = "Kowalski";
        await find.FindNowCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains(find.Results.Items, row => row.Name == "Jan Kowalski");
        Assert.Equal(ActionButtonState.Idle, button.State);

        // What the button and the line say from here on.
        var states = new List<ActionButtonState>();
        var said = new List<(PageStatusKind Kind, string Message)>();
        var stateDescriptor = DependencyPropertyDescriptor.FromProperty(ActionButton.StateProperty, typeof(ActionButton));
        EventHandler onState = (_, _) => states.Add(button.State);
        PropertyChangedEventHandler onStatus = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                said.Add((find.Status.Current.Kind, find.Status.Current.Message));
            }
        };
        stateDescriptor.AddValueChanged(button, onState);
        find.Status.PropertyChanged += onStatus;
        session.Latency = SlowDomainController;
        try
        {
            // Find Now on a slow domain controller.
            ControlHost.Press(button);
            await UiWait.UntilAsync(() => button.State == ActionButtonState.Running && find.Results.IsLoading, "the search runs");

            // The line says what runs; the button is the Cancel of this search and stays enabled - it keeps the focus -
            // although its start command cannot execute while it runs.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal($"Reading “{find.Results.Title}”…", line.Message);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.False(find.FindNowCommand.CanExecute(null));
            Assert.True(button.IsEnabled);
            Assert.Equal("Cancel: Find Now", NameOf(button));

            // Cancel where you started: the search ends well before the domain controller would have answered.
            ControlHost.Press(button);
            await UiWait.UntilAsync(() => button.State == ActionButtonState.Idle && !find.Results.IsLoading, "the search stopped");
        }
        finally
        {
            session.Latency = TimeSpan.Zero;
            stateDescriptor.RemoveValueChanged(button, onState);
            find.Status.PropertyChanged -= onStatus;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The button said "Stopping…" until the search had ended, and so did the line; then: attention.
        Assert.Equal([ActionButtonState.Running, ActionButtonState.Cancelling, ActionButtonState.Idle], states);
        var stopping = said.IndexOf((PageStatusKind.Working, "Stopping…"));
        Assert.True(stopping >= 0, "the line never said that the search is stopping");
        Assert.Equal(PageStatusKind.Attention, said[^1].Kind);
        Assert.Equal(said.Count - 2, stopping);

        Assert.True(find.Results.Partial);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Contains("was stopped before all results arrived", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Equal("Find Now", NameOf(button));
        Assert.True(find.FindNowCommand.CanExecute(null));
        Assert.True(button.IsEnabled);

        // The same button starts the next search.
        ControlHost.Press(button);
        await UiWait.UntilAsync(
            () => button.State == ActionButtonState.Idle && !find.Results.IsLoading && find.Results.Items.Count > 0 && line.Kind == PageStatusKind.Idle,
            "the next search");
        Assert.False(find.Results.Partial);
        Assert.Contains(find.Results.Items, row => row.Name == "Jan Kowalski");

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFailedSearchInTheFindWindow_IsAnErrorInTheLine_WithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, find) = await OpenFindAsync(context);
        var line = window.PageStatus;
        var button = window.FindNowButton;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The domain controller refuses the next search under the search base of the window.
        context.Directory.Session.FailNext(FakeOperationKind.Search, context.Company.EmployeesOu, DirectoryErrorKind.ServerUnavailable);
        window.NameBox.Text = "Kowalski";
        ControlHost.Press(button);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error && button.State == ActionButtonState.Idle, "the failed search");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The error is in the status line of the window - one place - with Retry; the button is "Find Now" again.
        var reason = Assert.IsType<string>(find.Results.Error);
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            PageStatusText.Failure($"Could not read “{find.Results.Title}”", reason, "Press Retry to try again.", ResultCode.Ldap(52)),
            line.Message);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.ResultsView.PageStatus.IsVisible);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text.Contains(reason, StringComparison.Ordinal));
        Assert.Equal("Find Now", NameOf(button));
        Assert.True(button.IsEnabled);

        // Retry, pressed in the line, runs the search again.
        ControlHost.Press(action);
        await UiWait.UntilAsync(
            () => line.Kind == PageStatusKind.Idle && !find.Results.IsLoading && find.Results.Items.Count > 0,
            "the search after Retry");
        Assert.Null(find.Results.Error);
        Assert.Contains(find.Results.Items, row => row.Name == "Jan Kowalski");
        Assert.StartsWith(find.Results.StatusText, line.Message, StringComparison.Ordinal);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The main window, connected, with the IT unit of the sample company (two objects) in its list.</summary>
    private static async Task<(MainWindow Window, ObjectListViewModel Document, ObjectListView View)> OpenListAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiTestSupport.WaitForIdleAsync();
        var document = Assert.IsType<ObjectListViewModel>(window.Shell.SelectedTab);
        var view = Assert.IsType<ObjectListView>(window.SelectedListView);
        Assert.False(document.IsLoading);
        Assert.Null(document.Error);
        return (window, document, view);
    }

    /// <summary>The Find window of the connected session, searching under the employees of the sample company.</summary>
    private static async Task<(FindWindow Window, FindViewModel Find)> OpenFindAsync(UiTestContext context)
    {
        await context.ConnectAsync();
        var find = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateFind(context.Company.EmployeesOu);
        var window = (FindWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(find);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return (window, find);
    }

    /// <summary>What UI Automation calls the button right now.</summary>
    private static string NameOf(Button button) =>
        Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button)).GetName();

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
