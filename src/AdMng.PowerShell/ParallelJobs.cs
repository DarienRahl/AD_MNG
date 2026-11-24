using System.Management.Automation;
using System.Management.Automation.PSTasks;
using System.Reflection;

namespace AdMng.PowerShell;

/// <summary>
/// Ends a <c>ForEach-Object -Parallel -AsJob</c> job (<see cref="PSTaskJob"/>) that .NET code of the trusted mode left in a session
/// without closing a runspace under the engine (docs/dev/r1-powershell-window.md 4.1, 12.11; CI run 45315307313).
/// <para>
/// PowerShell 7.6 hands the children of such a job to the job's pool (<c>PSTaskPool</c>) in a thread-pool work item of its own that
/// catches nothing (<c>PSTaskJob.Start</c>: <c>Add</c> for each child, then <c>Close</c>). <c>Add</c> waits until the pool has a free
/// place or is told to stop (<c>WaitHandle.WaitAny</c> on <c>_addAvailable</c> and <c>_stopAll</c>; a free place wins when both are
/// set), then <c>GetRunspace</c> adds a new runspace to the pool's list and opens it, and only then checks under the pool's lock whether
/// the pool is still open. The job's <c>StopJob</c> (<c>StopAll</c>) and <c>Dispose</c> close every runspace in that list - also one
/// that is still opening, whose <c>InitialSessionState.BindRunspace</c> then fails with a NullReferenceException on that work item, which
/// ends the process - and <c>Dispose</c> also disposes the two wait handles. Nothing orders either against the work item: a child that
/// ends frees its place and lets the work item open the next runspace at any moment until it has handed out its last child.
/// </para>
/// <para>
/// So the session stops and disposes such a job only once the work item is done with the pool (<see cref="PrepareEndAsync"/>): it
/// takes away the pool's free place and gives the stop signal - the two events <c>Add</c> waits on - so that the work item hands out
/// no child that has not got a place yet, waits until the work item has closed the pool (<c>PSTaskPool.IsOpen</c>, which only its last
/// step and a stop clear), and only then lets the job be stopped. A child that the work item is already opening when the signal comes
/// still starts, and is stopped with the job; so does the next one when a running child of a full pool ends and frees its place
/// (<c>HandleTaskStateChanged</c> sets <c>_addAvailable</c> again) in the moment the work item takes to leave its loop. Either way the
/// open ends before the pool closes, so the stop never meets it. These are private members of the engine (Microsoft.PowerShell.SDK 7.6.10, pinned in
/// Directory.Packages.props); <c>EscapeTests</c> fails when they change. Without them the session waits until every child has started,
/// which needs no member: a child leaves NotStarted only once its runspace is open.
/// </para>
/// </summary>
internal static class ParallelJobs
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The first wait between two looks at the pool; it doubles up to <see cref="LongestPoll"/>.</summary>
    private static readonly TimeSpan ShortestPoll = TimeSpan.FromMilliseconds(1);

    /// <summary>The longest wait between two looks at the pool: the work item takes as long as the engine takes to open one runspace.</summary>
    private static readonly TimeSpan LongestPoll = TimeSpan.FromMilliseconds(100);

    private static readonly FieldInfo? PoolField = typeof(PSTaskJob).GetField("_taskPool", Private);
    private static readonly FieldInfo? AddAvailableField = PoolField?.FieldType.GetField("_addAvailable", Private);
    private static readonly FieldInfo? StopAllField = PoolField?.FieldType.GetField("_stopAll", Private);
    private static readonly PropertyInfo? IsOpenProperty = PoolField?.FieldType.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);

    /// <summary>Whether this engine has the members of its parallel job's pool that <see cref="PrepareEndAsync"/> uses.</summary>
    internal static bool PoolMembersFound =>
        AddAvailableField?.FieldType == typeof(ManualResetEvent)
        && StopAllField?.FieldType == typeof(ManualResetEvent)
        && IsOpenProperty?.PropertyType == typeof(bool);

    /// <summary>
    /// Returns true once <paramref name="job"/> can be stopped and disposed with no runspace of its pool opening meanwhile, and false when
    /// something else stopped it first (trusted-mode code's own <c>StopJob</c>, which closes the pool while the work item may still be
    /// opening a runspace): then the session leaves it as it is. A job that is no longer Running when this is called has no work item
    /// left only if its pool ended by itself (Completed, Failed); a job never started has none.
    /// </summary>
    internal static async Task<bool> PrepareEndAsync(PSTaskJob job)
    {
        var state = job.JobStateInfo.State;
        if (state is JobState.Completed or JobState.Failed or JobState.NotStarted)
        {
            return true;
        }

        if (state != JobState.Running)
        {
            return false;
        }

        var children = job.ChildJobs.ToArray();
        Func<bool> handingOut;
        if (PoolMembersFound && PoolField!.GetValue(job) is { } pool)
        {
            // No child that waits for a place gets one; a work item that waits in Add, or comes to it, takes the stop signal.
            ((ManualResetEvent)AddAvailableField!.GetValue(pool)!).Reset();
            ((ManualResetEvent)StopAllField!.GetValue(pool)!).Set();
            handingOut = () => (bool)IsOpenProperty!.GetValue(pool)!;
        }
        else
        {
            handingOut = () => children.Any(child => child.JobStateInfo.State == JobState.NotStarted);
        }

        // The work item's progress has no event of its own (a skipped child stays NotStarted): look at it, each time a little later.
        var delay = ShortestPoll;
        while (job.JobStateInfo.State == JobState.Running && handingOut())
        {
            await Task.Delay(delay).ConfigureAwait(false);
            delay = delay < LongestPoll ? delay * 2 : LongestPoll;
        }

        // StopJob marks the job Stopping before it closes the pool: a pool closed while the job still runs was closed by the work item.
        Interlocked.MemoryBarrier();
        return job.JobStateInfo.State is JobState.Running or JobState.Completed or JobState.Failed;
    }
}
