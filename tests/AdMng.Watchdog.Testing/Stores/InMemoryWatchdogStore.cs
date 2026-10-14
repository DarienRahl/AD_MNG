using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>
/// In-memory implementation of every store interface of section 6.2 (Core tests, integration tests, Development
/// runs). Scaffold shell with the final name and signature – replaced by W-notify; every member throws
/// <see cref="NotImplementedException"/>.
/// </summary>
public sealed class InMemoryWatchdogStore :
    IDetectionStore, IEventStore, INotificationQueueStore, IOutboxStore, ISecurityEventStore, IExpiryNoticeStore,
    IDigestRunStore, IConfigStore, ISecretStore, IStoreMaintenance
{
    public InMemoryWatchdogStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
    }

    // IDetectionStore
    public Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken) => throw new NotImplementedException();

    public IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // IEventStore
    public Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task CommitEvaluationAsync(
        IReadOnlyList<EventEvaluation> evaluations,
        IReadOnlyList<PendingNotification> notifications,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<long> GetLatestIdAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    // INotificationQueueStore
    public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // IOutboxStore
    public Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // ISecurityEventStore
    public Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // IExpiryNoticeStore
    public Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    // IDigestRunStore
    public Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task RecordAsync(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int eventCount,
        IReadOnlyList<long> outboxIds,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    // IConfigStore
    public Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<ConfigEnvelope> SaveAsync(
        WatchdogConfiguration configuration,
        long expectedVersion,
        PrincipalRef by,
        string summary,
        IReadOnlyList<SecretChange> secretChanges,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();

    // ISecretStore
    public Task<StoredSecret?> GetAsync(string name, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task SetAsync(string name, SecretValue value, string binding, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task RemoveAsync(string name, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    // IStoreMaintenance
    public Task<PurgeResult> PurgeAsync(RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<string> BackupAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<StorageStatusDto> GetStatusAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
