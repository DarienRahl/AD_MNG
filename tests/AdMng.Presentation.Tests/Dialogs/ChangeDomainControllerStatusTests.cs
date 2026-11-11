using System.ComponentModel;
using System.Globalization;
using System.Net;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Connection;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of "Change domain controller" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"):
/// the list of domain controllers says that it is read, how many it shows and - for as long as it lasts - that it could
/// not be read or that its read was stopped; the window says the switch (with Stop), why the session was not moved,
/// and - as the state that lasts - that it has no session. A switch that succeeded says nothing: the dialog closes. How
/// the list is built and the session is switched is covered, unchanged, by <see cref="ChangeDomainControllerViewModelTests"/>.
/// </summary>
public sealed class ChangeDomainControllerStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLine_ShowsWhatTheListSays_HowManyDomainControllers_InBothLanguages()
    {
        var harness = await HarnessAsync();
        using var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, connector: null);
        Assert.IsAssignableFrom<IHasPageStatus>(dialog);
        Assert.Same(dialog.ListStatus, dialog.Status.Front);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);

        await dialog.InitializeAsync();

        // At rest: the summary of the list - said by the list, shown by the window; nothing to close, nothing lasting.
        Assert.Equal(PageStatusKind.Idle, dialog.Status.Current.Kind);
        Assert.Same(dialog.ListStatus.Current, dialog.Status.Current);
        Assert.Equal("3 domain controllers", dialog.Status.Current.Message);
        Assert.False(dialog.Status.IsWorking);
        Assert.False(dialog.ListStatus.IsWorking);
        Assert.Null(dialog.Status.Lasting);
        Assert.Null(dialog.ListStatus.Lasting);
        Assert.False(dialog.Status.CloseCommand.CanExecute(null));

        // The plural form of the language: 3 kontrolery, 5 kontrolerów, 1 kontroler.
        harness.Localizer.SetCulture(Polish);
        Assert.Equal("3 kontrolery domeny", dialog.Status.Current.Message);
        DomainControllerSeed.Add(harness.Session, "dc03.contoso.test", "Kraków");
        DomainControllerSeed.Add(harness.Session, "dc04.contoso.test", "Kraków");
        await dialog.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("5 kontrolerów domeny", dialog.Status.Current.Message);
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("5 domain controllers", dialog.Status.Current.Message);
    }

    [Fact]
    public async Task ReadingTheList_IsTheOperationOfTheList_AndAStoppedReadIsItsStateWithRefresh()
    {
        var harness = await HarnessAsync();
        var locator = new HeldLocator([new DomainControllerInfo("dc02.contoso.test", "Kraków", true, false, true)]);
        using var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, locator);
        var seen = Record(dialog.Status);
        Task loading;
        try
        {
            loading = dialog.InitializeAsync();
            await locator.Entered.WaitAsync(Limit, Ct);

            // Working - the operation is the list's, and so is its Stop; the window itself is not working.
            Assert.True(dialog.IsLoading);
            Assert.True(dialog.ListStatus.IsWorking);
            Assert.False(dialog.Status.IsWorking);
            Assert.Same(dialog.ListStatus.Current, dialog.Status.Current);
            Assert.Equal(PageStatusKind.Working, dialog.Status.Current.Kind);
            Assert.Equal("Reading the domain controllers of contoso.test…", dialog.Status.Current.Message);
            Assert.Equal("ChangeDc.Status.Reading", dialog.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(dialog.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(dialog.ListStatus.CancelCommand, stop.Command);
            Assert.False(dialog.RefreshCommand.CanExecute(null));

            // Pressed: "Stopping…" at once, for as long as the locator is on its way.
            stop.Command.Execute(stop.Parameter);
            Assert.True(dialog.ListStatus.IsCancelling);
            Assert.Equal(PageStatusKind.Working, dialog.Status.Current.Kind);
            Assert.Equal("Stopping…", dialog.Status.Current.Message);
            Assert.True(dialog.IsLoading);
        }
        finally
        {
            locator.Release();
        }

        await loading.WaitAsync(Limit, Ct);

        // Stopped: the list lacks domain controllers until it is read again - its state, with Refresh; a name can be typed.
        const string StoppedText =
            "Reading the domain controllers was stopped: the list may not show all of them. "
            + "Type the name of a domain controller, or press Refresh to read the list again.";
        Assert.Equal(
            [(PageStatusKind.Working, "Reading the domain controllers of contoso.test…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        var stopped = dialog.Status.Current;
        Assert.Same(dialog.ListStatus.Lasting, stopped);
        Assert.Equal("ChangeDc.Status.ReadStopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.False(dialog.IsLoading);
        Assert.False(dialog.ListStatus.IsWorking);
        Assert.Null(dialog.ListNote);
        Assert.Contains(dialog.DomainControllers, d => d.Entry.IsCurrent);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(dialog.RefreshCommand, refresh.Command);
        dialog.ServerText = "dc02.contoso.test";
        Assert.True(dialog.ChangeCommand.CanExecute(null));

        // Refresh reads the whole list: the state is over, the summary is back.
        await ((IAsyncRelayCommand)refresh.Command).ExecuteAsync(refresh.Parameter);
        Assert.Null(dialog.ListStatus.Lasting);
        Assert.Equal(PageStatusKind.Idle, dialog.Status.Current.Kind);
        Assert.Equal("3 domain controllers", dialog.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Could not read the list of domain controllers: ", " Type the name of a domain controller, or press Retry to read the list again. (AccessDenied)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać listy kontrolerów domeny: ", " Wpisz nazwę kontrolera domeny albo wybierz Ponów, aby wczytać listę ponownie. (AccessDenied)", "Ponów")]
    public async Task AListThatCouldNotBeRead_IsTheStateOfTheList_WithTheReasonAndRetry(string culture, string lead, string tail, string retryLabel)
    {
        var harness = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS)");
            }
        };
        using var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, connector: null);

        await dialog.InitializeAsync();

        // An attention, not an error - the dialog still works - that lasts while the list shows only the current DC.
        var failed = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, failed.Kind);
        Assert.Same(dialog.ListStatus.Lasting, failed);
        Assert.Equal(lead + harness.Errors.Translate(DirectoryErrorKind.AccessDenied) + tail, failed.Message);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("ChangeDc.Status.ReadFailed [AccessDenied]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.False(dialog.Status.CloseCommand.CanExecute(null));
        Assert.Equal(harness.Localizer[L.ChangeDc.ListUnavailable], dialog.ListNote);
        Assert.True(Assert.Single(dialog.DomainControllers).Entry.IsCurrent);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(dialog.RefreshCommand, retry.Command);

        // A name can still be typed and switched to - what the window then says covers the state, and gives it back.
        harness.ConnectToServer = (_, _, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "The server is not operational.");
        dialog.ServerText = "dc09.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Error, dialog.Status.Current.Kind);
        Assert.True(dialog.Status.Current.CanClose);
        dialog.Status.CloseCommand.Execute(null);
        Assert.Same(dialog.ListStatus.Lasting, dialog.Status.Current);

        // Retry reads the list: the state is over.
        fail = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        harness.Session.BeforeOperation = null;
        Assert.Null(dialog.ListStatus.Lasting);
        Assert.Null(dialog.ListNote);
        Assert.Equal(PageStatusKind.Idle, dialog.Status.Current.Kind);
        Assert.Equal(3, dialog.DomainControllers.Count);
    }

    [Fact]
    public async Task Switching_IsTheOperationOfTheWindow_AndStopReturnsControl()
    {
        var harness = await HarnessAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A domain controller that answers when the test lets it and - like a native bind - ignores a cancellation.
        harness.ConnectToServer = async (_, server, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new FakeDomainControllerSession(harness.Session, server);
        };
        using var dialog = await OpenAsync(harness);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.ServerText = "dc02.contoso.test";
        var seen = Record(dialog.Status);
        Task switching;
        try
        {
            switching = dialog.ChangeCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(Limit, Ct);

            // Working - the window's own operation, with its Stop; the list is not working.
            Assert.True(dialog.IsBusy);
            Assert.True(dialog.Status.IsWorking);
            Assert.False(dialog.ListStatus.IsWorking);
            var working = dialog.Status.Current;
            Assert.Equal(PageStatusKind.Working, working.Kind);
            Assert.Equal("Connecting to dc02.contoso.test…", working.Message);
            Assert.Equal("Connect.Connecting", working.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(working.Action);
            Assert.Same(dialog.Status.CancelCommand, stop.Command);

            // Stop: "Stopping…" at once, while the bind is still on its way.
            stop.Command.Execute(stop.Parameter);
            Assert.True(dialog.Status.IsCancelling);
            Assert.Equal("Stopping…", dialog.Status.Current.Message);
            Assert.True(dialog.IsBusy);
        }
        finally
        {
            release.TrySetResult();
        }

        await switching.WaitAsync(Limit, Ct);

        // Stopped: what was stopped, where the session is, and the way to try again. A result - it can be closed.
        const string StoppedText =
            "Connecting to dc02.contoso.test was stopped: the session stays connected to dc01.contoso.test. Press “Change” to try again.";
        Assert.Equal(
            [(PageStatusKind.Working, "Connecting to dc02.contoso.test…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        var stopped = dialog.Status.Current;
        Assert.Equal("ChangeDc.Status.Stopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Change", again.Label);
        Assert.Same(dialog.ChangeCommand, again.Command);
        Assert.False(dialog.IsBusy);
        Assert.False(dialog.Status.IsWorking);
        Assert.Null(closed);
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Empty(harness.AuditLog.Entries);

        // Closed: what the list says is shown again.
        dialog.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, dialog.Status.Current.Kind);
        Assert.Equal("3 domain controllers", dialog.Status.Current.Message);
    }

    [Fact]
    public async Task TheCancelOfTheDialog_WhileItSwitches_IsTheSameStop()
    {
        var harness = await HarnessAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ConnectToServer = async (_, server, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new FakeDomainControllerSession(harness.Session, server);
        };
        using var dialog = await OpenAsync(harness);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.ServerText = "dc02.contoso.test";

        var switching = dialog.ChangeCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(Limit, Ct);
        dialog.CancelCommand.Execute(null);
        await switching.WaitAsync(Limit, Ct);

        Assert.Null(closed);
        Assert.Equal(PageStatusKind.Attention, dialog.Status.Current.Kind);
        Assert.Equal("ChangeDc.Status.Stopped", dialog.Status.Current.Log.ToString());
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);
    }

    [Theory]
    [InlineData("en-US", "Could not move the session to dc02.contoso.test: ", "The session stays connected to dc01.contoso.test.")]
    [InlineData("pl-PL", "Nie udało się przenieść sesji na dc02.contoso.test: ", "Sesja pozostaje połączona z dc01.contoso.test.")]
    public async Task ASwitchThatFailed_IsAnErrorWithTheReasonTheAdviceWhereTheSessionIsAndTheCode(string culture, string lead, string stays)
    {
        var harness = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        var failure = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "The LDAP server is unavailable.") { LdapResultCode = 81 };
        harness.ConnectToServer = (_, _, _) => throw failure;
        using var dialog = await OpenAsync(harness);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.ServerText = "dc02.contoso.test";
        var seen = Record(dialog.Status);

        await dialog.ChangeCommand.ExecuteAsync(null);

        // What was tried: why it failed. What to do, and where the session is. (code) - one flowing text.
        var failed = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"{lead}{harness.Errors.Translate(failure)} {harness.Errors.ConnectHint(failure)} {stays} (LDAP 81)", failed.Message);
        Assert.DoesNotContain('\n', failed.Message);
        Assert.Equal("ChangeDc.Status.Failed [LDAP 81]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(failed.Action);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(s => s.Kind));
        Assert.Null(closed);
        Assert.False(dialog.IsBusy);
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);

        // Another domain controller can be tried right away; a switch that succeeds says nothing - the dialog closes.
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server));
        seen.Clear();
        dialog.ServerText = "dc05.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal("dc05.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Equal(PageStatusKind.Working, seen[0].Kind);
        Assert.DoesNotContain(seen, s => s.Kind is PageStatusKind.Done or PageStatusKind.Attention or PageStatusKind.Error);
        Assert.Null(dialog.Status.Lasting);
    }

    [Fact]
    public async Task WhatTheDialogExplainsItself_HasNoCode_AndAnAliasOfTheCurrentOneIsAnAttention()
    {
        var harness = await HarnessAsync();
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server, i => i with
        {
            DefaultNamingContext = "DC=oddział,DC=contoso,DC=test",
            DomainDnsName = "oddział.contoso.test",
        }));
        using var dialog = await OpenAsync(harness);

        // A domain controller of another domain.
        dialog.ServerText = "dc01.oddział.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);
        var other = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, other.Kind);
        Assert.Equal(
            "Could not move the session to dc01.oddział.contoso.test: dc01.oddział.contoso.test is a domain controller of oddział.contoso.test, "
            + "not of contoso.test. The session stays connected to dc01.contoso.test.",
            other.Message);
        Assert.Equal("ChangeDc.Status.Failed", other.Log.ToString());

        // An alias of the current one: nothing was changed, and that is all there is to say.
        harness.ConnectToServer = (_, _, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, "dc01.contoso.test"));
        dialog.ServerText = "10.0.0.11";
        await dialog.ChangeCommand.ExecuteAsync(null);
        var same = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, same.Kind);
        Assert.Equal("The domain controller was not changed: Already connected to dc01.contoso.test.", same.Message);
        Assert.Equal("ChangeDc.AlreadyConnected", same.Log.ToString());
        Assert.True(same.CanClose);
    }

    [Fact]
    public async Task TheListIsReadWhileTheOperatorSwitches_EachSaysItsOwn()
    {
        var harness = await HarnessAsync();
        var locator = new HeldLocator([new DomainControllerInfo("dc02.contoso.test", "Kraków", true, false, true)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ConnectToServer = async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "The server is not operational.");
        };
        using var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, locator);
        Task loading;
        Task switching;
        try
        {
            loading = dialog.InitializeAsync();
            await locator.Entered.WaitAsync(Limit, Ct);
            Assert.Equal("Reading the domain controllers of contoso.test…", dialog.Status.Current.Message);

            // A name is typed before the list arrived: the switch takes the line, the read goes on.
            dialog.ServerText = "dc02.contoso.test";
            switching = dialog.ChangeCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(Limit, Ct);
            Assert.True(dialog.Status.IsWorking);
            Assert.True(dialog.ListStatus.IsWorking);
            Assert.Equal("Connecting to dc02.contoso.test…", dialog.Status.Current.Message);
            Assert.Same(dialog.Status.CancelCommand, Assert.IsType<PageStatusAction>(dialog.Status.Current.Action).Command);

            // The list arrives meanwhile: it does not take the switch off the line.
            locator.Release();
            await loading.WaitAsync(Limit, Ct);
            Assert.False(dialog.ListStatus.IsWorking);
            Assert.Equal("3 domain controllers", dialog.ListStatus.Current.Message);
            Assert.Equal("Connecting to dc02.contoso.test…", dialog.Status.Current.Message);
        }
        finally
        {
            locator.Release();
            release.TrySetResult();
        }

        await switching.WaitAsync(Limit, Ct);
        Assert.Equal(PageStatusKind.Error, dialog.Status.Current.Kind);
        Assert.StartsWith("Could not move the session to dc02.contoso.test: ", dialog.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowWithoutASession_SaysSoAsItsState()
    {
        var harness = await HarnessAsync();
        await harness.Sessions.DisconnectAsync();
        using var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, connector: null);

        // Before anything was read, and after.
        Assert.Equal(PageStatusKind.Error, dialog.Status.Current.Kind);
        await dialog.InitializeAsync();

        var state = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal("Not connected to a domain.", state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.Same(dialog.Status.Lasting, state);
        Assert.Null(state.Action);
        Assert.False(state.CanClose);
        Assert.False(dialog.ListStatus.IsWorking);
        Assert.Null(dialog.ListStatus.Lasting);
        Assert.Equal("Not connected to a domain.", dialog.ErrorText);
    }

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindow_AlsoDuringAReadAndASwitch()
    {
        // The session ends while the list is read: the read ends without a word, the window says that it ended.
        var harness = await HarnessAsync();
        var locator = new HeldLocator([]);
        using (var reading = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, locator))
        {
            var seen = Record(reading.Status);
            var loading = reading.InitializeAsync();
            await locator.Entered.WaitAsync(Limit, Ct);
            await harness.Sessions.DisconnectAsync();

            var ended = reading.Status.Current;
            Assert.Equal(PageStatusKind.Error, ended.Kind);
            Assert.Equal(harness.Localizer[L.WindowSession.Ended], ended.Message);
            Assert.Equal("WindowSession.Ended", ended.Log.ToString());
            Assert.False(ended.CanClose);
            Assert.False(reading.ListStatus.IsWorking);
            locator.Release();
            await loading.WaitAsync(Limit, Ct);
            Assert.Same(reading.Status.Lasting, reading.Status.Current);
            Assert.Null(reading.ListStatus.Lasting);
            Assert.DoesNotContain(seen, s => s.Kind == PageStatusKind.Attention);

            // The state follows the language.
            harness.Localizer.SetCulture(Polish);
            Assert.Equal(harness.Localizer[L.WindowSession.Ended], reading.Status.Current.Message);
            Assert.StartsWith("Sesja, w której otwarto to okno", reading.Status.Current.Message, StringComparison.Ordinal);
            harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        }

        // The session ends while the dialog switches (a disconnect elsewhere): no "the session stays connected".
        harness = await HarnessAsync();
        var other = harness;
        harness.ConnectToServer = async (_, server, _) =>
        {
            await other.Sessions.DisconnectAsync();
            return new FakeDomainControllerSession(other.Session, server);
        };
        using var dialog = await OpenAsync(harness);
        dialog.ServerText = "dc02.contoso.test";
        var said = Record(dialog.Status);

        await dialog.ChangeCommand.ExecuteAsync(null);

        var state = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], state.Message);
        Assert.Same(dialog.Status.Lasting, state);
        Assert.False(state.CanClose);
        Assert.DoesNotContain(said, s => s.Message.Contains("stays connected", StringComparison.Ordinal));
        Assert.DoesNotContain(said, s => s.Message.StartsWith("Could not move", StringComparison.Ordinal));
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], dialog.ErrorText);
    }

    [Fact]
    public async Task ClosingTheDialog_EndsWhatItSays()
    {
        var harness = await HarnessAsync();
        var locator = new HeldLocator([]);
        var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, locator);
        var loading = dialog.InitializeAsync();
        await locator.Entered.WaitAsync(Limit, Ct);
        Assert.True(dialog.ListStatus.IsWorking);

        dialog.Dispose();

        Assert.False(dialog.ListStatus.IsWorking);
        Assert.False(dialog.Status.IsWorking);
        Assert.Null(dialog.Status.Front);
        locator.Release();
        await loading.WaitAsync(Limit, Ct);
        Assert.Null(dialog.ListStatus.Lasting);
        Assert.Equal(PageStatusKind.Idle, dialog.Status.Current.Kind);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverTheNameOfADomainControllerOrAServerText()
    {
        var harness = await HarnessAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var dialog = new ChangeDomainControllerViewModel(services, harness.AuditLog, connector: null);
        await dialog.InitializeAsync();
        harness.ConnectToServer = (_, _, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc02.contoso.test does not answer") { LdapResultCode = 81 };
        dialog.ServerText = "dc02.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Contains("dc02.contoso.test", dialog.Status.Current.Message, StringComparison.Ordinal);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("ChangeDc.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Connect.Connecting", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ChangeDc.Status.Failed", StringComparison.Ordinal) && m.Contains("LDAP 81", StringComparison.Ordinal));
        Assert.DoesNotContain("dc0", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not answer", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Connected to dc01 (Warszawa, PDC); the directory knows dc02 (Kraków) and an RODC in Gdańsk.</summary>
    private static async Task<B2Harness> HarnessAsync(CultureInfo? culture = null)
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync("dc01.contoso.test", culture);
        DomainControllerSeed.Add(harness.Session, "dc01.contoso.test", "Warszawa", pdc: true);
        DomainControllerSeed.Add(harness.Session, "dc02.contoso.test", "Kraków", globalCatalog: false);
        DomainControllerSeed.Add(harness.Session, "rodc-gdańsk.contoso.test", "Gdańsk", readOnly: true);
        return harness;
    }

    private static async Task<ChangeDomainControllerViewModel> OpenAsync(B2Harness harness)
    {
        var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, connector: null);
        await dialog.InitializeAsync();
        return dialog;
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

    /// <summary>
    /// A DC locator that answers only after <see cref="Release"/> and - like DsGetDcName - does not notice a cancellation:
    /// what the dialog says while it waits lasts until the test lets go.
    /// </summary>
    private sealed class HeldLocator(IReadOnlyList<DomainControllerInfo> located) : IDirectoryConnector
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed once the dialog asked the locator.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public string? GetComputerDomain() => "contoso.test";

        public async Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return located;
        }

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
