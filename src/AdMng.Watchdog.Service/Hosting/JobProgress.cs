namespace AdMng.Watchdog.Service.Hosting;

/// <summary>What a <see cref="JobHost"/> loop is doing.</summary>
public enum JobPhase
{
    /// <summary>The loop has not started yet.</summary>
    NotStarted,

    /// <summary>Inside <c>RunOnceAsync</c>.</summary>
    Running,

    /// <summary>Waiting for the next run (interval or wake signal).</summary>
    Waiting,

    /// <summary>Waiting after an unexpected exception.</summary>
    BackingOff,

    /// <summary>The loop has ended (host stopping).</summary>
    Stopped,
}

/// <summary>
/// The progress of one job loop as the systemd watchdog sees it (stage B design 12.1): the phase, when it began and how
/// long it may last. A loop that stays in one phase longer than expected – an iteration beyond its run budget, a wait
/// beyond its interval plus <see cref="WaitGrace"/>, an ended loop – is stalled, and <see cref="SystemdWatchdogNotifier"/>
/// stops the <c>WATCHDOG=1</c> pings so that systemd restarts the service.
/// </summary>
/// <param name="Phase">The current phase.</param>
/// <param name="Since">When the phase began.</param>
/// <param name="Expected">How long the phase may last: the run budget while running, the wait or backoff otherwise.</param>
public readonly record struct JobProgress(JobPhase Phase, DateTimeOffset Since, TimeSpan Expected)
{
    /// <summary>Tolerance beyond a wait or backoff (a timer that fires late on a loaded host is not a hang).</summary>
    public static readonly TimeSpan WaitGrace = TimeSpan.FromMinutes(2);

    /// <summary>The run budget of a job without an entry in <see cref="RunBudgets"/>.</summary>
    public static readonly TimeSpan DefaultRunBudget = TimeSpan.FromHours(1);

    /// <summary>
    /// Jobs whose single iteration may legitimately run for hours: the detection baseline and resync of a large domain
    /// (the cycle restarts from the beginning after a restart, so a budget below its duration would loop for ever), the
    /// scheduled scans and the nightly retention (<c>VACUUM INTO</c>, secure delete).
    /// </summary>
    public static IReadOnlyDictionary<string, TimeSpan> RunBudgets { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["detection"] = TimeSpan.FromHours(6),
        ["scans"] = TimeSpan.FromHours(6),
        ["retention"] = TimeSpan.FromHours(6),
    };

    /// <summary>The run budget of <paramref name="jobName"/>.</summary>
    public static TimeSpan RunBudget(string jobName) =>
        RunBudgets.TryGetValue(jobName, out var budget) ? budget : DefaultRunBudget;

    /// <summary>True when the loop has been in its phase longer than allowed at <paramref name="now"/>.</summary>
    public bool IsStalled(DateTimeOffset now) => Phase switch
    {
        JobPhase.NotStarted => false,
        JobPhase.Running => now - Since > Expected,
        // No overflow for a wait of (nearly) TimeSpan.MaxValue.
        JobPhase.Waiting or JobPhase.BackingOff => Expected < TimeSpan.MaxValue - WaitGrace && now - Since > Expected + WaitGrace,
        _ => true,
    };
}
