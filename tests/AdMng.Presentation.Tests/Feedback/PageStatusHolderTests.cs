using System.Globalization;
using AdMng.Presentation.Diagnostics;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// The status holder of a page and its operation scope (track V, stage V2; design note docs/dev/v-interface-renewal.md
/// 4.5, 6.7 and goals G2, G3): every transition, cancellation, double completion, disposal without completion, a second
/// operation, use from worker threads and the value-free log copy.
/// </summary>
public class PageStatusHolderTests
{
    private const string Reading = "Wczytywanie „Kraków”…";
    private static readonly PageStatusLog ReadingLog = new(L.ListStatus.Reading);

    [Fact]
    public void ANewHolder_SaysNothing_AndHasNothingToCloseOrStop()
    {
        var (holder, changes) = Create();

        Assert.Same(PageStatus.Empty, holder.Current);
        Assert.Same(PageStatus.Empty, holder.Idle);
        Assert.Null(holder.Operation);
        Assert.False(holder.IsWorking);
        Assert.False(holder.IsCancelling);
        Assert.False(holder.CloseCommand.CanExecute(null));
        Assert.False(holder.CancelCommand.CanExecute(null));
        Assert.Empty(changes);
    }

    [Fact]
    public void TheSummary_IsShownAtOnce_UnlessSomethingElseIsShown()
    {
        var (holder, changes) = Create();

        holder.SetIdle("6 obiektów");
        Assert.Equal("6 obiektów", holder.Current.Message);
        Assert.True(holder.Current.IsIdle);
        Assert.Same(holder.Idle, holder.Current);
        Assert.Contains(nameof(PageStatusHolder.Current), changes);

        // The same text again is no change.
        changes.Clear();
        holder.SetIdle("6 obiektów");
        Assert.Empty(changes);

        // Behind a result the summary changes quietly and is shown when the result is closed.
        var error = PageStatus.Error("Nie udało się wczytać „Kraków”.", new PageStatusLog(L.ListStatus.ReadFailed));
        holder.Show(error);
        changes.Clear();
        holder.SetIdle("6 obiektów · Zaznaczono: 2");
        Assert.Same(error, holder.Current);
        Assert.DoesNotContain(nameof(PageStatusHolder.Current), changes);
        Assert.Equal("6 obiektów · Zaznaczono: 2", holder.Idle.Message);

        Assert.True(holder.CloseCommand.CanExecute(null));
        holder.CloseCommand.Execute(null);
        Assert.Equal("6 obiektów · Zaznaczono: 2", holder.Current.Message);
        Assert.True(holder.Current.IsIdle);
        Assert.False(holder.CloseCommand.CanExecute(null));

        // No summary at all is the empty status.
        holder.SetIdle(null);
        Assert.Same(PageStatus.Empty, holder.Current);
    }

    [Fact]
    public void Show_ReplacesWhatIsShown_AndCloseFollowsTheStatus()
    {
        var (holder, _) = Create();
        var closeChanges = 0;
        holder.CloseCommand.CanExecuteChanged += (_, _) => closeChanges++;
        var done = PageStatus.Done("Wyłączono 12 kont w contoso.test.", new PageStatusLog("Accounts.Disabled", 12));
        var lasting = PageStatus.Error("Sesja tego okna została zakończona.", new PageStatusLog(L.WindowSession.Ended), canClose: false);

        holder.Show(done);
        Assert.Same(done, holder.Current);
        Assert.True(holder.CloseCommand.CanExecute(null));
        Assert.Equal(1, closeChanges);

        // A lasting state cannot be closed by the operator; the page replaces it.
        holder.Show(lasting);
        Assert.Same(lasting, holder.Current);
        Assert.False(holder.CloseCommand.CanExecute(null));
        Assert.Equal(2, closeChanges);
        holder.CloseCommand.Execute(null);
        Assert.Same(lasting, holder.Current);

        holder.Clear();
        Assert.Same(holder.Idle, holder.Current);

        // A summary handed to Show becomes the summary of the page.
        holder.Show(done);
        holder.Show(PageStatus.Idle("12 obiektów"));
        Assert.Equal("12 obiektów", holder.Current.Message);
        Assert.Equal("12 obiektów", holder.Idle.Message);
        Assert.Throws<ArgumentNullException>(() => holder.Show(null!));
    }

    [Fact]
    public void Show_RefusesARunningOperation_WorkingIsOnlyShownThroughAScope()
    {
        var (holder, _) = Create();

        Assert.Throws<ArgumentException>(() => holder.Show(PageStatus.Working(Reading, ReadingLog)));
        Assert.Same(PageStatus.Empty, holder.Current);
    }

    [Fact]
    public void Begin_PutsThePageIntoWorkingAtOnce_WithStopAsItsAction()
    {
        var (holder, changes) = Create();
        holder.SetIdle("6 obiektów");
        changes.Clear();

        using var scope = holder.Begin(Reading, ReadingLog);

        Assert.True(holder.IsWorking);
        Assert.False(holder.IsCancelling);
        Assert.Same(scope, holder.Operation);
        Assert.Equal(PageStatusKind.Working, holder.Current.Kind);
        Assert.Equal(Reading, holder.Current.Message);
        Assert.True(Assert.NotNull(holder.Current.Progress).IsIndeterminate);
        Assert.False(holder.Current.CanClose);
        Assert.False(holder.CloseCommand.CanExecute(null));
        Assert.Same(ReadingLog, holder.Current.Log);
        Assert.Contains(nameof(PageStatusHolder.Current), changes);
        Assert.Contains(nameof(PageStatusHolder.IsWorking), changes);
        Assert.Contains(nameof(PageStatusHolder.Operation), changes);

        // The cancel affordance comes with the operation: "Stop", bound to the holder's cancel command.
        var stop = Assert.IsType<PageStatusAction>(holder.Current.Action);
        Assert.Equal("Zatrzymaj", stop.Label);
        Assert.Same(holder.CancelCommand, stop.Command);
        Assert.True(holder.CancelCommand.CanExecute(null));

        Assert.False(scope.Token.IsCancellationRequested);
        Assert.True(scope.Token.CanBeCanceled);
        Assert.True(scope.CanCancel);
        Assert.False(scope.IsCancelling);
        Assert.False(scope.IsEnded);
    }

    [Fact]
    public void Report_ChangesTextProgressAndLogCopy_AndKeepsWhatIsNotGiven()
    {
        var (holder, _) = Create();
        using var scope = holder.Begin(Reading, ReadingLog);
        var stop = holder.Current.Action;

        scope.Report("Wczytywanie „Kraków”: na razie 1 500 obiektów…", log: new PageStatusLog(L.ListStatus.Reading, 1500));
        Assert.Equal("Wczytywanie „Kraków”: na razie 1 500 obiektów…", holder.Current.Message);
        Assert.True(Assert.NotNull(holder.Current.Progress).IsIndeterminate);
        Assert.Equal([1500L], holder.Current.Log.Counts);
        Assert.Same(stop, holder.Current.Action);
        Assert.True(holder.IsWorking);

        scope.Report(progress: PageProgress.Of(3, 4));
        Assert.Equal("Wczytywanie „Kraków”: na razie 1 500 obiektów…", holder.Current.Message);
        Assert.Equal(0.75, Assert.NotNull(holder.Current.Progress).Fraction);
        Assert.Equal([1500L], holder.Current.Log.Counts);

        scope.Report("Prawie gotowe…", PageProgress.Indeterminate);
        Assert.Equal("Prawie gotowe…", holder.Current.Message);
        Assert.True(Assert.NotNull(holder.Current.Progress).IsIndeterminate);
        Assert.False(holder.Current.CanClose);
    }

    [Fact]
    public void Cancel_SaysStoppingAtOnce_ThenCancelsTheToken_AndTheOperationEndsItself()
    {
        var (holder, changes) = Create();
        holder.SetIdle("6 obiektów");
        using var scope = holder.Begin(Reading, ReadingLog);
        PageStatus? seenByTheOperation = null;
        using var registration = scope.Token.Register(() => seenByTheOperation = holder.Current);
        changes.Clear();

        holder.CancelCommand.Execute(null);

        // The answer is on screen before the operation noticed anything: when its token fired, the page said "Stopping…".
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.Equal("Zatrzymywanie…", Assert.IsType<PageStatus>(seenByTheOperation).Message);
        Assert.Equal(PageStatusKind.Working, holder.Current.Kind);
        Assert.Equal("Zatrzymywanie…", holder.Current.Message);
        Assert.True(holder.IsWorking);
        Assert.True(holder.IsCancelling);
        Assert.True(scope.IsCancelling);
        Assert.Contains(nameof(PageStatusHolder.IsCancelling), changes);

        // While it stops there is nothing to press: no action, no close, no second cancel; reports are ignored.
        Assert.Null(holder.Current.Action);
        Assert.False(holder.Current.CanClose);
        Assert.False(holder.CancelCommand.CanExecute(null));
        scope.Report("Wczytywanie „Kraków”: na razie 2 000 obiektów…");
        scope.Cancel();
        Assert.Equal("Zatrzymywanie…", holder.Current.Message);

        // The operation says how it ended.
        var refresh = new PageStatusAction("Odśwież", holder.CloseCommand);
        scope.CompleteAttention("Wczytywanie „Kraków” zatrzymano: wczytano 150 obiektów.", new PageStatusLog(L.ListStatus.Stopped, 150), refresh);
        Assert.Equal(PageStatusKind.Attention, holder.Current.Kind);
        Assert.Same(refresh, holder.Current.Action);
        Assert.True(holder.Current.CanClose);
        Assert.False(holder.IsWorking);
        Assert.False(holder.IsCancelling);
        Assert.False(scope.IsCancelling);
        Assert.True(scope.IsEnded);
        Assert.Null(holder.Operation);
    }

    [Fact]
    public void AnOperationThatCannotBeCancelled_HasNoStop_AndIgnoresCancel()
    {
        var (holder, _) = Create();
        using var scope = holder.Begin("Zapisywanie zmian…", new PageStatusLog("Save.Working"), canCancel: false);

        Assert.False(scope.CanCancel);
        Assert.Null(holder.Current.Action);
        Assert.False(holder.CancelCommand.CanExecute(null));

        scope.Cancel();
        holder.CancelCommand.Execute(null);
        Assert.False(scope.Token.IsCancellationRequested);
        Assert.False(holder.IsCancelling);
        Assert.Equal("Zapisywanie zmian…", holder.Current.Message);
    }

    [Fact]
    public void AnOperation_EndsAsDoneAttentionOrError_AndTheFirstCompletionStands()
    {
        var (holder, _) = Create();
        holder.SetIdle("6 obiektów");

        var first = holder.Begin(Reading, ReadingLog);
        first.CompleteDone("Wyłączono 12 kont w contoso.test.", new PageStatusLog("Accounts.Disabled", 12));
        Assert.Equal(PageStatusKind.Done, holder.Current.Kind);
        Assert.False(holder.IsWorking);
        Assert.Null(holder.Operation);
        Assert.True(first.IsEnded);
        Assert.False(first.Token.IsCancellationRequested);
        var done = holder.Current;

        // A second completion, a late report, a late cancel and the disposal change nothing on the page.
        first.CompleteError("Nie udało się.", new PageStatusLog("Accounts.Failed"));
        first.Report("Jeszcze chwila…");
        first.Cancel();
        first.Dispose();
        Assert.Same(done, holder.Current);
        Assert.False(holder.IsCancelling);

        var second = holder.Begin(Reading, ReadingLog);
        second.CompleteError("Nie udało się wczytać „Kraków”: brak dostępu.", new PageStatusLog(L.ListStatus.ReadFailed), canClose: false);
        Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
        Assert.False(holder.Current.CanClose);
        second.Dispose();
        Assert.Equal(PageStatusKind.Error, holder.Current.Kind);

        var third = holder.Begin(Reading, ReadingLog);
        third.Complete(PageStatus.Attention("Pominięto 3 z 15 kont.", new PageStatusLog("Accounts.Skipped", 3, 15)));
        Assert.Equal(PageStatusKind.Attention, holder.Current.Kind);

        // An operation whose result is the page itself ends without a message: the summary again.
        var quiet = holder.Begin(Reading, ReadingLog);
        quiet.Complete(null);
        Assert.Same(holder.Idle, holder.Current);
        Assert.Equal("6 obiektów", holder.Current.Message);
        Assert.True(quiet.IsEnded);
        Assert.False(quiet.Token.IsCancellationRequested);

        // "Working" is never a result.
        var odd = holder.Begin(Reading, ReadingLog);
        odd.Complete(PageStatus.Working("Nadal trwa…", ReadingLog));
        Assert.Same(holder.Idle, holder.Current);
        Assert.False(holder.IsWorking);
    }

    [Fact]
    public void DisposingAScopeThatWasNotCompleted_NeverLeavesThePageInWorking()
    {
        var (holder, _) = Create();
        holder.SetIdle("6 obiektów");

        var scope = holder.Begin(Reading, ReadingLog);
        Assert.True(holder.IsWorking);
        scope.Dispose();

        Assert.False(holder.IsWorking);
        Assert.Same(holder.Idle, holder.Current);
        Assert.Null(holder.Operation);
        Assert.True(scope.IsEnded);
        Assert.True(scope.Token.IsCancellationRequested);
        scope.Dispose();
        Assert.Same(holder.Idle, holder.Current);

        // The same for a scope left by an exception, and for one that was stopping.
        Action failing = () =>
        {
            using var left = holder.Begin(Reading, ReadingLog);
            throw new InvalidOperationException("nieobsłużony błąd");
        };
        Assert.Throws<InvalidOperationException>(failing);
        Assert.False(holder.IsWorking);
        Assert.Same(holder.Idle, holder.Current);

        var stopping = holder.Begin(Reading, ReadingLog);
        stopping.Cancel();
        Assert.True(holder.IsCancelling);
        stopping.Dispose();
        Assert.False(holder.IsWorking);
        Assert.False(holder.IsCancelling);
        Assert.Same(holder.Idle, holder.Current);
        Assert.False(holder.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void ASecondOperation_ReplacesTheRunningOne_WhoseTokenIsCancelledAndWhoseWordsAreIgnored()
    {
        var (holder, _) = Create();
        var first = holder.Begin("Wczytywanie „Kraków”…", ReadingLog);
        PageStatus? shownWhenTheFirstWasCancelled = null;
        using var registration = first.Token.Register(() =>
        {
            // The first operation answers its cancellation on the spot, as a list load does.
            shownWhenTheFirstWasCancelled = holder.Current;
            first.CompleteAttention("Wczytywanie „Kraków” zatrzymano.", new PageStatusLog(L.ListStatus.Stopped, 0));
        });

        var second = holder.Begin("Wczytywanie „Warszawa”…", ReadingLog);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(first.IsEnded);
        Assert.False(first.IsCancelling);
        Assert.Same(second, holder.Operation);
        Assert.Equal("Wczytywanie „Warszawa”…", Assert.IsType<PageStatus>(shownWhenTheFirstWasCancelled).Message);
        Assert.Equal("Wczytywanie „Warszawa”…", holder.Current.Message);
        Assert.True(holder.IsWorking);
        Assert.False(second.Token.IsCancellationRequested);

        // Whatever the replaced operation still says or does is not the page's.
        first.Report("Wczytywanie „Kraków”: na razie 10 obiektów…");
        first.CompleteError("Nie udało się wczytać „Kraków”.", new PageStatusLog(L.ListStatus.ReadFailed));
        first.Cancel();
        first.Dispose();
        Assert.Equal("Wczytywanie „Warszawa”…", holder.Current.Message);
        Assert.True(holder.IsWorking);
        Assert.False(holder.IsCancelling);
        Assert.Same(second, holder.Operation);

        // A replaced operation that was stopping does not leave "stopping" behind.
        second.Cancel();
        Assert.True(holder.IsCancelling);
        using var third = holder.Begin("Wczytywanie „Gdańsk”…", ReadingLog);
        Assert.False(holder.IsCancelling);
        Assert.True(holder.CancelCommand.CanExecute(null));
        Assert.Equal("Wczytywanie „Gdańsk”…", holder.Current.Message);
        Assert.NotNull(holder.Current.Action);
    }

    [Fact]
    public void ShowAndClear_EndARunningOperation()
    {
        var (holder, _) = Create();
        holder.SetIdle("6 obiektów");

        var shown = holder.Begin(Reading, ReadingLog);
        var gone = PageStatus.Attention("Ten kontener już nie istnieje.", new PageStatusLog(L.List.LocationDeleted), canClose: false);
        holder.Show(gone);
        Assert.Same(gone, holder.Current);
        Assert.False(holder.IsWorking);
        Assert.Null(holder.Operation);
        Assert.True(shown.IsEnded);
        Assert.True(shown.Token.IsCancellationRequested);
        shown.CompleteAttention("Wczytywanie zatrzymano.", new PageStatusLog(L.ListStatus.Stopped, 0));
        Assert.Same(gone, holder.Current);

        var cleared = holder.Begin(Reading, ReadingLog);
        holder.Clear();
        Assert.Same(holder.Idle, holder.Current);
        Assert.False(holder.IsWorking);
        Assert.True(cleared.Token.IsCancellationRequested);
        cleared.CompleteDone("Gotowe.", new PageStatusLog("Some.Key"));
        Assert.Same(holder.Idle, holder.Current);
    }

    [Fact]
    public async Task AScope_MayBeUsedFromWorkerThreads_AndThePageEndsInTheLastState()
    {
        var dispatcher = new SynchronousUiDispatcher();
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), dispatcher);
        var onUiThread = new List<bool>();
        holder.PropertyChanged += (_, _) => onUiThread.Add(dispatcher.CheckAccess());
        var scope = holder.Begin(Reading, ReadingLog);
        onUiThread.Clear();

        // Eight workers report 200 times each: every change of the page happens on the UI thread, one at a time.
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 1; i <= 200; i++)
            {
                scope.Report($"Wczytywanie „Kraków”: robotnik {worker}, krok {i}…", PageProgress.Of(i, 200), new PageStatusLog(L.ListStatus.Reading, i));
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(workers);

        Assert.True(holder.IsWorking);
        Assert.Equal(1600, onUiThread.Count);
        Assert.All(onUiThread, onUi => Assert.True(onUi));
        Assert.EndsWith("krok 200…", holder.Current.Message, StringComparison.Ordinal);
        Assert.Equal(1d, Assert.NotNull(holder.Current.Progress).Fraction);
        Assert.Equal([200L], holder.Current.Log.Counts);

        // Cancelled on one thread, completed on another: the page says how the operation ended, once.
        await Task.Run(scope.Cancel, TestContext.Current.CancellationToken);
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.True(holder.IsCancelling);
        Assert.Equal("Zatrzymywanie…", holder.Current.Message);

        var completions = Enumerable.Range(0, 8).Select(worker => Task.Run(
            () => scope.CompleteAttention($"Zatrzymano przez robotnika {worker}.", new PageStatusLog(L.ListStatus.Stopped, worker)),
            TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(completions);
        await Task.Run(scope.Dispose, TestContext.Current.CancellationToken);

        Assert.Equal(PageStatusKind.Attention, holder.Current.Kind);
        Assert.StartsWith("Zatrzymano przez robotnika", holder.Current.Message, StringComparison.Ordinal);
        Assert.False(holder.IsWorking);
        Assert.False(holder.IsCancelling);
        Assert.Null(holder.Operation);
        Assert.True(scope.IsEnded);
        Assert.All(onUiThread, onUi => Assert.True(onUi));
    }

    [Fact]
    public void TheLogCopy_IsTheKeyTheCountsAndTheCode_NeverTheTextArguments()
    {
        const string container = "Kraków";
        const string person = "Żaneta Śliwa";
        var logger = new RecordingLogger();
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher(), logger);

        holder.SetIdle($"2 314 obiektów · Zaznaczono: {person}");
        Assert.Empty(logger.Entries);

        var scope = holder.Begin($"Wczytywanie „{container}”…", new PageStatusLog(L.ListStatus.Reading));
        scope.Report($"Wczytywanie „{container}”: na razie 1 500 obiektów…", log: new PageStatusLog(L.ListStatus.Reading, 1500));
        scope.Report($"Wczytywanie „{container}”: na razie 2 000 obiektów…", log: new PageStatusLog(L.ListStatus.Reading, 2000));
        scope.Cancel();
        scope.CompleteAttention($"Wczytywanie „{container}” zatrzymano: wczytano 2 000 obiektów.", new PageStatusLog(L.ListStatus.Stopped, 2000));
        holder.Show(PageStatus.Error(
            $"Nie udało się zapisać konta „{person}”: hasło jest za krótkie.",
            new PageStatusLog("Account.SaveFailed", 1).WithCode(ResultCode.Win32(0x52D))));
        holder.Show(PageStatus.Done($"Wyłączono konto „{person}”.", new PageStatusLog("Accounts.Disabled", 1)));
        holder.CloseCommand.Execute(null);

        // One entry per new message: a new count of the same message and the summary are not news.
        Assert.Equal(
            [
                (LogLevel.Debug, "Page status Working: List.Status.Reading () []"),
                (LogLevel.Debug, "Page status Working: Feedback.Stopping () []"),
                (LogLevel.Warning, "Page status Attention: List.Status.Stopped (2000) []"),
                (LogLevel.Error, "Page status Error: Account.SaveFailed (1) [0x52D]"),
                (LogLevel.Information, "Page status Done: Accounts.Disabled (1) []"),
            ],
            logger.Entries.Select(entry => (entry.Level, entry.Message)));

        // The message key is the name of the event; the id says the kind.
        Assert.Equal(
            ["List.Status.Reading", "Feedback.Stopping", "List.Status.Stopped", "Account.SaveFailed", "Accounts.Disabled"],
            logger.Entries.Select(entry => entry.Event.Name));
        Assert.Equal(
            [PageStatusKind.Working, PageStatusKind.Working, PageStatusKind.Attention, PageStatusKind.Error, PageStatusKind.Done],
            logger.Entries.Select(entry => (PageStatusKind)(entry.Event.Id - PageStatusHolder.LogEventBase)));

        // Nothing of what was on screen is in the log: not in the text, not in the values behind the template.
        foreach (var entry in logger.Entries)
        {
            Assert.All(entry.Values.Append(entry.Message), text =>
            {
                Assert.DoesNotContain(container, text, StringComparison.Ordinal);
                Assert.DoesNotContain(person, text, StringComparison.Ordinal);
                Assert.DoesNotContain("obiekt", text, StringComparison.Ordinal);
                Assert.DoesNotContain("hasło", text, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public void TheProblemReport_KeepsTheMessageKeyOfAStatus_AndNoValue()
    {
        var buffer = new DiagnosticLogBuffer(new ManualTimeProvider(TestHarness.Now));
        var logger = new DiagnosticLoggerProvider(buffer).CreateLogger(typeof(PageStatusHolder).FullName!);
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher(), logger);

        holder.Show(PageStatus.Error(
            "Nie udało się wczytać „Księgowość”: kontroler domeny jest niedostępny.",
            new PageStatusLog(L.ListStatus.ReadFailed).WithCode(ResultCode.Ldap(81))));

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(L.ListStatus.ReadFailed, entry.EventName);
        Assert.Equal(PageStatusHolder.LogEventBase + (int)PageStatusKind.Error, entry.EventId);
        Assert.Equal("Page status {Kind}: {MessageKey} ({Counts}) [{Code}]", entry.Template);
        Assert.DoesNotContain("Księgowość", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALogger_NothingIsWritten_AndNothingFails()
    {
        var (holder, _) = Create();

        holder.Show(PageStatus.Done("Gotowe.", new PageStatusLog("Some.Key")));
        using var scope = holder.Begin(Reading, ReadingLog);
        scope.CompleteError("Błąd.", new PageStatusLog("Some.Error"));

        Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
        Assert.Throws<ArgumentNullException>(() => new PageStatusHolder(null!, new SynchronousUiDispatcher()));
        Assert.Throws<ArgumentNullException>(() => new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), null!));
    }

    [Fact]
    public void TheHoldersOwnWords_FollowTheLanguage()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var holder = new PageStatusHolder(localizer, new SynchronousUiDispatcher());

        var english = holder.Begin("Reading “Kraków”…", ReadingLog);
        Assert.Equal("Stop", holder.Current.Action!.Label);
        english.Cancel();
        Assert.Equal("Stopping…", holder.Current.Message);
        english.Dispose();

        localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        var polish = holder.Begin(Reading, ReadingLog);
        Assert.Equal("Zatrzymaj", holder.Current.Action!.Label);
        polish.Cancel();
        Assert.Equal("Zatrzymywanie…", holder.Current.Message);
        polish.Dispose();
        Assert.Same(PageStatus.Empty, holder.Current);
    }

    private static (PageStatusHolder Holder, List<string?> Changes) Create()
    {
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var changes = new List<string?>();
        holder.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        return (holder, changes);
    }

    /// <summary>Keeps every log call: the level, the event, the formatted text and the values behind the template.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, EventId Event, string Message, IReadOnlyList<string> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty).ToList()
                : [];
            Entries.Add((logLevel, eventId, formatter(state, exception), values));
        }
    }
}
