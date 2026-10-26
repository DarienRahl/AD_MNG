using System.Globalization;
using System.Text.Json;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>The <c>outbox</c> and <c>outbox_events</c> tables (payload NULL after a successful send, spec 16.3); a link
/// keeps how an alert showed its event (severity, actor pending – schema 3). A message to a user (README U1b) stores no
/// subject and no recipients outside its payload.</summary>
internal static class OutboxRows
{
    /// <summary>Columns to select for <see cref="ReadMessage"/>.</summary>
    public const string MessageColumns =
        "id, message_id, channel_type, channel_id, kind, rule_id, severity, attempts, created_at, next_attempt_at, recipients, " +
        "target_key, sent_with_pending, payload, (SELECT group_concat(event_id) FROM outbox_events WHERE outbox_id = outbox.id), " +
        "(SELECT group_concat(event_id || ':' || severity || ':' || pending) FROM outbox_events " +
        "WHERE outbox_id = outbox.id AND severity IS NOT NULL AND pending IS NOT NULL)";

    /// <summary>Columns to select for <see cref="ReadItem"/>.</summary>
    public const string ItemColumns =
        "id, message_id, channel_type, channel_id, kind, status, attempts, created_at, next_attempt_at, sent_at, last_error_code, " +
        "last_error, subject, recipient_count, (SELECT count(*) FROM outbox_events WHERE outbox_id = outbox.id)";

    /// <summary>Statuses that still wait for delivery.</summary>
    public static readonly string WaitingStatuses =
        $"{(int)OutboxStatus.Pending}, {(int)OutboxStatus.Sending}, {(int)OutboxStatus.Retrying}";

    /// <summary>Inserts one message (status Pending, attempts as given) with its event links; returns the id. A duplicate
    /// MessageId or an unknown event id fails the transaction.</summary>
    public static long Insert(SqliteConnection connection, SqliteTransaction transaction, OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var command = SqliteValues.Command(
            connection,
            transaction,
            """
            INSERT INTO outbox (message_id, channel_type, channel_id, kind, rule_id, severity, status, attempts, created_at,
              next_attempt_at, subject, recipient_count, recipients, target_key, sent_with_pending, payload)
            VALUES ($messageId, $channelType, $channelId, $kind, $rule, $severity, $status, $attempts, $created, $next, $subject,
              $recipientCount, $recipients, $targetKey, $pendingAttribution, $payload)
            RETURNING id;
            """,
            ("$messageId", message.MessageId),
            ("$channelType", (int)message.ChannelType),
            ("$channelId", message.ChannelId),
            ("$kind", (int)message.Kind),
            ("$rule", SqliteValues.OrNull(message.RuleId)),
            ("$severity", (int)message.Severity),
            ("$status", (int)OutboxStatus.Pending),
            ("$attempts", message.Attempts),
            ("$created", SqliteValues.ToUnixMs(message.CreatedAt)),
            ("$next", SqliteValues.ToUnixMs(message.NextAttemptAt)),
            // A message to a user keeps neither its subject nor its recipients in the delivery log (README U1b): both
            // are directory data and live only in the payload, which is dropped once sent.
            ("$subject", message.Email?.UserMessage is not null ? DBNull.Value : SqliteValues.OrNull(message.Email?.Subject ?? message.Http?.Title)),
            ("$recipientCount", message.Email?.UserMessage is { } envelope ? envelope.RecipientCount : message.Recipients.Count),
            ("$recipients", message.Recipients.Count == 0
                ? DBNull.Value
                : JsonSerializer.Serialize([.. message.Recipients], WatchdogStorageJsonContext.Default.ListString)),
            ("$targetKey", SqliteValues.OrNull(message.TargetKey)),
            ("$pendingAttribution", message.SentWithPendingAttribution ? 1 : 0),
            ("$payload", message.Email is null && message.Http is null ? DBNull.Value : BlobCodec.EncodeOutbox(message.Email, message.Http)));
        var id = (long)command.ExecuteScalar()!;

        using var link = SqliteValues.Command(
            connection,
            transaction,
            "INSERT OR IGNORE INTO outbox_events (outbox_id, event_id, severity, pending) VALUES ($outbox, $event, $severity, $pending);");
        link.Parameters.AddWithValue("$outbox", id);
        var eventParameter = link.Parameters.Add("$event", SqliteType.Integer);
        var severityParameter = link.Parameters.Add("$severity", SqliteType.Integer);
        var pendingParameter = link.Parameters.Add("$pending", SqliteType.Integer);
        var states = message.EventStates.DistinctBy(s => s.EventId).ToDictionary(s => s.EventId);
        foreach (var eventId in message.EventIds)
        {
            eventParameter.Value = eventId;
            severityParameter.Value = states.TryGetValue(eventId, out var state) ? (int)state.Severity : DBNull.Value;
            pendingParameter.Value = state is null ? DBNull.Value : state.PendingAttribution ? 1 : 0;
            link.ExecuteNonQuery();
        }

        return id;
    }

    /// <summary>Maps a row selected with <see cref="MessageColumns"/>.</summary>
    public static OutboxMessage ReadMessage(SqliteDataReader reader)
    {
        var payload = SqliteValues.BlobOrNull(reader, 13) is { } blob ? BlobCodec.DecodeOutbox(blob) : null;
        var recipients = SqliteValues.StringOrNull(reader, 10) is { } json
            ? JsonSerializer.Deserialize(json, WatchdogStorageJsonContext.Default.ListString) ?? []
            : [];
        return new OutboxMessage
        {
            Id = reader.GetInt64(0),
            MessageId = reader.GetString(1),
            ChannelType = (DeliveryChannelType)reader.GetInt32(2),
            ChannelId = reader.GetString(3),
            Kind = (DeliveryKind)reader.GetInt32(4),
            RuleId = SqliteValues.StringOrNull(reader, 5),
            Severity = (Severity)reader.GetInt32(6),
            Attempts = reader.GetInt32(7),
            CreatedAt = SqliteValues.FromUnixMs(reader.GetInt64(8)),
            NextAttemptAt = SqliteValues.FromUnixMs(reader.GetInt64(9)),
            Recipients = recipients,
            TargetKey = SqliteValues.StringOrNull(reader, 11),
            SentWithPendingAttribution = reader.GetInt32(12) != 0,
            Email = payload?.Email,
            Http = payload?.Http,
            EventIds = ParseIds(SqliteValues.StringOrNull(reader, 14)),
            EventStates = ParseStates(SqliteValues.StringOrNull(reader, 15)),
        };
    }

    /// <summary>Maps a row selected with <see cref="ItemColumns"/> (the delivery log; no payload, no recipients).</summary>
    public static OutboxItemDto ReadItem(SqliteDataReader reader)
    {
        var status = (OutboxStatus)reader.GetInt32(5);
        var waiting = status is OutboxStatus.Pending or OutboxStatus.Sending or OutboxStatus.Retrying;
        return new OutboxItemDto
        {
            Id = reader.GetInt64(0),
            MessageId = reader.GetString(1),
            ChannelType = (DeliveryChannelType)reader.GetInt32(2),
            ChannelId = reader.GetString(3),
            Kind = (DeliveryKind)reader.GetInt32(4),
            Status = status,
            Attempts = reader.GetInt32(6),
            CreatedAt = SqliteValues.FromUnixMs(reader.GetInt64(7)),
            NextAttemptAt = waiting ? SqliteValues.FromUnixMs(reader.GetInt64(8)) : null,
            SentAt = SqliteValues.FromUnixMsOrNull(reader, 9),
            LastErrorCode = SqliteValues.StringOrNull(reader, 10),
            LastError = SqliteValues.StringOrNull(reader, 11),
            Subject = SqliteValues.StringOrNull(reader, 12),
            RecipientCount = reader.GetInt32(13),
            EventCount = reader.GetInt32(14),
        };
    }

    /// <summary>"eventId:severity:pending,…" → the per-event states ordered by event id.</summary>
    private static List<OutboxEventState> ParseStates(string? csv) =>
        string.IsNullOrEmpty(csv)
            ? []
            : [.. csv.Split(',')
                .Select(v => v.Split(':'))
                .Select(p => new OutboxEventState(
                    long.Parse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture),
                    (Severity)int.Parse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture),
                    p[2] != "0"))
                .OrderBy(s => s.EventId)];

    private static List<long> ParseIds(string? csv) =>
        string.IsNullOrEmpty(csv)
            ? []
            : [.. csv.Split(',').Select(v => long.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture)).Order()];
}
