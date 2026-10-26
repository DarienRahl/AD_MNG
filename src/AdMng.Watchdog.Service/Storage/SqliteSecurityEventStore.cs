using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="ISecurityEventStore"/> over <c>security_events</c> (unique per DC × record id × time created – migration 0004;
/// the parsed record in the <c>data</c> blob, lookup keys in indexed columns: upper-case SIDs, lower-case computer and
/// target user name, normalized object DN, the normalized 4624 <c>TargetLogonId</c>) and <c>eventlog_cursors</c>.
/// Correlation windows are closed <c>[From, To]</c>, results in time order.
/// </summary>
public sealed class SqliteSecurityEventStore : ISecurityEventStore
{
    private readonly SqliteDatabase _database;

    internal SqliteSecurityEventStore(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Task.FromResult(_database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT source, last_record_id, bookmark_xml, oldest_seen, updated_at FROM eventlog_cursors WHERE source = $source COLLATE NOCASE;",
            r => new EventLogCursor
            {
                Source = r.GetString(0),
                LastRecordId = r.GetInt64(1),
                BookmarkXml = SqliteValues.StringOrNull(r, 2),
                OldestRecordSeen = r.IsDBNull(3) ? null : r.GetInt64(3),
                UpdatedAt = SqliteValues.FromUnixMs(r.GetInt64(4)),
            },
            ("$source", source)).FirstOrDefault()));
    }

    public Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(cursor);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                foreach (var securityEvent in events)
                {
                    Insert(connection, transaction, securityEvent);
                }

                SqliteValues.Execute(connection, transaction, "DELETE FROM eventlog_cursors WHERE source = $source COLLATE NOCASE;", ("$source", cursor.Source));
                SqliteValues.Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO eventlog_cursors (source, last_record_id, bookmark_xml, oldest_seen, updated_at)
                    VALUES ($source, $last, $bookmark, $oldest, $updated);
                    """,
                    ("$source", cursor.Source),
                    ("$last", cursor.LastRecordId),
                    ("$bookmark", SqliteValues.OrNull(cursor.BookmarkXml)),
                    ("$oldest", SqliteValues.OrNull(cursor.OldestRecordSeen)),
                    ("$updated", SqliteValues.ToUnixMs(cursor.UpdatedAt)));
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var conditions = new List<string> { "time_created >= $from", "time_created <= $to" };
        var parameters = new List<(string Name, object? Value)>
        {
            ("$from", SqliteValues.ToUnixMs(criteria.From)),
            ("$to", SqliteValues.ToUnixMs(criteria.To)),
            ("$limit", Math.Max(0, criteria.Limit)),
        };
        if (criteria.EventCodes.Count > 0)
        {
            conditions.Add($"event_code IN ({SqliteValues.InList("code", [.. criteria.EventCodes.Distinct().Select(c => (object)c)], parameters)})");
        }

        AddEquals(conditions, parameters, "computer", criteria.Computer is null ? null : SqliteValues.LowerOrNull(criteria.Computer));
        AddEquals(conditions, parameters, "target_sid", criteria.TargetSid is null ? null : SqliteValues.SidOrNull(criteria.TargetSid));
        AddEquals(conditions, parameters, "member_sid", criteria.MemberSid is null ? null : SqliteValues.SidOrNull(criteria.MemberSid));
        AddEquals(conditions, parameters, "target_user", criteria.TargetUserName is null ? null : SqliteValues.LowerOrNull(criteria.TargetUserName));
        AddEquals(conditions, parameters, "object_dn_norm", criteria.ObjectDn is null ? null : SqliteValues.NormalizeDn(criteria.ObjectDn));
        AddEquals(conditions, parameters, "logon_id", criteria.LogonId is null ? null : SecurityEvent.NormalizeLogonId(criteria.LogonId) ?? criteria.LogonId);
        if (criteria.ObjectGuid is { } objectGuid)
        {
            conditions.Add("object_guid = $object_guid");
            parameters.Add(("$object_guid", SqliteValues.ToBlob(objectGuid)));
        }

        var sql = $"SELECT data FROM security_events WHERE {string.Join(" AND ", conditions)} ORDER BY time_created, record_id LIMIT $limit;";
        return Task.FromResult<IReadOnlyList<SecurityEvent>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            sql,
            r => BlobCodec.DecodeSecurityEvent((byte[])r.GetValue(0)),
            [.. parameters])));
    }

    public Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var last = _database.Read(c => SqliteValues.Scalar<long?>(
            c,
            null,
            "SELECT max(time_created) FROM security_events WHERE computer = $computer AND event_code = $code;",
            ("$computer", SqliteValues.LowerOrNull(computer)),
            ("$code", eventCode)));
        return Task.FromResult(last is { } value ? SqliteValues.FromUnixMs(value) : (DateTimeOffset?)null);
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, SecurityEvent securityEvent) =>
        SqliteValues.Execute(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO security_events (computer, record_id, source, event_code, time_created, target_sid, object_guid,
              member_sid, target_user, object_dn_norm, subject_sid, subject_logon_id, logon_id, data)
            VALUES ($computer, $record, $source, $code, $time, $targetSid, $objectGuid, $memberSid, $targetUser, $objectDn,
              $subjectSid, $subjectLogonId, $logonId, $data);
            """,
            ("$computer", SqliteValues.LowerOrNull(securityEvent.Computer)),
            ("$record", securityEvent.RecordId),
            ("$source", securityEvent.Source),
            ("$code", securityEvent.EventCode),
            ("$time", SqliteValues.ToUnixMs(securityEvent.TimeCreated)),
            ("$targetSid", SqliteValues.SidOrNull(securityEvent.TargetSid)),
            ("$objectGuid", SqliteValues.ToBlobOrNull(securityEvent.ObjectGuid)),
            ("$memberSid", SqliteValues.SidOrNull(securityEvent.Get("MemberSid"))),
            ("$targetUser", SqliteValues.LowerOrNull(securityEvent.Get("TargetUserName"))),
            ("$objectDn", SqliteValues.NormalizeDnOrNull(securityEvent.Get("ObjectDN"))),
            ("$subjectSid", SqliteValues.SidOrNull(securityEvent.SubjectSid)),
            ("$subjectLogonId", SqliteValues.OrNull(securityEvent.SubjectLogonId)),
            ("$logonId", securityEvent.EventCode == 4624 ? SqliteValues.OrNull(securityEvent.TargetLogonId) : DBNull.Value),
            ("$data", BlobCodec.EncodeSecurityEvent(securityEvent)));

    private static void AddEquals(List<string> conditions, List<(string Name, object? Value)> parameters, string column, object? value)
    {
        if (value is null)
        {
            return;
        }

        conditions.Add($"{column} = ${column}");
        parameters.Add(($"${column}", value));
    }
}
