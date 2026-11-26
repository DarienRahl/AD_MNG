using AdMng.Core.Batch;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Changes;

/// <summary>
/// How the execution of a change plan ended, in words: the sentence of the window's status line (<see cref="ExecutionStatus"/>)
/// and of the connection's report of a run whose window closed (<see cref="ChangeCoordinator"/>; the owner's decision D-A) -
/// one text for both, so the notification says exactly what the line would have said.
/// </summary>
/// <param name="Kind">Done (every object changed), attention (stopped, or some objects failed) or error (nothing changed).</param>
/// <param name="Message">What happened, how many, in which domain, and what can be done next.</param>
/// <param name="Log">The value-free copy for the diagnostic log (message key and numbers).</param>
internal readonly record struct ExecutionOutcome(PageStatusKind Kind, string Message, PageStatusLog Log)
{
    /// <summary>
    /// The outcome of <paramref name="result"/>. An object changed only in part (<see cref="ObjectResultStatus.PartiallySucceeded"/>:
    /// the password was set, the unlock failed) counts as changed: "nothing was changed" is said only when nothing was.
    /// </summary>
    /// <param name="localizer">Texts.</param>
    /// <param name="title">The title of the change.</param>
    /// <param name="domain">The domain the plan was executed in (empty for a window opened while disconnected).</param>
    /// <param name="result">The result.</param>
    /// <param name="next">What the operator can do about what was not changed (a sentence), or null. Not used for "done".</param>
    public static ExecutionOutcome Of(ILocalizer localizer, string title, string domain, BatchResult result, string? next = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);
        var changed = result.SucceededCount;
        var failed = result.FailedCount;
        var partly = result.Objects.Count(o => o.Status == ObjectResultStatus.PartiallySucceeded);
        var objects = PageStatusText.Objects(localizer, changed);

        // "Stopped" is said when the Stop left something undone. One that came while the last objects ran kept nothing from
        // running (they are carried through): that plan was executed, and the outcome says so.
        if (result.WasCutShort)
        {
            return new(
                PageStatusKind.Attention,
                PageStatusText.Failure(localizer.Format(L.ChangeStatus.Stopped, title, domain), StoppedCounts(localizer, result), next),
                new PageStatusLog(L.ChangeStatus.Stopped, changed, failed, result.FinishedAfterStopCount));
        }

        if (failed == 0)
        {
            return new(PageStatusKind.Done, localizer.Format(L.ChangeStatus.Executed, title, domain, objects), new PageStatusLog(L.ChangeStatus.Executed, changed));
        }

        if (changed > 0 || partly > 0)
        {
            var counts = partly == 0
                ? localizer.Format(L.ChangeStatus.Counts, objects, failed)
                : localizer.Format(L.ChangeStatus.CountsPartly, PageStatusText.Objects(localizer, changed + partly), partly, failed - partly);
            return new(
                PageStatusKind.Attention,
                PageStatusText.Failure(localizer.Format(L.ChangeStatus.ExecutedPartly, title, domain), counts, next),
                new PageStatusLog(L.ChangeStatus.ExecutedPartly, changed, failed));
        }

        return new(
            PageStatusKind.Error,
            PageStatusText.Failure(FailedLead(localizer, title, domain), localizer.Format(L.ChangeStatus.NothingChanged, failed), next),
            new PageStatusLog(L.ChangeStatus.ExecuteFailed, changed, failed));
    }

    /// <summary>What was tried when the plan could not be executed at all: "Could not execute “…” in contoso.test".</summary>
    public static string FailedLead(ILocalizer localizer, string title, string domain)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return domain.Length == 0
            ? localizer.Format(L.ChangeStatus.ExecuteFailedAnywhere, title)
            : localizer.Format(L.ChangeStatus.ExecuteFailed, title, domain);
    }

    /// <summary>
    /// What a Stop left, in one clause: "12 of 300 objects done, failed: 1, the one that ran was finished". Stop finishes the
    /// object in progress, so the objects that ran when it came are not counted as half done - they are done, and the
    /// clause says that they were carried through (nothing is said when the Stop came between two objects).
    /// </summary>
    private static string StoppedCounts(ILocalizer localizer, BatchResult result)
    {
        var done = result.SucceededCount;

        // The objects the plan had to change; the plan of a merged result (the bulk creation) holds all of them.
        var total = Math.Max(result.Plan.ExecutableCount, done + result.FailedCount);
        var counts = localizer.Format(
            CorrespondenceText.PluralKey(total, L.ChangeStatus.CountsStoppedOne, L.ChangeStatus.CountsStoppedFew, L.ChangeStatus.CountsStoppedMany),
            done,
            total,
            result.FailedCount);
        return result.FinishedAfterStopCount > 0
            ? $"{counts}, {PageStatusText.Count(localizer, result.FinishedAfterStopCount, L.ChangeStatus.FinishedOne, L.ChangeStatus.FinishedFew, L.ChangeStatus.FinishedMany)}"
            : counts;
    }
}
