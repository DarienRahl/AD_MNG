using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the Connect window (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window over the fake connector: one line under the header - empty at rest, the number of domain
/// controllers found as the summary, the connection attempt with Stop, a stopped attempt with "Connect", what is
/// missing as an attention, a refused bind as an error without Retry, a failed search with Retry - and "Connect" and
/// "Find domain controllers" as action buttons. A slow domain controller is a bind the test holds and releases; every
/// state that is asserted lasts until the test changes it. How the window connects is covered by
/// <see cref="ConnectWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConnectStatusUiTests
{
    private const string Domain = FakeDirectoryBuilder.DefaultDomain;

    [Fact]
    public Task TheWindow_HasOneStatusLine_UnderTheHeader_AndWhatWasFoundIsItsSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenAsync(context);
        var connect = window.ViewModel;
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, under the header and above the fields, of
        // the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(connect);
        Assert.Same(connect.Status.Current, line.Status);
        Assert.Same(connect.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(window.HeaderIcon, window) < TopOf(line, window), "the status line is not under the header");
        Assert.True(TopOf(line, window) < TopOf(window.ProfileBox, window), "the status line is not above the fields");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happened yet and there is no session to speak of: the line is there, and empty.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasIcon);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        // The two commands that start an operation are action buttons at rest - over the commands of before and the
        // Cancel of the window's operation; "Connect" is still the default button, in the accent look of a dialog.
        Assert.Equal(ActionButtonState.Idle, window.ConnectButton.State);
        Assert.Same(connect.ConnectCommand, window.ConnectButton.Command);
        Assert.Same(connect.Status.CancelCommand, window.ConnectButton.CancelCommand);
        Assert.True(window.ConnectButton.IsDefault);
        Assert.True(window.ConnectButton.IsEnabled);
        Assert.Same(Application.Current.FindResource("Ad.AccentDialogActionButton"), window.ConnectButton.Style);
        Assert.Equal(ActionButtonState.Idle, window.FindDomainControllersButton.State);
        Assert.Same(connect.FindDomainControllersCommand, window.FindDomainControllersButton.Command);
        Assert.Same(connect.Status.CancelCommand, window.FindDomainControllersButton.CancelCommand);
        Assert.True(window.FindDomainControllersButton.IsEnabled);

        // The separate Cancel buttons of the footer are gone.
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(window),
            other => other is not ActionButton && !other.IsDescendantOf(line) && other.Content as string == "Cancel");

        // Looking for the domain controllers fills the drop-down; the line says how many there are - its summary: one
        // line, nothing to close, and nothing under it moved.
        var fieldsTop = TopOf(window.ProfileBox, window);
        ControlHost.Press(window.FindDomainControllersButton);
        await UiWait.UntilAsync(
            () => connect.DomainControllers.Count == 2 && !connect.IsBusy && line.Kind == PageStatusKind.Idle && line.Message.Length > 0,
            "the domain controllers in the drop-down");
        Assert.Equal("Domain controllers found: 2", line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(ActionButtonState.Idle, window.FindDomainControllersButton.State);
        window.UpdateLayout();
        Assert.Equal(fieldsTop, TopOf(window.ProfileBox, window));

        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task Connecting_SaysSoWithStop_ConnectIsItsCancel_AndAStoppedAttemptIsAttentionWithConnect() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();

        // The bind of this test is closed after it was stopped: it must not be the shared directory itself.
        context.Connector.NonOwning = true;
        var window = await OpenAsync(context);
        var connect = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var fieldsTop = TopOf(window.ProfileBox, window);
        var connected = 0;
        window.Connected += (_, _) => connected++;

        // A domain controller that answers when the test lets it - and, like a native bind, ignores a cancellation: so
        // "connecting" and "stopping" last.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Connector.Gate = gate;
        try
        {
            ControlHost.Press(window.ConnectButton);
            await UiWait.UntilAsync(() => connect.IsConnecting && line.Kind == PageStatusKind.Working, "the bind is on its way");

            // Working: what the window connects to - with Stop, and nothing to close.
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Connecting to {Domain}…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // "Connect" is the Cancel of the attempt: enabled. The search cannot be started meanwhile and is at rest.
            Assert.Equal(ActionButtonState.Running, window.ConnectButton.State);
            Assert.True(window.ConnectButton.IsEnabled);
            Assert.Equal(ActionButtonState.Idle, window.FindDomainControllersButton.State);
            Assert.False(window.FindDomainControllersButton.IsEnabled);
            AssertNoOldFeedback(window);

            // The line was there before: the fields did not move.
            window.UpdateLayout();
            Assert.Equal(fieldsTop, TopOf(window.ProfileBox, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once - the bind that is on its way
            // still has to return.
            ControlHost.Press(window.ConnectButton);
            await UiWait.UntilAsync(
                () => window.ConnectButton.State == ActionButtonState.Cancelling && connect.Status.IsCancelling, "the attempt is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.ConnectButton.IsEnabled);
        }
        finally
        {
            gate.TrySetResult();
        }

        await UiWait.UntilAsync(() => !connect.IsBusy && line.Kind == PageStatusKind.Attention, "the attempt was stopped");
        context.Connector.Gate = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was stopped, that no session was opened, and "Connect" as the way to try again.
        Assert.Equal("Attention", line.Title);
        Assert.Equal($"Connecting to {Domain} was stopped: no session was opened. Press “Connect” to try again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Connect", Assert.IsType<string>(action.Content));
        Assert.Same(connect.ConnectCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.ConnectButton.State);
        Assert.True(window.ConnectButton.IsEnabled);
        Assert.True(window.FindDomainControllersButton.IsEnabled);
        Assert.Equal(0, connected);
        Assert.Null(context.Services.GetRequiredService<ISessionManager>().Current);
        Assert.True(window.IsVisible);

        // A result may need more than one line: the fields moved down by as much as the line grew.
        window.UpdateLayout();
        Assert.Equal(fieldsTop + GrowthOf(line), TopOf(window.ProfileBox, window), 0.5);

        // Closed: the line is empty and one line high again.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(string.Empty, line.Message);
        window.UpdateLayout();
        Assert.Equal(fieldsTop, TopOf(window.ProfileBox, window), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatIsMissing_IsAttention_ARefusedBind_AnErrorWithoutRetry_AndAFailedSearch_AnErrorWithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = await OpenAsync(context);
        var connect = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var errors = context.Services.GetRequiredService<ErrorTranslator>();

        // Explicit credentials without a password: nothing is tried, and the line says what is missing.
        connect.Authentication = AuthenticationMode.Credentials;
        connect.UserName = @"CONTOSO\jan.kowalski";
        await UiTestSupport.WaitForIdleAsync(rounds: 1);
        ControlHost.Press(window.ConnectButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention, "what is missing in the status line");
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Not connected: Enter the password.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(0, context.Connector.ConnectCount);
        Assert.Equal(ActionButtonState.Idle, window.ConnectButton.State);

        // The domain controller refuses the bind: what was tried, why, what to do, the code - and no Retry: the advice
        // is to change something first.
        const string Secret = "Zażółć-gęślą-jaźń-1";
        window.PasswordBox.Password = Secret;
        var refused = new DirectoryException(DirectoryErrorKind.InvalidCredentials, "80090308: LdapErr: DSID-0C09050F, data 52e") { LdapResultCode = 49 };
        context.Connector.ConnectError = refused;
        ControlHost.Press(window.ConnectButton);
        await UiWait.UntilAsync(() => !connect.IsBusy && line.Kind == PageStatusKind.Error, "the bind was refused");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Error", line.Title);
        Assert.Equal($"Could not connect to {Domain}: {errors.Translate(refused)} {errors.ConnectHint(refused)} (LDAP 49)", line.Message);
        Assert.DoesNotContain(Secret, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kowalski", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(1, context.Connector.ConnectCount);
        Assert.Equal(ActionButtonState.Idle, window.ConnectButton.State);
        Assert.True(window.ConnectButton.IsEnabled);
        Assert.Equal(Secret, window.PasswordBox.Password);
        Assert.Null(context.Services.GetRequiredService<ISessionManager>().Current);
        AssertNoOldFeedback(window);

        // A search that fails is safe to repeat: its error has Retry.
        context.Connector.ConnectError = null;
        context.Connector.FindError = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the locator does not answer");
        ControlHost.Press(window.FindDomainControllersButton);
        await UiWait.UntilAsync(() => !connect.IsBusy && line.Kind == PageStatusKind.Error && line.HasAction, "the search failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.StartsWith($"Could not find the domain controllers of {Domain}: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" (ServerUnavailable)", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not answer", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(connect.FindDomainControllersCommand, action.Command);
        Assert.Equal(ActionButtonState.Idle, window.FindDomainControllersButton.State);

        // Retry, pressed in the line, finds them: the summary.
        context.Connector.FindError = null;
        ControlHost.Press(action);
        await UiWait.UntilAsync(
            () => !connect.IsBusy && line.Kind == PageStatusKind.Idle && connect.DomainControllers.Count == 2, "the domain controllers were found");
        Assert.Equal("Domain controllers found: 2", line.Message);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The Connect window of the default connection, shown off-screen and loaded (the computer's domain prefilled).</summary>
    private static async Task<ConnectWindow> OpenAsync(UiTestContext context)
    {
        var window = context.Services.GetRequiredService<ConnectWindow>();
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Domain, window.ViewModel.Domain);
        return window;
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(ConnectWindow window)
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
