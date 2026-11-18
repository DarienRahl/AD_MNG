using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// The progress of the taskbar button of a window (track V, stage V2b-5b, owner's decision 6; design note
/// docs/dev/v-interface-renewal.md 6.7): it follows the holder in front, after the delay of the line, and says nothing the line
/// does not. Everything on a fake clock - no wait.
/// </summary>
public class TaskbarProgressTrackerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void ANewTracker_AndAWindowWithoutAHolder_ShowNothing()
    {
        var (follower, _, _, _) = Create();
        using var tracker = new TaskbarProgressTracker(new SynchronousUiDispatcher(), new FakeTimeProvider(), () => Delay);
        var changes = new List<TaskbarProgressState>();
        tracker.PropertyChanged += (_, _) => changes.Add(tracker.State);

        Assert.Null(tracker.Holder);
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Empty(changes);

        // A tracker that follows a holder which says nothing shows nothing either.
        Assert.Equal(TaskbarProgressState.None, follower.State);
    }

    [Fact]
    public void AnOperationWithACount_ShowsThePartDone_AfterTheDelay_AndFollowsIt()
    {
        var (tracker, holder, changes, time) = Create();
        using var operation = holder.Begin("Wykonywanie… 0 z 300", new PageStatusLog("A"), PageProgress.Of(0, 300));

        // The line has not shown it yet and neither has the button.
        time.Advance(Delay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Empty(changes);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TaskbarProgressState.Normal(0d), tracker.State);

        operation.Report("Wykonywanie… 75 z 300", PageProgress.Of(75, 300));
        Assert.Equal(TaskbarProgressState.Normal(0.25d), tracker.State);
        operation.Report("Wykonywanie… 300 z 300", PageProgress.Of(300, 300));
        Assert.Equal(TaskbarProgressState.Normal(1d), tracker.State);
        Assert.Equal(TaskbarProgressKind.Normal, tracker.State.Kind);
    }

    [Fact]
    public void AnOperationWithoutACount_IsIndeterminate_AndGetsACountWhenItKnowsOne()
    {
        var (tracker, holder, _, time) = Create();
        using var operation = holder.Begin("Wczytywanie…", new PageStatusLog("A"));

        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.Indeterminate, tracker.State);

        operation.Report("Wczytywanie… 40 z 80", PageProgress.Of(40, 80));
        Assert.Equal(TaskbarProgressState.Normal(0.5d), tracker.State);
    }

    [Fact]
    public void ARunThatEndsBeforeTheDelay_NeverTouchesTheButton()
    {
        var (tracker, holder, changes, time) = Create();
        using (var quick = holder.Begin("Wczytywanie…", new PageStatusLog("A"), PageProgress.Of(1, 2)))
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            quick.CompleteDone("Wczytano.", new PageStatusLog("B"));
        }

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Empty(changes);
    }

    [Fact]
    public void AnOperationThatEnds_TakesTheProgressOffTheButton_WhateverItEndsWith()
    {
        var (tracker, holder, _, time) = Create();
        using (var done = holder.Begin("Wykonywanie…", new PageStatusLog("A"), PageProgress.Of(1, 2)))
        {
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressKind.Normal, tracker.State.Kind);
            done.CompleteDone("Wykonano.", new PageStatusLog("B"));
        }

        Assert.Equal(TaskbarProgressState.None, tracker.State);

        // A result that can be closed - an attention or an error the operator may dismiss - is not a state that lasts.
        foreach (var closable in new[]
        {
            PageStatus.Attention("Zatrzymano.", new PageStatusLog("S")),
            PageStatus.Error("Nie udało się.", new PageStatusLog("E")),
        })
        {
            using var operation = holder.Begin("Wykonywanie…", new PageStatusLog("A"), PageProgress.Of(1, 2));
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressKind.Normal, tracker.State.Kind);
            operation.Complete(closable);
            Assert.Equal(TaskbarProgressState.None, tracker.State);
        }

        // Ended without a message: the summary.
        holder.SetIdle("6 obiektów");
        using (var read = holder.Begin("Wczytywanie…", new PageStatusLog("A")))
        {
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressKind.Indeterminate, tracker.State.Kind);
        }

        Assert.Equal(TaskbarProgressState.None, tracker.State);
    }

    [Fact]
    public void AnErrorThatLasts_AnOperationEndedWith_IsRedWhereItStopped_UntilThePageSaysSomethingElse()
    {
        var (tracker, holder, _, time) = Create();
        var failed = PageStatus.Error("Nie udało się wczytać „Kraków”.", new PageStatusLog("E"));
        using (var operation = holder.Begin("Wczytywanie…", new PageStatusLog("A"), PageProgress.Of(30, 100)))
        {
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressState.Normal(0.3d), tracker.State);

            // The page keeps the error as its state that lasts, and the operation ends without a message of its own.
            holder.SetLasting(failed);
            operation.Complete(null);
        }

        Assert.Equal(new TaskbarProgressState(TaskbarProgressKind.Error, 0.3d), tracker.State);

        // It lasts as long as the page shows that state - not a moment longer than the page says so.
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TaskbarProgressKind.Error, tracker.State.Kind);
        holder.SetLasting(null);
        Assert.Equal(TaskbarProgressState.None, tracker.State);
    }

    [Fact]
    public void AnAttentionThatLasts_IsYellow_AndANewOperationTakesTheButtonBack()
    {
        var (tracker, holder, _, time) = Create();
        var attention = PageStatus.Attention("Nie wszystko udało się wczytać.", new PageStatusLog("W"), canClose: false);
        using (var operation = holder.Begin("Wczytywanie…", new PageStatusLog("A")))
        {
            time.Advance(Delay);
            operation.Complete(attention);
        }

        Assert.Equal(new TaskbarProgressState(TaskbarProgressKind.Paused, 1d), tracker.State);

        // The next operation takes the button when its progress is shown, not before: until then the line says the attention too.
        using var again = holder.Begin("Wczytywanie ponownie…", new PageStatusLog("A"), PageProgress.Of(1, 4));
        Assert.Equal(TaskbarProgressKind.Paused, tracker.State.Kind);
        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.Normal(0.25d), tracker.State);
    }

    [Fact]
    public void AStateThatLasts_WithoutAnOperationBehindIt_IsNotTheButtonsBusiness()
    {
        var (tracker, holder, changes, time) = Create();

        // The session of the window ended; an object was deleted: the line says it, the button is as it was.
        holder.SetLasting(PageStatus.Error("Sesja tego okna zakończyła się.", new PageStatusLog("S"), canClose: false));
        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Empty(changes);
    }

    [Fact]
    public void AStateThatWasThereBeforeAnOperation_IsNotPutOnTheButton_WhenTheOperationEndedWell()
    {
        var (tracker, holder, _, time) = Create();

        // "Group Policy changed after these results were made": a state of the page that no operation produced.
        holder.SetLasting(PageStatus.Attention("Zasady grupy zmieniły się po utworzeniu tych wyników.", new PageStatusLog("Stale"), canClose: false));
        Assert.Equal(TaskbarProgressState.None, tracker.State);

        // An unrelated operation runs longer than the delay and ends well, without a message ("Preparing the changes…").
        using (var preparing = holder.Begin("Przygotowywanie zmian…", new PageStatusLog("Preparing")))
        {
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressState.Indeterminate, tracker.State);
            preparing.Complete(null);
        }

        // The page shows its state again; the operation did not end badly, and the button does not say it did.
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TaskbarProgressState.None, tracker.State);

        // The same for an error that lasts and an operation that counted to its end.
        holder.SetLasting(PageStatus.Error("Nie udało się wczytać szczegółów.", new PageStatusLog("E"), canClose: false));
        using (var export = holder.Begin("Eksport… 1 z 4", new PageStatusLog("X"), PageProgress.Of(1, 4)))
        {
            time.Advance(Delay);
            export.Report("Eksport… 4 z 4", PageProgress.Of(4, 4));
            export.Complete(null);
        }

        Assert.Equal(TaskbarProgressState.None, tracker.State);
    }

    [Fact]
    public void AStateAnOperationLeftOnTheButton_ComesBack_WhenTheRetryFailsTheSameWay()
    {
        var (tracker, holder, _, time) = Create();
        var failed = PageStatus.Error("Nie udało się wczytać „Kraków”.", new PageStatusLog("E"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var operation = holder.Begin("Wczytywanie…", new PageStatusLog("A"));
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressState.Indeterminate, tracker.State);
            holder.SetLasting(failed);
            operation.Complete(null);

            // Also the second time: the page showed this error before the retry, but it was an operation's, and so is this one.
            Assert.Equal(new TaskbarProgressState(TaskbarProgressKind.Error, 1d), tracker.State);
        }
    }

    [Fact]
    public void AnOperationThatFailedAtItsFirstItem_IsStillSeenOnTheButton()
    {
        var (tracker, holder, _, time) = Create();
        using (var operation = holder.Begin("Wykonywanie… 0 z 12", new PageStatusLog("A"), PageProgress.Of(0, 12)))
        {
            time.Advance(Delay);
            Assert.Equal(TaskbarProgressState.Normal(0d), tracker.State);
            operation.Complete(PageStatus.Error("Nie udało się wykonać: brak połączenia.", new PageStatusLog("E"), canClose: false));
        }

        // Red at nothing is drawn as no state at all: the button keeps a part that can be seen.
        Assert.Equal(new TaskbarProgressState(TaskbarProgressKind.Error, TaskbarProgressTracker.MinimumLatchedFraction), tracker.State);
    }

    [Fact]
    public void AnOperationThatFailedBeforeTheButtonShowedIt_LeavesItToTheLine()
    {
        var (tracker, holder, changes, time) = Create();
        using (var quick = holder.Begin("Wczytywanie…", new PageStatusLog("A")))
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            holder.SetLasting(PageStatus.Error("Nie udało się wczytać.", new PageStatusLog("E")));
            quick.Complete(null);
        }

        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Empty(changes);
    }

    [Fact]
    public void Stopping_IsIndeterminate_AndAStoppedResultThatCanBeClosedEndsIt()
    {
        var (tracker, holder, _, time) = Create();
        using var operation = holder.Begin("Wykonywanie…", new PageStatusLog("A"), PageProgress.Of(12, 300));
        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.Normal(0.04d), tracker.State);

        holder.CancelCommand.Execute(null);
        Assert.Equal(TaskbarProgressState.Indeterminate, tracker.State);

        operation.CompleteAttention("Zatrzymano.", new PageStatusLog("S"));
        Assert.Equal(TaskbarProgressState.None, tracker.State);
    }

    [Fact]
    public void ThePageInFront_IsTheOneThatCounts_AndAnotherHolderStartsFromWhatItSays()
    {
        var window = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var page = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var other = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var time = new FakeTimeProvider();
        using var tracker = new TaskbarProgressTracker(new SynchronousUiDispatcher(), time, () => Delay) { Holder = window };
        window.Front = page;

        using var running = page.Begin("Wczytywanie…", new PageStatusLog("A"), PageProgress.Of(1, 2));
        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.Normal(0.5d), tracker.State);

        // The window follows another holder (the main window: another tab): what was on the button for the first one goes.
        tracker.Holder = other;
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        using var elsewhere = other.Begin("Zapisywanie…", new PageStatusLog("B"));
        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.Indeterminate, tracker.State);

        // A holder that is working when it is handed over shows it after the delay, as it would have if it had started then.
        tracker.Holder = page;
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        time.Advance(Delay);
        Assert.Equal(TaskbarProgressState.Normal(0.5d), tracker.State);

        tracker.Holder = null;
        Assert.Equal(TaskbarProgressState.None, tracker.State);
    }

    [Fact]
    public void WithoutADelay_ItAppearsAtOnce_AndADisposedTrackerSaysNothingMore()
    {
        var time = new FakeTimeProvider();
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var tracker = new TaskbarProgressTracker(new SynchronousUiDispatcher(), time, () => null) { Holder = holder };
        var changes = new List<TaskbarProgressState>();
        tracker.PropertyChanged += (_, _) => changes.Add(tracker.State);

        using var operation = holder.Begin("Wczytywanie…", new PageStatusLog("A"), PageProgress.Of(1, 4));
        Assert.Equal(TaskbarProgressState.Normal(0.25d), tracker.State);

        tracker.Dispose();
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        operation.Report("Wczytywanie…", PageProgress.Of(3, 4));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Equal([TaskbarProgressState.Normal(0.25d), TaskbarProgressState.None], changes);
    }

    [Fact]
    public void ATimerThatFiresAfterTheOperationEnded_ChangesNothing()
    {
        var (tracker, holder, changes, time) = Create();
        var operation = holder.Begin("Wczytywanie…", new PageStatusLog("A"));
        time.Advance(Delay - TimeSpan.FromMilliseconds(10));
        operation.Dispose();
        holder.SetIdle("6 obiektów");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TaskbarProgressState.None, tracker.State);
        Assert.Empty(changes);
    }

    private static (TaskbarProgressTracker Tracker, PageStatusHolder Holder, List<TaskbarProgressState> Changes, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var tracker = new TaskbarProgressTracker(new SynchronousUiDispatcher(), time, () => Delay) { Holder = holder };
        var changes = new List<TaskbarProgressState>();
        tracker.PropertyChanged += (_, _) => changes.Add(tracker.State);
        return (tracker, holder, changes, time);
    }
}
