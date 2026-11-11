using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of "Change Domain Controller" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window over the fake directory: one line under the header - the number of domain controllers as the
/// summary of the list, the read of the list with Stop and a stopped read as the state of the list with Refresh, the
/// switch with Stop and "Change" as its Cancel, a stopped and a failed switch, the end of the session. A slow locator
/// and a slow domain controller are calls the test holds and releases; every state that is asserted lasts until the
/// test changes it. What the dialog lists and how it switches is covered by <see cref="ChangeDomainControllerWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ChangeDomainControllerStatusUiTests
{
    [Fact]
    public Task TheWindow_HasOneStatusLine_UnderTheHeader_WithTheSummaryOfTheList() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await ConnectedAsync();
        var window = Create(context);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var dialog = window.ViewModel;
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, under the header and above the list, of the
        // fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(dialog);
        Assert.Same(dialog.Status.Current, line.Status);
        Assert.Same(dialog.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(window.CurrentTextBlock, window) < TopOf(line, window), "the status line is not under the header");
        Assert.True(TopOf(line, window) < TopOf(window.DomainControllerList, window), "the status line is not above the list");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: what the list says - how many domain controllers it shows.
        Assert.Equal(3, dialog.DomainControllers.Count);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("3 domain controllers", line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        // "Change" is an action button at rest - over the command of before and the Cancel of the window's operation -
        // and still the default button; the dialog's own Cancel is still its cancel button.
        Assert.Equal(ActionButtonState.Idle, window.ChangeButton.State);
        Assert.Same(dialog.ChangeCommand, window.ChangeButton.Command);
        Assert.Same(dialog.Status.CancelCommand, window.ChangeButton.CancelCommand);
        Assert.True(window.ChangeButton.IsDefault);
        Assert.Same(Application.Current.FindResource("Ad.AccentDialogActionButton"), window.ChangeButton.Style);
        Assert.False(window.ChangeButton.IsEnabled);
        Assert.True(window.CancelButton.IsCancel);
        Assert.True(window.CancelButton.IsEnabled);

        // What belongs to the name box stays next to it: "this is the current domain controller".
        Assert.True(window.TargetHintText.IsVisible);
        Assert.Equal(dialog.TargetHint, window.TargetHintText.Text);

        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ReadingTheList_SaysSoWithStop_AndAStoppedReadIsTheStateOfTheListWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await ConnectedAsync();

        // A locator that answers when the test lets it - and, like DsGetDcName, ignores a cancellation.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Connector.FindGate = gate;
        var window = Create(context);
        var dialog = window.ViewModel;
        var line = window.PageStatus;
        try
        {
            await UiWait.UntilAsync(() => dialog.IsLoading && line.Kind == PageStatusKind.Working, "the list is being read");
            var action = ControlHost.Part<Button>(line, "PART_ActionButton");
            var close = ControlHost.Part<Button>(line, "PART_CloseButton");
            var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
            var listTop = TopOf(window.DomainControllerList, window);

            // Working: what is read - with Stop, and nothing to close. "Change" is not running: it is not its operation.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading the domain controllers of contoso.test…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(dialog.ListStatus.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Idle, window.ChangeButton.State);
            Assert.False(window.RefreshButton.IsEnabled);
            AssertNoOldFeedback(window);

            // Stop, pressed in the line: "Stopping…" at once - the locator still has to return.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => dialog.ListStatus.IsCancelling && line.Message == "Stopping…", "the read is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            window.UpdateLayout();
            Assert.Equal(listTop, TopOf(window.DomainControllerList, window));

            gate.TrySetResult();
            await UiWait.InitializedAsync(window.Initialization);
            await UiWait.UntilAsync(() => !dialog.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
            context.Connector.FindGate = null;
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Stopped: the list may lack domain controllers until it is read again - a state, so it cannot be closed -
            // with Refresh; the current one is in the list and a name can be typed.
            Assert.Equal("Attention", line.Title);
            Assert.Equal(
                "Reading the domain controllers was stopped: the list may not show all of them. "
                + "Type the name of a domain controller, or press Refresh to read the list again.",
                line.Message);
            Assert.Equal(AppIcons.StatusWarning, line.IconKey);
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
            Assert.False(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
            Assert.Same(dialog.RefreshCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Contains(dialog.DomainControllers, d => d.Entry.IsCurrent);
            Assert.True(window.RefreshButton.IsEnabled);
            Assert.True(window.ServerBox.IsEnabled);
            window.UpdateLayout();
            Assert.Equal(listTop + GrowthOf(line), TopOf(window.DomainControllerList, window), 0.5);

            // Refresh, pressed in the line, reads the whole list: its summary again, one line high.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => !dialog.IsLoading && line.Kind == PageStatusKind.Idle, "the list was read again");
            Assert.Equal("3 domain controllers", line.Message);
            Assert.Equal(3, window.DomainControllerList.Items.Count);
            window.UpdateLayout();
            Assert.Equal(listTop, TopOf(window.DomainControllerList, window), 0.5);
        }
        finally
        {
            gate.TrySetResult();
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task Switching_SaysSoWithStop_ChangeIsItsCancel_AFailedSwitchIsAnError_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await ConnectedAsync();
        var window = Create(context);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var dialog = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var sessions = context.Services.GetRequiredService<ISessionManager>();
        var listTop = TopOf(window.DomainControllerList, window);
        window.DomainControllerList.SelectedItem = dialog.DomainControllers.Single(d => d.HostName == "dc03.contoso.test");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.ChangeButton.IsEnabled);

        // A domain controller that answers when the test lets it and ignores a cancellation.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Connector.ServerGate = gate;
        try
        {
            ControlHost.Press(window.ChangeButton);
            await UiWait.UntilAsync(() => dialog.IsBusy && line.Kind == PageStatusKind.Working, "the switch is on its way");

            // Working: where the session is being moved - with Stop; "Change" is the Cancel of the switch.
            Assert.Equal("Connecting to dc03.contoso.test…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(dialog.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, window.ChangeButton.State);
            Assert.True(window.ChangeButton.IsEnabled);
            Assert.True(window.CancelButton.IsEnabled);
            AssertNoOldFeedback(window);
            window.UpdateLayout();
            Assert.Equal(listTop, TopOf(window.DomainControllerList, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once.
            ControlHost.Press(window.ChangeButton);
            await UiWait.UntilAsync(
                () => window.ChangeButton.State == ActionButtonState.Cancelling && dialog.Status.IsCancelling, "the switch is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
        }
        finally
        {
            gate.TrySetResult();
        }

        await UiWait.UntilAsync(() => !dialog.IsBusy && line.Kind == PageStatusKind.Attention, "the switch was stopped");
        context.Connector.ServerGate = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was stopped, where the session is, and "Change" as the way to try again. The dialog is open.
        Assert.Equal(
            "Connecting to dc03.contoso.test was stopped: the session stays connected to dc01.contoso.test. Press “Change” to try again.",
            line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Change", Assert.IsType<string>(action.Content));
        Assert.Same(dialog.ChangeCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.ChangeButton.State);
        Assert.True(window.ChangeButton.IsEnabled);
        Assert.True(window.IsVisible);
        Assert.Equal("dc01.contoso.test", sessions.Current!.Info.Server);
        window.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(window.DomainControllerList, window), 0.5);

        // "Change", pressed in the line, tries again - and this time the domain controller does not answer: an error
        // with the reason, the advice, where the session is and the code; it can be closed, and nothing to retry blindly.
        context.Connector.ServerError = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc03 does not answer");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !dialog.IsBusy && line.Kind == PageStatusKind.Error, "the switch failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Error", line.Title);
        Assert.StartsWith("Could not move the session to dc03.contoso.test: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" The session stays connected to dc01.contoso.test. (ServerUnavailable)", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not answer", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(window.IsVisible);
        Assert.Equal("dc01.contoso.test", sessions.Current!.Info.Server);
        context.Connector.ServerError = null;
        AssertNoOldFeedback(window);

        // Closed: what the list says is shown again.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal("3 domain controllers", line.Message);

        // The session of the window ends: an error that cannot be closed takes the line, and nothing can be switched.
        await sessions.DisconnectAsync();
        await UiWait.UntilAsync(() => dialog.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.ChangeButton.IsEnabled);
        Assert.False(window.RefreshButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, window.ChangeButton.State);
        Assert.True(window.CancelButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Connected to a session of dc01 (which a switch may close); the directory records dc01 (PDC) and dc03.</summary>
    private static async Task<UiTestContext> ConnectedAsync()
    {
        var context = await UiTestContext.CreateAsync();
        var directory = context.Directory.Session;
        directory.SeedDomainController("dc01.contoso.test", new Guid("1b4f0d2e-0000-4000-8000-0000000000d1"), isPdc: true);
        directory.SeedDomainController("dc03.contoso.test", new Guid("1b4f0d2e-0000-4000-8000-0000000000d3"), isPdc: false);
        context.Connector.NextSession = new FakeDomainControllerSession(directory, "dc01.contoso.test");
        await context.ConnectAsync();
        return context;
    }

    /// <summary>The dialog, shown off-screen (opened non-modally); its list is being read - await <c>Initialization</c>.</summary>
    private static ChangeDomainControllerWindow Create(UiTestContext context)
    {
        var dialog = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateChangeDomainController();
        var window = (ChangeDomainControllerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        return window;
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(ChangeDomainControllerWindow window)
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
