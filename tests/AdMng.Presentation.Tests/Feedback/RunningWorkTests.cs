using System.Globalization;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// The one question of a window that is closed while it works (track V, stage V2b-5b, the owner's decision 6; design note
/// docs/dev/v-interface-renewal.md "V2b-5b as built"): only an operation that writes is asked about, Yes stops it through the
/// holder's own Stop and the window closes after it ended, No keeps the window, an operation that cannot be stopped keeps the
/// window and says so, and a read closes without a question. Everything is the view-model level - no WPF type.
/// </summary>
public class RunningWorkTests
{
    private const string Running = "Wykonywanie „Wyłącz 120 kont” w contoso.test: 12 z 300…";

    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task ARead_ClosesWithoutAQuestion_AndIsLeftToItsOwner()
    {
        var (holder, dialogs) = Create();
        using var read = holder.Begin("Wczytywanie „Kraków”…", new PageStatusLog(L.ListStatus.Reading));

        Assert.True(await Ask(dialogs, holder));

        // Nothing was said and nothing was stopped here: the window cancels its reads itself when it is disposed.
        Assert.Empty(dialogs.Shown);
        Assert.False(read.IsCancelling);
        Assert.False(read.Token.IsCancellationRequested);
        Assert.False(read.IsWrite);
    }

    [Fact]
    public async Task AWindowThatDoesNothing_ClosesWithoutAQuestion()
    {
        var (holder, dialogs) = Create();
        holder.SetIdle("6 obiektów");

        Assert.True(await Ask(dialogs, holder));
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task AWriteThatCanBeStopped_IsAskedAbout_AndYesStopsItAndClosesAfterItEnded()
    {
        var (holder, dialogs) = Create();
        using var execution = holder.Begin(Running, new PageStatusLog(L.ChangeStatus.Executing), canCancel: true, writes: OperationWrites.Changes);
        dialogs.ConfirmAnswers.Enqueue(true);

        var closing = Ask(dialogs, holder);

        // The question says what runs and what the stop does (the object in progress is finished first).
        var question = Assert.Single(dialogs.Shown);
        Assert.Equal(
            "confirm:" + Running + "\n\nZatrzymać trwającą operację i zamknąć okno? Nic nowego nie zostanie rozpoczęte, obiekt, który jest właśnie "
            + "zmieniany, zostanie najpierw dokończony (żaden nie zostanie w połowie), a obiekty, do których operacja nie dotarła, pozostaną bez zmian.",
            question);

        // Yes is the holder's own Stop: the line says "Stopping…" and the token is cancelled - and the window waits for the end.
        Assert.True(execution.IsCancelling);
        Assert.True(execution.Token.IsCancellationRequested);
        Assert.Equal("Zatrzymywanie…", holder.Current.Message);
        Assert.False(closing.IsCompleted);

        // The operation finishes the object it is at and ends the way it ends a Stop: only then the window closes.
        execution.CompleteAttention("Wykonywanie „Wyłącz 120 kont” zatrzymano.", new PageStatusLog(L.ChangeStatus.Stopped, 12, 0, 1));
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(execution.IsEnded);
    }

    [Fact]
    public async Task No_KeepsTheWindow_AndTheOperationKeepsRunning()
    {
        var (holder, dialogs) = Create();
        using var execution = holder.Begin(Running, new PageStatusLog(L.ChangeStatus.Executing), writes: OperationWrites.Changes);
        dialogs.ConfirmAnswers.Enqueue(false);

        Assert.False(await Ask(dialogs, holder));

        Assert.Single(dialogs.Shown);
        Assert.False(execution.IsCancelling);
        Assert.False(execution.Token.IsCancellationRequested);
        Assert.Equal(Running, holder.Current.Message);
        Assert.True(holder.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task AWriteThatWasStoppedAlready_IsNotAskedAboutAgain_TheWindowWaitsForItsEnd()
    {
        var (holder, dialogs) = Create();
        using var execution = holder.Begin(Running, new PageStatusLog(L.ChangeStatus.Executing), writes: OperationWrites.Changes);
        holder.CancelCommand.Execute(null);
        Assert.True(execution.IsCancelling);

        var closing = Ask(dialogs, holder);

        Assert.Empty(dialogs.Shown);
        Assert.False(closing.IsCompleted);
        execution.Complete(null);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWriteThatCannotBeStopped_KeepsTheWindowOpen_AndSaysItIsStillWorking()
    {
        var (holder, dialogs) = Create();
        using var saving = holder.Begin(
            "Zapisywanie „Jan Kowalski”…", new PageStatusLog(L.PropertySheetStatus.Saving), canCancel: false, writes: OperationWrites.Changes);

        Assert.False(await Ask(dialogs, holder));

        // Not a question - nothing is asked, nothing is stopped: the window cannot be closed until it ends, and it says so.
        Assert.Equal(
            "info:Zapisywanie „Jan Kowalski”…\n\nTej operacji nie można zatrzymać po jej rozpoczęciu. Poczekaj, aż się zakończy, i zamknij okno.",
            Assert.Single(dialogs.Shown));
        Assert.False(saving.IsCancelling);
        Assert.False(saving.Token.IsCancellationRequested);

        // When it ends the window closes without a question.
        saving.CompleteDone("Zapisano „Jan Kowalski”.", new PageStatusLog(L.PropertySheetStatus.Saving));
        dialogs.Shown.Clear();
        Assert.True(await Ask(dialogs, holder));
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task TheHolderInFront_IsFollowed_ThroughTheWholeChain()
    {
        var window = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        var page = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        var part = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        window.Front = page;
        page.Front = part;
        using var write = part.Begin("Zapisywanie…", new PageStatusLog(L.WatchdogConfigStatus.Saving), canCancel: true, writes: OperationWrites.Other);
        var dialogs = new ScriptedDialogService();
        dialogs.ConfirmAnswers.Enqueue(true);

        var closing = RunningWork.ConfirmCloseAsync(Polish, dialogs, "Okno", RunningWork.HoldersOf(new WindowOf(window)));

        Assert.Contains("To, co już zapisano, pozostanie", Assert.Single(dialogs.Shown));
        Assert.True(write.IsCancelling);
        write.Complete(null);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APageThatIsNotInFront_IsAskedAboutToo_WhenTheViewModelListsIt()
    {
        // The Korespondencja window: the send runs on its page while the operator looks at another tab.
        var window = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        var recipients = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        var send = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        window.Front = recipients;
        using var sending = send.Begin("Wysyłam wiadomości: 3 z 40…", new PageStatusLog(L.CorrespondenceSend.ProgressStarting), writes: OperationWrites.Messages);
        var dialogs = new ScriptedDialogService();
        dialogs.ConfirmAnswers.Enqueue(true);

        var closing = RunningWork.ConfirmCloseAsync(Polish, dialogs, "Korespondencja", RunningWork.HoldersOf(new WindowOf(window, recipients, send)));

        Assert.Equal(
            "confirm:Wysyłam wiadomości: 3 z 40…\n\nZatrzymać trwającą operację i zamknąć okno? Wiadomości, które jeszcze nie zostały wysłane, "
            + "nie zostaną wysłane; wiadomości przyjętych już przez serwer lub przez Outlooka nie da się wycofać.",
            Assert.Single(dialogs.Shown));
        sending.Complete(null);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeveralWrites_AreOneQuestion_AndAllAreStopped()
    {
        var one = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        var two = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        using var first = one.Begin("Pierwsza…", new PageStatusLog("A"), writes: OperationWrites.Other);
        using var second = two.Begin("Druga…", new PageStatusLog("B"), writes: OperationWrites.Changes);
        var dialogs = new ScriptedDialogService();
        dialogs.ConfirmAnswers.Enqueue(true);

        var closing = RunningWork.ConfirmCloseAsync(Polish, dialogs, "Okno", [one, two]);

        // One question, the strongest sentence (an execution), what runs one under another.
        var question = Assert.Single(dialogs.Shown);
        Assert.StartsWith("confirm:Pierwsza…\nDruga…\n\nZatrzymać trwającą operację", question, StringComparison.Ordinal);
        Assert.Contains("dokończony", question, StringComparison.Ordinal);
        Assert.True(first.IsCancelling);
        Assert.True(second.IsCancelling);
        first.Complete(null);
        Assert.False(closing.IsCompleted);
        second.Complete(null);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWriteThatAnotherOperationTookOffTheLine_IsStillAWrite_UntilItsCodeEndedIt()
    {
        var (holder, dialogs) = Create();
        using var first = holder.Begin("Zapisywanie…", new PageStatusLog("A"), writes: OperationWrites.Other);
        dialogs.ConfirmAnswers.Enqueue(true);
        var closing = Ask(dialogs, holder);

        // A read takes the page: the write is over for the line (its token is cancelled, what it says is ignored)...
        using var next = holder.Begin("Wczytywanie…", new PageStatusLog("B"));
        Assert.True(first.IsEnded);
        Assert.Same(next, holder.Operation);

        // ...but its code still runs, and the window must not close over it.
        Assert.False(first.IsFinished);
        Assert.True(RunningWork.HasRunningWrites(new WindowOf(holder)));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(closing.IsCompleted);

        first.CompleteDone("Zapisano.", new PageStatusLog("A"));
        Assert.True(first.IsFinished);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(RunningWork.HasRunningWrites(new WindowOf(holder)));

        // What the replaced write said at its end is not the page's: the read still has the line.
        Assert.Equal("Wczytywanie…", holder.Current.Message);
    }

    [Fact]
    public async Task AWriteThatCannotBeStopped_AndLostTheLineToARead_StillKeepsTheWindowOpen()
    {
        // The acknowledgement of events is on its way (it cannot be stopped) and a refresh takes the line: the title bar
        // must still say "wait" - a line that shows the read is no proof that nothing writes.
        var (holder, dialogs) = Create();
        using var write = holder.Begin("Potwierdzanie 3 zdarzeń…", new PageStatusLog("A"), canCancel: false, writes: OperationWrites.Other);
        using (holder.Begin("Wczytywanie zdarzeń…", new PageStatusLog("B")))
        {
            Assert.False(await Ask(dialogs, holder));
            Assert.StartsWith("info:Potwierdzanie 3 zdarzeń…", Assert.Single(dialogs.Shown), StringComparison.Ordinal);
            Assert.False(write.IsFinished);
        }

        dialogs.Shown.Clear();
        write.Complete(null);
        Assert.True(await Ask(dialogs, holder));
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task AWriteThatBeginsWhileTheQuestionIsOpen_IsAskedAboutAndWaitedForToo()
    {
        var (holder, dialogs) = Create();
        var first = holder.Begin("Krok 1…", new PageStatusLog("A"), writes: OperationWrites.Changes);
        OperationScope? second = null;
        dialogs.BeforeConfirmAnswer = () =>
        {
            // While the operator reads the question the first step ends and the code that ran it starts its second write.
            dialogs.BeforeConfirmAnswer = null;
            first.Complete(null);
            second = holder.Begin("Krok 2…", new PageStatusLog("B"), writes: OperationWrites.Changes);
            return Task.CompletedTask;
        };
        dialogs.ConfirmAnswers.Enqueue(true);
        dialogs.ConfirmAnswers.Enqueue(true);

        var closing = Ask(dialogs, holder);

        // The answer was about step 1. Step 2 is asked about in its own right, stopped, and waited for.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(closing.IsCompleted);
        Assert.Equal(2, dialogs.Shown.Count);
        Assert.Contains("Krok 2…", dialogs.Shown[1], StringComparison.Ordinal);
        Assert.True(second!.IsCancelling);
        second.Complete(null);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_ToTheSecondQuestion_KeepsTheWindow()
    {
        var (holder, dialogs) = Create();
        var first = holder.Begin("Krok 1…", new PageStatusLog("A"), writes: OperationWrites.Changes);
        OperationScope? second = null;
        dialogs.BeforeConfirmAnswer = () =>
        {
            dialogs.BeforeConfirmAnswer = null;
            first.Complete(null);
            second = holder.Begin("Krok 2…", new PageStatusLog("B"), writes: OperationWrites.Changes);
            return Task.CompletedTask;
        };
        dialogs.ConfirmAnswers.Enqueue(true);
        dialogs.ConfirmAnswers.Enqueue(false);

        Assert.False(await Ask(dialogs, holder).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(second!.IsCancelling);
        second.Dispose();
    }

    [Fact]
    public void TheWordsOfTheQuestion_ExistInBothLanguages_ForEveryKindOfWrite()
    {
        foreach (var key in new[] { L.CloseWork.StopChanges, L.CloseWork.StopMessages, L.CloseWork.StopOther, L.CloseWork.Wait })
        {
            foreach (var localizer in new[] { Polish, English })
            {
                var text = localizer.Format(key, "Co trwa");
                Assert.StartsWith("Co trwa\n\n", text, StringComparison.Ordinal);
                Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
            }
        }

        Assert.StartsWith(
            "Co trwa\n\nStop the running operation and close the window?", English.Format(L.CloseWork.StopOther, "Co trwa"), StringComparison.Ordinal);
        Assert.Contains("finished first", English.Format(L.CloseWork.StopChanges, "x"), StringComparison.Ordinal);
        Assert.Contains("cannot be recalled", English.Format(L.CloseWork.StopMessages, "x"), StringComparison.Ordinal);
        Assert.Contains("Wait until it ends", English.Format(L.CloseWork.Wait, "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void HoldersOf_NamesEachHolderOnce_AndIgnoresWhatHasNone()
    {
        var shared = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        var window = new PageStatusHolder(Polish, new SynchronousUiDispatcher());
        window.Front = shared;

        var holders = RunningWork.HoldersOf(new WindowOf(window, shared, window), "not a view model", null);

        Assert.Equal([window, shared], holders);
        Assert.Empty(RunningWork.HoldersOf());
    }

    [Fact]
    public void ExecutionStatus_IsAWrite_ThatWindowsDoNotHaveToSay()
    {
        var (holder, _) = Create();
        using var execution = ExecutionStatus.Begin(holder, Polish, "Wyłącz 120 kont", "contoso.test", 120);

        var operation = Assert.IsType<OperationScope>(holder.Operation);
        Assert.Equal(OperationWrites.Changes, operation.Writes);
        Assert.True(operation.IsWrite);
        Assert.True(operation.CanCancel);
        Assert.True(RunningWork.HasRunningWrites(new WindowOf(holder)));
    }

    [Fact]
    public void ARead_IsNotAWrite_ByDefault()
    {
        var (holder, _) = Create();
        using var read = holder.Begin("Wczytywanie…", new PageStatusLog(L.ListStatus.Reading));

        Assert.Equal(OperationWrites.None, read.Writes);
        Assert.False(RunningWork.HasRunningWrites(new WindowOf(holder)));
    }

    private static (PageStatusHolder Holder, ScriptedDialogService Dialogs) Create() =>
        (new PageStatusHolder(Polish, new SynchronousUiDispatcher()), new ScriptedDialogService());

    private static Task<bool> Ask(ScriptedDialogService dialogs, PageStatusHolder holder) =>
        RunningWork.ConfirmCloseAsync(Polish, dialogs, "Okno", RunningWork.HoldersOf(new WindowOf(holder)));

    /// <summary>A window's view model: its own holder and, when it has pages that are not in front, the holders of those.</summary>
    private sealed class WindowOf(PageStatusHolder status, params PageStatusHolder[] parts) : IHasPageStatus, IHasPageStatusParts
    {
        public PageStatusHolder Status { get; } = status;

        public IEnumerable<PageStatusHolder> StatusParts => parts;
    }
}
