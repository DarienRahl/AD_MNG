namespace AdMng.Core.EventLogs.Analysis;

/// <summary>Record helpers shared by the analyses.</summary>
internal static class AnalysisRecords
{
    /// <summary>
    /// The records without duplicates (same host, channel and record ID – overlapping reads) in a deterministic order:
    /// time, host (normalized), record ID.
    /// </summary>
    public static List<EventLogRecord> Prepare(IEnumerable<EventLogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var seen = new HashSet<(string Host, string Channel, long RecordId)>();
        var keyed = new List<(EventLogRecord Record, string Host)>();
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(records));
            var host = AnalysisValues.Host(record.Host);
            if (seen.Add((host, record.Channel.ToLowerInvariant(), record.RecordId)))
            {
                keyed.Add((record, host));
            }
        }

        keyed.Sort(static (a, b) =>
        {
            var byTime = a.Record.TimeCreated.CompareTo(b.Record.TimeCreated);
            if (byTime != 0)
            {
                return byTime;
            }

            var byHost = string.CompareOrdinal(a.Host, b.Host);
            return byHost != 0 ? byHost : a.Record.RecordId.CompareTo(b.Record.RecordId);
        });
        return keyed.ConvertAll(k => k.Record);
    }

    /// <summary>Code counts ordered by count (descending), event ID, code.</summary>
    public static IReadOnlyList<EventCodeCount> CodeCounts(Dictionary<(int EventId, string? Code), (LogonFailureKind Kind, int Count)> counts) =>
    [
        .. counts
            .Select(c => new EventCodeCount(c.Key.EventId, c.Key.Code, c.Value.Kind, c.Value.Count))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.EventId)
            .ThenBy(c => c.Code, StringComparer.Ordinal),
    ];

    /// <summary>Adds one record of <paramref name="eventId"/> with <paramref name="code"/> to <paramref name="counts"/>.</summary>
    public static void Count(Dictionary<(int EventId, string? Code), (LogonFailureKind Kind, int Count)> counts, int eventId, string? code, LogonFailureKind kind)
    {
        var key = (eventId, code);
        counts[key] = counts.TryGetValue(key, out var current) ? (current.Kind, current.Count + 1) : (kind, 1);
    }

    /// <summary>The index of the first element of the sorted <paramref name="times"/> not before <paramref name="time"/>.</summary>
    public static int LowerBound(List<DateTimeOffset> times, DateTimeOffset time)
    {
        int low = 0, high = times.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (times[middle] < time)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The index of the first element of the sorted <paramref name="times"/> after <paramref name="time"/>.</summary>
    public static int UpperBound(List<DateTimeOffset> times, DateTimeOffset time)
    {
        int low = 0, high = times.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (times[middle] <= time)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
