using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the account review (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"),
/// through the real window over the fake directory: one line at the top - the running check of either tab with Stop,
/// else what the tab shown says (its summary, a check that was stopped or failed with its action), the end of the
/// session - and "Check" as an action button that is the Cancel of the check. A slow domain controller is a query the
/// test holds and releases, a failing one an exception of the fake session; every state that is asserted lasts until
/// the test changes it. What the checks find is covered by <see cref="AccountReviewWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class AccountReviewStatusUiTests
{
    /// <summary>How long a held query waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndItFollowsTheTabShown() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, review) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the scope and the tabs, of the fixed
        // height. Before the first check there is nothing to sum up.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(review);
        Assert.Same(review.Status.Current, line.Status);
        Assert.Same(review.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.ScopeKindBox, window), "the status line is not above the scope");
        Assert.True(TopOf(window.ScopeKindBox, window) < TopOf(window.Tabs, window), "the scope is not above the tabs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // "Check" is an action button at rest - over the command of before and the Cancel of the window's check; the
        // button that reads the exact last logon is one over the Cancel of its tab's read.
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        Assert.Same(review.RunCommand, window.RunButton.Command);
        Assert.Same(review.Status.CancelCommand, window.RunButton.CancelCommand);
        Assert.True(window.RunButton.IsEnabled);
        Assert.Same(review.ReadExactLastLogonCommand, window.ExactLastLogonButton.Command);
        Assert.Same(review.AccountsStatus.CancelCommand, window.ExactLastLogonButton.CancelCommand);
        var tabsTop = TopOf(window.Tabs, window);

        // After a check: the summary of the tab - in the line, and not a second time in the footer.
        await review.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, review.StatusText);
        Assert.Equal(review.StatusText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == review.StatusText);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        window.UpdateLayout();
        Assert.Equal(tabsTop, TopOf(window.Tabs, window));

        // The other tab in front: the line is about that tab - nothing before its first check, then its summary.
        window.Tabs.SelectedItem = window.PrivilegesTab;
        await UiWait.UntilAsync(() => review.IsPrivilegesTab && line.Message.Length == 0, "the line of the tab that was not checked yet");
        await review.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Null(review.Privileges.Error);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, review.Privileges.StatusText);
        Assert.Equal(review.Privileges.StatusText, line.Message);

        // Back: the summary of the first tab again.
        window.Tabs.SelectedItem = window.AccountsTab;
        await UiWait.UntilAsync(() => !review.IsPrivilegesTab && line.Message == review.StatusText, "the summary of the first tab");
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACheck_IsSaidOnBothTabs_CheckIsItsCancel_AndTheStoppedCheckIsAttentionWithCheck() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, review) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        var tabsTop = TopOf(window.Tabs, window);

        // The query of the review (the user accounts with their attributes) waits for the test.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (IsReviewQuery(operation) && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.RunButton);
            await UiWait.UntilAsync(() => held.IsSet && review.IsRunning && line.IsWorking, "the check runs");

            // Working: what is checked and in which scope - with Stop, and nothing to close.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Checking the accounts in the scope “{domain}”…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // "Check" is the Cancel of the check: enabled although its command cannot execute. There is no separate
            // Stop button and no progress bar.
            Assert.Equal(ActionButtonState.Running, window.RunButton.State);
            Assert.True(window.RunButton.IsEnabled);
            Assert.False(review.RunCommand.CanExecute(null));
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line) && other.Content as string == "Stop");
            AssertNoOldFeedback(window);
            window.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(window.Tabs, window));

            // The other tab in front: the check is the window's - the line and the button still say it.
            window.Tabs.SelectedItem = window.PrivilegesTab;
            await UiWait.UntilAsync(() => review.IsPrivilegesTab, "the other tab is shown");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal($"Checking the accounts in the scope “{domain}”…", line.Message);
            Assert.Equal(ActionButtonState.Running, window.RunButton.State);
            window.Tabs.SelectedItem = window.AccountsTab;
            await UiWait.UntilAsync(() => !review.IsPrivilegesTab, "the first tab is shown again");

            // Cancel where you started: "Stopping…" on the button and in the line at once - the query that is on its
            // way to the domain controller still has to return.
            ControlHost.Press(window.RunButton);
            await UiWait.UntilAsync(() => window.RunButton.State == ActionButtonState.Cancelling && review.Status.IsCancelling, "the check is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.RunButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Attention, "the check was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the results are incomplete, and "Check" runs the check again.
        Assert.Equal("Attention", line.Title);
        Assert.StartsWith("Checking the accounts was stopped: the results are incomplete (read: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith("). Press “Check” to check again.", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Check", Assert.IsType<string>(action.Content));
        Assert.Same(review.RunCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        Assert.True(window.RunButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(tabsTop, TopOf(window.Tabs, window));

        // "Check", pressed in the line, runs the whole check: the summary of the tab.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the check ran");
        Assert.Equal(review.StatusText, line.Message);
        Assert.Null(review.Error);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACheckThatFails_IsAnErrorWithRetry_OnItsTab_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, review) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;

        // The domain controller does not answer the query of the review.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && IsReviewQuery(operation))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.RunButton);
        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Error, "the check failed");

        // The tab shows no results and the line says why: what was tried, the reason, what to do, the code - with
        // Retry, and it cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(review.Error);
        Assert.Equal($"Could not check the accounts: {review.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(review.RunCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        AssertNoOldFeedback(window);

        // It is the state of that tab: the other tab says its own, and back here the error is said again.
        window.Tabs.SelectedItem = window.PrivilegesTab;
        await UiWait.UntilAsync(() => review.IsPrivilegesTab && line.Kind == PageStatusKind.Idle, "the line of the other tab");
        window.Tabs.SelectedItem = window.AccountsTab;
        await UiWait.UntilAsync(() => !review.IsPrivilegesTab && line.Kind == PageStatusKind.Error, "the error of the first tab again");

        // Retry, pressed in the line, checks again.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the check ran");
        session.BeforeOperation = null;
        Assert.Null(review.Error);
        Assert.Equal(review.StatusText, line.Message);

        // The session of the window ends: an error that cannot be closed takes the line - on both tabs - and nothing
        // can be checked any more.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => review.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.RunButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        Assert.True(window.CloseButton.IsEnabled);
        window.Tabs.SelectedItem = window.PrivilegesTab;
        await UiWait.UntilAsync(() => review.IsPrivilegesTab, "the other tab is shown");
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The account review window over the sample company with the privileged access sample, not checked yet.</summary>
    private static async Task<(AccountReviewWindow Window, AccountReviewViewModel Review)> OpenAsync(UiTestContext context)
    {
        context.Directory.AddPrivilegedAccessSample(context.Company);
        await context.ConnectAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (AccountReviewWindow)windows.CreateWindow(factory.CreateAccountReview());
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        return (window, window.ViewModel);
    }

    /// <summary>The query of the "Account state" check: the user accounts of the scope with the attributes it evaluates.</summary>
    private static bool IsReviewQuery(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search.Attributes.Count: > 0 }
        && operation.Search.Filter.Contains("805306368", StringComparison.Ordinal);

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(AccountReviewWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
