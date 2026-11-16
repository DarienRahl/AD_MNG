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
/// The question of a window that is closed while an operation runs (track V, stage V2b-5b; the owner's decision 6, design
/// note docs/dev/v-interface-renewal.md "V2b-5b as built"): one mechanism for every window, in a service of the view-model
/// level - no WPF type - that <see cref="Dialogs.WindowViewModelBase"/>, the tabs and the main window ask.
/// </summary>
/// <remarks>
/// <para><b>What counts.</b> Only an operation that <em>writes</em> (<see cref="OperationScope.Writes"/>, set by the code that starts it:
/// an execution, a send, a restore, an update of the central store, a save of the Watchdog configuration…). A window
/// whose operation only reads closes without a question and the read is cancelled, as before. The holders looked at are
/// those of the view model (<see cref="IHasPageStatus"/> and <see cref="IHasPageStatusParts"/>) and every holder in
/// front of them (<see cref="PageStatusHolder.Front"/>).</para>
/// <para><b>What happens.</b> A write that can be stopped is asked about - "Stop the running operation and close the
/// window?", with what is running and what the stop does - and, on Yes, stopped through the holder's own Stop; the window
/// closes <em>after the operation ended</em> (a write is never killed in the middle of a step: the object in progress is
/// finished first, <c>BatchExecutor</c>'s rule). No keeps the window. A write that was stopped already is not asked
/// about again: the window waits for its end. A write that cannot be stopped (<see cref="OperationScope.CanCancel"/> is
/// false) is not asked about: the window cannot be closed during it, it says so and stays open.</para>
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

    /// <summary>The operations of <paramref name="holders"/> that write and have not ended (also those that are stopping).</summary>
    public static IReadOnlyList<OperationScope> RunningWrites(IEnumerable<PageStatusHolder> holders)
    {
        ArgumentNullException.ThrowIfNull(holders);
        var running = new List<OperationScope>();
        foreach (var holder in holders)
        {
            if (holder.Operation is { IsWrite: true, IsEnded: false } operation && !running.Contains(operation))
            {
                running.Add(operation);
            }
        }

        return running;
    }

    /// <summary>True while one of <paramref name="viewModels"/> runs an operation that writes.</summary>
    public static bool HasRunningWrites(params object?[] viewModels) => RunningWrites(HoldersOf(viewModels)).Count > 0;

    /// <summary>
    /// The close question of a window, over everything it holds (<see cref="HoldersOf"/>). True when the window may close:
    /// nothing writes, or the operator agreed and the writing operations have ended. False when the operator kept the
    /// window, or an operation that cannot be stopped still runs (the operator was told).
    /// </summary>
    /// <param name="localizer">Texts.</param>
    /// <param name="dialogs">The question and the notice.</param>
    /// <param name="title">The title of the dialog: the window's.</param>
    /// <param name="holders">The holders of the window: its own, its parts, the tabs of the main window, the windows it owns.</param>
    public static async Task<bool> ConfirmCloseAsync(ILocalizer localizer, IDialogService dialogs, string title, IEnumerable<PageStatusHolder> holders)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(title);
        var running = RunningWrites(holders);
        if (running.Count == 0)
        {
            return true;
        }

        var what = string.Join("\n", running.Select(operation => operation.Working.Message));
        if (running.Any(operation => !operation.CanCancel))
        {
            // Nothing can stop it and closing the window would leave it half done: the window stays, and says why.
            await dialogs.ShowInfoAsync(title, localizer.Format(L.CloseWork.Wait, what)).ConfigureAwait(true);
            return false;
        }

        if (running.Any(operation => !operation.IsCancelling))
        {
            var sentence = running.Any(o => o.Writes == OperationWrites.Changes) ? L.CloseWork.StopChanges
                : running.Any(o => o.Writes == OperationWrites.Messages) ? L.CloseWork.StopMessages
                : L.CloseWork.StopOther;
            if (!await dialogs.ConfirmAsync(title, localizer.Format(sentence, what), destructive: true).ConfigureAwait(true))
            {
                return false;
            }

            foreach (var operation in running)
            {
                // The holder's own Stop: the line says "Stopping…" and the code that runs ends it the way it ends a Stop.
                operation.Cancel();
            }
        }

        // After it ended - never in the middle of a step: the object in progress is finished, the window closes then.
        await Task.WhenAll(running.Select(operation => operation.Ended)).ConfigureAwait(true);
        return true;
    }

    private static void Add(List<PageStatusHolder> holders, PageStatusHolder holder)
    {
        for (var part = holder; part is not null && !holders.Contains(part); part = part.Front)
        {
            holders.Add(part);
        }
    }
}
