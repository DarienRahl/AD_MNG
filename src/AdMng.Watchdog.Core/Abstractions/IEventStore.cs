using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Stored events: feed, attribution updates, rule evaluation, acknowledgement.</summary>
public interface IEventStore
{
    Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken);   // scans, health, event-log-only
    Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken);
    /// <summary>Completes when an event with Id &gt; afterId exists or the timeout elapses (long-poll).</summary>
    Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken);
    Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken);
    Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken);
    IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    /// <summary>Events with attribution Pending whose next attempt time is ≤ dueBefore (new Pending events are due
    /// immediately: the store sets next_attribution_at = detected_at on insert).</summary>
    Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken);
    /// <summary>Stores attribution, added tags, re-resolved severity and lockout sources; updates the search text
    /// (workstation/IP). update.NextAttemptAt = null when the attribution is final.</summary>
    Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken);
    Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken);
    /// <summary>Atomically: stores severities/matched rules, marks events evaluated, inserts pending notifications
    /// (duplicates by (event, target, follow-up) ignored).</summary>
    Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken);
    Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken);
    /// <summary>Returns the number of events acknowledged (already acknowledged ones are left unchanged).</summary>
    Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken);
    Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken);
    Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken);
    Task<long> GetLatestIdAsync(CancellationToken cancellationToken);
}
