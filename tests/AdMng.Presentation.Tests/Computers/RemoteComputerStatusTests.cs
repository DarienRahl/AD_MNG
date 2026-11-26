using System.Globalization;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>
/// What the remote actions window says in its status line (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as
/// built"), en-US unless a test says otherwise, over the fake WinRM client: the connection as the summary, one action at a
/// time with Stop - a read ends without a message, an action as done, a stopped one as an attention that says it is not
/// known whether the computer got the request, a failed one as an error with what to do - and what lasts: the session ended,
/// the computer has no DNS name, remote actions are not available. Nothing of the computer in a log copy. What each action
/// does is covered, unchanged, by <see cref="RemoteComputerViewModelTests"/>.
/// </summary>
public sealed class RemoteComputerStatusTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static async Task<(ComputerScenario Scenario, RemoteComputerViewModel Window)> OpenAsync(string culture = "en-US")
    {
        var scenario = await ComputerScenario.CreateAsync(culture);
        var dn = scenario.Sample.Pro24H2;
        var host = scenario.Harness.Value(dn, Core.ActiveDirectory.Attr.DnsHostName)!;
        var remote = new FakeRemoteComputer
        {
            OperatingSystem = new RemoteOperatingSystem("Microsoft Windows 11 Pro", "10.0.26100", 26100, 4652, new DateTimeOffset(2026, 10, 26, 7, 15, 0, TimeSpan.Zero)),
        };
        remote.Users.Add(new LoggedOnUser(@"CONTOSO\jan.kowalski", 2, new DateTimeOffset(2026, 10, 27, 7, 30, 0, TimeSpan.Zero), false));
        remote.Services.Add(new RemoteService("Spooler", "Print Spooler", "Running", "Auto", false, "LocalSystem"));
        remote.Services.Add(new RemoteService("BITS", "Background Intelligent Transfer Service", "Stopped", "Auto", true, "LocalSystem"));
        scenario.Remote.Computers[host] = remote;
        var window = scenario.Factory.CreateRemote(dn);
        await window.InitializeAsync();
        return (scenario, window);
    }

    private static List<string> LogsOf(PageStatusHolder status)
    {
        var logs = new List<string>();
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(status.Current.ToString());
            }
        };
        return logs;
    }

    [Fact]
    public async Task TheConnection_IsTheSummary_AndNothingIsSaidUntilSomethingIsDone()
    {
        var (_, window) = await OpenAsync();
        using var _window = window;
        Assert.IsAssignableFrom<IHasPageStatus>(window);

        var summary = window.Status.Current;
        Assert.Equal(PageStatusKind.Idle, summary.Kind);
        Assert.Equal(@"Connection: WinRM (HTTP 5985, Kerberos) as CONTOSO\Administrator", summary.Message);
        Assert.Equal(window.ConnectionText, summary.Message);
        Assert.Null(window.Status.Lasting);
        Assert.False(window.IsReadingStatus || window.IsReadingUsers || window.IsReadingServices || window.IsRefreshingPolicy || window.IsSchedulingRestart || window.IsAbortingRestart);
    }

    [Fact]
    public async Task AReadSaysWhatItReads_AndEndsAsTheSummary_BecauseTheCardOrTheListIsTheResult()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        var seen = new StatusRecorder(window.Status);
        var logs = LogsOf(window.Status);

        await window.LoadServicesCommand.ExecuteAsync(null);
        await window.LoadUsersCommand.ExecuteAsync(null);
        await window.RefreshStatusCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                (PageStatusKind.Working, "Reading the services of KRK-PC101…"),
                (PageStatusKind.Idle, window.ConnectionText),
                (PageStatusKind.Working, "Reading the logged-on users of KRK-PC101…"),
                (PageStatusKind.Idle, window.ConnectionText),
                (PageStatusKind.Working, "Reading the status of KRK-PC101…"),
                (PageStatusKind.Idle, window.ConnectionText),
            ],
            seen.Plain);
        Assert.Equal(2, window.ServiceRows.Count);
        Assert.Single(window.LoggedOnUsers);
        Assert.NotEmpty(window.SystemText);
        Assert.Equal(3, window.Log.Count);
        Assert.Contains("Working: RemoteActions.Status.TitleServices", logs);
        Assert.DoesNotContain(logs, log => log.Contains("krk", StringComparison.OrdinalIgnoreCase) || log.Contains("pc101", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(scenario.Harness.Notifications.Items);
    }

    [Fact]
    public async Task Stop_OfARead_ReturnsControlAtOnce_AndSaysItAgain_WithTheButtonAsItsAction()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        scenario.Remote.HoldNextOperation = true;

        var call = window.LoadUsersCommand.ExecuteAsync(null);
        await scenario.Remote.Held.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Working, with Stop - one operation: the flag of the button that started it follows it, the others wait.
        var working = window.Status.Current;
        Assert.Equal((PageStatusKind.Working, "Reading the logged-on users of KRK-PC101…"), (working.Kind, working.Message));
        Assert.Same(window.Status.CancelCommand, working.Action?.Command);
        Assert.True(window.IsReadingUsers);
        Assert.False(window.IsReadingServices);
        Assert.False(window.LoadServicesCommand.CanExecute(null));
        Assert.False(window.RestartCommand.CanExecute(null));

        working.Action!.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Stopping…"), (window.Status.Current.Kind, window.Status.Current.Message));
        await call.WaitAsync(Limit, TestContext.Current.CancellationToken);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Reading the logged-on users of KRK-PC101 was stopped. Press “Show logged-on users” to read again.", stopped.Message);
        Assert.Equal("RemoteActions.Status.StoppedRead", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Same(window.LoadUsersCommand, stopped.Action?.Command);
        Assert.False(window.IsBusy);
        Assert.False(window.IsReadingUsers);

        // The log of the window and the sentence of the error say what they said before.
        Assert.Equal("Cancelled.", window.Error);
        Assert.Equal("Logged-on users: Cancelled.", window.Log[0].Text);
    }

    [Fact]
    public async Task AStopDuringThePing_SaysThatTheStatusReadWasStopped_AndTheComputerWasNotAsked()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        scenario.Ping.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var status = window.RefreshStatusCommand.ExecuteAsync(null);
        await scenario.Ping.Held.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.True(window.IsReadingStatus);
        window.Status.CancelCommand.Execute(null);
        Assert.Equal("Stopping…", window.Status.Current.Message);
        await status.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.Equal("Reading the status of KRK-PC101 was stopped. Press “Check status” to read again.", window.Status.Current.Message);
        Assert.Same(window.RefreshStatusCommand, window.Status.Current.Action?.Command);
        Assert.Empty(scenario.Remote.Calls);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task AnAction_EndsAsDone_WithTheComputer_AndAStoppedOneSaysThatItIsNotKnownWhetherTheComputerGotIt()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        var seen = new StatusRecorder(window.Status);

        await window.RefreshPolicyCommand.ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Working, "Refreshing Group Policy on KRK-PC101…"), seen.Plain[0]);
        var done = window.Status.Current;
        Assert.Equal(PageStatusKind.Done, done.Kind);
        Assert.StartsWith("KRK-PC101: policy refreshed (computer policy processed ", done.Message, StringComparison.Ordinal);
        Assert.Equal("RemoteActions.Status.ActionDone", done.Log.ToString());
        Assert.Null(done.Action);

        // The restart names the computer by itself.
        scenario.Dialogs.RestartHandler = d =>
        {
            d.DelayMinutes = 15;
            d.ConfirmCommand.Execute(null);
            return true;
        };
        await window.RestartCommand.ExecuteAsync(null);
        Assert.Equal((PageStatusKind.Done, "KRK-PC101 restarts in 15 min"), (window.Status.Current.Kind, window.Status.Current.Message));

        // The cancellation of the restart.
        await window.AbortRestartCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.StartsWith("KRK-PC101: cancellation of a scheduled restart requested", window.Status.Current.Message, StringComparison.Ordinal);

        // Stopped on its way: nobody knows, and no link in the line repeats an action.
        scenario.Remote.HoldNextOperation = true;
        scenario.Dialogs.RestartHandler = d =>
        {
            d.ConfirmCommand.Execute(null);
            return true;
        };
        var restart = window.RestartCommand.ExecuteAsync(null);
        await scenario.Remote.Held.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.True(window.IsSchedulingRestart);
        window.Status.CancelCommand.Execute(null);
        await restart.WaitAsync(Limit, TestContext.Current.CancellationToken);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Scheduling the restart of KRK-PC101 was stopped: it is not known whether the computer received the request.", stopped.Message);
        Assert.Null(stopped.Action);
        Assert.False(window.IsSchedulingRestart);
    }

    [Fact]
    public async Task AFailedRead_IsAnErrorWithRetry_AndAFailedAction_IsAnErrorWithoutOne()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        scenario.Remote.ConnectFailures["krk-pc101.contoso.test"] = RemoteErrorKind.WinRmNotAvailable;
        var logs = LogsOf(window.Status);

        await window.LoadServicesCommand.ExecuteAsync(null);

        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            $"Could not read the services of KRK-PC101: {window.Error} Press Retry to try again. (WinRmNotAvailable)",
            failed.Message);
        Assert.Equal("RemoteActions.Status.FailedServices [WinRmNotAvailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Equal("Retry", failed.Action?.Label);
        Assert.Same(window.LoadServicesCommand, failed.Action?.Command);

        // An action that changes something is never repeated by a link.
        await window.RefreshPolicyCommand.ExecuteAsync(null);
        var action = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, action.Kind);
        Assert.Equal($"Could not refresh Group Policy on KRK-PC101: {window.Error} (WinRmNotAvailable)", action.Message);
        Assert.Null(action.Action);
        Assert.DoesNotContain(logs, log => log.Contains("krk", StringComparison.OrdinalIgnoreCase));

        // Closing it shows the summary; the same reason stays in the log of the window.
        window.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.True(window.Log[0].IsError);
    }

    [Fact]
    public async Task APolishWindow_SaysItsActions_InPolish()
    {
        var (scenario, window) = await OpenAsync("pl-PL");
        using var _window = window;
        scenario.Remote.ConnectFailures["krk-pc101.contoso.test"] = RemoteErrorKind.AccessDenied;
        var seen = new StatusRecorder(window.Status);

        await window.LoadUsersCommand.ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Working, "Odczytywanie zalogowanych użytkowników komputera KRK-PC101…"), seen.Plain[0]);
        Assert.StartsWith("Nie udało się odczytać zalogowanych użytkowników komputera KRK-PC101: Odmowa dostępu.", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith("Wybierz Ponów, aby spróbować ponownie. (AccessDenied)", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal("Ponów", window.Status.Current.Action?.Label);
    }

    [Fact]
    public async Task AComputerWithoutADnsName_IsTheStateOfTheWindow_NotADialog()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var window = scenario.Factory.CreateRemote(scenario.Sample.Prestaged);
        await window.InitializeAsync();

        var state = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.StartsWith("PREST-PC201 has no DNS host name in Active Directory", state.Message, StringComparison.Ordinal);
        Assert.Equal("Computers.NoDnsName", state.Log.ToString());
        Assert.Equal(state.Message, window.Error);
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Same(window.Status.Lasting, state);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
        Assert.False(window.RefreshStatusCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnObjectThatIsNotAComputer_IsSaidAsSuch_NotAsAMissingDnsName()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var window = scenario.Factory.CreateRemote(scenario.Company.JanKowalski);
        await window.InitializeAsync();

        var state = window.Status.Current;
        Assert.Equal((PageStatusKind.Error, "The object is not a computer (any more)."), (state.Kind, state.Message));
        Assert.Equal("Computers.NotAComputer", state.Log.ToString());
        Assert.Equal(state.Message, window.Error);
        Assert.False(state.CanClose);
        Assert.False(window.RefreshStatusCommand.CanExecute(null));
    }

    [Fact]
    public async Task AComputerThatCouldNotBeRead_IsTheStateOfTheWindow_WithWhatWasTriedWhyAndTheCode()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var dn = scenario.Sample.Pro24H2;
        scenario.Harness.Session.FailNext(FakeOperationKind.Get, dn, Core.Directory.DirectoryErrorKind.ServerUnavailable);
        using var window = scenario.Factory.CreateRemote(dn);
        var logs = LogsOf(window.Status);
        await window.InitializeAsync();

        // Not "no DNS name": the directory could not be read, and the line says so with the reason and the code - in its
        // log copy the key and the code, never the computer.
        var state = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal("Could not read the computer “KRK-PC101”: The domain controller is not available. (LDAP 52)", state.Message);
        Assert.Equal("Computers.TargetReadFailed [LDAP 52]", state.Log.ToString());
        Assert.Equal(state.Message, window.Error);
        Assert.False(state.CanClose);
        Assert.Same(window.Status.Lasting, state);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
        Assert.False(window.RefreshStatusCommand.CanExecute(null));
        Assert.DoesNotContain(logs, log => log.Contains("krk", StringComparison.OrdinalIgnoreCase));

        // The same in Polish.
        scenario.Harness.Session.FailNext(FakeOperationKind.Get, dn, Core.Directory.DirectoryErrorKind.ServerUnavailable);
        scenario.Harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        using var polish = scenario.Factory.CreateRemote(dn);
        await polish.InitializeAsync();
        Assert.Equal("Nie udało się odczytać komputera „KRK-PC101”: Kontroler domeny jest niedostępny. (LDAP 52)", polish.Status.Current.Message);
    }

    [Fact]
    public async Task WithoutARemoteClient_TheWindowSaysThatRemoteActionsAreNotAvailable()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var window = new RemoteComputerViewModel(
            scenario.Services, scenario.Actions, new WindowsReleaseCatalog(), scenario.Harness.AuditLog, scenario.Harness.PowerShell, scenario.Sample.Pro24H2);
        await window.InitializeAsync();

        var state = window.Status.Current;
        Assert.Equal((PageStatusKind.Error, window.Error), (state.Kind, state.Message));
        Assert.Equal("This action is available only in AD Manager on Windows.", state.Message);
        Assert.Equal("Computers.ToolsNotAvailable", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.False(window.RefreshStatusCommand.CanExecute(null));
        Assert.True(window.RemoteDesktopCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindow_AndCoversTheActionThatRuns()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        scenario.Remote.HoldNextOperation = true;
        var seen = new StatusRecorder(window.Status);

        var call = window.LoadUsersCommand.ExecuteAsync(null);
        await scenario.Remote.Held.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        await scenario.Harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: scenario.Harness.Time).Session);
        await call.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // From "working" straight to the end of the session - no "Stopping…", no summary in between.
        Assert.True(window.IsSessionEnded);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.False(window.Status.Current.CanClose);
        Assert.Same(window.Status.Lasting, window.Status.Current);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Plain.Select(s => s.Kind).Distinct().ToList());
        Assert.DoesNotContain(seen.Plain, s => s.Message == "Stopping…");
        Assert.False(window.Status.IsWorking);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task ClosingTheWindow_DuringAnAction_EndsTheOperation()
    {
        var (scenario, window) = await OpenAsync();
        scenario.Remote.HoldNextOperation = true;

        var call = window.LoadServicesCommand.ExecuteAsync(null);
        await scenario.Remote.Held.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        window.Dispose();
        await call.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.False(window.Status.IsWorking);
        Assert.Null(window.Status.Operation);
    }

    [Fact]
    public async Task TheStateOfTheSession_FollowsALanguageSwitchAtOnce()
    {
        var (scenario, window) = await OpenAsync();
        using var _window = window;
        await scenario.Harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: scenario.Harness.Time).Session);
        var english = window.Status.Current.Message;
        Assert.Equal(window.SessionEndedMessage, english);

        scenario.Harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Equal(window.SessionEndedMessage, window.Status.Current.Message);
        Assert.NotEqual(english, window.Status.Current.Message);
    }
}
