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

    /// <summary>Re-planned rows (<c>INotificationQueueStore.ReplanAsync</c>): the waiting row with the same (event, target
    /// key, follow-up) takes rule, target, severity, bucket and hold and keeps the earlier due time; a missing one is
    /// inserted when <paramref name="insertMissing"/>.</summary>
    public static void Replan(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingNotification> notifications,
        bool insertMissing, DateTimeOffset now)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        using var command = SqliteValues.Command(
            connection,
            transaction,
            insertMissing
                ? """
                  INSERT INTO pending_notifications (event_id, rule_id, target, target_key, severity, bucket_key, due_at,
                    hold_until, is_follow_up, reply_to, created_at)
                  VALUES ($event, $rule, $target, $targetKey, $severity, $bucket, $due, $hold, $followUp, $replyTo, $created)
                  ON CONFLICT (event_id, target_key, is_follow_up) DO UPDATE SET
                    rule_id = excluded.rule_id, target = excluded.target, severity = excluded.severity,
                    bucket_key = excluded.bucket_key, hold_until = excluded.hold_until, due_at = min(due_at, excluded.due_at);
                  """
                : """
                  UPDATE pending_notifications SET rule_id = $rule, target = $target, severity = $severity, bucket_key = $bucket,
                    hold_until = $hold, due_at = min(due_at, $due)
                  WHERE event_id = $event AND target_key = $targetKey AND is_follow_up = $followUp;
                  """);
        foreach (var notification in notifications)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$event", notification.EventId);
            command.Parameters.AddWithValue("$rule", notification.RuleId);
            command.Parameters.AddWithValue("$target", BlobCodec.Encode(notification.Target, WatchdogStorageJsonContext.Default.NotificationTarget));
            command.Parameters.AddWithValue("$targetKey", notification.Target.Key);
            command.Parameters.AddWithValue("$severity", (int)notification.Severity);
            command.Parameters.AddWithValue("$bucket", notification.BucketKey);
            command.Parameters.AddWithValue("$due", SqliteValues.ToUnixMs(notification.DueAt));
            command.Parameters.AddWithValue("$hold", SqliteValues.ToUnixMsOrNull(notification.HoldForAttributionUntil));
            command.Parameters.AddWithValue("$followUp", notification.IsFollowUp ? 1 : 0);
            if (insertMissing)
            {
                command.Parameters.AddWithValue("$replyTo", SqliteValues.OrNull(notification.ReplyToMessageId));
                command.Parameters.AddWithValue("$created", SqliteValues.ToUnixMs(notification.CreatedAt == default ? now : notification.CreatedAt));
            }

            command.ExecuteNonQuery();
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
