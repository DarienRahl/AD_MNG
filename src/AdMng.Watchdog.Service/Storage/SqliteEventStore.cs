using System.Runtime.CompilerServices;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="IEventStore"/> over the <c>events</c> table: AUTOINCREMENT ids (never reused after retention), cursor paging
/// with every <see cref="EventQuery"/> filter (<see cref="EventQuerySql"/>), attribution and evaluation updates, acknowledgement
/// and the in-process long-poll (<see cref="WaitForNewerAsync"/>, timeout on the injected <see cref="TimeProvider"/>).
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly EventAppendedNotifier _notifier;

    internal SqliteEventStore(SqliteDatabase database, TimeProvider timeProvider, EventAppendedNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(notifier);
        _database = database;
        _timeProvider = timeProvider;
        _notifier = notifier;
    }

    public async Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var stored = await _database.WriteAsync((c, t) => EventRows.Insert(c, t, watchEvent), cancellationToken).ConfigureAwait(false);
        _notifier.Notify();
        return stored.Id;
    }

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken) => QueryAsync(query, textMatchesSources: true, cancellationToken);

    /// <summary><see cref="QueryAsync(EventQuery, CancellationToken)"/>; with <paramref name="textMatchesSources"/> false the
    /// text filter ignores workstation and IP (Minimal viewers).</summary>
    public Task<EventPage> QueryAsync(EventQuery query, bool textMatchesSources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new EventQuerySql(query, textMatchesSources);

        // One snapshot for the page and LatestId: the live feed advances its cursor to LatestId when HasMore is false,
        // so an event committed between two autocommit statements would be skipped for good.
        var page = _database.ReadSnapshot((connection, transaction) =>
        {
            var rows = SqliteValues.Query(connection, transaction, sql.Sql, EventRows.Read, [.. sql.Parameters]);
            var latest = SqliteValues.Scalar<long>(connection, transaction, "SELECT coalesce(max(id), 0) FROM events;");
            return new EventPage { Items = [.. rows.Take(sql.Limit)], HasMore = rows.Count > sql.Limit, LatestId = latest };
        });
        return Task.FromResult(page);
    }

    public async Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Arm the wake-up before the check, so an append between the check and the wait is never missed.
        var appended = _notifier.Next;
        if (LatestId() > afterId)
        {
            return;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, _timeProvider, timer.Token);
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(appended, delay).ConfigureAwait(false);
                if (completed == delay)
                {
                    await delay.ConfigureAwait(false);   // throws when cancelled
                    return;
                }

                appended = _notifier.Next;
                if (LatestId() > afterId)
                {
                    return;
                }
            }
        }
        finally
        {
            await timer.CancelAsync().ConfigureAwait(false);
        }
    }

    public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => EventRows.Load(c, null, id)));

    public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT {EventRows.Columns} FROM events WHERE event_id = $eventId;",
            EventRows.Read,
            ("$eventId", SqliteValues.ToBlob(eventId))).FirstOrDefault()));

    public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken)
    {
        if (changeSetId == Guid.Empty)
        {
            return Task.FromResult<IReadOnlyList<WatchEvent>>([]);
        }

        return Task.FromResult<IReadOnlyList<WatchEvent>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT {EventRows.Columns} FROM events WHERE change_set_id = $changeSet ORDER BY id;",
            EventRows.Read,
            ("$changeSet", SqliteValues.ToBlob(changeSetId)))));
    }

    public async IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Pages of 500 by id, each materialized before it is yielded (no reader stays open across an await).
        var lastId = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = lastId;
            var page = _database.Read(c => SqliteValues.Query(
                c,
                null,
                $"SELECT {EventRows.Columns} FROM events WHERE id > $after AND detected_at >= $from AND detected_at < $to ORDER BY id LIMIT 500;",
                EventRows.Read,
                ("$after", after),
                ("$from", SqliteValues.ToUnixMs(from)),
                ("$to", SqliteValues.ToUnixMs(to))));
            foreach (var watchEvent in page)
            {
                yield return watchEvent;
            }

            if (page.Count < 500)
            {
                yield break;
            }

            lastId = page[^1].Id;
            await Task.Yield();
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WatchEvent>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"""
            SELECT {EventRows.Columns} FROM events
            WHERE actor_status = $pending AND next_attribution_at IS NOT NULL AND next_attribution_at <= $due
            ORDER BY next_attribution_at, id LIMIT $limit;
            """,
            EventRows.Read,
            ("$pending", (int)AttributionStatus.Pending),
            ("$due", SqliteValues.ToUnixMs(dueBefore)),
            ("$limit", Math.Max(0, limit)))));

    public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                if (EventRows.Load(connection, transaction, id) is not { } current)
                {
                    return;
                }

                var updated = EventSeverities.WithAttribution(current, update);
                EventRows.UpdateAttribution(connection, transaction, updated, update.NextAttemptAt);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WatchEvent>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT {EventRows.Columns} FROM events WHERE rules_state = 0 ORDER BY id LIMIT $limit;",
            EventRows.Read,
            ("$limit", Math.Max(0, limit)))));

    public Task CommitEvaluationAsync(
        IReadOnlyList<EventEvaluation> evaluations,
        IReadOnlyList<PendingNotification> notifications,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        ArgumentNullException.ThrowIfNull(notifications);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                foreach (var evaluation in evaluations)
                {
                    if (EventRows.Load(connection, transaction, evaluation.EventId) is { } current)
                    {
                        EventRows.UpdateEvaluation(
                            connection,
                            transaction,
                            current with { Severity = evaluation.Severity, MatchedRules = [.. evaluation.MatchedRules] });
                    }
                }

                PendingRows.Insert(connection, transaction, notifications, _timeProvider.GetUtcNow());
            },
            cancellationToken);
    }

    public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(by);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                if (EventRows.Load(connection, transaction, id) is not { } current)
                {
                    return false;
                }

                if (current.AcknowledgedAt is null)
                {
                    EventRows.UpdateAcknowledgement(connection, transaction, current with { AcknowledgedAt = at, AcknowledgedBy = by, AcknowledgeComment = comment });
                }

                return true;
            },
            cancellationToken);
    }

    public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(by);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                var count = 0;
                foreach (var id in ids.Distinct())
                {
                    if (EventRows.Load(connection, transaction, id) is { AcknowledgedAt: null } current
                        && EventRows.UpdateAcknowledgement(connection, transaction, current with { AcknowledgedAt = at, AcknowledgedBy = by, AcknowledgeComment = comment }))
                    {
                        count++;
                    }
                }

                return count;
            },
            cancellationToken);
    }

    public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult((int)_database.Read(c => SqliteValues.Scalar<long>(
            c,
            null,
            "SELECT count(*) FROM events WHERE object_guid = $guid AND kind = $kind AND detected_at >= $since;",
            ("$guid", SqliteValues.ToBlob(objectGuid)),
            ("$kind", (int)kind),
            ("$since", SqliteValues.ToUnixMs(since)))));

    public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var counts = _database.Read(c =>
        {
            var bySeverity = new Dictionary<Severity, int>();
            int total = 0, unacknowledged = 0, unacknowledgedHigh = 0;
            foreach (var (severity, count, unacked) in SqliteValues.Query(
                c,
                null,
                "SELECT severity, count(*), sum(acked_at IS NULL) FROM events WHERE detected_at >= $since GROUP BY severity;",
                r => ((Severity)r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)),
                ("$since", SqliteValues.ToUnixMs(since))))
            {
                bySeverity[severity] = count;
                total += count;
                unacknowledged += unacked;
                if (severity >= Severity.High)
                {
                    unacknowledgedHigh += unacked;
                }
            }

            return new EventCountsDto
            {
                Total = total,
                BySeverity = bySeverity,
                Unacknowledged = unacknowledged,
                UnacknowledgedHighOrAbove = unacknowledgedHigh,
            };
        });
        return Task.FromResult(counts);
    }

    public Task<long> GetLatestIdAsync(CancellationToken cancellationToken) => Task.FromResult(LatestId());

    private long LatestId() => _database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT coalesce(max(id), 0) FROM events;"));
}
