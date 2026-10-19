using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Pending notifications waiting for their bucket to flush (section 13).</summary>
public interface INotificationQueueStore
{
    Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken);
    /// <summary>Follow-ups and previous-configuration notifications; duplicates by (event, target, follow-up) ignored.</summary>
    Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken);
    /// <summary>Atomically deletes the pending rows and (when not null) enqueues the rendered message linked to their events.</summary>
    Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken);
    Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken);
    /// <summary>Moves the rows' DueAt (throttled buckets, 13.3).</summary>
    Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken);
    /// <summary>Re-planned targets (13.2 #1, 11.4 #9, 13.3), in one transaction: the waiting row with the same (event,
    /// target, follow-up) takes the row's rule, target, severity, bucket and hold and keeps the earlier of both due times;
    /// a row without a waiting counterpart (it was flushed or dropped meanwhile) is inserted when
    /// <paramref name="insertMissing"/>, otherwise skipped. An inserted row of an unknown event violates the foreign key.</summary>
    Task ReplanAsync(IReadOnlyList<PendingNotification> notifications, bool insertMissing, CancellationToken cancellationToken);
}
