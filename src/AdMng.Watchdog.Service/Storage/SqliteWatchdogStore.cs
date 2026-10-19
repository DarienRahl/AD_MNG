using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Secrets;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The SQLite store as ONE object implementing every store interface of spec 6.2 (the shape
/// <c>WatchdogStoreContractTests</c> and the service's DI registrations use): it composes the per-interface stores over one
/// <see cref="SqliteDatabase"/> and delegates every member. <see cref="Open"/> creates the database file, runs the
/// migrations and wires the parts; disposing releases the connection pool of a database it opened.
/// </summary>
public sealed class SqliteWatchdogStore :
    IDetectionStore, IEventStore, INotificationQueueStore, IOutboxStore, ISecurityEventStore, IExpiryNoticeStore,
    IDigestRunStore, IConfigStore, ISecretStore, IStoreMaintenance, Api.IViewerEventSearch, IAsyncDisposable
{
    private readonly bool _ownsDatabase;

    /// <summary>Wires the parts over an already migrated database (the service's DI does this).</summary>
    public SqliteWatchdogStore(SqliteDatabase database, SecretProtector protector, TimeProvider timeProvider)
        : this(database, protector, timeProvider, ownsDatabase: false)
    {
    }

    private SqliteWatchdogStore(SqliteDatabase database, SecretProtector protector, TimeProvider timeProvider, bool ownsDatabase)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Database = database;
        _ownsDatabase = ownsDatabase;
        var notifier = new EventAppendedNotifier();
        Detection = new SqliteDetectionStore(database, timeProvider, notifier);
        Events = new SqliteEventStore(database, timeProvider, notifier);
        Queue = new SqliteNotificationQueueStore(database, timeProvider);
        Outbox = new SqliteOutboxStore(database);
        SecurityEvents = new SqliteSecurityEventStore(database);
        Expiry = new SqliteExpiryNoticeStore(database);
        DigestRuns = new SqliteDigestRunStore(database, timeProvider);
        Config = new SqliteConfigStore(database, protector, timeProvider);
        Secrets = new SqliteSecretStore(database, protector, timeProvider);
        Maintenance = new SqliteStoreMaintenance(database, timeProvider);
    }

    public SqliteDatabase Database { get; }

    public SqliteDetectionStore Detection { get; }

    public SqliteEventStore Events { get; }

    public SqliteNotificationQueueStore Queue { get; }

    public SqliteOutboxStore Outbox { get; }

    public SqliteSecurityEventStore SecurityEvents { get; }

    public SqliteExpiryNoticeStore Expiry { get; }

    public SqliteDigestRunStore DigestRuns { get; }

    public SqliteConfigStore Config { get; }

    public SqliteSecretStore Secrets { get; }

    public SqliteStoreMaintenance Maintenance { get; }

    /// <summary>Creates/migrates <c>&lt;dataDirectory&gt;/watchdog.db</c> and returns a store that owns the database.</summary>
    public static SqliteWatchdogStore Open(string dataDirectory, SecretProtector protector, TimeProvider timeProvider, ILogger<MigrationRunner> logger)
    {
        var database = new SqliteDatabase(dataDirectory);
        new MigrationRunner(database, timeProvider, logger).Run();
        return new SqliteWatchdogStore(database, protector, timeProvider, ownsDatabase: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDatabase)
        {
            await Database.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Detection

    public Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken) =>
        Detection.GetDcStateAsync(domain, cancellationToken);

    public Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken) =>
        Detection.GetWatermarkAsync(dsServiceName, invocationId, cancellationToken);

    public Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken) =>
        Detection.GetPendingCreationsAsync(dueBefore, cancellationToken);

    public Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken) =>
        Detection.GetByKindAsync(kind, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken) =>
        Detection.GetSnapshotsAsync(objectGuids, cancellationToken);

    public Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken) =>
        Detection.FindByDnAsync(distinguishedName, cancellationToken);

    public Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken) =>
        Detection.FindBySidAsync(sid, cancellationToken);

    public IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, CancellationToken cancellationToken) =>
        Detection.GetDescendantsAsync(ancestorDn, cancellationToken);

    public IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync(CancellationToken cancellationToken) =>
        Detection.GetLiveObjectGuidsAsync(cancellationToken);

    public Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken) =>
        Detection.CountLiveObjectsAsync(cancellationToken);

    public Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken) =>
        Detection.GetMembersAsync(groupGuid, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken) =>
        Detection.GetGroupsContainingAsync(memberKey, cancellationToken);

    public Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken) =>
        Detection.GetAllMembershipsAsync(cancellationToken);

    public Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken) =>
        Detection.GetPrivilegeStateAsync(cancellationToken);

    public Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken) =>
        Detection.GetNonDefaultPrimaryGroupsAsync(cancellationToken);

    public Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken) =>
        Detection.CommitAsync(commit, cancellationToken);

    // Events

    public Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken) =>
        Events.AppendAsync(watchEvent, cancellationToken);

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken) =>
        Events.QueryAsync(query, cancellationToken);

    public Task<EventPage> QueryWithoutSourcesAsync(EventQuery query, CancellationToken cancellationToken) =>
        Events.QueryAsync(query, textMatchesSources: false, cancellationToken);

    public Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken) =>
        Events.WaitForNewerAsync(afterId, timeout, cancellationToken);

    public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken) =>
        Events.GetAsync(id, cancellationToken);

    public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken) =>
        Events.GetByEventIdAsync(eventId, cancellationToken);

    public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken) =>
        Events.GetChangeSetAsync(changeSetId, cancellationToken);

    public IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Events.GetRangeAsync(from, to, cancellationToken);

    public Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken) =>
        Events.GetPendingAttributionAsync(dueBefore, limit, cancellationToken);

    public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken) =>
        Events.UpdateAttributionAsync(id, update, cancellationToken);

    public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
        Events.GetUnevaluatedAsync(limit, cancellationToken);

    public Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) =>
        Events.CommitEvaluationAsync(evaluations, notifications, cancellationToken);

    public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        Events.AcknowledgeAsync(id, by, comment, at, cancellationToken);

    public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        Events.AcknowledgeManyAsync(ids, by, comment, at, cancellationToken);

    public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken) =>
        Events.CountForObjectAsync(objectGuid, kind, since, cancellationToken);

    public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Events.CountAsync(since, cancellationToken);

    public Task<long> GetLatestIdAsync(CancellationToken cancellationToken) =>
        Events.GetLatestIdAsync(cancellationToken);

    // Queue

    public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) =>
        Queue.GetAllAsync(cancellationToken);

    public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) =>
        Queue.AddAsync(notifications, cancellationToken);

    public Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken) =>
        Queue.CompleteAsync(pendingIds, message, cancellationToken);

    public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken) =>
        Queue.DropAsync(pendingIds, cancellationToken);

    public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
        Queue.RescheduleAsync(pendingIds, dueAt, cancellationToken);

    // Outbox

    public Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        Outbox.EnqueueAsync(message, cancellationToken);

    public Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken) =>
        Outbox.LeaseDueAsync(now, limit, lease, cancellationToken);

    public Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) =>
        Outbox.MarkSentAsync(id, at, cancellationToken);

    public Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken) =>
        Outbox.MarkRetryAsync(id, nextAttemptAt, errorCode, error, countAttempt, cancellationToken);

    public Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken) =>
        Outbox.MarkDeadAsync(id, errorCode, error, cancellationToken);

    public Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) =>
        Outbox.RequeueAsync(id, at, cancellationToken);

    public Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken) =>
        Outbox.QueryAsync(query, cancellationToken);

    public Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken) =>
        Outbox.GetDeliveriesForEventAsync(eventId, cancellationToken);

    public Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken) =>
        Outbox.GetMessagesForEventAsync(eventId, cancellationToken);

    public Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken) =>
        Outbox.WasNotifiedAboutObjectAsync(ruleId, objectGuid, since, cancellationToken);

    public Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Outbox.CountSentAsync(channelId, ruleId, since, cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken) =>
        Outbox.CountPendingByChannelAsync(cancellationToken);

    // SecurityEvents

    public Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken) =>
        SecurityEvents.GetCursorAsync(source, cancellationToken);

    public Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken) =>
        SecurityEvents.AppendAsync(events, cursor, cancellationToken);

    public Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken) =>
        SecurityEvents.FindAsync(criteria, cancellationToken);

    public Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken) =>
        SecurityEvents.GetLastSeenAsync(computer, eventCode, cancellationToken);

    // Expiry

    public Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken) =>
        Expiry.TryRecordAsync(notice, cancellationToken);

    public Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken) =>
        Expiry.ReplaceUpcomingAsync(kind, upcoming, cancellationToken);

    public Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken) =>
        Expiry.GetUpcomingAsync(cancellationToken);

    // DigestRuns

    public Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken) =>
        DigestRuns.GetLastPeriodEndAsync(cancellationToken);

    public Task RecordAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, int eventCount, IReadOnlyList<long> outboxIds, CancellationToken cancellationToken) =>
        DigestRuns.RecordAsync(periodStart, periodEnd, eventCount, outboxIds, cancellationToken);

    // Config

    public Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken) =>
        Config.GetAsync(cancellationToken);

    public Task<ConfigEnvelope> SaveAsync(WatchdogConfiguration configuration, long expectedVersion, PrincipalRef by, string summary, IReadOnlyList<SecretChange> secretChanges, CancellationToken cancellationToken) =>
        Config.SaveAsync(configuration, expectedVersion, by, summary, secretChanges, cancellationToken);

    public Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken) =>
        Config.GetAuditAsync(before, limit, cancellationToken);

    // Secrets

    public Task<StoredSecret?> GetAsync(string name, CancellationToken cancellationToken) =>
        Secrets.GetAsync(name, cancellationToken);

    public Task SetAsync(string name, SecretValue value, string binding, CancellationToken cancellationToken) =>
        Secrets.SetAsync(name, value, binding, cancellationToken);

    public Task RemoveAsync(string name, CancellationToken cancellationToken) =>
        Secrets.RemoveAsync(name, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken) =>
        Secrets.ListAsync(cancellationToken);

    // Maintenance

    public Task<PurgeResult> PurgeAsync(RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken) =>
        Maintenance.PurgeAsync(retention, now, cancellationToken);

    public Task<string> BackupAsync(CancellationToken cancellationToken) =>
        Maintenance.BackupAsync(cancellationToken);

    public Task<StorageStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        Maintenance.GetStatusAsync(cancellationToken);
}
