namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Result of one job iteration; NextRunIn null = the job's DefaultInterval.</summary>
public readonly record struct JobResult(TimeSpan? NextRunIn, bool Failed = false, string? FailureCode = null);

/// <summary>One step of the processing pipeline, hosted by a JobHost in the service (section 2).</summary>
public interface IWatchdogJob
{
    string Name { get; }                                         // "detection", "attribution", … (logs, health codes)
    TimeSpan DefaultInterval { get; }
    IReadOnlyList<WatchdogSignal> WakeSignals { get; }           // signals that end the wait early
    /// <summary>One iteration; never throws for expected failures (returns Failed). Unexpected exceptions are logged by
    /// the host and retried with backoff (1 s … 5 min).</summary>
    Task<JobResult> RunOnceAsync(CancellationToken cancellationToken);
}
