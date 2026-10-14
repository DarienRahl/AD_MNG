using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Rendered messages waiting for (or after) delivery (section 15).</summary>
public interface IOutboxStore
{
    Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken);
    /// <summary>Due Pending/Retrying messages, marked Sending with a lease; expired leases become due again (crash
    /// safety). Leasing does not change Attempts.</summary>
    Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken);
    /// <summary>Marks Sent, increments Attempts and sets the payload to NULL (only metadata is kept, 16.3).</summary>
    Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken);
    /// <summary>countAttempt = false for rate-limiter deferrals (no attempt was made).</summary>
    Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken);
    /// <summary>Marks Dead and increments Attempts; the payload is kept for a manual retry until OutboxDeadDays.</summary>
    Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken);
    Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken);    // manual retry of Dead
    Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken);
    /// <summary>Messages (any status, payload possibly null) that contain this event – FollowUpPlanner input.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken);
    /// <summary>RuleDelivery.RepeatSuppression: was an immediate (non-digest) message of this rule about this object
    /// enqueued since the given time?</summary>
    Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken);
    Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken);
}
