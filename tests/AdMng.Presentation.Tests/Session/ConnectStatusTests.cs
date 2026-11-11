using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Session;

/// <summary>
/// The status line of the Connect window (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"):
/// connecting and looking for the domain controllers of the domain are its operations - with Stop - and the line says
/// how they ended: stopped, failed with the reason, the advice and the code, or why nothing was started. A connection
/// that succeeded says nothing (the window closes). Nothing of the credential is ever part of a text or of its log
/// copy. How the window connects is covered, unchanged, by <see cref="SessionAndConnectTests"/>.
/// </summary>
public sealed class ConnectStatusTests
{
    private const string Secret = "Tajne!Hasło1";
    private const string Account = @"CONTOSO\admin.łukasz";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AtRest_TheLineSaysNothing_AndTheWindowHasNoStateThatLasts()
    {
        var (vm, _, _) = CreateConnect();
        Assert.IsAssignableFrom<IHasPageStatus>(vm);

        vm.Initialize();

        // No session yet: nothing to summarize, and nothing like "the session of this window ended".
        Assert.Same(PageStatus.Empty, vm.Status.Current);
        Assert.Null(vm.Status.Lasting);
        Assert.False(vm.Status.IsWorking);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsFindingDomainControllers);
        Assert.False(vm.Status.CloseCommand.CanExecute(null));
        Assert.False(vm.Status.CancelCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("en-US", "Not connected: Enter the password.")]
    [InlineData("pl-PL", "Nie połączono: Wprowadź hasło.")]
    public async Task AConnectionThatWasNotTried_IsSaidAsNotConnected_WithWhatIsMissing(string culture, string expected)
    {
        var (vm, harness, password) = CreateConnect();
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo(culture));
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = Account;
        password.Password = string.Empty;
        var seen = Record(vm.Status);

        await vm.ConnectCommand.ExecuteAsync(null);

        // Nothing was started: an attention the operator can close - never "working" in between.
        var said = vm.Status.Current;
        Assert.Equal(PageStatusKind.Attention, said.Kind);
        Assert.Equal(expected, said.Message);
        Assert.Equal("Connect.PasswordRequired", said.Log.ToString());
        Assert.Null(said.Action);
        Assert.True(said.CanClose);
        Assert.Equal([(PageStatusKind.Attention, expected)], seen);
        Assert.Equal(0, harness.Connector.ConnectCount);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsConnecting);
        Assert.DoesNotContain("admin", said.Message, StringComparison.OrdinalIgnoreCase);

        // Closed: the line is empty again.
        vm.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, vm.Status.Current);

        // Every missing field is said the same way.
        vm.UserName = string.Empty;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, vm.Status.Current.Kind);
        Assert.Equal("Connect.UserNameRequired", vm.Status.Current.Log.ToString());
        Assert.EndsWith(vm.ErrorText!, vm.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connecting_IsTheOperationOfTheWindow_AndStopReturnsControl()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = Account;
        var connected = 0;
        vm.Connected += (_, _) => connected++;

        // A bind that waits for the test and - like a native bind - does not notice a cancellation.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeConnect = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var seen = Record(vm.Status);
        Task connecting;
        try
        {
            connecting = vm.ConnectCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(Limit, Ct);

            // Working: what the window connects to - the domain, never the account - with Stop.
            Assert.True(vm.IsConnecting);
            Assert.True(vm.IsBusy);
            Assert.True(vm.Status.IsWorking);
            var working = vm.Status.Current;
            Assert.Equal(PageStatusKind.Working, working.Kind);
            Assert.Equal("Connecting to contoso.test…", working.Message);
            Assert.Equal("Connect.Connecting", working.Log.ToString());
            Assert.True(working.Progress?.IsIndeterminate);
            Assert.False(working.CanClose);
            var stop = Assert.IsType<PageStatusAction>(working.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(vm.Status.CancelCommand, stop.Command);
            Assert.False(vm.FindDomainControllersCommand.CanExecute(null));

            // Pressed: "Stopping…" at once, for as long as the bind is on its way.
            stop.Command.Execute(stop.Parameter);
            Assert.True(vm.Status.IsCancelling);
            Assert.Equal(PageStatusKind.Working, vm.Status.Current.Kind);
            Assert.Equal("Stopping…", vm.Status.Current.Message);
            Assert.Null(vm.Status.Current.Action);
            Assert.True(vm.IsConnecting);
            Assert.False(vm.Status.CancelCommand.CanExecute(null));
        }
        finally
        {
            release.TrySetResult();
        }

        await connecting.WaitAsync(Limit, Ct);

        // Stopped: what was stopped, that nothing was opened, and the way to try again. A result - it can be closed.
        const string StoppedText = "Connecting to contoso.test was stopped: no session was opened. Press “Connect” to try again.";
        var stopped = vm.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(StoppedText, stopped.Message);
        Assert.Equal("Connect.Status.Stopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Connect", again.Label);
        Assert.Same(vm.ConnectCommand, again.Command);
        Assert.Equal(
            [(PageStatusKind.Working, "Connecting to contoso.test…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsBusy);
        Assert.False(vm.Status.IsWorking);
        Assert.Equal(0, connected);
        Assert.Null(harness.Sessions.Current);
        Assert.True(vm.ConnectCommand.CanExecute(null));

        // "Connect", pressed in the line, connects: the stopped attempt is over and nothing more is said.
        harness.Connector.BeforeConnect = null;
        harness.Connector.NextSession = new InMemoryDirectorySession();
        await ((IAsyncRelayCommand)again.Command).ExecuteAsync(again.Parameter);
        Assert.Equal(1, connected);
        Assert.Same(PageStatus.Empty, vm.Status.Current);
    }

    [Fact]
    public async Task AConnectionThatSucceeded_SaysNothing_TheWindowCloses()
    {
        var (vm, harness, password) = CreateConnect();
        vm.Initialize();
        vm.Server = "dc02.contoso.test";
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = Account;
        var connected = 0;
        vm.Connected += (_, _) => connected++;
        var seen = Record(vm.Status);

        await vm.ConnectCommand.ExecuteAsync(null);

        // Working names the domain controller that was chosen; then the line is empty - no "done" nobody would read.
        Assert.Equal(1, connected);
        Assert.Equal([(PageStatusKind.Working, "Connecting to dc02.contoso.test…"), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Same(PageStatus.Empty, vm.Status.Current);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsBusy);
        Assert.True(password.Cleared);
        Assert.NotNull(harness.Sessions.Current);
    }

    [Theory]
    [InlineData("en-US", "Could not connect to contoso.test: ")]
    [InlineData("pl-PL", "Nie udało się połączyć z contoso.test: ")]
    public async Task AConnectionThatFailed_IsAnErrorWithTheReasonTheAdviceAndTheCode_AndNoRetry(string culture, string lead)
    {
        var (vm, harness, _) = CreateConnect();
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo(culture));
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = Account;
        var failure = new DirectoryException(
            DirectoryErrorKind.StrongAuthRequired,
            "00002028: LdapErr: DSID-0C090259, comment: The server requires binds to turn on integrity checking")
        {
            LdapResultCode = 8,
        };
        harness.Connector.ConnectError = failure;
        var seen = Record(vm.Status);

        await vm.ConnectCommand.ExecuteAsync(null);

        // What was tried: why it failed. What to do. (code) - one flowing text, without the server's own words.
        var failed = vm.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"{lead}{harness.Errors.Translate(failure)} {harness.Errors.ConnectHint(failure)} (LDAP 8)", failed.Message);
        Assert.DoesNotContain('\n', failed.Message);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Connect.Status.Failed [LDAP 8]", failed.Log.ToString());
        Assert.True(failed.CanClose);

        // No Retry: the advice is to change something first, and a repeated bind counts towards a lockout.
        Assert.Null(failed.Action);
        Assert.Equal(2, seen.Count);
        Assert.Equal(PageStatusKind.Working, seen[0].Kind);
        Assert.Equal(PageStatusKind.Error, seen[1].Kind);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsBusy);
        Assert.Equal(harness.Errors.TranslateConnectError(failure), vm.ErrorText);
        Assert.Null(harness.Sessions.Current);

        // Nothing of the credential in what the window says.
        Assert.All(seen, s =>
        {
            Assert.DoesNotContain(Secret, s.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("admin", s.Message, StringComparison.OrdinalIgnoreCase);
        });

        // The next attempt takes the error off the line.
        harness.Connector.ConnectError = null;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Same(PageStatus.Empty, vm.Status.Current);
    }

    [Fact]
    public async Task AnAttemptWithoutAnAdvice_SaysTheReasonAndTheCodeOnly()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        harness.Connector.ConnectError = new InvalidOperationException("Negotiate failed for CONTOSO\\admin.łukasz");

        await vm.ConnectCommand.ExecuteAsync(null);

        var failed = vm.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not connect to contoso.test: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" (InvalidOperationException)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Connect.Status.Failed [InvalidOperationException]", failed.Log.ToString());
        Assert.DoesNotContain("admin", failed.Log.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheWindowClosingWhileItConnects_IsTheSameStop()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeConnect = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(Limit, Ct);
        Assert.True(vm.Status.IsWorking);

        // What the view does when it closes while the bind runs: the cancel of the command.
        vm.ConnectCancelCommand.Execute(null);
        await connecting.WaitAsync(Limit, Ct);

        Assert.False(vm.Status.IsWorking);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsBusy);
        Assert.Equal(PageStatusKind.Attention, vm.Status.Current.Kind);
        Assert.Equal("Connect.Status.Stopped", vm.Status.Current.Log.ToString());
        Assert.Null(harness.Sessions.Current);
    }

    [Theory]
    [InlineData("en-US", "Looking for domain controllers of contoso.test…", "Domain controllers found: 2")]
    [InlineData("pl-PL", "Wyszukiwanie kontrolerów domeny contoso.test…", "Znalezione kontrolery domeny: 2")]
    public async Task LookingForDomainControllers_IsAnOperation_AndWhatWasFoundIsTheSummary(string culture, string working, string summary)
    {
        var (vm, harness, _) = CreateConnect();
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo(culture));
        vm.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeFind = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var seen = Record(vm.Status);
        Task finding;
        try
        {
            finding = vm.FindDomainControllersCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(Limit, Ct);

            Assert.True(vm.IsFindingDomainControllers);
            Assert.False(vm.IsConnecting);
            Assert.True(vm.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, vm.Status.Current.Kind);
            Assert.Equal(working, vm.Status.Current.Message);
            Assert.Equal("Connect.FindingDomainControllers", vm.Status.Current.Log.ToString());
            Assert.Same(vm.Status.CancelCommand, Assert.IsType<PageStatusAction>(vm.Status.Current.Action).Command);
            Assert.False(vm.ConnectCommand.CanExecute(null));
        }
        finally
        {
            release.TrySetResult();
        }

        await finding.WaitAsync(Limit, Ct);

        // The result is the list of the drop-down; the line says how long it is - the summary, nothing to close.
        Assert.Equal(2, vm.DomainControllers.Count);
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Idle, summary)], seen);
        Assert.Equal(PageStatusKind.Idle, vm.Status.Current.Kind);
        Assert.Equal(summary, vm.Status.Current.Message);
        Assert.False(vm.Status.CloseCommand.CanExecute(null));
        Assert.False(vm.IsFindingDomainControllers);
        Assert.False(vm.IsBusy);

        // A later result covers the summary and gives it back when it is closed.
        vm.Authentication = AuthenticationMode.Credentials;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, vm.Status.Current.Kind);
        vm.Status.CloseCommand.Execute(null);
        Assert.Equal(summary, vm.Status.Current.Message);
    }

    [Fact]
    public async Task ASearchThatWasStopped_IsAttentionWithItsButton_AndOneThatFailedAnErrorWithRetry()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeFind = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var seen = Record(vm.Status);

        var finding = vm.FindDomainControllersCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(Limit, Ct);
        vm.Status.CancelCommand.Execute(null);
        await finding.WaitAsync(Limit, Ct);

        // Stopped by the operator: the drop-down is as it was, and the button of the search is the way to look again.
        const string StoppedText =
            "Looking for the domain controllers of contoso.test was stopped: the list was not changed. Press “Find domain controllers” to look again.";
        Assert.Equal(
            [(PageStatusKind.Working, "Looking for domain controllers of contoso.test…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        var stopped = vm.Status.Current;
        Assert.Equal("Connect.Status.FindStopped", stopped.Log.ToString());
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Find domain controllers", again.Label);
        Assert.Same(vm.FindDomainControllersCommand, again.Command);
        Assert.Empty(vm.DomainControllers);
        Assert.False(vm.IsFindingDomainControllers);
        Assert.False(vm.IsBusy);

        // Failed: what was tried, why, what to do, the code - with Retry, and it can be closed.
        harness.Connector.BeforeFind = null;
        var failure = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "The LDAP server is unavailable: dc09.contoso.test");
        harness.Connector.FindError = failure;
        await vm.FindDomainControllersCommand.ExecuteAsync(null);

        var failed = vm.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            $"Could not find the domain controllers of contoso.test: {harness.Errors.Translate(failure)} {harness.Errors.ConnectHint(failure)} (ServerUnavailable)",
            failed.Message);
        Assert.Equal("Connect.Status.FindFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.DoesNotContain("dc09", failed.Message, StringComparison.Ordinal);
        Assert.True(failed.CanClose);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(vm.FindDomainControllersCommand, retry.Command);

        // Retry finds them: the summary.
        harness.Connector.FindError = null;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Equal(PageStatusKind.Idle, vm.Status.Current.Kind);
        Assert.Equal("Domain controllers found: 2", vm.Status.Current.Message);
    }

    [Fact]
    public async Task ASearchWithoutADomain_IsSaidAsNotLookedFor()
    {
        var (vm, harness, _) = CreateConnect();
        harness.Connector.ComputerDomain = null;
        vm.Initialize();

        await vm.FindDomainControllersCommand.ExecuteAsync(null);

        var said = vm.Status.Current;
        Assert.Equal(PageStatusKind.Attention, said.Kind);
        Assert.Equal("The domain controllers were not looked for: Enter the domain name (this computer is not joined to a domain).", said.Message);
        Assert.Equal("Connect.DomainRequired", said.Log.ToString());
        Assert.False(vm.IsFindingDomainControllers);
    }

    [Fact]
    public async Task ChoosingAProfile_TakesAResultOffTheLine()
    {
        var (vm, harness, password) = CreateConnect();
        await harness.Settings.UpdateAsync(
            s => s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Produkcja", Domain = "contoso.test", Authentication = AuthenticationMode.CurrentUser }),
            Ct);
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = Account;
        password.Password = string.Empty;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, vm.Status.Current.Kind);

        // What the line said was about the connection typed before.
        vm.SelectedProfile = Assert.Single(vm.Profiles);

        Assert.Same(PageStatus.Empty, vm.Status.Current);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverTheDomainTheAccountOrThePassword()
    {
        var harness = TestHarness.Disconnected();
        var logger = new ListLogger<PageStatusHolder>();
        var vm = new ConnectViewModel(
            harness.Connector, harness.Sessions, harness.Settings, harness.Localizer, harness.Errors, harness.Dispatcher, statusLog: logger)
        {
            PasswordInput = new FakePasswordInput(Secret),
        };
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = Account;

        // Looked for, failed to connect, connected.
        await vm.FindDomainControllersCommand.ExecuteAsync(null);
        harness.Connector.ConnectError = new DirectoryException(DirectoryErrorKind.InvalidCredentials, "80090308: LdapErr: DSID-0C09050F, data 52e")
        {
            LdapResultCode = 49,
            Win32Error = 0x52E,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("contoso.test", vm.Status.Current.Message, StringComparison.Ordinal);
        harness.Connector.ConnectError = null;
        await vm.ConnectCommand.ExecuteAsync(null);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("Connect.FindingDomainControllers", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Connect.Connecting", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Connect.Status.Failed", StringComparison.Ordinal) && m.Contains("LDAP 49, 0x52E", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("łukasz", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);
    }

    private static (ConnectViewModel Vm, TestHarness Harness, FakePasswordInput Password) CreateConnect()
    {
        var harness = TestHarness.Disconnected();
        var password = new FakePasswordInput(Secret);
        var vm = new ConnectViewModel(harness.Connector, harness.Sessions, harness.Settings, harness.Localizer, harness.Errors, harness.Dispatcher)
        {
            PasswordInput = password,
        };
        return (vm, harness, password);
    }

    private static List<(PageStatusKind Kind, string Message)> Record(PageStatusHolder status)
    {
        var seen = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (seen)
                {
                    seen.Add((status.Current.Kind, status.Current.Message));
                }
            }
        };
        status.PropertyChanged += handler;
        return seen;
    }
}
