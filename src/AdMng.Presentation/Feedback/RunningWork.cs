using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Feedback;

/// <summary>
/// A view model that owns more status holders than the one it shows (track V, stage V2b-5b): the pages of a window, the
/// tabs of the main window. The one question of a window that is closed while it works (<see cref="RunningWork"/>) looks
/// at all of them - a send that runs on a page that is not in front still has to be asked about.
/// </summary>
public interface IHasPageStatusParts
{
    /// <summary>The holders of the pages (or tabs); the ones in front need not be among them - the chain of <see cref="PageStatusHolder.Front"/> is followed anyway.</summary>
    IEnumerable<PageStatusHolder> StatusParts { get; }
}

/// <summary>
/// The question of a window that is closed while an operation runs (track V, stage V2b-5b; the owner's decisions 6 and D-A,
/// design note docs/dev/v-interface-renewal.md "V2b-5b as built" and "D-A repair as built"): one mechanism for every window,
/// in a service of the view-model level - no WPF type - that <see cref="Dialogs.WindowViewModelBase"/>, the tabs and the main
/// window ask.
/// </summary>
/// <remarks>
/// <para><b>What counts.</b> Only an operation that <em>writes</em> (<see cref="OperationScope.Writes"/>, set by the code that starts it:
/// an execution, a send, a restore, an update of the central store, a save of the Watchdog configuration…). A window
/// whose operation only reads closes without a question and the read is cancelled, as before. The holders looked at are
/// those of the view model (<see cref="IHasPageStatus"/> and <see cref="IHasPageStatusParts"/>) and every holder in
/// front of them (<see cref="PageStatusHolder.Front"/>). The connection window also counts the work of its connection that
/// no window follows (<see cref="IConnectionWork"/>): closing it ends the connection.</para>
/// <para><b>What is asked.</b> A write that cannot be stopped (<see cref="OperationScope.CanCancel"/> is false) is not asked
/// about: the window cannot be closed during it, it says so and stays open. Otherwise one question with labelled answers
/// (<see cref="IDialogService.AskCloseAsync"/>). When everything that writes is the execution of a change plan that may go on
/// without the window (<see cref="OperationScope.Run"/>: <see cref="IWindowRun.CanLeave"/>, no
/// <see cref="IWindowRun.MustStayReason"/>) there are three: <em>Stop and close</em>, <em>Close and continue in the
/// background</em>, <em>Don't close</em>. Otherwise two - Stop and close, Don't close - and the question says why the third
/// is missing (the generated passwords, the caller that waits, the first account, the connection).</para>
/// <para><b>What the answers do.</b> Stop and close stops every write through the holder's own Stop (and the run's, for an
/// execution that lost the line) and the connection's background work, and the window closes <em>after they ended</em> -
/// a write is never killed in the middle of a step: the object in progress is finished first, <c>BatchExecutor</c>'s
/// rule. Close and continue lets every run leave its window at once (<see cref="IWindowRun.LeaveWindow"/>): the window closes
/// and the connection says how the run ended. Don't close keeps the window and nothing is stopped. The window's own questions
/// (unsaved changes, generated passwords) come after the answer and before a run leaves; for writes that stay they come after
/// the end, when their results are visible.</para>
/// <para><b>Who says how a run ended.</b> A window that closes after its run ended - Stop and close, a Stop that was pressed
/// before the close and needs no question, a result that came back to the window while the question was open or while the
/// window reads its list again - takes its line with it: the run is handed to the connection, which says how it ended once
/// the window has closed (<see cref="IWindowRun.ReportByConnection"/>, <see cref="IWindowRun.WindowClosed"/>), unless the
/// window said it in a notification of its own. Such a run never keeps the window open against the answer: there is nothing
/// left to stop or to leave, the window closes once its code ended. The connection window hands nothing on: closing it ends
/// the connection.</para>
/// <para><b>What is looked at</b> is every write whose code has not ended (<see cref="PageStatusHolder.Writes"/>), not only
/// the operation a line shows: a write that another operation took off the line still writes. And it is looked at again
/// after the question and after the wait, because the window stays usable under both: a write that began meanwhile is
/// asked about in its own right. An answer that can no longer be honoured (a run that must stay with the window now) is asked
/// for again.</para>
/// </remarks>
public static class RunningWork
{
    /// <summary>The holders of <paramref name="viewModels"/>: their own, their parts and everything in front of them, each once.</summary>
    public static IReadOnlyList<PageStatusHolder> HoldersOf(params object?[] viewModels)
    {
        ArgumentNullException.ThrowIfNull(viewModels);
        var holders = new List<PageStatusHolder>();
        foreach (var viewModel in viewModels)
        {
            if (viewModel is IHasPageStatus { Status: { } own })
            {
                Add(holders, own);
            }

            if (viewModel is IHasPageStatusParts parts)
            {
                foreach (var part in parts.StatusParts)
                {
                    Add(holders, part);
                }
            }
        }

        return holders;
    }

    /// <summary>
    /// The operations of <paramref name="holders"/> that write and whose code has not ended them (also those that are
    /// stopping, and those that another operation took off the line: they still write).
    /// </summary>
    public static IReadOnlyList<OperationScope> RunningWrites(IEnumerable<PageStatusHolder> holders)
    {
        ArgumentNullException.ThrowIfNull(holders);
        var running = new List<OperationScope>();
        foreach (var holder in holders)
        {
            foreach (var operation in holder.Writes)
            {
                if (!running.Contains(operation))
                {
                    running.Add(operation);
                }
            }
        }

        return running;
    }

    /// <summary>True while one of <paramref name="viewModels"/> runs an operation that writes.</summary>
    public static bool HasRunningWrites(params object?[] viewModels) => RunningWrites(HoldersOf(viewModels)).Count > 0;

    /// <summary>
    /// The close question of a window, over everything it holds (<see cref="HoldersOf"/>). True when the window may close:
    /// nothing writes, or the operator answered and what writes has ended or left the window - and the window's own
    /// questions allowed it. False when the operator kept the window or an operation that cannot be stopped still runs (the
    /// operator was told).
    /// </summary>
    /// <param name="localizer">Texts.</param>
    /// <param name="dialogs">The question and the notice.</param>
    /// <param name="title">The title of the dialog: the window's.</param>
    /// <param name="holders">The holders of the window: its own, its parts, the tabs of the main window, the windows it owns.</param>
    /// <param name="connection">
    /// The work of the connection that no window follows - for the connection window only, whose close ends the connection:
    /// it is listed, it is stopped with the rest, nothing is offered to continue in the background, and nothing is handed to
    /// the connection to report.
    /// </param>
    /// <param name="ownQuestions">The window's own questions (<c>CanCloseCoreAsync</c>), asked at the right moment; null: none.</param>
    /// <param name="reportOnClose">
    /// Where the runs this close looked at are put, for the window to tell them <see cref="IWindowRun.WindowClosed"/> once it
    /// has closed (<c>WindowViewModelBase.Dispose</c>): the connection then says how those that ended in the window ended. They
    /// are taken out again - and told <see cref="IWindowRun.WindowStays"/> - when the window stays. Null: the runs are told at
    /// once when the window may close.
    /// </param>
    public static async Task<bool> ConfirmCloseAsync(
        ILocalizer localizer,
        IDialogService dialogs,
        string title,
        IEnumerable<PageStatusHolder> holders,
        IConnectionWork? connection = null,
        Func<Task<bool>>? ownQuestions = null,
        ICollection<IWindowRun>? reportOnClose = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(title);
        var holderList = holders as IReadOnlyCollection<PageStatusHolder> ?? [.. holders];
        var runs = reportOnClose ?? new List<IWindowRun>();
        var looked = new List<IWindowRun>();
        var closes = false;
        try
        {
            closes = await DecideAsync(localizer, dialogs, title, holderList, connection, ownQuestions, run =>
            {
                // A run this close looks at keeps the connection's report until the close is decided: should its result come
                // back under the question, the window that then closes hands it on.
                run.WindowMayClose();
                if (!looked.Contains(run))
                {
                    looked.Add(run);
                }

                if (!runs.Contains(run))
                {
                    runs.Add(run);
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            if (!closes)
            {
                // The window stays (also when the question could not be asked) and says the results in its lines itself.
                foreach (var run in looked)
                {
                    run.WindowStays();
                    runs.Remove(run);
                }
            }
            else if (reportOnClose is null)
            {
                foreach (var run in looked)
                {
                    run.WindowClosed();
                }
            }
        }

        return closes;
    }

    private static async Task<bool> DecideAsync(
        ILocalizer localizer,
        IDialogService dialogs,
        string title,
        IReadOnlyCollection<PageStatusHolder> holders,
        IConnectionWork? connection,
        Func<Task<bool>>? ownQuestions,
        Action<IWindowRun> look)
    {
        var ownAsked = false;

        // The window stays usable under the question and during the wait, so what writes is read again after each of
        // them: a write that began meanwhile is asked about and waited for like the first, not closed over.
        while (true)
        {
            var running = RunningWrites(holders);
            var background = connection?.RunningInBackground ?? [];
            if (connection is null)
            {
                foreach (var operation in running)
                {
                    if (operation.Run is { LeftWindow: false } run)
                    {
                        look(run);
                    }
                }
            }

            if (running.Count == 0 && background.Count == 0)
            {
                return ownAsked || ownQuestions is null || await ownQuestions().ConfigureAwait(true);
            }

            if (running.Where(operation => operation.Run is { HasReturned: true }).ToList() is { Count: > 0 } finishing)
            {
                // A run whose result came back to the window, which has not said it yet (it reads its list again first): nothing
                // can be stopped or left any more. No question: the window closes once that code ended - and, as its line goes
                // with it, the connection says how the run ended.
                HandOn(finishing, connection);
                await Task.WhenAll(finishing.Select(operation => operation.Finished)).ConfigureAwait(true);
                continue;
            }

            if (running.Any(operation => !operation.CanCancel))
            {
                // Nothing can stop it and closing the window would leave it half done: the window stays, and says why.
                var waitFor = string.Join("\n", running.Select(operation => operation.Working.Message));
                await dialogs.ShowInfoAsync(title, localizer.Format(L.CloseWork.Wait, waitFor)).ConfigureAwait(true);
                return false;
            }

            // One that is stopping, or that lost the line (its token is cancelled already), is not asked about again - unless
            // it is an execution, whose run can still be stopped. The window waits for those: everything stops already (Stop
            // in the line or in the list of background operations), and the lines go with the window.
            var toStop = running.Where(NeedsStop).ToList();
            var backgroundToStop = background.Where(work => !work.IsStopping).ToList();
            if (toStop.Count == 0 && backgroundToStop.Count == 0)
            {
                HandOn(running, connection);
                await WaitAsync(running, background).ConfigureAwait(true);
                continue;
            }

            var canContinue = connection is null && CanAllLeave(running);
            var answer = await dialogs.AskCloseAsync(title, Question(localizer, running, background, connection is not null, canContinue), canContinue)
                .ConfigureAwait(true);
            if (answer == CloseAnswer.KeepOpen)
            {
                return false;
            }

            // A run whose result came back to the window while the question was open: there is nothing to stop or to leave any
            // more. The window closes as answered once its code ended, and the connection says how the run ended.
            var cameBack = running.Where(operation => operation.Run is { LeftWindow: false } run && (operation.IsFinished || run.HasReturned)).ToList();
            HandOn(cameBack, connection);
            var stillRunning = running.Except(cameBack).ToList();

            if (answer == CloseAnswer.CloseAndContinue)
            {
                if (stillRunning.Count > 0 && (!canContinue || !CanAllLeave(stillRunning)))
                {
                    // An answer that was not offered, or one that cannot be honoured any more (a run that must stay now): ask again.
                    continue;
                }

                if (!ownAsked && ownQuestions is not null && !await ownQuestions().ConfigureAwait(true))
                {
                    return false;
                }

                ownAsked = true;
                foreach (var operation in stillRunning)
                {
                    if (!operation.Run!.LeaveWindow())
                    {
                        // Its result came back while the window's own question was open: the window closes once its code ended.
                        operation.Run.ReportByConnection();
                    }
                }

                continue;
            }

            if (canContinue)
            {
                // Everything here could have gone on without the window: the window's own questions first, before anything
                // is stopped - after the stop the window closes without another word.
                if (!ownAsked && ownQuestions is not null && !await ownQuestions().ConfigureAwait(true))
                {
                    return false;
                }

                ownAsked = true;
            }

            // The window goes right after the end, its lines with it: the connection says how the runs ended.
            HandOn(stillRunning, connection);
            foreach (var operation in toStop.Except(cameBack))
            {
                // The holder's own Stop: the line says "Stopping…" and the code that runs ends it the way it ends a Stop. An
                // execution that lost the line is stopped through its run.
                operation.Cancel();
                operation.Run?.Stop();
            }

            foreach (var work in backgroundToStop)
            {
                work.StopBecauseTheConnectionCloses();
            }

            // After its code ended - never in the middle of a step: the object in progress is finished, the window closes then.
            await WaitAsync(running, background).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The window closes right after these runs ended: the connection says how they ended once it closed. Not for the connection
    /// window, whose close ends the connection.
    /// </summary>
    private static void HandOn(IEnumerable<OperationScope> operations, IConnectionWork? connection)
    {
        if (connection is not null)
        {
            return;
        }

        foreach (var operation in operations)
        {
            operation.Run?.ReportByConnection();
        }
    }

    /// <summary>A write that still needs the operator's answer.</summary>
    private static bool NeedsStop(OperationScope operation) =>
        !operation.IsCancelling && (!operation.IsEnded || operation.Run is { IsStopping: false, LeftWindow: false });

    /// <summary>True when every write is an execution that may go on without the window now.</summary>
    private static bool CanAllLeave(IReadOnlyList<OperationScope> running) =>
        running.Count > 0 && running.All(operation => operation.Run is { CanLeave: true, MustStayReason: null });

    private static Task WaitAsync(IReadOnlyList<OperationScope> running, IReadOnlyList<IBackgroundWork> background) =>
        Task.WhenAll(running.Select(operation => operation.Finished).Concat(background.Select(work => work.Ended)));

    /// <summary>
    /// The text of the question: what runs, one line each, then what the answers do - and, without the background answer,
    /// why it is missing.
    /// </summary>
    private static string Question(
        ILocalizer localizer, IReadOnlyList<OperationScope> running, IReadOnlyList<IBackgroundWork> background, bool isConnection, bool canContinue)
    {
        var what = string.Join("\n", running.Select(operation => operation.Working.Message).Concat(background.Select(work => work.Line)));
        if (canContinue)
        {
            return localizer.Format(L.CloseWork.Changes, what);
        }

        var changes = background.Count > 0 || running.Any(o => o.Writes == OperationWrites.Changes);
        var sentence = changes ? L.CloseWork.StopChanges
            : running.Any(o => o.Writes == OperationWrites.Messages) ? L.CloseWork.StopMessages
            : L.CloseWork.StopOther;
        // The connection window never offers the background, for one reason: its close ends the connection. The reasons of the
        // windows it owns speak of "this window" and are not said under its title.
        List<string> reasons = isConnection
            ? changes ? [localizer[L.CloseWork.NoBackgroundConnection]] : []
            : running.Select(operation => operation.Run?.MustStayReason)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

        var text = localizer.Format(sentence, what);
        return reasons.Count == 0 ? text : $"{text}\n\n{string.Join("\n", reasons)}";
    }

    private static void Add(List<PageStatusHolder> holders, PageStatusHolder holder)
    {
        for (var part = holder; part is not null && !holders.Contains(part); part = part.Front)
        {
            holders.Add(part);
        }
    }
}
