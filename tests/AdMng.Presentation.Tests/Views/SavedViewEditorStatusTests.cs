using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Views;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Views;

/// <summary>
/// The status line of the editor of a saved view (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"):
/// the test of the view on the domain controller is the one operation of the window - counting in the search base (with
/// Stop), how many objects match, a count that was stopped or failed, and why a test was not started. A filter that
/// cannot be read is a note and never reaches the line. How a view is edited and saved is covered, unchanged, by
/// <see cref="ViewsTests"/>.
/// </summary>
public sealed class SavedViewEditorStatusTests
{
    private const string Users = "(objectClass=user)";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held search waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AtRest_TheLineIsEmpty_AndAFilterThatCannotBeReadIsNoStatus()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = Editor(harness);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.Status.Lasting);
        Assert.False(editor.Status.IsWorking);
        var seen = Record(editor.Status);

        // A note under the filter - why Save and Test cannot be pressed - and nothing in the line.
        editor.Filter = "(&(objectClass=user)";
        Assert.NotNull(editor.FilterError);
        Assert.False(editor.TestCommand.CanExecute(null));
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        editor.Filter = Users;
        Assert.Null(editor.FilterError);
        Assert.True(editor.TestCommand.CanExecute(null));
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData("en-US", "Counting the matching objects in “contoso.test/Firma/Pracownicy/Księgowość”…", "Matching objects in “contoso.test/Firma/Pracownicy/Księgowość”: 3 (", "Stop")]
    [InlineData("pl-PL", "Liczenie pasujących obiektów w „contoso.test/Firma/Pracownicy/Księgowość”…", "Pasujące obiekty w „contoso.test/Firma/Pracownicy/Księgowość”: 3 (", "Zatrzymaj")]
    public async Task TheTest_IsTheOperationOfTheWindow_AndSaysHowManyObjectsMatchInTheSearchBase(string culture, string working, string doneStart, string stopLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var editor = Accounting(harness);
        var seen = Record(editor.Status);
        using var hold = new Hold(harness.Session);
        Task testing;
        try
        {
            testing = editor.TestCommand.ExecuteAsync(null);
            await hold.Entered.WaitAsync(Limit, Ct);

            // Working: what is counted and where - the search base as a path - with Stop.
            Assert.True(editor.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind);
            Assert.Equal(working, editor.Status.Current.Message);
            Assert.Equal("SavedViewEditor.Status.Testing", editor.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(editor.Status.Current.Action);
            Assert.Equal(stopLabel, stop.Label);
            Assert.Same(editor.Status.CancelCommand, stop.Command);
            Assert.False(editor.TestCommand.CanExecute(null));
        }
        finally
        {
            hold.Release();
        }

        await testing.WaitAsync(Limit, Ct);

        // Done: how many, where, how long it took - a result the operator can close.
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Done], seen.Select(s => s.Kind));
        var done = editor.Status.Current;
        Assert.StartsWith(doneStart, done.Message, StringComparison.Ordinal);
        Assert.EndsWith(" s).", done.Message, StringComparison.Ordinal);
        Assert.Equal("SavedViewEditor.Status.Tested (3)", done.Log.ToString());
        Assert.Null(done.Action);
        Assert.True(done.CanClose);
        Assert.NotNull(editor.TestResult);
        editor.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, editor.Status.Current);
    }

    [Theory]
    [InlineData("en-US", "Counting the matching objects was stopped: the view was not tested. Press “Test” to test again.", "Test")]
    [InlineData("pl-PL", "Liczenie pasujących obiektów zatrzymano: widoku nie przetestowano. Wybierz „Testuj”, aby przetestować ponownie.", "Testuj")]
    public async Task Stop_SaysStoppingAtOnce_AndTheTestEndsAsAttentionWithTest(string culture, string stoppedText, string again)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var editor = Accounting(harness);
        var seen = Record(editor.Status);
        string working;
        Task testing;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                testing = editor.TestCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                working = editor.Status.Current.Message;

                // Pressed: "Stopping…" at once, for as long as the domain controller has not answered.
                editor.Status.CancelCommand.Execute(null);
                Assert.True(editor.Status.IsCancelling);
                Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind);
                Assert.Equal(harness.Localizer[L.Feedback.Stopping], editor.Status.Current.Message);
                Assert.False(editor.Status.CancelCommand.CanExecute(null));
            }
            finally
            {
                hold.Release();
            }

            await testing.WaitAsync(Limit, Ct);
        }

        Assert.Equal(
            [(PageStatusKind.Working, working), (PageStatusKind.Working, harness.Localizer[L.Feedback.Stopping]), (PageStatusKind.Attention, stoppedText)],
            seen);
        var stopped = editor.Status.Current;
        Assert.Equal("SavedViewEditor.Status.TestStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(editor.Status.IsWorking);
        var action = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(again, action.Label);
        Assert.Same(editor.TestCommand, action.Command);

        // The action tests again: the count. The cancel of the command - the view uses it when the window closes - is
        // the same Stop.
        await ((IAsyncRelayCommand)action.Command).ExecuteAsync(action.Parameter);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                testing = editor.TestCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                editor.TestCancelCommand.Execute(null);
                Assert.True(editor.Status.IsCancelling);
            }
            finally
            {
                hold.Release();
            }

            await testing.WaitAsync(Limit, Ct);
        }

        Assert.Equal("SavedViewEditor.Status.TestStopped", editor.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task AViewThatChangesWhileItIsTested_EndsTheTestWithoutAWord_AndAResultGoesWithItsView()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = Accounting(harness);
        var seen = Record(editor.Status);
        Task testing;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                testing = editor.TestCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                Assert.True(editor.Status.IsWorking);

                // Another search base: the count that is on its way is about the view of before.
                editor.BaseDn = "OU=IT,OU=Pracownicy,OU=Firma,{domain}";
                Assert.False(editor.Status.IsWorking);
                Assert.Same(PageStatus.Empty, editor.Status.Current);
            }
            finally
            {
                hold.Release();
            }

            await testing.WaitAsync(Limit, Ct);
        }

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Idle], seen.Select(s => s.Kind));
        Assert.Same(PageStatus.Empty, editor.Status.Current);

        // A result is about the view that was tested: it goes with the filter, the base and the scope - not with the name.
        await editor.TestCommand.ExecuteAsync(null);
        Assert.StartsWith("Matching objects in “contoso.test/Firma/Pracownicy/IT”: 2 (", editor.Status.Current.Message, StringComparison.Ordinal);
        editor.Name = "Dział IT";
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        editor.Scope = SearchScope.OneLevel;
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        editor.Filter = "(objectClass=group)";
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.TestResult);
    }

    [Theory]
    [InlineData("en-US", "Could not test the view in “contoso.test/Firma/Pracownicy/Księgowość”: ", " Press Retry to try again. (AccessDenied)", "Retry")]
    [InlineData("pl-PL", "Nie udało się przetestować widoku w „contoso.test/Firma/Pracownicy/Księgowość”: ", " Wybierz Ponów, aby spróbować ponownie. (AccessDenied)", "Ponów")]
    public async Task ATestThatFails_IsAnErrorWithTheReasonTheCodeAndRetry(string culture, string lead, string tail, string retryLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var editor = Accounting(harness);
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS)");
            }
        };
        var seen = Record(editor.Status);

        await editor.TestCommand.ExecuteAsync(null);

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(s => s.Kind));
        var failed = editor.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("SavedViewEditor.Status.TestFailed [AccessDenied]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(editor.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(editor.TestCommand, retry.Command);

        fail = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        Assert.Equal("SavedViewEditor.Status.Tested (3)", editor.Status.Current.Log.ToString());
    }

    [Theory]
    [InlineData("en-US", "The view was not tested: ")]
    [InlineData("pl-PL", "Widoku nie przetestowano: ")]
    public async Task WithoutASession_TheLineSaysWhyTheTestWasNotStarted(string culture, string lead)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        await harness.Sessions.DisconnectAsync();
        var editor = Editor(harness);
        editor.Name = "Księgowość";
        editor.Filter = Users;

        // The editor works without a session - nothing lasts in the line - and the view can be saved.
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.Status.Lasting);
        Assert.True(editor.SaveCommand.CanExecute(null));

        await editor.TestCommand.ExecuteAsync(null);

        var skipped = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, skipped.Kind);
        Assert.Equal(lead + harness.Localizer[L.Errors.NotConnected], skipped.Message);
        Assert.Equal("Errors.NotConnected", skipped.Log.ToString());
        Assert.Null(skipped.Action);
        Assert.True(skipped.CanClose);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysNumbersAndCodes_NeverTheSearchBaseTheFilterOrAServerText()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var editor = new SavedViewEditorViewModel(
            harness.Sessions, harness.Dialogs, harness.Localizer, harness.Errors, harness.Dispatcher, existing: null, isNew: null, statusLog: logger)
        {
            BaseDn = "OU=Księgowość,OU=Pracownicy,OU=Firma,{domain}",
            Filter = Users,
        };

        await editor.TestCommand.ExecuteAsync(null);
        Assert.Contains("Księgowość", editor.Status.Current.Message, StringComparison.Ordinal);
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };
        await editor.TestCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, editor.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("SavedViewEditor.Status.Testing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("SavedViewEditor.Status.Tested (3)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("SavedViewEditor.Status.TestFailed", StringComparison.Ordinal) && m.Contains("ServerUnavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Księgowość", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("objectClass", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Firma", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static SavedViewEditorViewModel Editor(B2Harness harness) =>
        new(harness.Sessions, harness.Dialogs, harness.Localizer, harness.Errors, harness.Dispatcher);

    /// <summary>The users of OU=Księgowość: three of the sample company (two in OU=IT).</summary>
    private static SavedViewEditorViewModel Accounting(B2Harness harness)
    {
        var editor = Editor(harness);
        editor.Name = "Księgowość";
        editor.BaseDn = "OU=Księgowość,OU=Pracownicy,OU=Firma,{domain}";
        editor.Filter = Users;
        return editor;
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
    /// A slow domain controller: the first search of the session waits until the test lets go - and, like a real
    /// server, does not notice a cancellation meanwhile - so what the window says while it waits lasts until then.
    /// </summary>
    private sealed class Hold : IDisposable
    {
        private readonly FakeDirectorySession _session;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _armed = 1;

        public Hold(FakeDirectorySession session)
        {
            _session = session;
            session.BeforeOperation = operation =>
            {
                if (operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    _entered.TrySetResult();
                    _release.Wait(HoldLimit);
                }
            };
        }

        /// <summary>Completed once the search reached the domain controller.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _session.BeforeOperation = null;
        }
    }
}
