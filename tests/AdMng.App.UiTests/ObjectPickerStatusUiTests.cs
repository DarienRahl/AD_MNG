using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the object picker (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"), through
/// the real window over the fake directory: one line at the top - empty at rest, the search of "Find Now" with Stop in
/// the line, the count of the list as the summary, a search that was stopped or failed with its action, the names that
/// were not resolved, the end of the session. A slow domain controller is a search the test holds and releases, a
/// failing one an exception of the fake session; every state that is asserted lasts until the test changes it. How
/// names are checked and objects are chosen is covered by <see cref="ObjectDialogTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ObjectPickerStatusUiTests
{
    /// <summary>How long a held search waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndTheCountOfTheListIsItsSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, picker) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the dialog, at the top - above the location, the names
        // and the list of results - of the fixed height, and empty: nothing was searched.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(picker);
        Assert.Same(picker.Status.Current, line.Status);
        Assert.Same(picker.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.LocationBox, window), "the status line is not above the location");
        Assert.True(TopOf(window.LocationBox, window) < TopOf(window.SearchBox, window), "the location is not above the names");
        Assert.True(TopOf(window.SearchBox, window) < TopOf(window.ResultsList, window), "the names are not above the results");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // "Find Now" and "Check Names" are the buttons of before - neither ever had a Stop of its own; their operations
        // are stopped in the line. OK is still the default button.
        Assert.IsNotType<ActionButton>(window.FindNowButton);
        Assert.IsNotType<ActionButton>(window.CheckNamesButton);
        Assert.Same(picker.FindNowCommand, window.FindNowButton.Command);
        Assert.Same(picker.CheckNamesCommand, window.CheckNamesButton.Command);
        Assert.True(window.FindNowButton.IsEnabled);
        Assert.True(window.OkButton.IsDefault);

        // Find Now fills the list: the line says how many objects it shows - and nothing under the names box does.
        var listTop = TopOf(window.ResultsList, window);
        ControlHost.Press(window.FindNowButton);
        await UiWait.UntilAsync(() => !picker.IsSearching && picker.Results.Count > 0 && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the groups were listed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(picker.Results.Count > 1);
        Assert.Equal($"Found: {picker.Results.Count} objects", line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.NotNull(picker.StatusText);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == picker.StatusText);
        Assert.Equal(picker.Results.Count, window.ResultsList.Items.Count);

        // The line was there before: the list did not move.
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.ResultsList, window));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task FindNow_SaysSearchingWithStopInTheLine_AndAStoppedSearchIsAttentionWithFindNow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, picker) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        var listTop = TopOf(window.ResultsList, window);

        // The search of Find Now waits for the test, so "searching" lasts - and "stopping" after Stop.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.Subtree } && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.FindNowButton);
            await UiWait.UntilAsync(() => held.IsSet && picker.IsSearching && line.Kind == PageStatusKind.Working, "the dialog is searching");

            // Working: where the dialog searches - with Stop, and nothing to close. The buttons that start a search or a
            // check wait; there is no bar and no text under the names box.
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Searching in “{domain}”…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(picker.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.False(window.FindNowButton.IsEnabled);
            Assert.False(window.OkButton.IsEnabled);
            AssertNoOldFeedback(window);

            // The line was there before: the list did not move.
            window.UpdateLayout();
            Assert.Equal(listTop, TopOf(window.ResultsList, window));

            // Stop, pressed in the line: "Stopping…" at once - the search that is on its way still has to return.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => picker.Status.IsCancelling && line.Message == "Stopping…", "the search is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(picker.IsSearching);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !picker.IsSearching && line.Kind == PageStatusKind.Attention && !picker.FindNowCommand.IsRunning, "the search was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the list is as it was - empty - and "Find Now" is the way to search again. The fields moved down by
        // as much as the line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Searching was stopped: the list was not changed. Press “Find Now” to search again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Find Now", Assert.IsType<string>(action.Content));
        Assert.Same(picker.FindNowCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Empty(picker.Results);
        Assert.True(window.FindNowButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(window.ResultsList, window), 0.5);

        // Find Now, pressed in the line, searches: the list, and its count as the summary.
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !picker.IsSearching && picker.Results.Count > 0 && line.Kind == PageStatusKind.Idle, "the groups were listed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal($"Found: {picker.Results.Count} objects", line.Message);
        window.UpdateLayout();
        Assert.Equal(listTop, TopOf(window.ResultsList, window), 0.5);
        context.AssertHealthy();
    });

    [Fact]
    public Task NamesThatWereNotResolved_AreAnAttention_AFailedSearchIsAnErrorWithRetry_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, picker) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;

        // Two names: one that several groups answer and one that nobody has. The line says what was skipped and why -
        // an attention the operator can close; the names stay in the box, the matches are listed.
        window.SearchBox.Text = "GG_; nikt";
        await UiTestSupport.WaitForIdleAsync();
        ControlHost.Press(window.CheckNamesButton);
        await UiWait.UntilAsync(() => !picker.IsSearching && line.Kind == PageStatusKind.Attention && !picker.CheckNamesCommand.IsRunning, "the names were checked");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Attention", line.Title);
        Assert.Equal(
            "Not every name was resolved: Several objects match 'GG_' - select one in the list. No object found for 'nikt'.",
            line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal("GG_; nikt", window.SearchBox.Text);
        Assert.Equal(3, window.ResultsList.Items.Count);
        Assert.Empty(picker.Chosen);
        AssertNoOldFeedback(window);

        // The domain controller does not answer the next search: what was tried, the reason, what to do, the code -
        // with Retry; the list is as it was.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.FindNowButton);
        await UiWait.UntilAsync(() => !picker.IsSearching && line.Kind == PageStatusKind.Error && !picker.FindNowCommand.IsRunning, "the search failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Error", line.Title);
        Assert.NotNull(picker.StatusText);
        Assert.Equal($"Could not search in “{domain}”: {picker.StatusText} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(picker.FindNowCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(3, window.ResultsList.Items.Count);

        // Retry, pressed in the line, searches.
        Volatile.Write(ref fail, 0);
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !picker.IsSearching && line.Kind == PageStatusKind.Idle && picker.Results.Count > 3, "the groups were listed");
        session.BeforeOperation = null;
        Assert.Equal($"Found: {picker.Results.Count} objects", line.Message);

        // The session of the dialog ends: an error that cannot be closed takes the line; nothing can be searched or
        // checked any more, and the dialog can still be cancelled.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => picker.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.FindNowButton.IsEnabled);
        Assert.False(window.CheckNamesButton.IsEnabled);
        Assert.True(picker.CancelCommand.CanExecute(null));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The picker of groups in the whole domain (several objects may be chosen), shown off-screen.</summary>
    private static async Task<(ObjectPickerWindow Window, ObjectPickerViewModel Picker)> OpenAsync(UiTestContext context)
    {
        var session = await context.ConnectAsync();
        var picker = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateObjectPicker(session, [ObjectKind.Group], multiSelect: true);
        var window = (ObjectPickerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(picker);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return (window, picker);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(ObjectPickerWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    /// <summary>
    /// How much higher than one line the status line is right now: a result that does not fit one line wraps (to three
    /// lines at most), and what lies under the line moves down by as much - by nothing when the result fits.
    /// </summary>
    private static double GrowthOf(StatusLine line)
    {
        var growth = line.ActualHeight - (double)Application.Current.FindResource("Ad.Size.StatusLine");
        Assert.InRange(growth, -0.5, 40.5);
        return growth;
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
