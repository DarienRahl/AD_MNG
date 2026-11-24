using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// The one question of a window that is closed while it works (track V, stage V2b-5b, the owner's decisions 6 and D-A; design
/// note docs/dev/v-interface-renewal.md "V2b-5b as built" and "D-A repair as built"): only an operation that writes is asked
/// about; "Stop and close" stops it through the holder's own Stop and the window closes after it ended; "Close and continue in
/// the background" - offered only for the execution of a plan that may go on without the window - lets the run leave the
/// window at once; "Don't close" keeps the window; an operation that cannot be stopped keeps the window and says so; and a read
/// closes without a question. Everything is the view-model level - no WPF type.
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
    public async Task StopAndClose_StopsAndClosesOnlyAfterTheCodeEnded()
    {
        var (holder, dialogs) = Create();
        using var execution = holder.Begin(Running, new PageStatusLog(L.ChangeStatus.Executing), canCancel: true, writes: OperationWrites.Changes);
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

        var closing = Ask(dialogs, holder);

        // The question says what runs and what the stop does (the object in progress is finished first). A write that is not the
        // execution of a plan a window follows cannot go on without the window: two answers.
        var question = Assert.Single(dialogs.Shown);
        Assert.Equal(
            "close:" + Running + "\n\nZatrzymać trwającą operację i zamknąć okno? Nic nowego nie zostanie rozpoczęte, obiekt, który jest właśnie "
            + "zmieniany, zostanie najpierw dokończony (żaden nie zostanie w połowie), a obiekty, do których operacja nie dotarła, pozostaną bez zmian.",
            question);
        Assert.False(Assert.Single(dialogs.CloseQuestions).CanContinue);

        // Stop and close is the holder's own Stop: the line says "Stopping…" and the token is cancelled - and the window waits for the end.
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
    public async Task DontClose_KeepsTheWindowAndTheRun()
    {
        var (holder, dialogs) = Create();
        using var execution = holder.Begin(Running, new PageStatusLog(L.ChangeStatus.Executing), writes: OperationWrites.Changes);
        dialogs.CloseAnswers.Enqueue(CloseAnswer.KeepOpen);

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
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

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
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

        var closing = RunningWork.ConfirmCloseAsync(Polish, dialogs, "Korespondencja", RunningWork.HoldersOf(new WindowOf(window, recipients, send)));

        Assert.Equal(
            "close:Wysyłam wiadomości: 3 z 40…\n\nZatrzymać trwającą operację i zamknąć okno? Wiadomości, które jeszcze nie zostały wysłane, "
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
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

        var closing = RunningWork.ConfirmCloseAsync(Polish, dialogs, "Okno", [one, two]);

        // One question, the strongest sentence (an execution), what runs one under another.
        var question = Assert.Single(dialogs.Shown);
        Assert.StartsWith("close:Pierwsza…\nDruga…\n\nZatrzymać trwającą operację", question, StringComparison.Ordinal);
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
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
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
        dialogs.BeforeCloseAnswer = () =>
        {
            // While the operator reads the question the first step ends and the code that ran it starts its second write.
            dialogs.BeforeCloseAnswer = null;
            first.Complete(null);
            second = holder.Begin("Krok 2…", new PageStatusLog("B"), writes: OperationWrites.Changes);
            return Task.CompletedTask;
        };
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

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
    public async Task DontClose_ToTheSecondQuestion_KeepsTheWindow()
    {
        var (holder, dialogs) = Create();
        var first = holder.Begin("Krok 1…", new PageStatusLog("A"), writes: OperationWrites.Changes);
        OperationScope? second = null;
        dialogs.BeforeCloseAnswer = () =>
        {
            dialogs.BeforeCloseAnswer = null;
            first.Complete(null);
            second = holder.Begin("Krok 2…", new PageStatusLog("B"), writes: OperationWrites.Changes);
            return Task.CompletedTask;
        };
        dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
        dialogs.CloseAnswers.Enqueue(CloseAnswer.KeepOpen);

        Assert.False(await Ask(dialogs, holder).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(second!.IsCancelling);
        second.Dispose();
    }

    // ---- The execution of a plan that may go on without the window (the owner's decision D-A) ----------------------------

    [Fact]
    public async Task AnExecutionThatMayGoOn_IsAskedWithThreeAnswers_InBothLanguages()
    {
        foreach (var culture in new[] { "en-US", "pl-PL" })
        {
            var run = await RunAsync(culture, title: culture == "pl-PL" ? "Wyłącz 3 konta" : "Disable 3 accounts");
            var closing = Ask(run.Dialogs, run.Holder, run.Localizer);

            var question = Assert.Single(run.Dialogs.CloseQuestions);
            Assert.True(question.CanContinue);
            var what = culture == "pl-PL"
                ? "Wykonywanie „Wyłącz 3 konta” w domenie contoso.test: 0 z 3…"
                : "Executing “Disable 3 accounts” in contoso.test: 0 of 3…";
            Assert.Equal(run.Localizer.Format(L.CloseWork.Changes, what), question.Message);
            Assert.StartsWith(what + "\n\n", question.Message, StringComparison.Ordinal);
            Assert.False(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await run.EndAsync();
        }

        Assert.Equal(
            "Executing “x” in contoso.test: 0 of 3…\n\nThe window is executing changes.\nStop and close: nothing new is started, the object being changed is "
            + "finished first (none is left half done), the objects not reached stay unchanged, and the window closes when the execution has ended.\n"
            + "Close and continue in the background: the window closes now and the execution goes on to its end. Its progress is in the list of "
            + "background operations, its outcome is shown in a notification (failures in a dialog with their reasons), and every change goes to the "
            + "session log.",
            English.Format(L.CloseWork.Changes, "Executing “x” in contoso.test: 0 of 3…"));
        Assert.Equal(
            "x\n\nOkno wykonuje zmiany.\nZatrzymaj i zamknij: nic nowego nie zostanie rozpoczęte, obiekt, który jest właśnie zmieniany, zostanie "
            + "najpierw dokończony (żaden nie zostanie przerwany w połowie), obiekty, do których wykonanie nie dotarło, pozostaną bez zmian, a okno "
            + "zamknie się po zakończeniu wykonania.\nZamknij i kontynuuj w tle: okno zamknie się od razu, a wykonanie potrwa do końca. Jego postęp "
            + "będzie widać na liście operacji w tle, wynik pojawi się jako powiadomienie (niepowodzenia – w oknie z przyczynami), a każda zmiana "
            + "trafi do dziennika sesji.",
            Polish.Format(L.CloseWork.Changes, "x"));

        // The labelled buttons, in both languages.
        Assert.Equal(["Stop and close", "Close and continue in the background", "Don't close"], ButtonTexts(English));
        Assert.Equal(["Zatrzymaj i zamknij", "Zamknij i kontynuuj w tle", "Nie zamykaj"], ButtonTexts(Polish));
    }

    [Fact]
    public async Task CloseAndContinue_ClosesAtOnce_TheRunLeavesTheWindow_NothingIsStopped()
    {
        var run = await RunAsync();
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.CloseAndContinue);
        var operation = run.Holder.Operation!;

        Assert.True(await Ask(run.Dialogs, run.Holder, run.Localizer).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // The window closes at once: the run left it - the holder no longer counts the write and its line is at rest - and
        // nothing was stopped: the activity runs on, in the list of background operations, without a window.
        Assert.True(run.Execution.LeftWindow);
        Assert.Empty(RunningWork.RunningWrites([run.Holder]));
        Assert.False(run.Holder.IsWorking);
        Assert.False(operation.Token.IsCancellationRequested);
        Assert.False(run.Work.Token.IsCancellationRequested);
        Assert.True(run.Activity.IsRunning);
        Assert.False(run.Activity.IsCancelling);
        Assert.False(run.Activity.HasWindow);
        Assert.True(run.Activity.HasLeftWindow);
        Assert.Same(run.Activity, Assert.Single(run.Harness.Activities.RunningInBackground));
        await run.EndAsync();
    }

    [Fact]
    public async Task StopAndClose_OfARunThatMayLeave_AsksTheWindowFirst_StopsIt_AndTheConnectionSaysHowItEnded()
    {
        var run = await RunAsync();
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
        var asked = 0;

        var closing = RunningWork.ConfirmCloseAsync(run.Localizer, run.Dialogs, "Okno", [run.Holder], ownQuestions: () =>
        {
            // The window's own question comes before anything is stopped: after the stop the window closes without a word.
            asked++;
            Assert.False(run.Activity.IsCancelling);
            return Task.FromResult(true);
        });

        Assert.Equal(1, asked);
        Assert.True(run.Activity.IsCancelling);
        Assert.True(run.Activity.ReportsByConnection);
        Assert.True(run.Execution.ReportsByConnection);
        Assert.False(run.Execution.LeftWindow);
        Assert.Equal("Stopping…", run.Holder.Current.Message);
        Assert.False(closing.IsCompleted);

        // The window's code ends the execution the way it ends a Stop; only then the window closes - and asks nothing more.
        await run.EndAsync(complete: true);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData(L.CloseWork.NoBackgroundPasswords, "It cannot continue in the background: the generated passwords exist only in this window.")]
    [InlineData(L.CloseWork.NoBackgroundCaller, "It cannot continue in the background: the window this one was opened from is waiting for its result.")]
    [InlineData(
        L.CloseWork.NoBackgroundFirstAccount,
        "It can continue in the background once the first account has been created: this window starts creating the other accounts.")]
    public async Task ARunThatMustStay_OffersStopAndDontCloseOnly_AndSaysWhy(string reasonKey, string reason)
    {
        var run = await RunAsync();
        run.Execution.StaysWithWindow(reasonKey);
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.CloseAndContinue);

        Assert.False(await Ask(run.Dialogs, run.Holder, run.Localizer));

        // Two answers, and why the third is missing - under what the stop does.
        var question = run.Dialogs.CloseQuestions[0];
        Assert.False(question.CanContinue);
        Assert.Equal(
            run.Localizer.Format(L.CloseWork.StopChanges, "Executing “Disable 3 accounts” in contoso.test: 0 of 3…") + "\n\n" + reason,
            question.Message);

        // An answer that was not offered is not honoured: it is asked again, and Don't close keeps the window.
        Assert.False(run.Execution.LeftWindow);
        Assert.True(run.Activity.HasWindow);
        Assert.Equal(2, run.Dialogs.CloseQuestions.Count);
        Assert.False(run.Dialogs.CloseQuestions[1].CanContinue);
        Assert.False(run.Activity.IsCancelling);
        await run.EndAsync();
    }

    [Fact]
    public void TheReasons_ExistInPolish_AndAgreeWithTheQuestion()
    {
        // "Zatrzymać trwającą operację…?" - the reason that follows speaks of "it" in the feminine: "jej".
        Assert.Equal("Nie można jej kontynuować w tle: wygenerowane hasła istnieją tylko w tym oknie.", Polish[L.CloseWork.NoBackgroundPasswords]);
        Assert.Equal("Nie można jej kontynuować w tle: okno, z którego je otwarto, czeka na wynik.", Polish[L.CloseWork.NoBackgroundCaller]);
        Assert.Equal(
            "Kontynuowanie w tle będzie możliwe po utworzeniu pierwszego konta: tworzenie pozostałych kont rozpoczyna to okno.",
            Polish[L.CloseWork.NoBackgroundFirstAccount]);
        Assert.Equal("Zamknięcie tego okna kończy jego połączenie, więc nic nie może być kontynuowane w tle.", Polish[L.CloseWork.NoBackgroundConnection]);
    }

    [Theory]
    [InlineData(CloseAnswer.StopAndClose)]
    [InlineData(CloseAnswer.CloseAndContinue)]
    public async Task AnExecutionThatEndedWhileTheQuestionWasOpen_KeepsTheWindow(CloseAnswer answer)
    {
        var run = await RunAsync();
        run.Dialogs.CloseAnswers.Enqueue(answer);
        run.Dialogs.BeforeCloseAnswer = () => run.EndAsync(complete: true);

        Assert.False(await Ask(run.Dialogs, run.Holder, run.Localizer).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // The result came back to the window while the operator read the question: the window stays, and its line says how it ended.
        Assert.False(run.Execution.LeftWindow);
        Assert.False(run.Activity.ReportsByConnection);
        Assert.Equal(PageStatusKind.Done, run.Holder.Current.Kind);
        Assert.Equal("“Disable 3 accounts” was executed in contoso.test: 0 objects changed. The report is in the list of background operations.", run.Holder.Current.Message);
        Assert.Single(run.Dialogs.CloseQuestions);
    }

    [Fact]
    public async Task TheWindowsOwnQuestions_ComeBeforeLeaving()
    {
        var run = await RunAsync();
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.CloseAndContinue);

        Assert.False(await RunningWork.ConfirmCloseAsync(run.Localizer, run.Dialogs, "Okno", [run.Holder], ownQuestions: () => Task.FromResult(false)));

        // Declined by the window's own question (generated passwords of an earlier run, unsaved changes): the window stays and
        // still follows the run.
        Assert.False(run.Execution.LeftWindow);
        Assert.True(run.Activity.HasWindow);
        Assert.True(run.Holder.IsWorking);
        Assert.False(run.Activity.IsCancelling);
        await run.EndAsync();
    }

    [Fact]
    public async Task AWriteWithoutRun_NextToAnExecution_RemovesTheBackgroundAnswer()
    {
        var run = await RunAsync();
        var other = new PageStatusHolder(run.Localizer, run.Harness.Dispatcher);
        using var saving = other.Begin("Saving…", new PageStatusLog("A"), writes: OperationWrites.Other);
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

        var closing = RunningWork.ConfirmCloseAsync(run.Localizer, run.Dialogs, "Okno", [run.Holder, other]);

        // The other write has no run that could go on: two answers, the sentence of an execution, both stopped.
        var question = Assert.Single(run.Dialogs.CloseQuestions);
        Assert.False(question.CanContinue);
        Assert.Equal(run.Localizer.Format(L.CloseWork.StopChanges, "Executing “Disable 3 accounts” in contoso.test: 0 of 3…\nSaving…"), question.Message);
        Assert.True(saving.IsCancelling);
        Assert.True(run.Activity.IsCancelling);

        // The run could have left alone: the connection says how it ended, because its line goes with the window.
        Assert.True(run.Activity.ReportsByConnection);
        saving.Complete(null);
        await run.EndAsync(complete: true);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWriteThatCannotBeStopped_StillGetsTheNotice()
    {
        var run = await RunAsync();
        var sheet = new PageStatusHolder(run.Localizer, run.Harness.Dispatcher);
        using var saving = sheet.Begin("Saving “Jan Kowalski”…", new PageStatusLog("A"), canCancel: false, writes: OperationWrites.Changes);

        Assert.False(await RunningWork.ConfirmCloseAsync(run.Localizer, run.Dialogs, "Okno", [run.Holder, sheet]));

        Assert.Empty(run.Dialogs.CloseQuestions);
        Assert.Equal(
            "info:" + run.Localizer.Format(L.CloseWork.Wait, "Executing “Disable 3 accounts” in contoso.test: 0 of 3…\nSaving “Jan Kowalski”…"),
            Assert.Single(run.Dialogs.Shown));
        Assert.True(run.Activity.HasWindow);
        Assert.False(run.Activity.IsCancelling);
        await run.EndAsync();
    }

    [Fact]
    public async Task AnExecutionThatLostTheLine_IsStoppedThroughItsRun()
    {
        var run = await RunAsync();

        // Another operation of the window takes the line (a read): the execution's operation is over for the line, its token is
        // cancelled - which is not a Stop - and its activity runs on.
        using var read = run.Holder.Begin("Reading…", new PageStatusLog("B"));
        Assert.False(run.Activity.IsCancelling);
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

        var closing = Ask(run.Dialogs, run.Holder, run.Localizer);

        // It is still asked about, and Stop and close stops its activity - the window does not wait for the whole execution.
        Assert.Single(run.Dialogs.CloseQuestions);
        Assert.True(run.Activity.IsCancelling);
        Assert.True(run.Work.Token.IsCancellationRequested);
        await run.EndAsync(complete: true);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheConnection_ListsBackgroundActivities_NeverOffersToContinue_StopWaitsForThem()
    {
        // The connection window: an execution its batch editor follows, and one that left its window - plus a change of a simple
        // action that no window ever followed.
        var run = await RunAsync();
        var left = await RunAsync(harness: run.Harness, title: "Move 2 accounts");
        Assert.True(left.Execution.LeaveWindow());
        var applied = new Work();
        var (simple, simpleRun) = run.Harness.Activities.Start("Unlock account", 1, applied.RunAsync);
        await applied.Started;
        run.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);

        var closing = RunningWork.ConfirmCloseAsync(run.Localizer, run.Dialogs, "contoso.test", [run.Holder], run.Harness.Activities);

        // One question: the window's execution, then what runs in the background - and why nothing can continue there.
        var question = Assert.Single(run.Dialogs.CloseQuestions);
        Assert.False(question.CanContinue);
        Assert.Equal(
            run.Localizer.Format(
                L.CloseWork.StopChanges,
                "Executing “Disable 3 accounts” in contoso.test: 0 of 3…\nIn the background: “Unlock account”, 0 / 1\nIn the background: “Move 2 accounts”, 0 / 3")
            + "\n\nClosing this window ends its connection, so nothing can continue in the background.",
            question.Message);

        // Stop and close stops all of them; the background ones are not reported (the connection goes), and the window waits.
        Assert.True(run.Activity.IsCancelling);
        Assert.True(left.Activity.IsCancelling);
        Assert.True(simple.IsCancelling);
        Assert.True(left.Activity.IsReportSuppressed);
        Assert.True(simple.IsReportSuppressed);
        Assert.False(run.Activity.ReportsByConnection);
        await run.EndAsync(complete: true);
        await left.EndAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(closing.IsCompleted);
        applied.End(Result());
        await simpleRun;
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheWordsOfTheQuestion_ExistInBothLanguages_ForEveryKindOfWrite()
    {
        foreach (var key in new[] { L.CloseWork.StopChanges, L.CloseWork.StopMessages, L.CloseWork.StopOther, L.CloseWork.Wait, L.CloseWork.Changes })
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
        Assert.Same(execution, operation.Run);
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

    private static Task<bool> Ask(ScriptedDialogService dialogs, PageStatusHolder holder, ILocalizer? localizer = null) =>
        RunningWork.ConfirmCloseAsync(localizer ?? Polish, dialogs, "Okno", RunningWork.HoldersOf(new WindowOf(holder)));

    private static string[] ButtonTexts(ILocalizer localizer) =>
        [localizer[L.CloseWork.StopAndClose], localizer[L.CloseWork.CloseAndContinue], localizer[L.CloseWork.KeepOpen]];

    /// <summary>
    /// A window that executes a plan: its holder, the execution it says in its line, and the activity of the connection that
    /// runs it, whose work the test ends.
    /// </summary>
    private static async Task<ExecutionRun> RunAsync(string culture = "en-US", B2Harness? harness = null, string title = "Disable 3 accounts")
    {
        harness ??= await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var holder = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        var execution = ExecutionStatus.Begin(holder, harness.Localizer, title, "contoso.test", 3);
        var work = new Work();
        var (activity, task) = harness.Activities.Start(title, 3, work.RunAsync);
        await work.Started;
        execution.Follow(activity);
        harness.Dialogs.Shown.Clear();
        return new ExecutionRun(harness, holder, execution, activity, work, task);
    }

    private static BatchResult Result()
    {
        var at = DateTimeOffset.UnixEpoch;
        return new BatchResult(new ChangePlan { Objects = [] }, [], "dc01.contoso.test", "CONTOSO\\operator", at, at, WasCancelled: false);
    }

    /// <summary>An execution a window follows; <see cref="EndAsync"/> ends its work and, like the window's code, its line.</summary>
    private sealed record ExecutionRun(
        B2Harness Harness, PageStatusHolder Holder, ExecutionStatus Execution, ActivityViewModel Activity, Work Work, Task<BatchResult> Task)
    {
        public ScriptedDialogService Dialogs => Harness.Dialogs;

        public ILocalizer Localizer => Harness.Localizer;

        public async Task EndAsync(bool complete = false)
        {
            Work.End(Result());
            var result = await Task;
            if (complete && Activity.TryReturnToWindow())
            {
                Execution.Complete(result);
            }

            Execution.Dispose();
        }
    }

    /// <summary>The work of an activity that the test drives: it ends when the test says so.</summary>
    private sealed class Work
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<BatchResult> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public CancellationToken Token { get; private set; }

        public Task<BatchResult> RunAsync(IProgress<BatchProgress> progress, CancellationToken token)
        {
            Token = token;
            _started.SetResult();
            return _end.Task;
        }

        public void End(BatchResult result) => _end.TrySetResult(result);
    }

    /// <summary>A window's view model: its own holder and, when it has pages that are not in front, the holders of those.</summary>
    private sealed class WindowOf(PageStatusHolder status, params PageStatusHolder[] parts) : IHasPageStatus, IHasPageStatusParts
    {
        public PageStatusHolder Status { get; } = status;

        public IEnumerable<PageStatusHolder> StatusParts => parts;
    }
}
