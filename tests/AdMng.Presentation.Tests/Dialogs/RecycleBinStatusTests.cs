using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of the AD Recycle Bin (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"): the
/// list of deleted objects says what it reads and how much, that the read was stopped or why it failed; the window
/// says the check and the restore of the selection and how they ended, the summary of the restore preview, and - as
/// the state that lasts - that it has no session. How objects are listed and restored is covered, unchanged, by
/// <see cref="RecycleBinViewModelTests"/>.
/// </summary>
public sealed class RecycleBinStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>A domain controller that does not answer within a test.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromMinutes(5);

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLine_ShowsWhatTheListSays_TheSummaryAndTheSelection_InBothLanguages()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        Assert.IsAssignableFrom<IHasPageStatus>(bin);
        Assert.Same(bin.ListStatus, bin.Status.Front);

        await bin.InitializeAsync();

        // At rest: what was read and when - said by the list, shown by the window.
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.Same(bin.ListStatus.Current, bin.Status.Current);
        Assert.StartsWith("4 objects · ", bin.StatusText, StringComparison.Ordinal);
        Assert.Equal(bin.StatusText, bin.Status.Current.Message);
        Assert.False(bin.Status.IsWorking);
        Assert.False(bin.ListStatus.IsWorking);
        Assert.Null(bin.Status.Lasting);
        Assert.Null(bin.ListStatus.Lasting);
        Assert.False(bin.Status.CloseCommand.CanExecute(null));

        // The selection joins the summary.
        bin.SelectedItems = bin.Items.Take(2).ToList();
        Assert.Equal("2 selected", bin.SelectionText);
        Assert.Equal($"{bin.StatusText} · 2 selected", bin.Status.Current.Message);

        // Both follow the language.
        harness.Localizer.SetCulture(Polish);
        Assert.Equal("Usunięte obiekty: 4", bin.StatusText);
        Assert.Equal("Usunięte obiekty: 4 · Zaznaczono: 2", bin.Status.Current.Message);
    }

    [Fact]
    public async Task ReadingTheList_IsTheOperationOfTheList_AndStopReturnsControl()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        harness.Session.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            var loading = bin.InitializeAsync();

            // Working - the operation is the list's, and so is its Stop; the window itself is not working.
            Assert.True(bin.IsLoading);
            Assert.True(bin.ListStatus.IsWorking);
            Assert.False(bin.Status.IsWorking);
            Assert.Same(bin.ListStatus.Current, bin.Status.Current);
            Assert.Equal(PageStatusKind.Working, bin.Status.Current.Kind);
            Assert.Equal("Reading the deleted objects of contoso.test…", bin.Status.Current.Message);
            Assert.Equal("RecycleBin.Status.Reading", bin.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(bin.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(bin.ListStatus.CancelCommand, stop.Command);

            // Pressed: "Stopping…" at once, and the read ends well before the domain controller would have answered.
            seen = Record(bin.Status);
            stop.Command.Execute(stop.Parameter);
            Assert.True(bin.ListStatus.IsCancelling);
            await loading.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        // Stopped: what is there, and Refresh as the way to the whole list. A result - it can be closed.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = bin.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Reading the deleted objects was stopped before all results arrived: 0 objects loaded. Press Refresh to read the whole list.",
            stopped.Message);
        Assert.Equal("RecycleBin.Status.Stopped (0)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(bin.IsLoading);
        Assert.False(bin.ListStatus.IsWorking);
        Assert.Null(bin.Error);
        Assert.Null(bin.ListStatus.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(bin.RefreshCommand, refresh.Command);

        // Refresh reads the whole list: the summary, and the "stopped" is gone with the read.
        await bin.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.Equal(bin.StatusText, bin.Status.Current.Message);
        Assert.Equal("Jan Kowalski", Assert.Single(bin.Items).Name);
    }

    [Theory]
    [InlineData("en-US", "Reading the deleted objects of contoso.test: 8 objects so far…", "Reading the deleted objects was stopped before all results arrived: 8 objects loaded. Press Refresh to read the whole list.")]
    [InlineData("pl-PL", "Wczytywanie usuniętych obiektów domeny contoso.test: na razie 8 obiektów…", "Wczytywanie usuniętych obiektów zatrzymano, zanim nadeszły wszystkie wyniki: wczytano 8 obiektów. Wybierz Odśwież, aby wczytać całą listę.")]
    public async Task TheRead_SaysHowManyObjectsItHasSoFar_AndAStoppedReadSaysWhatIsThere(string culture, string working, string stoppedText)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        await harness.Session.DeleteAsync(harness.Company.EmployeesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();

        // Users only: their deleted containers are not listed and are read one by one after the list - the first of
        // those reads waits for the test.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (hold && operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.Base, Search.IncludeDeleted: true })
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        List<(PageStatusKind Kind, string Message)> seen;
        Task loading;
        try
        {
            bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.User);
            loading = bin.Loading;
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));

            // The list is there, the read goes on: one message, with the number of objects so far.
            Assert.Equal(8, bin.Items.Count);
            Assert.True(bin.IsLoading);
            Assert.Equal(PageStatusKind.Working, bin.Status.Current.Kind);
            Assert.Equal(working, bin.Status.Current.Message);
            Assert.Equal("RecycleBin.Status.Reading (8)", bin.Status.Current.Log.ToString());
            Assert.True(bin.Status.Current.Progress?.IsIndeterminate);

            seen = Record(bin.Status);
            bin.ListStatus.CancelCommand.Execute(null);
            Assert.Equal(PageStatusKind.Working, bin.Status.Current.Kind);
            Assert.Equal(harness.Localizer[L.Feedback.Stopping], bin.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await loading.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        Assert.Equal(PageStatusKind.Working, seen[0].Kind);
        var stopped = bin.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(stoppedText, stopped.Message);
        Assert.Equal("RecycleBin.Status.Stopped (8)", stopped.Log.ToString());
        Assert.Equal(8, bin.Items.Count);

        // Closed: the summary of the incomplete list.
        bin.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.Equal(bin.StatusText, bin.Status.Current.Message);
        Assert.Contains("8", bin.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewerRead_ReplacesTheRunningOne_WithoutAMessage()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (hold && operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel })
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task first;
        Task second;
        var seen = Record(bin.Status);
        try
        {
            first = bin.RefreshCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.True(bin.ListStatus.IsWorking);

            // Another filter while the list is read: the newer read takes over, the older one says nothing.
            bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.User);
            second = bin.Loading;
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        Assert.DoesNotContain(seen, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.Equal(bin.StatusText, bin.Status.Current.Message);
        Assert.Equal("Jan Kowalski", Assert.Single(bin.Items).Name);
    }

    [Fact]
    public async Task AReadThatFails_IsTheStateOfTheList_WithRetry_UntilItIsReadAgain()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel })
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };

        await bin.InitializeAsync();

        // The list is empty and the line says why - for as long as that is so; it cannot be closed.
        var failed = bin.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Same(bin.ListStatus.Lasting, failed);
        Assert.NotNull(bin.Error);
        Assert.Equal($"Could not read the deleted objects of contoso.test: {bin.Error} Press Retry to try again. (ServerUnavailable)", failed.Message);
        Assert.Equal("RecycleBin.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.DoesNotContain("dc01", failed.Log.ToString(), StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.False(bin.Status.CloseCommand.CanExecute(null));
        Assert.False(bin.IsEmpty);
        Assert.Empty(bin.Items);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(bin.RefreshCommand, retry.Command);

        // Retry reads the list: the state is over.
        fail = false;
        await bin.RefreshCommand.ExecuteAsync(null);
        Assert.Null(bin.ListStatus.Lasting);
        Assert.Null(bin.Error);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.Equal(bin.StatusText, bin.Status.Current.Message);
        Assert.Single(bin.Items);
        harness.Session.BeforeOperation = null;
    }

    [Fact]
    public async Task NoRightToListTheDeletedObjects_IsSaidWithWhatIsNeeded()
    {
        var harness = await B2Harness.CreateAsync();
        using var bin = harness.Factory.CreateRecycleBin();
        harness.Session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel })
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS)");
            }
        };

        await bin.InitializeAsync();

        var failed = bin.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not read the deleted objects of contoso.test: The deleted objects cannot be read. Listing CN=Deleted Objects requires", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (AccessDenied)", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("RecycleBin.Status.ReadFailed [AccessDenied]", failed.Log.ToString());
        harness.Session.BeforeOperation = null;
    }

    [Fact]
    public async Task AWindowWithoutASession_SaysSoAsItsState()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Sessions.DisconnectAsync();
        using var bin = harness.Factory.CreateRecycleBin();

        // Before anything was read, and after.
        Assert.Equal(PageStatusKind.Error, bin.Status.Current.Kind);
        await bin.InitializeAsync();

        var state = bin.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal("Not connected to a domain.", state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.Same(bin.Status.Lasting, state);
        Assert.Null(state.Action);
        Assert.False(state.CanClose);
        Assert.Null(bin.ListStatus.Lasting);
        Assert.False(bin.ListStatus.IsWorking);
        Assert.Equal("Not connected to a domain.", bin.Error);
    }

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindow_InTheLanguageOfTheMoment()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = Assert.Single(bin.Items);
        await bin.RestoreCommand.ExecuteAsync(null);
        Assert.True(bin.IsPreviewing);

        await harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: harness.Time).Session);
        await bin.Loading;

        // An error that cannot be closed: the list of the ended session is gone, nothing can be restored.
        Assert.True(bin.IsSessionEnded);
        Assert.False(bin.IsPreviewing);
        var ended = bin.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(bin.SessionEndedMessage, ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.Same(bin.Status.Lasting, ended);
        Assert.False(ended.CanClose);
        Assert.Null(ended.Action);
        Assert.False(bin.Status.IsWorking);
        Assert.False(bin.ListStatus.IsWorking);
        Assert.Null(bin.ListStatus.Lasting);
        Assert.Empty(bin.Items);
        Assert.False(bin.RefreshCommand.CanExecute(null));

        harness.Localizer.SetCulture(Polish);
        Assert.Equal(bin.SessionEndedMessage, bin.Status.Current.Message);
        Assert.StartsWith("Sesja, w której otwarto to okno", bin.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingARestore_IsTheOperationOfTheWindow_AndStopEndsItAsAttentionWithRetry()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = Assert.Single(bin.Items);
        var listed = bin.Status.Current.Message;
        harness.Session.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            var checking = bin.RestoreCommand.ExecuteAsync(null);

            // The window's own operation takes the line while it runs; the list keeps its summary behind it.
            Assert.True(bin.IsPlanning);
            Assert.True(bin.Status.IsWorking);
            Assert.False(bin.ListStatus.IsWorking);
            Assert.Equal(PageStatusKind.Working, bin.Status.Current.Kind);
            Assert.Equal("Checking the objects to restore (1)…", bin.Status.Current.Message);
            Assert.Equal("RecycleBin.Status.Checking (1)", bin.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(bin.Status.Current.Action);
            Assert.Same(bin.Status.CancelCommand, stop.Command);

            seen = Record(bin.Status);
            stop.Command.Execute(stop.Parameter);
            await checking.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        // Stopped: what was stopped, that nothing was restored, and Retry - a result of the window.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = bin.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Checking the objects to restore was stopped: nothing was restored. Press Retry to try again.", stopped.Message);
        Assert.Equal("RecycleBin.Status.CheckStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(bin.IsPlanning);
        Assert.False(bin.IsBusy);
        Assert.False(bin.IsPreviewing);
        Assert.Empty(harness.Dialogs.Errors);
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry", retry.Label);

        // Retry checks the same restore again: the preview, and its summary in the line.
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(retry.Command).ExecuteAsync(retry.Parameter);
        Assert.True(bin.IsPreviewing);
        Assert.Null(bin.Status.Front);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.Equal("1 of 1 objects will be restored.", bin.Status.Current.Message);

        // Back: the list says its summary again.
        bin.BackCommand.Execute(null);
        Assert.Same(bin.ListStatus, bin.Status.Front);
        Assert.Equal(listed, bin.Status.Current.Message);
    }

    [Fact]
    public async Task TheListIsReadWhileARestoreIsChecked_BothKeepWhatTheySay()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Jan Kowalski");

        // The check waits at its first read of the object.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            var readsOneObject = operation.Kind is FakeOperationKind.Get or FakeOperationKind.GetAllValues
                || operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.Base };
            if (hold && readsOneObject)
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task checking;
        try
        {
            checking = bin.RestoreCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.True(bin.Status.IsWorking);

            // Another filter meanwhile: the list is read (its own operation), the line stays the window's.
            bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.Computer);
            await bin.Loading.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.Equal(["KRK-LT001"], bin.Items.Select(r => r.Name));
            Assert.True(bin.Status.IsWorking);
            Assert.Equal("Checking the objects to restore (1)…", bin.Status.Current.Message);
            Assert.False(bin.ListStatus.IsWorking);
        }
        finally
        {
            release.Set();
        }

        await checking.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        // The check was not disturbed by the read: its preview is shown.
        Assert.True(bin.IsPreviewing);
        Assert.Equal("Jan Kowalski", Assert.Single(bin.PreviewItems).Name);
        Assert.Equal(bin.PreviewSummary, bin.Status.Current.Message);
    }

    [Fact]
    public async Task ACheckThatFails_IsAnErrorInTheLine_WithRetry_AndNoDialog()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = Assert.Single(bin.Items);
        var fail = true;
        harness.Session.BeforeOperation = _ =>
        {
            if (fail)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };

        await bin.RestoreCommand.ExecuteAsync(null);

        var failed = bin.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not check the objects to restore: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (ServerUnavailable)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("RecycleBin.Status.CheckFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.False(bin.IsPreviewing);
        Assert.False(bin.IsBusy);
        Assert.Empty(harness.Dialogs.Errors);

        // The operator reads the list anew: the result of the last check goes with that.
        fail = false;
        harness.Session.BeforeOperation = null;
        await bin.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.StartsWith(bin.StatusText, bin.Status.Current.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Executing “Restore from the AD Recycle Bin (1)” in contoso.test: 0 of 1…", "“Restore from the AD Recycle Bin (1)” was executed in contoso.test: 1 object changed. The report is in the list of background operations.")]
    [InlineData("pl-PL", "Wykonywanie „Przywracanie z Kosza AD (1)” w domenie contoso.test: 0 z 1…", "Wykonano „Przywracanie z Kosza AD (1)” w domenie contoso.test: zmieniono 1 obiekt. Raport jest na liście operacji w tle.")]
    public async Task ARestore_IsTheOperationOfTheWindow_AndTheResultNamesTheDomain(string culture, string working, string done)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Jan Kowalski");
        await bin.RestoreCommand.ExecuteAsync(null);
        var seen = Record(bin.Status);

        await bin.ConfirmRestoreCommand.ExecuteAsync(null);

        // Working from the first moment and until the list was read again - the read after the restore belongs to it,
        // so nothing else is said in between - then what happened and where.
        Assert.Equal((PageStatusKind.Working, working), seen[0]);
        Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        var result = bin.Status.Current;
        Assert.Equal(PageStatusKind.Done, result.Kind);
        Assert.Equal(done, result.Message);
        Assert.Equal("Changes.Status.Executed (1)", result.Log.ToString());
        Assert.Equal(seen[^1], (result.Kind, result.Message));
        Assert.True(result.CanClose);
        Assert.False(bin.IsPreviewing);
        Assert.False(bin.IsBusy);
        Assert.Same(bin.ListStatus, bin.Status.Front);
        Assert.True(harness.Session.Exists(harness.Company.JanKowalski));
        Assert.Equal(["KRK-LT001"], bin.Items.Select(r => r.Name));
        Assert.Empty(harness.Dialogs.Errors);

        // The main window, for which the restore finished elsewhere, still gets its toast.
        Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success);

        // Closed: the summary of the list that was read after the restore.
        bin.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.StartsWith(bin.StatusText, bin.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoreThatFailsInPart_SaysWhatWasRestored_WhyNotTheRest_AndWhereItIs_WithoutADialog()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        var laptop = bin.Items.Single(r => r.Name == "KRK-LT001");
        bin.SelectedItems = bin.Items.ToList();
        await bin.RestoreCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.RestoreDeleted, laptop.Dn, DirectoryErrorKind.AccessDenied);

        await bin.ConfirmRestoreCommand.ExecuteAsync(null);

        var result = bin.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.StartsWith(
            "“Restore from the AD Recycle Bin (2)” was executed only in part in contoso.test: 1 object changed, failed: 1. ",
            result.Message,
            StringComparison.Ordinal);
        Assert.EndsWith(" What was not restored is still in the list.", result.Message, StringComparison.Ordinal);
        Assert.Contains(harness.Errors.TranslateFailure(DirectoryErrorKind.AccessDenied, null), result.Message, StringComparison.Ordinal);
        Assert.Equal("Changes.Status.ExecutedPartly (1, 1)", result.Log.ToString());
        Assert.True(result.CanClose);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.False(bin.IsPreviewing);
        Assert.Equal(["KRK-LT001"], bin.Items.Select(r => r.Name));

        // Another filter is the operator's next step: the result goes, the list says its own.
        bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.Computer);
        await bin.Loading;
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
        Assert.StartsWith(bin.StatusText, bin.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestore_StoppedInTheLine_SaysStopping_ThenWhatWasNotExecuted()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, Ct);
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItems = bin.Items.ToList();
        await bin.RestoreCommand.ExecuteAsync(null);

        // The first restore waits for the test.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (hold && operation.Kind == FakeOperationKind.RestoreDeleted)
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task restoring;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities is [{ IsRunning: true }]);
            Assert.True(bin.IsBusy);
            Assert.True(bin.Status.IsWorking);
            var stop = Assert.IsType<PageStatusAction>(bin.Status.Current.Action);
            Assert.Same(bin.Status.CancelCommand, stop.Command);

            // Stop in the line cancels the activity that restores - and says so at once.
            stop.Command.Execute(stop.Parameter);
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities[0].IsCancelling);
            Assert.True(bin.Status.IsCancelling);
            Assert.Equal("Stopping…", bin.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        // The object that was on its way was restored (Stop finishes the object in progress); the next one was not started
        // and is still in the list.
        var result = bin.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.Equal(
            "Executing “Restore from the AD Recycle Bin (2)” in contoso.test was stopped: 1 of 2 objects done, failed: 0, the one that ran was finished. "
            + "What was not restored is still in the list.",
            result.Message);
        Assert.Equal("Changes.Status.Stopped (1, 0, 1)", result.Log.ToString());
        Assert.Single(bin.Items);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task TheCloseButton_AndEsc_AskWhatTheTitleBarAsks_WhileARestoreRuns()
    {
        // The footer button "Close" (IsCancel: also Esc) is bound to CloseCommand. It used to raise CloseRequested
        // directly - a close the host does not ask about - so the window closed over the restore without a word, while
        // its title bar asked "Stop the running operation and close the window?".
        var harness = await B2Harness.CreateAsync();
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, Ct);
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        var bin = harness.Factory.CreateRecycleBin();
        var window = new TestWindowHost(bin);
        await bin.InitializeAsync();
        bin.SelectedItems = bin.Items.ToList();
        await bin.RestoreCommand.ExecuteAsync(null);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (hold && operation.Kind == FakeOperationKind.RestoreDeleted)
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var toastedAfterTheClose = new List<bool>();
        harness.Notifications.OnShow = (_, _) => toastedAfterTheClose.Add(window.IsClosed);
        Task restoring;
        Task closing;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities is [{ IsRunning: true }]);
            Assert.True(RunningWork.HasRunningWrites(bin));
            harness.Dialogs.Shown.Clear();

            // Don't close: the window stays and the restore goes on. The restore may go on without the window: three answers.
            harness.Dialogs.CloseAnswers.Enqueue(CloseAnswer.KeepOpen);
            await bin.CloseCommand.ExecuteAsync(null);
            Assert.StartsWith("close:", Assert.Single(harness.Dialogs.Shown), StringComparison.Ordinal);
            Assert.True(Assert.Single(harness.Dialogs.CloseQuestions).CanContinue);
            Assert.False(window.IsClosed);
            Assert.False(bin.Status.IsCancelling);

            // Stop and close: the restore is stopped, and the window closes only after it ended.
            harness.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
            harness.Notifications.Clear();
            closing = bin.CloseCommand.ExecuteAsync(null);
            Assert.True(bin.Status.IsCancelling);
            Assert.False(window.IsClosed);
        }
        finally
        {
            release.Set();
        }

        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await closing.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;
        Assert.True(window.IsClosed);
        Assert.True(window.Result);
        Assert.False(RunningWork.HasRunningWrites(bin));

        // The line went with the window: the connection says how the restore ended - once, and only once the window had closed
        // (a dialog of it is never owned by the window that closes); the window's own toast is left out.
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Warning, toast.Severity);
        Assert.Equal(
            "Executing “Restore from the AD Recycle Bin (2)” in contoso.test was stopped: 1 of 2 objects done, failed: 0, the one that ran was finished.",
            toast.Message);
        Assert.Equal([true], toastedAfterTheClose);
    }

    [Fact]
    public async Task ARestoreThatCameBackUnderTheQuestion_StopAndClose_ClosesAfterTheReRead_AndTheConnectionSaysWhatFailed()
    {
        // The restore ends - with failures - while the operator reads the question, and the window starts reading the list
        // again. "Stop and close" can stop nothing any more, but the operator asked to close: the window closes once its code
        // ended, and the failures - which its line said only for a moment - are said by the connection after the window closed.
        var (harness, bin, window, sales) = await FailingRestoreAsync();
        using var heldRestore = new SemaphoreSlim(0);
        using var releaseRestore = new ManualResetEventSlim();
        using var heldRead = new SemaphoreSlim(0);
        using var releaseRead = new ManualResetEventSlim();
        var holdRestore = 1;
        var restored = 0;
        var holdRead = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.RestoreDeleted)
            {
                Volatile.Write(ref restored, 1);
                if (Interlocked.Exchange(ref holdRestore, 0) == 1)
                {
                    heldRestore.Release();
                    releaseRestore.Wait(HoldLimit);
                }
            }
            else if (Volatile.Read(ref restored) == 1 && operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref holdRead, 0) == 1)
            {
                heldRead.Release();
                releaseRead.Wait(HoldLimit);
            }
        };
        var reportedAfterTheClose = new List<bool>();
        harness.Dialogs.OnError = (_, _) => reportedAfterTheClose.Add(window.IsClosed);
        Task restoring;
        Task<bool> closing;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await heldRestore.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            harness.Dialogs.BeforeCloseAnswer = async () =>
            {
                releaseRestore.Set();
                Assert.True(await heldRead.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            };
            harness.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
            closing = window.CloseByTitleBarAsync();
            await B2Harness.WaitUntilAsync(() => harness.Dialogs.CloseQuestions.Count == 1);
            Assert.True(Assert.Single(harness.Dialogs.CloseQuestions).CanContinue);
            await Task.Delay(100, Ct);
            Assert.False(window.IsClosed);
        }
        finally
        {
            releaseRestore.Set();
            releaseRead.Set();
        }

        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        Assert.True(window.IsClosed);
        Assert.Single(harness.Dialogs.CloseQuestions);
        var (title, message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Restore from the AD Recycle Bin (2)", title);
        var lines = message.Split('\n');
        Assert.Equal("Could not execute “Restore from the AD Recycle Bin (2)” in contoso.test: nothing was changed, failed: 2.", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("• " + sales + ": ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("• Paweł Lewandowski: ", StringComparison.Ordinal));
        Assert.Equal("Every change and its result are in the session log.", lines[^1]);
        Assert.Equal([true], reportedAfterTheClose);
        Assert.Empty(harness.Notifications.Items);
    }

    [Fact]
    public async Task AStopInTheLine_ThenTheWindowIsClosed_AsksNothing_ClosesAfterTheEnd_AndTheConnectionSaysWhatFailed()
    {
        // Stop was pressed in the line before the window was closed: nothing is left to ask, the window waits for the end and
        // closes. Its line - which would have said what failed - goes with it, so the connection says it.
        var (harness, bin, window, sales) = await FailingRestoreAsync();
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.RestoreDeleted && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var reportedAfterTheClose = new List<bool>();
        harness.Dialogs.OnError = (_, _) => reportedAfterTheClose.Add(window.IsClosed);
        Task restoring;
        Task<bool> closing;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities is [{ IsRunning: true }]);
            bin.Status.CancelCommand.Execute(null);
            await B2Harness.WaitUntilAsync(() => harness.Activities.Activities[0].IsCancelling);
            closing = window.CloseByTitleBarAsync();
            Assert.Empty(harness.Dialogs.CloseQuestions);
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        harness.Session.BeforeOperation = null;

        Assert.True(window.IsClosed);
        var (_, message) = Assert.Single(harness.Dialogs.Errors);
        var lines = message.Split('\n');
        Assert.StartsWith("Executing “Restore from the AD Recycle Bin (2)” in contoso.test was stopped: 0 of 2 objects done, failed: 1", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("• " + sales + ": ", StringComparison.Ordinal));
        Assert.Equal("Every change and its result are in the session log.", lines[^1]);
        Assert.Equal([true], reportedAfterTheClose);
    }

    [Fact]
    public async Task ClosingDuringTheReadAfterTheRestore_AsksNothing_ClosesWhenTheReadEnded_AndTheWindowsToastIsTheOnlyReport()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        var bin = harness.Factory.CreateRecycleBin();
        var window = new TestWindowHost(bin);
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Jan Kowalski");
        await bin.RestoreCommand.ExecuteAsync(null);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var restored = false;
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.RestoreDeleted)
            {
                restored = true;
            }
            else if (restored && hold && operation.Kind == FakeOperationKind.Search)
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task restoring;
        Task<bool> closing;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));

            // The restore ended and its result came back to the window, which reads the list again before it says it: nothing
            // can be stopped or left in the background any more - no question; the close waits for the window's code.
            closing = window.CloseByTitleBarAsync();
            Assert.Empty(harness.Dialogs.CloseQuestions);
            Assert.False(closing.IsCompleted);
            Assert.False(window.IsClosed);
        }
        finally
        {
            release.Set();
        }

        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        // Then the window closes, as the operator asked. The restore succeeded and the window said so in its own toast before it
        // read the list: that is the one report - the connection adds nothing.
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.True(window.IsClosed);
        Assert.Empty(harness.Dialogs.CloseQuestions);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Success, toast.Severity);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task AForcedCloseDuringTheReadAfterAFailedRestore_TheConnectionSaysWhatFailed()
    {
        // The owner window closes (nothing is asked) while the Recycle Bin reads its list again after a restore that failed: the
        // restore cannot leave the window any more - its result came back - but the window and its line are gone, so the
        // connection says what was not restored.
        var (harness, bin, _, sales) = await FailingRestoreAsync();
        using var heldRead = new SemaphoreSlim(0);
        using var releaseRead = new ManualResetEventSlim();
        var restored = 0;
        var holdRead = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.RestoreDeleted)
            {
                Volatile.Write(ref restored, 1);
            }
            else if (Volatile.Read(ref restored) == 1 && operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref holdRead, 0) == 1)
            {
                heldRead.Release();
                releaseRead.Wait(HoldLimit);
            }
        };
        Task restoring;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await heldRead.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            bin.Dispose();
        }
        finally
        {
            releaseRead.Set();
        }

        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        var (_, message) = Assert.Single(harness.Dialogs.Errors);
        Assert.StartsWith("Could not execute “Restore from the AD Recycle Bin (2)” in contoso.test: nothing was changed, failed: 2.", message, StringComparison.Ordinal);
        Assert.Contains("• " + sales + ": ", message, StringComparison.Ordinal);
        Assert.Empty(harness.Notifications.Items);
    }

    [Fact]
    public async Task TheCloseButton_OfAWindowThatDoesNothing_ClosesAtOnce()
    {
        var harness = await B2Harness.CreateAsync();
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        var closeRequests = new List<bool?>();
        bin.CloseRequested += (_, result) => closeRequests.Add(result);

        bin.CloseCommand.Execute(null);

        Assert.Equal([true], closeRequests);
        Assert.Empty(harness.Dialogs.Shown);
    }

    [Fact]
    public async Task CloseAndContinue_ClosesAtOnce_TheRestoreGoesOn_AndOnlyTheConnectionSaysHowItEnded()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        var bin = harness.Factory.CreateRecycleBin();
        var window = new TestWindowHost(bin);
        await bin.InitializeAsync();
        var sales = bin.Items.Single(r => r.Name == "Sprzedaż");
        bin.SelectedItems = bin.Items.Where(r => r.Name is "Sprzedaż" or "Paweł Lewandowski").ToList();
        await bin.RestoreCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.RestoreDeleted, sales.Dn, DirectoryErrorKind.AccessDenied);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (hold && operation.Kind == FakeOperationKind.RestoreDeleted)
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task restoring;
        try
        {
            restoring = bin.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));

            // The Close button (also Esc) asks what the title bar asks; Close and continue in the background: the window closes
            // at once - the host disposes it - and the restore goes on as an activity of the connection.
            harness.Dialogs.CloseAnswers.Enqueue(CloseAnswer.CloseAndContinue);
            await bin.CloseCommand.ExecuteAsync(null);
            Assert.True(window.IsClosed);
            Assert.True(window.Result);
            Assert.True(bin.IsDisposed);
            Assert.False(bin.Status.IsWorking);
            Assert.Null(bin.Status.Front);
            Assert.True(harness.Activities.Activities is [{ IsRunning: true, IsCancelling: false }]);
        }
        finally
        {
            release.Set();
        }

        await restoring.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        harness.Session.BeforeOperation = null;

        // The operator still learns what was not restored: one dialog, from the connection - the domain, both objects with their
        // reasons, and where every change is. The window that is gone said nothing.
        var (title, message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Restore from the AD Recycle Bin (2)", title);
        var lines = message.Split('\n');
        Assert.Equal("Could not execute “Restore from the AD Recycle Bin (2)” in contoso.test: nothing was changed, failed: 2.", lines[0]);
        Assert.Equal(2, lines.Count(l => l.StartsWith("• ", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.StartsWith("• Sprzedaż: ", StringComparison.Ordinal) && l.Length > "• Sprzedaż: ".Length);
        Assert.Contains(lines, l => l.StartsWith("• Paweł Lewandowski: ", StringComparison.Ordinal));
        Assert.Equal("Every change and its result are in the session log.", lines[^1]);
        Assert.DoesNotContain(harness.Notifications.Items, n => n.Message.Contains("Restore", StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Idle, bin.Status.Current.Kind);
    }

    /// <summary>
    /// The Recycle Bin in its window host, with the restore of "Sprzedaż" and "Paweł Lewandowski" previewed; the DC refuses
    /// "Sprzedaż" (so the user in it cannot be restored either). Returns the name of the refused object.
    /// </summary>
    private static async Task<(B2Harness Harness, RecycleBinViewModel Bin, TestWindowHost Window, string Refused)> FailingRestoreAsync()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        var bin = harness.Factory.CreateRecycleBin();
        var window = new TestWindowHost(bin);
        await bin.InitializeAsync();
        var sales = bin.Items.Single(r => r.Name == "Sprzedaż");
        bin.SelectedItems = bin.Items.Where(r => r.Name is "Sprzedaż" or "Paweł Lewandowski").ToList();
        await bin.RestoreCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.RestoreDeleted, sales.Dn, DirectoryErrorKind.AccessDenied);
        return (harness, bin, window, sales.Name);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndNumbers_NeverANameADomainOrAServerText()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var bin = new RecycleBinViewModel(services);
        await bin.InitializeAsync();
        var laptop = bin.Items.Single(r => r.Name == "KRK-LT001");
        bin.SelectedItems = bin.Items.ToList();
        await bin.RestoreCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.RestoreDeleted, laptop.Dn, DirectoryErrorKind.AccessDenied);
        await bin.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Contains("contoso.test", bin.Status.Current.Message, StringComparison.Ordinal);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("RecycleBin.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("RecycleBin.Status.Checking (2)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Changes.Status.Executing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Changes.Status.ExecutedPartly (1, 1)", StringComparison.Ordinal));
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("KRK-LT001", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Recycle Bin (", logger.AllText, StringComparison.Ordinal);
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
