using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Computers;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.GroupPolicy;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Services;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.Computers;

/// <summary>
/// The status line of the computer review and of the remote actions window, and the note of the restart dialog (track V, stage
/// V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as built"), through the real windows over the fake directory and the fake
/// remote client: one line at the top of each - the summary or the connection, the operation that runs with Stop, how it ended
/// with its action, what lasts - and the buttons that start an operation as action buttons that are its Cancel. A slow domain
/// controller is a query the test holds, a slow computer a call of the fake client; every state that is asserted lasts until
/// the test changes it. What the windows read and do is covered by <see cref="ComputerWindowsTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ComputerStatusUiTests
{
    private const string FileServerHost = "srv-pliki01.contoso.test";

    /// <summary>The query that streams the computers (the owners and the creators are read with other attributes).</summary>
    private static bool IsComputerQuery(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search: { } search } && search.Attributes.Contains(Attr.OperatingSystem, StringComparer.OrdinalIgnoreCase);

    private static async Task<(ComputerReviewWindow Window, ComputerReviewViewModel Review)> OpenReviewAsync(UiTestContext context)
    {
        context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await main.Shell.ShowComputerReviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<ComputerReviewWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return (window, window.ViewModel);
    }

    [Fact]
    public Task TheReview_HasOneStatusLine_AtTheTop_WithTheSummary_AndFourActionButtons() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en");
        var (window, review) = await OpenReviewAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the scope, the parameters and the tiles, of
        // the fixed height. Before the first check there is nothing to sum up.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.IsAssignableFrom<IHasPageStatus>(review);
        Assert.Same(review.Status.Current, line.Status);
        Assert.Same(review.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.ScopeKindBox, window), "the status line is not above the scope");
        Assert.True(TopOf(window.ScopeKindBox, window) < TopOf(window.ParametersExpander, window), "the scope is not above the parameters");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);

        // The four buttons that start an operation are action buttons over the commands of before and the Cancel of the window.
        foreach (var (button, command) in new (ActionButton, System.Windows.Input.ICommand)[]
        {
            (window.RunButton, review.RunCommand),
            (window.ExactLastLogonButton, review.ReadExactLastLogonCommand),
            (window.ResolveSitesButton, review.ResolveSitesCommand),
            (window.PingButton, review.PingCommand),
        })
        {
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.Same(command, button.Command);
            Assert.Same(review.Status.CancelCommand, button.CancelCommand);
        }

        // The age of the release catalogue is a note - whether it shows depends on the day the test runs.
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.CatalogStaleBar.Style);

        // After a check: the summary - in the line, and not a second time in the footer.
        var tilesTop = TopOf(window.ParametersExpander, window);
        await review.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, review.StatusText);
        Assert.Equal(review.StatusText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == review.StatusText);
        window.UpdateLayout();
        Assert.Equal(tilesTop, TopOf(window.ParametersExpander, window));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACheck_SaysTheScopeAndHowMuchSoFar_RunIsItsCancel_AndAStoppedCheckIsAttentionWithCheck() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en");
        var (window, review) = await OpenReviewAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var parametersTop = TopOf(window.ParametersExpander, window);

        // The query of the check (the computers with their attributes) waits for the test.
        using var held = new HeldRead(session, IsComputerQuery);
        ControlHost.Press(window.RunButton);
        await UiWait.UntilAsync(() => held.IsReached && review.IsRunning && line.IsWorking, "the check runs");

        // Working: what is checked and in which scope - with Stop, and nothing to close.
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Working", line.Title);
        Assert.Equal($"Checking the computers in the scope “{session.Info.DomainDnsName}”…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        // "Check" is the Cancel of the check: enabled although its command cannot execute. There is no separate Stop button
        // and no progress bar.
        Assert.Equal(ActionButtonState.Running, window.RunButton.State);
        Assert.True(window.RunButton.IsEnabled);
        Assert.False(review.RunCommand.CanExecute(null));
        Assert.Equal(ActionButtonState.Idle, window.ExactLastLogonButton.State);
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(window),
            other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line) && other.Content as string == "Stop");
        AssertNoOldFeedback(window);
        window.UpdateLayout();
        Assert.Equal(parametersTop, TopOf(window.ParametersExpander, window));

        // Cancel where you started: "Stopping…" on the button and in the line at once - the query that is on its way to the
        // domain controller still has to return.
        ControlHost.Press(window.RunButton);
        await UiWait.UntilAsync(() => window.RunButton.State == ActionButtonState.Cancelling && review.Status.IsCancelling, "the check is stopping");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Stopping…", line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.True(window.RunButton.IsEnabled);
        held.Release();

        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Attention, "the check was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the results are incomplete, and "Check" runs the check again.
        Assert.Equal("Attention", line.Title);
        Assert.StartsWith("Checking the computers was stopped: the results are incomplete (read: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith("). Press “Check” to check again.", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Check", Assert.IsType<string>(action.Content));
        Assert.Same(review.RunCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        window.UpdateLayout();
        Assert.Equal(parametersTop + GrowthOf(line), TopOf(window.ParametersExpander, window), 0.5);

        // "Check", pressed in the line, runs the whole check: the summary.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the check ran");
        Assert.Equal(review.StatusText, line.Message);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFailedCheck_IsAnErrorWithRetry_AndTheEndOfTheSessionTakesTheLine() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en");
        var (window, review) = await OpenReviewAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;

        // The domain controller does not answer the query of the check.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && IsComputerQuery(operation))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.RunButton);
        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Error, "the check failed");

        // The list is empty and the line says why: what was tried, the reason, what to do, the code - with Retry, and it cannot
        // be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(review.Error);
        Assert.Equal($"Could not check the computers: {review.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(review.RunCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        AssertNoOldFeedback(window);

        // Retry runs the check again.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !review.IsRunning && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the check ran again");
        session.BeforeOperation = null;
        Assert.Equal(review.StatusText, line.Message);

        // The session of the window ends: an error that cannot be closed takes the line.
        await context.Services.GetRequiredService<AdMng.Presentation.Session.ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => review.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[AdMng.Presentation.Localization.L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.False(window.RunButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheRemoteWindow_HasOneStatusLine_UnderItsHeader_SaysTheConnection_AndItsSixStartButtonsAreActionButtons() => WpfTestHost.RunAsync(async () =>
    {
        var (context, window, remote) = await OpenRemoteAsync();
        await using var _ = context;
        var vm = window.ViewModel;
        var line = window.PageStatus;

        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.IsAssignableFrom<IHasPageStatus>(vm);
        Assert.Same(vm.Status.Current, line.Status);
        Assert.Same(vm.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(window.HostText, window) < TopOf(line, window), "the status line is not under the header");
        Assert.True(TopOf(line, window) < TopOf(window.RefreshPolicyButton, window), "the status line is not above the actions");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // The connection is the summary - in the line, and not a second time in the footer.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(vm.ConnectionText, line.Message);
        Assert.Contains("Kerberos", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == vm.ConnectionText);

        foreach (var (button, command) in new (ActionButton, System.Windows.Input.ICommand)[]
        {
            (window.RefreshStatusButton, vm.RefreshStatusCommand),
            (window.LoadUsersButton, vm.LoadUsersCommand),
            (window.LoadServicesButton, vm.LoadServicesCommand),
            (window.RefreshPolicyButton, vm.RefreshPolicyCommand),
            (window.RestartButton, vm.RestartCommand),
            (window.AbortRestartButton, vm.AbortRestartCommand),
        })
        {
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.Same(command, button.Command);
            Assert.Same(vm.Status.CancelCommand, button.CancelCommand);
            Assert.True(button.IsEnabled);
        }

        AssertNoOldFeedback(window);
        Assert.Empty(remote.Calls);
        context.AssertHealthy();
    });

    [Fact]
    public Task ARemoteRead_SaysWhatItReads_ItsButtonIsItsCancel_AndAStoppedReadIsAttentionWithTheButton() => WpfTestHost.RunAsync(async () =>
    {
        var (context, window, remote) = await OpenRemoteAsync();
        await using var _ = context;
        var vm = window.ViewModel;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var actionsTop = TopOf(window.RefreshPolicyButton, window);

        remote.HoldNextOperation = true;
        remote.HeldIgnoresCancellation = true;
        ControlHost.Press(window.LoadUsersButton);
        await UiWait.UntilAsync(() => remote.Held.Task.IsCompleted && vm.IsBusy && line.IsWorking, "the read is held");

        // Working: what is read and from which computer - with Stop, and nothing to close.
        Assert.Equal(PageStatusKind.Working, line.Kind);
        Assert.Equal("Reading the logged-on users of SRV-PLIKI01…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        // The button that started it is its Cancel, the other five wait; no separate Stop button and no progress bar.
        Assert.Equal(ActionButtonState.Running, window.LoadUsersButton.State);
        Assert.True(window.LoadUsersButton.IsEnabled);
        Assert.False(vm.LoadUsersCommand.CanExecute(null));
        Assert.Equal(ActionButtonState.Idle, window.LoadServicesButton.State);
        Assert.False(window.LoadServicesButton.IsEnabled);
        Assert.False(window.RestartButton.IsEnabled);
        AssertNoOldFeedback(window);
        window.UpdateLayout();
        Assert.Equal(actionsTop, TopOf(window.RefreshPolicyButton, window));

        // "Stopping…" on the button and in the line at once.
        ControlHost.Press(window.LoadUsersButton);
        await UiWait.UntilAsync(() => window.LoadUsersButton.State == ActionButtonState.Cancelling && vm.Status.IsCancelling, "the read is stopping");
        Assert.Equal("Stopping…", line.Message);
        remote.ReleaseHeld();
        await UiWait.UntilAsync(() => !vm.IsBusy && line.Kind == PageStatusKind.Attention, "the read was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the list is as it was, and the button reads again.
        Assert.Equal("Reading the logged-on users of SRV-PLIKI01 was stopped. Press “Show logged-on users” to read again.", line.Message);
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Show logged-on users", Assert.IsType<string>(action.Content));
        Assert.Same(vm.LoadUsersCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.LoadUsersButton.State);
        window.UpdateLayout();
        Assert.Equal(actionsTop + GrowthOf(line), TopOf(window.RefreshPolicyButton, window), 0.5);

        // Pressed in the line, it reads: the list is the result, and the line is the summary again.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !vm.IsBusy && line.Kind == PageStatusKind.Idle, "the users were read");
        Assert.Equal(vm.ConnectionText, line.Message);
        Assert.Single(window.UsersList.Items);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AnActionThatFails_IsAnErrorWithoutALinkToRepeatIt_AndWhatCannotBeDoneIsTheStateOfTheWindow() => WpfTestHost.RunAsync(async () =>
    {
        var (context, window, remote) = await OpenRemoteAsync();
        await using var _ = context;
        var vm = window.ViewModel;
        var line = window.PageStatus;
        remote.ConnectFailures[FileServerHost] = RemoteErrorKind.Unreachable;

        ControlHost.Press(window.RefreshPolicyButton);
        await UiWait.UntilAsync(() => !vm.IsBusy && line.Kind == PageStatusKind.Error, "the action failed");

        Assert.Equal("Error", line.Title);
        Assert.Equal($"Could not refresh Group Policy on SRV-PLIKI01: {vm.Error} (Unreachable)", line.Message);
        Assert.False(line.HasAction);
        Assert.True(line.CanClose);
        Assert.Equal(ActionButtonState.Idle, window.RefreshPolicyButton.State);
        Assert.Equal(vm.Log.Count, window.LogList.Items.Count);
        Assert.True(vm.Log[0].IsError);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task AComputerWithoutADnsName_IsTheStateOfTheRemoteWindow_AndTheRestartDialogHasANoteNotABanner() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en");
        var sample = context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
        var factory = context.Services.GetRequiredService<ComputerViewModelFactory>();

        // No DNS name: the window opens and says why nothing can be done, in its line, with no dialog on top of it.
        var window = (RemoteComputerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreateRemote(sample.Prestaged));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var line = window.PageStatus;
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.StartsWith("PREST-PC201 has no DNS host name in Active Directory", line.Message, StringComparison.Ordinal);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.False(window.RefreshStatusButton.IsEnabled);
        Assert.False(window.RestartButton.IsEnabled);
        AssertNoOldFeedback(window);
        window.Close();

        // The restart dialog: why "Restart" is not possible yet is a note next to the fields.
        var target = new RemoteTarget("CN=KRK-PC101,OU=Komputery,OU=Firma,DC=contoso,DC=test", "KRK-PC101", "krk-pc101.contoso.test", ComputerType.Workstation);
        var dialog = (RestartComputerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreateRestart(target));
        UiTestSupport.ShowOffScreen(dialog);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(dialog.ErrorNote.IsVisible);
        dialog.DelayBox.Text = "1441";
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(dialog.ErrorNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), dialog.ErrorNote.Style);
        Assert.Equal(dialog.ViewModel.Error, dialog.ErrorText.Text);
        Assert.Empty(UiWait.Descendants<StatusLine>(dialog));
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.DoesNotContain(UiWait.Descendants<Border>(dialog), b => ReferenceEquals(b.Style, errorBar) || ReferenceEquals(b.Style, warningBar));
        dialog.Close();
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>The remote actions window of the file server over the fake client (a Windows Server 2022 with one user and two services).</summary>
    private static async Task<(UiTestContext Context, RemoteComputerWindow Window, FakeRemoteComputerClient Remote)> OpenRemoteAsync()
    {
        var remote = new FakeRemoteComputerClient();
        var server = new FakeRemoteComputer
        {
            OperatingSystem = new("Microsoft Windows Server 2022 Standard", "10.0.20348", 20348, 2762, new DateTimeOffset(2026, 10, 20, 5, 30, 0, TimeSpan.Zero)),
        };
        server.Users.Add(new LoggedOnUser(@"CONTOSO\jan.kowalski", 2, new DateTimeOffset(2026, 10, 27, 7, 45, 0, TimeSpan.Zero), IsConsole: false));
        server.Services.Add(new RemoteService("Spooler", "Print Spooler", "Stopped", "Auto", false, "LocalSystem"));
        server.Services.Add(new RemoteService("LanmanServer", "Server", "Running", "Auto", false, "LocalSystem"));
        remote.Computers[FileServerHost] = server;
        var ping = new FakePingService();
        ping.Answering[FileServerHost] = TimeSpan.FromMilliseconds(1);
        var context = await UiTestContext.CreateAsync("en", services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRemoteComputerClient>(remote));
            services.Replace(ServiceDescriptor.Singleton<IPingService>(ping));
        });
        var sample = context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
        var dialogs = Assert.IsAssignableFrom<IComputerDialogs>(context.Services.GetRequiredService<IDialogService>());
        await dialogs.ShowRemoteActionsAsync(sample.FileServer);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<RemoteComputerWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(window.ViewModel.Error);
        return (context, window, remote);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(FrameworkElement window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Single(UiWait.Descendants<StatusLine>(window));
    }
}
