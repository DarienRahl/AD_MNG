using AdMng.Watchdog.Contracts.Events;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The <c>events</c> table: the payload (the full <see cref="WatchEvent"/>, stored with <c>Id = 0</c>) is authoritative and
/// rewritten on every change; the other columns are derived lookup values (normalized DNs, lower-case search text and
/// tag/rule lists, upper-case actor SID). The event's own severity (<c>own_severity</c>, schema 2) is a column of its own;
/// rows stored before schema 2 keep it NULL (the migration does not rewrite them) and are read with their payload
/// severity – stored together with the <c>severity</c> column by every write – until an attribution update fills it.
/// </summary>
internal static class EventRows
{
    /// <summary>Columns to select for <see cref="Read"/>.</summary>
    public const string Columns = "id, payload, own_severity";

    /// <summary>Inserts one event (unevaluated; a pending attribution is due at detection time) and returns it with its id.</summary>
    public static WatchEvent Insert(SqliteConnection connection, SqliteTransaction transaction, WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        watchEvent = watchEvent with { OwnSeverity = watchEvent.OwnSeverity ?? watchEvent.Severity };
        var payload = watchEvent with { Id = 0 };
        using var command = SqliteValues.Command(
            connection,
            transaction,
            """
            INSERT INTO events (event_id, change_set_id, kind, severity, own_severity, object_kind, object_guid, object_dn, object_dn_norm,
              previous_dn_norm, search_text, tags, detected_at, originating_at, originating_dc, source, actor_status, actor_sid,
              next_attribution_at, rules_state, matched_rules, acked_at, acked_by, ack_comment, payload)
            VALUES ($eventId, $changeSet, $kind, $severity, $ownSeverity, $objectKind, $objectGuid, $dn, $dnNorm, $previousDnNorm, $search,
              $tags, $detectedAt, $originatingAt, $originatingDc, $source, $actorStatus, $actorSid, $nextAttribution, 0, $rules,
              $ackedAt, $ackedBy, $ackComment, $payload)
            RETURNING id;
            """,
            ("$eventId", SqliteValues.ToBlob(watchEvent.EventId)),
            ("$changeSet", SqliteValues.ToBlobOrNull(watchEvent.ChangeSetId)),
            ("$kind", (int)watchEvent.Kind),
            ("$severity", (int)watchEvent.Severity),
            ("$ownSeverity", (int)watchEvent.OwnSeverity!.Value),
            ("$objectKind", (int)watchEvent.Object.Kind),
            ("$objectGuid", SqliteValues.ToBlobOrNull(watchEvent.Object.ObjectGuid)),
            ("$dn", watchEvent.Object.Dn ?? string.Empty),
            ("$dnNorm", SqliteValues.NormalizeDn(watchEvent.Object.Dn)),
            ("$previousDnNorm", SqliteValues.NormalizeDnOrNull(watchEvent.PreviousDn)),
            ("$search", SearchText(watchEvent)),
            ("$tags", SqliteValues.PipeList(watchEvent.Tags)),
            ("$detectedAt", SqliteValues.ToUnixMs(watchEvent.DetectedAt)),
            ("$originatingAt", SqliteValues.ToUnixMsOrNull(watchEvent.OriginatingTime)),
            ("$originatingDc", SqliteValues.OrNull(watchEvent.OriginatingDc)),
            ("$source", (int)watchEvent.Source),
            ("$actorStatus", (int)watchEvent.Attribution.Status),
            ("$actorSid", SqliteValues.SidOrNull(watchEvent.Attribution.Actor?.Sid)),
            ("$nextAttribution", watchEvent.Attribution.Status == AttributionStatus.Pending
                ? SqliteValues.ToUnixMs(watchEvent.DetectedAt)
                : DBNull.Value),
            ("$rules", RulesColumn(watchEvent.MatchedRules)),
            ("$ackedAt", SqliteValues.ToUnixMsOrNull(watchEvent.AcknowledgedAt)),
            ("$ackedBy", SqliteValues.OrNull(watchEvent.AcknowledgedBy)),
            ("$ackComment", SqliteValues.OrNull(watchEvent.AcknowledgeComment)),
            ("$payload", BlobCodec.EncodeContract(payload)));
        var id = (long)command.ExecuteScalar()!;
        return watchEvent with { Id = id };
    }

    /// <summary>Maps a row selected with <see cref="Columns"/> (the own severity from its column; NULL – a row of schema 1 –
    /// falls back to the payload's own severity, else its severity).</summary>
    public static WatchEvent Read(SqliteDataReader reader)
    {
        var payload = BlobCodec.DecodeContract<WatchEvent>((byte[])reader.GetValue(1));
        var own = reader.IsDBNull(2) ? payload.OwnSeverity ?? payload.Severity : (Severity)reader.GetInt32(2);
        return payload with { Id = reader.GetInt64(0), OwnSeverity = own };
    }

    /// <summary>Loads one event inside a transaction (null when missing).</summary>
    public static WatchEvent? Load(SqliteConnection connection, SqliteTransaction? transaction, long id) =>
        SqliteValues.Query(connection, transaction, $"SELECT {Columns} FROM events WHERE id = $id;", Read, ("$id", id))
            .FirstOrDefault();

    /// <summary>Stores a new attribution state: payload, own and evaluated severity, search text, tags, actor and the next
    /// attempt.</summary>
    public static void UpdateAttribution(SqliteConnection connection, SqliteTransaction transaction, WatchEvent updated, DateTimeOffset? nextAttemptAt)
    {
        SqliteValues.Execute(
            connection,
            transaction,
            """
            UPDATE events SET severity = $severity, own_severity = $ownSeverity, search_text = $search, tags = $tags, actor_status = $actorStatus,
              actor_sid = $actorSid, next_attribution_at = $next, payload = $payload
            WHERE id = $id;
            """,
            ("$id", updated.Id),
            ("$severity", (int)updated.Severity),
            ("$ownSeverity", (int)(updated.OwnSeverity ?? updated.Severity)),
            ("$search", SearchText(updated)),
            ("$tags", SqliteValues.PipeList(updated.Tags)),
            ("$actorStatus", (int)updated.Attribution.Status),
            ("$actorSid", SqliteValues.SidOrNull(updated.Attribution.Actor?.Sid)),
            ("$next", updated.Attribution.Status == AttributionStatus.Pending ? SqliteValues.ToUnixMsOrNull(nextAttemptAt) : DBNull.Value),
            ("$payload", BlobCodec.EncodeContract(updated with { Id = 0 })));
    }

    /// <summary>Stores the rule evaluation (evaluated severity, matched rules) and marks the event evaluated; the own
    /// severity is not changed.</summary>
    public static void UpdateEvaluation(SqliteConnection connection, SqliteTransaction transaction, WatchEvent updated) =>
        SqliteValues.Execute(
            connection,
            transaction,
            "UPDATE events SET severity = $severity, matched_rules = $rules, rules_state = 1, payload = $payload WHERE id = $id;",
            ("$id", updated.Id),
            ("$severity", (int)updated.Severity),
            ("$rules", RulesColumn(updated.MatchedRules)),
            ("$payload", BlobCodec.EncodeContract(updated with { Id = 0 })));

    /// <summary>Stores an acknowledgement unless the event is already acknowledged; true when it was stored.</summary>
    public static bool UpdateAcknowledgement(SqliteConnection connection, SqliteTransaction transaction, WatchEvent updated) =>
        SqliteValues.Execute(
            connection,
            transaction,
            "UPDATE events SET acked_at = $at, acked_by = $by, ack_comment = $comment, payload = $payload WHERE id = $id AND acked_at IS NULL;",
            ("$id", updated.Id),
            ("$at", SqliteValues.ToUnixMsOrNull(updated.AcknowledgedAt)),
            ("$by", SqliteValues.OrNull(updated.AcknowledgedBy)),
            ("$comment", SqliteValues.OrNull(updated.AcknowledgeComment)),
            ("$payload", BlobCodec.EncodeContract(updated with { Id = 0 }))) == 1;

    /// <summary>The <c>search_text</c> column: lower-case "name|sAMAccountName|DN|actor account|workstation|IP".</summary>
    public static string SearchText(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        string?[] parts =
        [
            watchEvent.Object.Name, watchEvent.Object.SamAccountName, watchEvent.Object.Dn,
            watchEvent.Attribution.Actor?.Account, watchEvent.Attribution.Workstation, watchEvent.Attribution.IpAddress,
        ];
        return string.Join('|', parts.Select(p => p ?? string.Empty)).ToLowerInvariant();
    }

    private static object RulesColumn(IReadOnlyList<string> rules) =>
        rules.Count == 0 ? DBNull.Value : SqliteValues.PipeList(rules);
}
