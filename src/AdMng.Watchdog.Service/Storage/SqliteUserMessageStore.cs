using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.UserMessages;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary><see cref="IUserMessageStore"/> over <c>user_messages</c> (schema 6) and <c>outbox</c>: the idempotency check,
/// the limits and both inserts run in ONE write transaction, so concurrent requests can neither exceed a limit nor enqueue
/// one id twice.</summary>
public sealed class SqliteUserMessageStore : IUserMessageStore
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly SqliteDatabase _database;

    internal SqliteUserMessageStore(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<UserMessageSubmission> SubmitAsync(
        UserMessageAuditRecord record,
        OutboxMessage message,
        int maxPerHour,
        int maxPerDay,
        long maxStoredImageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(message);
        return _database.WriteAsync((connection, transaction) =>
        {
            var id = Key(record.MessageId);
            var earlier = SqliteValues.Query(
                connection,
                transaction,
                "SELECT by_sid, by_account, content_hash, outbox_id FROM user_messages WHERE message_id = $id;",
                r => (Sid: SqliteValues.StringOrNull(r, 0), Account: r.GetString(1), Hash: r.GetString(2), OutboxId: r.IsDBNull(3) ? (long?)null : r.GetInt64(3)),
                ("$id", id));
            if (earlier.Count > 0)
            {
                var row = earlier[0];
                var sameCaller = row.Sid is not null && record.BySid is not null
                    ? string.Equals(row.Sid, record.BySid, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(row.Account, record.ByAccount, StringComparison.OrdinalIgnoreCase);
                return sameCaller && string.Equals(row.Hash, record.ContentHash, StringComparison.Ordinal)
                    ? new UserMessageSubmission(UserMessageSubmitOutcome.Duplicate, row.OutboxId)
                    : new UserMessageSubmission(UserMessageSubmitOutcome.Conflict);
            }

            var now = record.SubmittedAt;
            if (Limit(connection, transaction, now, Hour, maxPerHour) is { } hourly)
            {
                return new UserMessageSubmission(UserMessageSubmitOutcome.HourlyLimit, RetryAfter: hourly);
            }

            if (Limit(connection, transaction, now, Day, maxPerDay) is { } daily)
            {
                return new UserMessageSubmission(UserMessageSubmitOutcome.DailyLimit, RetryAfter: daily);
            }

            // U1c: the images held in outbox payloads are capped; a message without images is never refused here.
            var imageBytes = OutboxRows.ImageBytes(message);
            if (imageBytes > 0 && StoredImageBytes(connection, transaction) + imageBytes > maxStoredImageBytes)
            {
                return new UserMessageSubmission(UserMessageSubmitOutcome.ImageStorageFull, RetryAfter: TimeSpan.FromHours(1));
            }

            var outboxId = OutboxRows.Insert(connection, transaction, message);
            SqliteValues.Execute(
                connection,
                transaction,
                """
                INSERT INTO user_messages (message_id, submitted_at, by_sid, by_account, recipient_count, recipients_hash, content_hash, outbox_id)
                VALUES ($id, $at, $sid, $account, $count, $recipients, $content, $outbox);
                """,
                ("$id", id),
                ("$at", SqliteValues.ToUnixMs(now)),
                ("$sid", SqliteValues.OrNull(record.BySid)),
                ("$account", record.ByAccount),
                ("$count", record.RecipientCount),
                ("$recipients", record.RecipientsHash),
                ("$content", record.ContentHash),
                ("$outbox", outboxId));
            return new UserMessageSubmission(UserMessageSubmitOutcome.Created, outboxId);
        }, cancellationToken);
    }

    /// <summary>Without an image cap (messages without images).</summary>
    public Task<UserMessageSubmission> SubmitAsync(UserMessageAuditRecord record, OutboxMessage message, int maxPerHour, int maxPerDay, CancellationToken cancellationToken) =>
        SubmitAsync(record, message, maxPerHour, maxPerDay, long.MaxValue, cancellationToken);

    public Task<(int LastHour, int LastDay)> CountAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => (Count(c, null, now - Hour), Count(c, null, now - Day))));

    public Task<long> StoredImageBytesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => StoredImageBytes(c, null)));

    private static long StoredImageBytes(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteValues.Scalar<long>(connection, transaction, "SELECT coalesce(sum(image_bytes), 0) FROM outbox WHERE image_bytes > 0;");

    /// <summary>The wait until the oldest submission of the window leaves it, when the window is full; else null.</summary>
    private static TimeSpan? Limit(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, TimeSpan window, int max)
    {
        var since = now - window;
        var count = Count(connection, transaction, since);
        var limit = Math.Max(1, max);
        if (count < limit)
        {
            return null;
        }

        // The row that has to leave the window for one more message to fit.
        var offset = count - limit;
        var oldest = SqliteValues.Scalar<long>(
            connection,
            transaction,
            $"SELECT submitted_at FROM user_messages WHERE submitted_at > $since ORDER BY submitted_at LIMIT 1 OFFSET {offset};",
            ("$since", SqliteValues.ToUnixMs(since)));
        var wait = SqliteValues.FromUnixMs(oldest) + window - now;
        return wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
    }

    private static int Count(SqliteConnection connection, SqliteTransaction? transaction, DateTimeOffset since) =>
        (int)SqliteValues.Scalar<long>(
            connection,
            transaction,
            "SELECT count(*) FROM user_messages WHERE submitted_at > $since;",
            ("$since", SqliteValues.ToUnixMs(since)));

    private static string Key(Guid id) => id.ToString("N");
}
