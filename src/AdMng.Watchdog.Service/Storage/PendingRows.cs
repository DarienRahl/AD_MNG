using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>The <c>pending_notifications</c> table (unique per event × target key × follow-up; cascades with its event).</summary>
internal static class PendingRows
{
    /// <summary>Columns to select for <see cref="Read"/>.</summary>
    public const string Columns =
        "id, event_id, rule_id, target, severity, bucket_key, due_at, hold_until, is_follow_up, reply_to, created_at";

    /// <summary>Inserts rows, ignoring duplicates by (event, target key, follow-up); an unknown event id violates the
    /// foreign key and fails the whole transaction.</summary>
    public static void Insert(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingNotification> notifications, DateTimeOffset now)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        using var insert = SqliteValues.Command(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO pending_notifications (event_id, rule_id, target, target_key, severity, bucket_key, due_at,
              hold_until, is_follow_up, reply_to, created_at)
            VALUES ($event, $rule, $target, $targetKey, $severity, $bucket, $due, $hold, $followUp, $replyTo, $created);
            """);
        foreach (var notification in notifications)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$event", notification.EventId);
            insert.Parameters.AddWithValue("$rule", notification.RuleId);
            insert.Parameters.AddWithValue("$target", BlobCodec.Encode(notification.Target, WatchdogStorageJsonContext.Default.NotificationTarget));
            insert.Parameters.AddWithValue("$targetKey", notification.Target.Key);
            insert.Parameters.AddWithValue("$severity", (int)notification.Severity);
            insert.Parameters.AddWithValue("$bucket", notification.BucketKey);
            insert.Parameters.AddWithValue("$due", SqliteValues.ToUnixMs(notification.DueAt));
            insert.Parameters.AddWithValue("$hold", SqliteValues.ToUnixMsOrNull(notification.HoldForAttributionUntil));
            insert.Parameters.AddWithValue("$followUp", notification.IsFollowUp ? 1 : 0);
            insert.Parameters.AddWithValue("$replyTo", SqliteValues.OrNull(notification.ReplyToMessageId));
            insert.Parameters.AddWithValue("$created", SqliteValues.ToUnixMs(notification.CreatedAt == default ? now : notification.CreatedAt));
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Maps a row selected with <see cref="Columns"/>.</summary>
    public static PendingNotification Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        EventId = reader.GetInt64(1),
        RuleId = reader.GetString(2),
        Target = ReadTarget((byte[])reader.GetValue(3)),
        Severity = (Severity)reader.GetInt32(4),
        BucketKey = reader.GetString(5),
        DueAt = SqliteValues.FromUnixMs(reader.GetInt64(6)),
        HoldForAttributionUntil = SqliteValues.FromUnixMsOrNull(reader, 7),
        IsFollowUp = reader.GetInt32(8) != 0,
        ReplyToMessageId = SqliteValues.StringOrNull(reader, 9),
        CreatedAt = SqliteValues.FromUnixMs(reader.GetInt64(10)),
    };

    private static NotificationTarget ReadTarget(byte[] blob)
    {
        var target = BlobCodec.Decode(blob, WatchdogStorageJsonContext.Default.NotificationTarget);
        return target with { Recipients = target.Recipients ?? [] };
    }
}
