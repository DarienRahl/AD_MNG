using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Filters;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Filters;

/// <summary>
/// The status line of the filter builder (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): the
/// test of the filter on the domain controller is the one operation of the window - counting (with Stop), how many
/// objects match, a count that was stopped or failed, and why a test was not started. What is wrong with the filter
/// itself is a note and never reaches the line. How the filter is built and saved is covered, unchanged, by
/// <see cref="SavedFiltersTests"/>.
/// </summary>
public sealed class FilterEditorStatusTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held search waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AtRest_TheLineIsEmpty_AndWhatIsWrongWithTheFilterIsNoStatus()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = Editor(harness);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);

        // A new filter has a condition without an attribute: a note next to the filter, nothing in the line.
        Assert.NotNull(editor.Error);
        Assert.False(editor.TestCommand.CanExecute(null));
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.Status.Lasting);
        Assert.False(editor.Status.IsWorking);
        Assert.False(editor.Status.CloseCommand.CanExecute(null));

        // A filter that is valid: still nothing to say until it is tested.
        var seen = Record(editor.Status);
        Accounting(editor);
        Assert.Null(editor.Error);
        Assert.True(editor.TestCommand.CanExecute(null));
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        editor.IsRaw = true;
        editor.RawFilter = "(&(objectClass=user)";
        Assert.NotNull(editor.Error);
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData("en-US", "Counting the matching objects in contoso.test…", "Matching objects in contoso.test: 3 (", " s).", "Stop")]
    [InlineData("pl-PL", "Liczenie pasujących obiektów w domenie contoso.test…", "Pasujące obiekty w domenie contoso.test: 3 (", " s).", "Zatrzymaj")]
    public async Task TheTest_IsTheOperationOfTheWindow_AndSaysHowManyObjectsMatch(string culture, string working, string doneStart, string doneEnd, string stopLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var editor = Editor(harness);
        Accounting(editor);
        var seen = Record(editor.Status);
        using var hold = new Hold(harness.Session);
        Task testing;
        try
        {
            testing = editor.TestCommand.ExecuteAsync(null);
            await hold.Entered.WaitAsync(Limit, Ct);

            // Working: what is counted and where, with Stop - the Stop of the window's own operation.
            Assert.True(editor.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind);
            Assert.Equal(working, editor.Status.Current.Message);
            Assert.Equal("FilterEditor.Status.Testing", editor.Status.Current.Log.ToString());
            Assert.True(editor.Status.Current.Progress?.IsIndeterminate);
            var stop = Assert.IsType<PageStatusAction>(editor.Status.Current.Action);
            Assert.Equal(stopLabel, stop.Label);
            Assert.Same(editor.Status.CancelCommand, stop.Command);
            Assert.False(editor.Status.CloseCommand.CanExecute(null));
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
        Assert.EndsWith(doneEnd, done.Message, StringComparison.Ordinal);
        Assert.Equal("FilterEditor.Status.Tested (3)", done.Log.ToString());
        Assert.Null(done.Action);
        Assert.True(done.CanClose);
        Assert.False(editor.Status.IsWorking);
        Assert.True(editor.TestCommand.CanExecute(null));
        Assert.NotNull(editor.TestResult);

        editor.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, editor.Status.Current);
    }

    [Theory]
    [InlineData("en-US", "Counting the matching objects was stopped: the filter was not tested. Press “Test” to test again.", "Test")]
    [InlineData("pl-PL", "Liczenie pasujących obiektów zatrzymano: filtru nie przetestowano. Wybierz „Testuj”, aby przetestować ponownie.", "Testuj")]
    public async Task Stop_SaysStoppingAtOnce_AndTheTestEndsAsAttentionWithTest(string culture, string stoppedText, string again)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var editor = Editor(harness);
        Accounting(editor);
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
                Assert.True(editor.Status.IsWorking);
                Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind);
                Assert.Equal(harness.Localizer[L.Feedback.Stopping], editor.Status.Current.Message);
                Assert.Null(editor.Status.Current.Action);
                Assert.False(editor.Status.CancelCommand.CanExecute(null));
            }
            finally
            {
                hold.Release();
            }

            await testing.WaitAsync(Limit, Ct);
        }

        // Stopped: what was not done, and the button that tests again - also when the domain controller answered.
        Assert.Equal(
            [(PageStatusKind.Working, working), (PageStatusKind.Working, harness.Localizer[L.Feedback.Stopping]), (PageStatusKind.Attention, stoppedText)],
            seen);
        var stopped = editor.Status.Current;
        Assert.Equal("FilterEditor.Status.TestStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(editor.Status.IsWorking);
        Assert.False(editor.Status.IsCancelling);
        var action = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(again, action.Label);
        Assert.Same(editor.TestCommand, action.Command);

        // The action tests again: the count.
        await ((IAsyncRelayCommand)action.Command).ExecuteAsync(action.Parameter);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        Assert.Equal("FilterEditor.Status.Tested (3)", editor.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task TheCancelOfTheCommand_WhichTheViewUsesWhenTheWindowCloses_IsTheSameStop()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = Editor(harness);
        Accounting(editor);
        Task testing;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                testing = editor.TestCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                Assert.True(editor.TestCancelCommand.CanExecute(null));
                editor.TestCancelCommand.Execute(null);
                Assert.True(editor.Status.IsCancelling);
                Assert.Equal("Stopping…", editor.Status.Current.Message);
            }
            finally
            {
                hold.Release();
            }

            await testing.WaitAsync(Limit, Ct);
        }

        Assert.Equal(PageStatusKind.Attention, editor.Status.Current.Kind);
        Assert.Equal("FilterEditor.Status.TestStopped", editor.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task AFilterThatChangesWhileItIsTested_EndsTheTestWithoutAWord_AndAResultGoesWithItsFilter()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = Editor(harness);
        Accounting(editor);
        var seen = Record(editor.Status);
        Task testing;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                testing = editor.TestCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                Assert.True(editor.Status.IsWorking);

                // Another value: the count that is on its way is about the filter of before.
                editor.Groups[0].Conditions[0].Value = "IT";
                Assert.False(editor.Status.IsWorking);
                Assert.Same(PageStatus.Empty, editor.Status.Current);
            }
            finally
            {
                hold.Release();
            }

            await testing.WaitAsync(Limit, Ct);
        }

        // Nothing was said when the count arrived.
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Idle], seen.Select(s => s.Kind));
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.TestResult);

        // A result is about the filter that was tested: it goes when the filter changes.
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        Assert.StartsWith("Matching objects in contoso.test: 2 (", editor.Status.Current.Message, StringComparison.Ordinal);
        editor.Groups[0].Conditions[0].Value = "Księgowość";
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.TestResult);

        // The name and the description are not the filter: a result stays.
        await editor.TestCommand.ExecuteAsync(null);
        editor.Name = "Księgowość";
        editor.Description = "Dział księgowości";
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
    }

    [Theory]
    [InlineData("en-US", "Could not test the filter in contoso.test: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się przetestować filtru w domenie contoso.test: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task ATestThatFails_IsAnErrorWithTheReasonTheCodeAndRetry(string culture, string lead, string tail, string retryLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var editor = Editor(harness);
        Accounting(editor);
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller dc01.contoso.test does not answer");
            }
        };
        var seen = Record(editor.Status);

        await editor.TestCommand.ExecuteAsync(null);

        // What was tried, why it failed, what to do, the code - a result that can be closed: the editor is as it was.
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(s => s.Kind));
        var failed = editor.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Contains(harness.Errors.Translate(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "x")), failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dc01", failed.Message, StringComparison.Ordinal);
        Assert.Equal("FilterEditor.Status.TestFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(editor.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(editor.TestCommand, retry.Command);
        Assert.True(editor.SaveCommand.CanExecute(null) || string.IsNullOrWhiteSpace(editor.Name));

        // Retry, with a domain controller that answers again: the count.
        fail = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);
        Assert.Equal("FilterEditor.Status.Tested (3)", editor.Status.Current.Log.ToString());
    }

    [Theory]
    [InlineData("en-US", "The filter was not tested: ")]
    [InlineData("pl-PL", "Filtru nie przetestowano: ")]
    public async Task WithoutASession_TheLineSaysWhyTheTestWasNotStarted(string culture, string lead)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        await harness.Sessions.DisconnectAsync();
        var editor = Editor(harness);
        Accounting(editor);

        // The editor works without a session - nothing lasts in the line - and the filter can be saved.
        Assert.Same(PageStatus.Empty, editor.Status.Current);
        Assert.Null(editor.Status.Lasting);
        editor.Name = "Księgowość";
        Assert.True(editor.SaveCommand.CanExecute(null));

        await editor.TestCommand.ExecuteAsync(null);

        // The answer to the press: what was skipped and why - an attention the operator can close.
        var skipped = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, skipped.Kind);
        Assert.Equal(lead + harness.Localizer[L.Errors.NotConnected], skipped.Message);
        Assert.Equal("Errors.NotConnected", skipped.Log.ToString());
        Assert.Null(skipped.Action);
        Assert.True(skipped.CanClose);
        Assert.False(editor.Status.IsWorking);

        // It goes with the filter it was about.
        editor.Groups[0].Conditions[0].Value = "IT";
        Assert.Same(PageStatus.Empty, editor.Status.Current);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysNumbersAndCodes_NeverTheFilterTheDomainOrAServerText()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var editor = new FilterEditorViewModel(
            harness.Sessions, harness.Dialogs, harness.Localizer, harness.Errors, harness.Time, harness.Dispatcher, existing: null, statusLog: logger);
        Accounting(editor);

        await editor.TestCommand.ExecuteAsync(null);
        Assert.Contains("contoso.test", editor.Status.Current.Message, StringComparison.Ordinal);
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94 for department=Księgowość");
            }
        };
        await editor.TestCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, editor.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("FilterEditor.Status.Testing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("FilterEditor.Status.Tested (3)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("FilterEditor.Status.TestFailed", StringComparison.Ordinal) && m.Contains("AccessDenied", StringComparison.Ordinal));
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Księgowość", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("department", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static FilterEditorViewModel Editor(B2Harness harness) =>
        new(harness.Sessions, harness.Dialogs, harness.Localizer, harness.Errors, harness.Time, harness.Dispatcher);

    /// <summary>department = Księgowość: three users of the sample company (two for "IT").</summary>
    private static void Accounting(FilterEditorViewModel editor)
    {
        var condition = editor.Groups[0].Conditions[0];
        condition.Attribute = Attr.Department;
        condition.Value = "Księgowość";
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
