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
}
