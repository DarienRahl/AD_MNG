using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of "Reset Password" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): the list
/// of accounts says that their names are read; the window says the reset - checking the accounts, then the execution
/// of the plan, both with Stop - how it ended, with the domain, afterwards the counts of the result list, why nothing
/// was reset, and - as the state that lasts - that it has no session. No password, typed or generated, is ever part of
/// a text or of its log copy. How passwords are reset is covered, unchanged, by <see cref="ResetPasswordTests"/>.
/// </summary>
public sealed class ResetPasswordStatusTests
{
    private const string Typed = "Nowe!Haslo2026";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AtRest_TheLineSaysNothing_AndReadingTheAccountsIsTheOperationOfTheList()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        Assert.IsAssignableFrom<IHasPageStatus>(dialog);
        Assert.Same(dialog.TargetsStatus, dialog.Status.Front);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
        Assert.Null(dialog.Status.Lasting);

        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var seen = Record(dialog.Status);
        Task reading;
        try
        {
            reading = dialog.InitializeAsync();
            Assert.True(await held.WaitAsync(Limit, Ct));

            // Working - the operation is the list's, and so is its Stop; the window is not working, and OK can be pressed.
            Assert.True(dialog.TargetsStatus.IsWorking);
            Assert.False(dialog.Status.IsWorking);
            Assert.Same(dialog.TargetsStatus.Current, dialog.Status.Current);
            Assert.Equal(PageStatusKind.Working, dialog.Status.Current.Kind);
            Assert.Equal("Reading the accounts (1)…", dialog.Status.Current.Message);
            Assert.Equal("ResetPassword.Status.Reading (1)", dialog.Status.Current.Log.ToString());
            Assert.Same(dialog.TargetsStatus.CancelCommand, Assert.IsType<PageStatusAction>(dialog.Status.Current.Action).Command);
            Assert.False(dialog.IsBusy);
            Assert.True(dialog.ResetCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
        }

        await reading.WaitAsync(Limit, Ct);
        harness.Session.BeforeOperation = null;

        // The result is the list itself: nothing to say, nothing to close.
        Assert.Equal([(PageStatusKind.Working, "Reading the accounts (1)…"), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
        Assert.Equal("Żaneta Szymańska", Assert.Single(dialog.Targets).Name);
        Assert.True(dialog.IsAnyLocked);
        Assert.Null(dialog.Error);
    }

    [Fact]
    public async Task AReadOfTheAccounts_ThatWasStoppedOrFailed_IsAResultWithRetry()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        var fail = 0;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind != FakeOperationKind.Search)
            {
                return;
            }

            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }

            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var seen = Record(dialog.Status);
        Task reading;
        try
        {
            reading = dialog.InitializeAsync();
            Assert.True(await held.WaitAsync(Limit, Ct));

            // Stop: "Stopping…" at once, for as long as the read is on its way.
            dialog.TargetsStatus.CancelCommand.Execute(null);
            Assert.True(dialog.TargetsStatus.IsCancelling);
            Assert.Equal(PageStatusKind.Working, dialog.Status.Current.Kind);
            Assert.Equal("Stopping…", dialog.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await reading.WaitAsync(Limit, Ct);

        // Stopped: what may be missing, and Retry. A result - the list is as it was, the passwords can be reset.
        const string StoppedText = "Reading the accounts was stopped: names and the lockout state may be missing. Press Retry to read them again.";
        Assert.Equal(
            [(PageStatusKind.Working, "Reading the accounts (2)…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        var stopped = dialog.Status.Current;
        Assert.Equal("ResetPassword.Status.ReadStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Null(dialog.TargetsStatus.Lasting);
        Assert.Equal(2, dialog.Targets.Count);
        Assert.True(dialog.ResetCommand.CanExecute(null));
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry", retry.Label);

        // Retry fails: what was tried, why, what to do, the code - without the words of the server.
        Volatile.Write(ref fail, 1);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        var failed = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"Could not read the accounts: {dialog.Error} Press Retry to try again. (ServerUnavailable)", failed.Message);
        Assert.Equal("ResetPassword.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.DoesNotContain("dc01", failed.Message, StringComparison.Ordinal);
        Assert.True(failed.CanClose);
        var again = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", again.Label);

        // Retry reads them: the line is empty again.
        Volatile.Write(ref fail, 0);
        await ((IAsyncRelayCommand)again.Command).ExecuteAsync(again.Parameter);
        harness.Session.BeforeOperation = null;
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
        Assert.Contains(dialog.Targets, t => t.Name == "Jan Kowalski");
    }

    [Theory]
    [InlineData("en-US", "No password was reset: The passwords do not match.", "No password was reset: Enter the new password.")]
    [InlineData("pl-PL", "Nie zresetowano żadnego hasła: Hasła nie są zgodne.", "Nie zresetowano żadnego hasła: Wprowadź nowe hasło.")]
    public async Task WhatWasNotStarted_IsSaidAsNoPasswordWasReset_WithTheReason(string culture, string mismatch, string missing)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        await dialog.InitializeAsync();
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput("Inne!Haslo2026");
        var seen = Record(dialog.Status);

        await dialog.ResetCommand.ExecuteAsync(null);

        // An attention the operator can close - never "working" in between, and never a password.
        var said = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, said.Kind);
        Assert.Equal(mismatch, said.Message);
        Assert.Equal("Batch.PasswordMismatch", said.Log.ToString());
        Assert.Null(said.Action);
        Assert.True(said.CanClose);
        Assert.Equal([(PageStatusKind.Attention, mismatch)], seen);
        Assert.Empty(harness.Session.PasswordResets);
        Assert.False(dialog.IsBusy);
        Assert.False(dialog.IsCompleted);

        dialog.PasswordInput = new TestPasswordInput(string.Empty);
        await dialog.ResetCommand.ExecuteAsync(null);
        Assert.Equal(missing, dialog.Status.Current.Message);
        Assert.Equal("Batch.PasswordRequired", dialog.Status.Current.Log.ToString());
        Assert.All(seen, s => Assert.DoesNotContain("Haslo", s.Message, StringComparison.Ordinal));

        // Closed: the line is empty again.
        dialog.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
    }

    [Theory]
    [InlineData(
        "en-US",
        "Checking the accounts (1)…",
        "Executing “Reset Password” in contoso.test: 0 of 1…",
        "“Reset Password” was executed in contoso.test: 1 object changed. The report is in the list of background operations.",
        "Succeeded: 1, failed: 0, skipped: 0")]
    [InlineData(
        "pl-PL",
        "Sprawdzanie kont (1)…",
        "Wykonywanie „Resetuj hasło” w domenie contoso.test: 0 z 1…",
        "Wykonano „Resetuj hasło” w domenie contoso.test: zmieniono 1 obiekt. Raport jest na liście operacji w tle.",
        "Powodzenie: 1, niepowodzenie: 0, pominięte: 0")]
    public async Task TheReset_IsCheckingThenExecuting_AndSaysHowItEndedWithTheDomain(string culture, string checking, string executing, string done, string summary)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        await dialog.InitializeAsync();
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        var seen = Record(dialog.Status);

        await dialog.ResetCommand.ExecuteAsync(null);

        // Working from the press to the end - the execution takes the line from the check without a pause - then done.
        List<(PageStatusKind Kind, string Message)> said;
        lock (seen)
        {
            said = [.. seen];
        }

        Assert.Equal((PageStatusKind.Working, checking), said[0]);
        Assert.Equal((PageStatusKind.Working, executing), said[1]);
        Assert.Equal((PageStatusKind.Done, done), said[^1]);
        Assert.All(said.Take(said.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        var result = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Done, result.Kind);
        Assert.Equal("Changes.Status.Executed (1)", result.Log.ToString());
        Assert.True(result.CanClose);
        Assert.True(dialog.IsCompleted);
        Assert.False(dialog.IsBusy);
        Assert.False(dialog.Status.IsWorking);
        Assert.Equal(Typed, Assert.Single(harness.Session.PasswordResets).Password);

        // The typed password is nowhere in what the window said.
        Assert.All(said, s => Assert.DoesNotContain(Typed, s.Message, StringComparison.Ordinal));

        // Behind the result: the counts of the result list - the summary of the window from now on.
        dialog.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, dialog.Status.Current.Kind);
        Assert.Equal(summary, dialog.Status.Current.Message);
        Assert.Equal(dialog.ResultSummary, dialog.Status.Current.Message);
    }

    [Fact]
    public async Task ManyAccounts_TheLineIsAtRestWhileTheConfirmationIsAsked_AndAGeneratedPasswordIsNeverSaid()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        await dialog.InitializeAsync();
        harness.Session.FailNext(FakeOperationKind.ResetPassword, harness.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        var seen = Record(dialog.Status);

        // Declined: checked, then nothing more - no password was touched.
        harness.Dialogs.TypedConfirmAnswers.Enqueue(false);
        await dialog.ResetCommand.ExecuteAsync(null);
        Assert.Equal([(PageStatusKind.Working, "Checking the accounts (2)…"), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Empty(harness.Session.PasswordResets);
        Assert.False(dialog.IsBusy);

        // Confirmed: one of the two fails - what was changed, how many failed, where the reasons are.
        lock (seen)
        {
            seen.Clear();
        }

        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await dialog.ResetCommand.ExecuteAsync(null);

        List<(PageStatusKind Kind, string Message)> said;
        lock (seen)
        {
            said = [.. seen];
        }

        Assert.Equal((PageStatusKind.Working, "Checking the accounts (2)…"), said[0]);
        Assert.Equal((PageStatusKind.Idle, string.Empty), said[1]);
        Assert.Equal((PageStatusKind.Working, "Executing “Reset Password” in contoso.test: 0 of 2…"), said[2]);
        var result = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.Equal(
            "“Reset Password” was executed only in part in contoso.test: 1 object changed, failed: 1. The reasons are in the list of results.",
            result.Message);
        Assert.Equal("Changes.Status.ExecutedPartly (1, 1)", result.Log.ToString());
        Assert.Null(result.Action);
        Assert.True(dialog.IsCompleted);
        Assert.Equal(2, dialog.Results.Count);

        // The generated password is in its row, on request - and in nothing the line said.
        var generated = Assert.Single(harness.Session.PasswordResets).Password;
        var row = dialog.Results.Single(r => r.HasPassword);
        row.IsRevealed = true;
        Assert.Equal(generated, row.DisplayedPassword);
        Assert.All(said, s => Assert.DoesNotContain(generated, s.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(generated, dialog.Status.Idle.Message, StringComparison.Ordinal);
        Assert.Equal("Succeeded: 1, failed: 1, skipped: 0", dialog.Status.Idle.Message);
    }

    [Fact]
    public async Task AResetOfWhichNothingSucceeded_IsAnError()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        await dialog.InitializeAsync();
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        harness.Session.FailNext(FakeOperationKind.ResetPassword, harness.Company.ZanetaSzymanska, DirectoryErrorKind.AccessDenied);

        await dialog.ResetCommand.ExecuteAsync(null);

        var result = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, result.Kind);
        Assert.Equal(
            "Could not execute “Reset Password” in contoso.test: nothing was changed, failed: 1. The reasons are in the list of results.",
            result.Message);
        Assert.Equal("Changes.Status.ExecuteFailed (0, 1)", result.Log.ToString());
        Assert.True(dialog.IsCompleted);
        Assert.False(Assert.Single(dialog.Results).Succeeded);
        Assert.DoesNotContain(Typed, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountThatCannotBeReset_IsSaidWithTheReason_AndNothingIsExecuted()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([$"CN=Nie Ma Takiego,{harness.Company.ItOu}"]);
        await dialog.InitializeAsync();
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        var seen = Record(dialog.Status);

        await dialog.ResetCommand.ExecuteAsync(null);

        // Checked, and no account can be reset: why - one flowing text; the window stays as it was.
        var said = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, said.Kind);
        Assert.StartsWith("No password was reset: ", said.Message, StringComparison.Ordinal);
        Assert.True(said.Message.Length > "No password was reset: ".Length, "the reason is missing");
        Assert.DoesNotContain('\n', said.Message);
        Assert.Equal("ResetPassword.NothingToReset", said.Log.ToString());
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Attention], seen.Select(s => s.Kind));
        Assert.StartsWith(harness.Localizer[L.ResetPassword.NothingToReset], dialog.Error, StringComparison.Ordinal);
        Assert.False(dialog.IsCompleted);
        Assert.Empty(harness.Session.PasswordResets);
        Assert.Empty(harness.Activities.Activities);
    }

    [Fact]
    public async Task StopWhileTheAccountsAreChecked_ReturnsControl_AndNoPasswordIsReset()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        await dialog.InitializeAsync();
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);

        // The first read of the planner waits for the test.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = _ =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var seen = Record(dialog.Status);
        Task resetting;
        try
        {
            resetting = dialog.ResetCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(Limit, Ct));

            // Working - the window's own operation, with its Stop.
            Assert.True(dialog.IsBusy);
            Assert.True(dialog.Status.IsWorking);
            Assert.Equal("Checking the accounts (1)…", dialog.Status.Current.Message);
            Assert.Equal("ResetPassword.Status.Checking (1)", dialog.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(dialog.Status.Current.Action);
            Assert.Same(dialog.Status.CancelCommand, stop.Command);

            stop.Command.Execute(stop.Parameter);
            Assert.True(dialog.Status.IsCancelling);
            Assert.Equal("Stopping…", dialog.Status.Current.Message);
            Assert.True(dialog.IsBusy);
        }
        finally
        {
            release.Set();
        }

        await resetting.WaitAsync(Limit, Ct);
        harness.Session.BeforeOperation = null;

        // Whether the planner noticed the Stop or finished all the same: nothing was executed.
        const string StoppedText = "Checking the accounts was stopped: no password was reset. Press Retry to try again.";
        Assert.Equal(
            [(PageStatusKind.Working, "Checking the accounts (1)…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        var stopped = dialog.Status.Current;
        Assert.Equal("ResetPassword.Status.CheckStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Empty(harness.Session.PasswordResets);
        Assert.Empty(harness.Activities.Activities);
        Assert.False(dialog.IsBusy);
        Assert.False(dialog.IsCompleted);

        // Retry resets: the same command, with the passwords that are still in the boxes.
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(dialog.ResetCommand, retry.Command);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Equal(PageStatusKind.Done, dialog.Status.Current.Kind);
        Assert.Single(harness.Session.PasswordResets);
    }

    [Fact]
    public async Task StopWhileThePasswordsAreReset_CancelsTheActivity_AndSaysWhatWasNotExecuted()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, Ct);
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        await dialog.InitializeAsync();
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);

        // The first reset waits for the test.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.ResetPassword && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task resetting;
        try
        {
            resetting = dialog.ResetCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(Limit, Ct));
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities is [{ IsRunning: true }]);
            Assert.True(dialog.IsBusy);
            Assert.True(dialog.Status.IsWorking);
            Assert.Equal("Executing “Reset Password” in contoso.test: 0 of 2…", dialog.Status.Current.Message);
            var stop = Assert.IsType<PageStatusAction>(dialog.Status.Current.Action);
            Assert.Same(dialog.Status.CancelCommand, stop.Command);

            // Stop in the line cancels the activity that resets - and says so at once.
            stop.Command.Execute(stop.Parameter);
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities[0].IsCancelling);
            Assert.True(dialog.Status.IsCancelling);
            Assert.Equal("Stopping…", dialog.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await resetting.WaitAsync(Limit, Ct);
        harness.Session.BeforeOperation = null;

        // The step that was on its way ended (its account is not complete: the steps after it were not made); the other
        // account was not touched. Both are in the result list, and the line says that the rest was not executed - and
        // does not point to reasons the list does not have.
        var result = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.StartsWith("Executing “Reset Password” in contoso.test was stopped: ", result.Message, StringComparison.Ordinal);
        Assert.EndsWith(" the rest was not executed.", result.Message, StringComparison.Ordinal);
        Assert.StartsWith("Changes.Status.Stopped (", result.Log.ToString(), StringComparison.Ordinal);
        Assert.True(result.CanClose);
        Assert.Single(harness.Session.PasswordResets);
        Assert.True(dialog.IsCompleted);
        Assert.Equal(2, dialog.Results.Count);
        Assert.False(dialog.IsBusy);
        Assert.False(dialog.Status.IsWorking);
    }

    [Fact]
    public async Task AWindowWithoutASession_AndTheEndOfTheSession_AreTheStateOfTheWindow()
    {
        // Opened while disconnected.
        var harness = await B2Harness.CreateAsync();
        var dn = harness.Company.ZanetaSzymanska;
        await harness.Sessions.DisconnectAsync();
        using (var disconnected = harness.Factory.CreateResetPassword([dn]))
        {
            Assert.Equal(PageStatusKind.Error, disconnected.Status.Current.Kind);
            await disconnected.InitializeAsync();
            var state = disconnected.Status.Current;
            Assert.Equal("Not connected to a domain.", state.Message);
            Assert.Equal("Errors.NotConnected", state.Log.ToString());
            Assert.Same(disconnected.Status.Lasting, state);
            Assert.False(state.CanClose);
            Assert.False(disconnected.TargetsStatus.IsWorking);
            Assert.Equal("Not connected to a domain.", disconnected.Error);
        }

        // The session ends while the window is open - also while the names are still read.
        harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var seen = Record(dialog.Status);
        Task reading;
        try
        {
            reading = dialog.InitializeAsync();
            Assert.True(await held.WaitAsync(Limit, Ct));
            await harness.Sessions.DisconnectAsync();

            // The read ends without a word; the window says for good that its session ended.
            var ended = dialog.Status.Current;
            Assert.Equal(PageStatusKind.Error, ended.Kind);
            Assert.Equal(harness.Localizer[L.WindowSession.Ended], ended.Message);
            Assert.Equal("WindowSession.Ended", ended.Log.ToString());
            Assert.Same(dialog.Status.Lasting, ended);
            Assert.False(ended.CanClose);
            Assert.False(dialog.TargetsStatus.IsWorking);
            Assert.False(dialog.ResetCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
        }

        await reading.WaitAsync(Limit, Ct);
        Assert.Same(dialog.Status.Lasting, dialog.Status.Current);
        Assert.DoesNotContain(seen, s => s.Kind == PageStatusKind.Attention);

        // The state follows the language.
        harness.Localizer.SetCulture(Polish);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], dialog.Status.Current.Message);
        Assert.StartsWith("Sesja, w której otwarto to okno", dialog.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndNumbers_NeverANameADomainOrAPassword()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var dialog = new ResetPasswordViewModel(
            services, harness.Planner, harness.Engines.Reports, [harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        await dialog.InitializeAsync();
        harness.Session.FailNext(FakeOperationKind.ResetPassword, harness.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await dialog.ResetCommand.ExecuteAsync(null);
        Assert.Contains("contoso.test", dialog.Status.Current.Message, StringComparison.Ordinal);
        var generated = Assert.Single(harness.Session.PasswordResets).Password;

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("ResetPassword.Status.Reading (2)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ResetPassword.Status.Checking (2)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Changes.Status.Executing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Changes.Status.ExecutedPartly (1, 1)", StringComparison.Ordinal));
        Assert.DoesNotContain(generated, logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Wiśniewski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Reset Password", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
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
