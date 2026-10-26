using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="IOutboxStore"/> over <c>outbox</c> / <c>outbox_events</c>: leasing (crash-safe, never counts an attempt),
/// sent messages lose their payload (spec 16.3), dead messages keep it for a manual retry, the delivery log pages
/// backwards by id. Messages to users (README U1b) are leased after every other due message, so a bulk send from AD
/// Manager never delays an alert by more than one rate-limit token.
/// </summary>
public sealed class SqliteOutboxStore : IOutboxStore
{
    private const int MaxPageSize = 500;

    private readonly SqliteDatabase _database;

    internal SqliteOutboxStore(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _database.WriteAsync((c, t) => OutboxRows.Insert(c, t, message), cancellationToken);
    }

    public Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken) =>
        _database.WriteAsync<IReadOnlyList<OutboxMessage>>(
            (connection, transaction) =>
            {
                var nowMs = SqliteValues.ToUnixMs(now);
                var ids = SqliteValues.Query(
                    connection,
                    transaction,
                    """
                    SELECT id FROM outbox
                    WHERE (status IN ($pending, $retrying) AND next_attempt_at <= $now)
                       OR (status = $sending AND lease_until IS NOT NULL AND lease_until <= $now)
                    ORDER BY kind = $userMessage, next_attempt_at, id LIMIT $limit;
                    """,
                    r => r.GetInt64(0),
                    ("$userMessage", (int)DeliveryKind.UserMessage),
                    ("$pending", (int)OutboxStatus.Pending),
                    ("$retrying", (int)OutboxStatus.Retrying),
                    ("$sending", (int)OutboxStatus.Sending),
                    ("$now", nowMs),
                    ("$limit", Math.Max(0, limit)));
                var result = new List<OutboxMessage>(ids.Count);
                foreach (var id in ids)
                {
                    SqliteValues.Execute(
                        connection,
                        transaction,
                        "UPDATE outbox SET status = $sending, lease_until = $until WHERE id = $id;",
                        ("$sending", (int)OutboxStatus.Sending),
                        ("$until", SqliteValues.ToUnixMs(now + lease)),
                        ("$id", id));
                    result.AddRange(SqliteValues.Query(
                        connection,
                        transaction,
                        $"SELECT {OutboxRows.MessageColumns} FROM outbox WHERE id = $id;",
                        OutboxRows.ReadMessage,
                        ("$id", id)));
                }

                return result;
            },
            cancellationToken);

    public Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (c, t) => SqliteValues.Execute(
                c,
                t,
                "UPDATE outbox SET status = $sent, sent_at = $at, lease_until = NULL, attempts = attempts + 1, payload = NULL WHERE id = $id;",
                ("$sent", (int)OutboxStatus.Sent),
                ("$at", SqliteValues.ToUnixMs(at)),
                ("$id", id)),
            cancellationToken);

    public Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errorCode);
        return _database.WriteAsync(
            (c, t) => SqliteValues.Execute(
                c,
                t,
                """
                UPDATE outbox SET status = $retrying, next_attempt_at = $next, lease_until = NULL, last_error_code = $code,
                  last_error = $error, attempts = attempts + $increment
                WHERE id = $id;
                """,
                ("$retrying", (int)OutboxStatus.Retrying),
                ("$next", SqliteValues.ToUnixMs(nextAttemptAt)),
                ("$code", errorCode),
                ("$error", SqliteValues.OrNull(error)),
                ("$increment", countAttempt ? 1 : 0),
                ("$id", id)),
            cancellationToken);
    }

    public Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errorCode);
        return _database.WriteAsync(
            (c, t) => SqliteValues.Execute(
                c,
                t,
                "UPDATE outbox SET status = $dead, lease_until = NULL, last_error_code = $code, last_error = $error, attempts = attempts + 1 WHERE id = $id;",
                ("$dead", (int)OutboxStatus.Dead),
                ("$code", errorCode),
                ("$error", SqliteValues.OrNull(error)),
                ("$id", id)),
            cancellationToken);
    }

    public Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (c, t) => SqliteValues.Execute(
                c,
                t,
                "UPDATE outbox SET status = $pending, next_attempt_at = $at, lease_until = NULL, attempts = 0 WHERE id = $id AND status = $dead;",
                ("$pending", (int)OutboxStatus.Pending),
                ("$dead", (int)OutboxStatus.Dead),
                ("$at", SqliteValues.ToUnixMs(at)),
                ("$id", id)) == 1,
            cancellationToken);

    public Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, MaxPageSize);
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        if (query.Before is { } before)
        {
            conditions.Add("id < $before");
            parameters.Add(("$before", before));
        }

        if (query.Status is { } status)
        {
            conditions.Add("status = $status");
            parameters.Add(("$status", (int)status));
        }

        if (!string.IsNullOrEmpty(query.ChannelId))
        {
            conditions.Add("channel_id = $channel COLLATE NOCASE");
            parameters.Add(("$channel", query.ChannelId));
        }

        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        var rows = _database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT {OutboxRows.ItemColumns} FROM outbox{where} ORDER BY id DESC LIMIT {limit + 1};",
            OutboxRows.ReadItem,
            [.. parameters]));
        return Task.FromResult(new OutboxPage { Items = [.. rows.Take(limit)], HasMore = rows.Count > limit });
    }

    public Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EventDeliveryDto>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            """
            SELECT o.id, o.channel_type, o.channel_id, o.kind, o.status, o.created_at, o.sent_at, o.last_error_code
            FROM outbox o JOIN outbox_events oe ON oe.outbox_id = o.id
            WHERE oe.event_id = $event ORDER BY o.id;
            """,
            r => new EventDeliveryDto
            {
                OutboxId = r.GetInt64(0),
                ChannelType = (DeliveryChannelType)r.GetInt32(1),
                ChannelId = r.GetString(2),
                Kind = (DeliveryKind)r.GetInt32(3),
                Status = (OutboxStatus)r.GetInt32(4),
                CreatedAt = SqliteValues.FromUnixMs(r.GetInt64(5)),
                SentAt = SqliteValues.FromUnixMsOrNull(r, 6),
                LastErrorCode = SqliteValues.StringOrNull(r, 7),
            },
            ("$event", eventId))));

    public Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT {OutboxRows.MessageColumns} FROM outbox WHERE id IN (SELECT outbox_id FROM outbox_events WHERE event_id = $event) ORDER BY id;",
            OutboxRows.ReadMessage,
            ("$event", eventId))));

    public Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return Task.FromResult(_database.Read(c => SqliteValues.Scalar<long>(
            c,
            null,
            """
            SELECT EXISTS (
              SELECT 1 FROM outbox o
              JOIN outbox_events oe ON oe.outbox_id = o.id
              JOIN events e ON e.id = oe.event_id
              WHERE o.kind IN ($alert, $followUp, $health) AND o.rule_id = $rule COLLATE NOCASE AND o.created_at >= $since
                AND e.object_guid = $guid);
            """,
            ("$alert", (int)DeliveryKind.Alert),
            ("$followUp", (int)DeliveryKind.FollowUp),
            ("$health", (int)DeliveryKind.Health),
            ("$rule", ruleId),
            ("$since", SqliteValues.ToUnixMs(since)),
            ("$guid", SqliteValues.ToBlob(objectGuid))) == 1));
    }

    public Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        var ruleFilter = ruleId is null ? string.Empty : " AND rule_id = $rule COLLATE NOCASE";
        return Task.FromResult((int)_database.Read(c => SqliteValues.Scalar<long>(
            c,
            null,
            $"SELECT count(*) FROM outbox WHERE status = $sent AND sent_at >= $since AND channel_id = $channel COLLATE NOCASE{ruleFilter};",
            ("$sent", (int)OutboxStatus.Sent),
            ("$since", SqliteValues.ToUnixMs(since)),
            ("$channel", channelId),
            ("$rule", ruleId))));
    }

    public Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channel, count) in _database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT channel_id, count(*) FROM outbox WHERE status IN ({OutboxRows.WaitingStatuses}) GROUP BY channel_id;",
            r => (r.GetString(0), r.GetInt32(1)))))
        {
            counts[channel] = counts.GetValueOrDefault(channel) + count;
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
    }
}
