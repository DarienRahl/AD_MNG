using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.PSTasks;
using System.Reflection;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// What the probe's <c>parallel-job-children</c> needs from the scripts of a <c>ForEach-Object -Parallel</c> job: a first child that
/// waits until it is let go, and a first child that ends by itself at the moment the window stops the job (its Stopping state, which
/// <c>PSTaskJob.StopJob</c> raises on the stopping thread before it stops the pool). A child that ends frees its place under the
/// throttle limit, and the engine's work item that hands the children to the pool then opens a runspace for the next one - unless it
/// has already handed out its last child; <see cref="AtStop"/> says which of the two the stop met.
/// </summary>
public static class ParallelJobChild
{
    private static readonly ManualResetEventSlim Released = new(false);
    private static int _watched;

    /// <summary>What the first child's end at the stop of its job met: "a runspace opened", "pool closed", "timeout" or "none".</summary>
    public static string AtStop { get; private set; } = "none";

    /// <summary>The first child's script calls this: it returns once the child is let go (at most 60 s).</summary>
    public static void Wait() => Released.Wait(TimeSpan.FromSeconds(60));

    /// <summary>Returns once the first child of <paramref name="job"/> has started (at most 60 s).</summary>
    public static void WaitUntilStarted(Job job)
    {
        var watch = Stopwatch.StartNew();
        while (job.ChildJobs[0].JobStateInfo.State == JobState.NotStarted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException("The first child of the job did not start.");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// When <paramref name="job"/> begins to stop, lets its first child go and waits on the stopping thread until that child has ended
    /// and either a runspace of a later child is held at the gate (<see cref="ParallelRunspaceGate"/>, armed to skip the first child's)
    /// or the work item has closed the pool; the job's stop goes on after that.
    /// </summary>
    public static void EndFirstChildAtStop(Job job)
    {
        var gate = ParallelRunspaceGate.Current ?? throw new InvalidOperationException("No gate is installed.");
        job.StateChanged += (_, e) =>
        {
            if (e.JobStateInfo.State != JobState.Stopping || Interlocked.Exchange(ref _watched, 1) == 1)
            {
                return;
            }

            Released.Set();
            var first = job.ChildJobs[0];
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (gate.Held)
                {
                    AtStop = "a runspace opened";
                    return;
                }

                if (first.JobStateInfo.State is (JobState.Completed or JobState.Failed or JobState.Stopped) && !PoolOpen(job))
                {
                    AtStop = "pool closed";
                    return;
                }

                Thread.Sleep(5);
            }

            AtStop = "timeout";
        };
    }

    /// <summary>
    /// Whether the engine's work item still hands children of <paramref name="job"/> to its pool: PowerShell 7.6 closes the pool
    /// (<c>PSTaskPool.IsOpen</c>) when it has handed out the last one, or when the job is stopped.
    /// </summary>
    public static bool PoolOpen(Job job)
    {
        var pool = typeof(PSTaskJob).GetField("_taskPool", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(job)
            ?? throw new InvalidOperationException("The engine's parallel job has no pool field.");
        return pool.GetType().GetProperty("IsOpen")?.GetValue(pool) as bool?
            ?? throw new InvalidOperationException("The engine's parallel job pool has no IsOpen.");
    }
}
