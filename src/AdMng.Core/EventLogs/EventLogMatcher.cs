namespace AdMng.Core.EventLogs;

/// <summary>
/// Applies an <see cref="EventLogQuery"/> to records in memory with the semantics of the query XML
/// (<see cref="EventQueryXml"/>) as the event log service evaluates it – for the fake connector and for post-filtering
/// file reads (design note e-event-logs.md 3). The caller passes the records of one log (a channel or the files);
/// <see cref="EventLogQuery.Channel"/> is not compared.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Event IDs, levels and providers: one of the listed (empty = any); provider names compare ordinally.</item>
/// <item>Outcome: the audit success or failure keyword is set.</item>
/// <item>Time: inclusive bounds rounded to the millisecond like the query XML (<see cref="EventQueryXml.TimeBounds"/>).</item>
/// <item>Data: every condition holds; a condition holds when a value of that name (ordinal name) equals one of its
/// values ordinally – case-sensitive, like the service. A condition value is also compared decoded
/// (<see cref="InsertionStringDecoder"/>), as records hold decoded values; values the parser omitted (<c>-</c>, empty)
/// never match. A condition without values matches nothing (<see cref="EventQueryXml.Build"/> rejects it). The record
/// does not say where a value came from, so values the parser took from <c>UserData</c> or named by position
/// (<c>Data1</c>, …) match here although the service's <c>EventData/Data[@Name]</c> would not see them – a superset.</item>
/// <item><see cref="EventLogQuery.RawXPath"/> is <b>not evaluated</b>: with a raw XPath every record of the log is
/// selected (only the target can evaluate it); ordering and <see cref="EventLogQuery.MaxRecords"/> still apply.</item>
/// <item>Order: <see cref="EventLogQuery.NewestFirst"/> sorts by <see cref="EventLogRecord.TimeCreated"/> then
/// <see cref="EventLogRecord.RecordId"/>, descending (ascending otherwise); then the first
/// <see cref="EventLogQuery.MaxRecords"/> are kept.</item>
/// </list>
/// </remarks>
public static class EventLogMatcher
{
    /// <summary>True when <paramref name="record"/> is selected by <paramref name="query"/> (ordering and the record
    /// limit aside).</summary>
    public static bool Matches(EventLogQuery query, EventLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(record);
        if (query.RawXPath is not null)
        {
            return true;
        }

        if (query.EventIds.Count > 0 && !query.EventIds.Contains(record.EventId))
        {
            return false;
        }

        if (query.Levels.Count > 0 && (record.Level is not { } level || !query.Levels.Contains(level)))
        {
            return false;
        }

        if (query.Providers.Count > 0 && (record.Provider is not { } provider || !query.Providers.Contains(provider, StringComparer.Ordinal)))
        {
            return false;
        }

        if (query.Outcome is { } outcome && !(outcome == EventAuditOutcome.Success ? record.IsAuditSuccess : record.IsAuditFailure))
        {
            return false;
        }

        var (from, to) = EventQueryXml.TimeBounds(query.From, query.To);
        if ((from is { } lower && record.TimeCreated < lower) || (to is { } upper && record.TimeCreated > upper))
        {
            return false;
        }

        foreach (var condition in query.Data)
        {
            if (!Holds(condition, record))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The records <paramref name="query"/> selects, ordered and limited (see the class remarks).</summary>
    public static IReadOnlyList<EventLogRecord> Apply(EventLogQuery query, IEnumerable<EventLogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(records);
        var selected = records.Where(r => Matches(query, r));
        var ordered = query.NewestFirst
            ? selected.OrderByDescending(r => r.TimeCreated).ThenByDescending(r => r.RecordId)
            : selected.OrderBy(r => r.TimeCreated).ThenBy(r => r.RecordId);
        return query.MaxRecords is { } max ? [.. ordered.Take(Math.Max(0, max))] : [.. ordered];
    }

    private static bool Holds(EventDataCondition condition, EventLogRecord record)
    {
        if (condition.Values is not { Count: > 0 } values)
        {
            return false;
        }

        foreach (var (name, value) in record.Data)
        {
            if (!string.Equals(name, condition.Name, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var wanted in values)
            {
                if (string.Equals(value, wanted, StringComparison.Ordinal)
                    || string.Equals(value, InsertionStringDecoder.Decode(wanted), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
