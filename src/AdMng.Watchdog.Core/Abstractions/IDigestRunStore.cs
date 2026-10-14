namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Digest periods already enqueued (section 13.4).</summary>
public interface IDigestRunStore
{
    /// <summary>End of the last digest period that was enqueued, or null before the first digest.</summary>
    Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken);
    Task RecordAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, int eventCount, IReadOnlyList<long> outboxIds, CancellationToken cancellationToken);
}
