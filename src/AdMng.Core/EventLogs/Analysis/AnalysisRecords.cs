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

    /// <summary>How far apart in time a PDC emulator's retry of another DC's NTLM failure and that failure may be logged.</summary>
    public static readonly TimeSpan PdcRetryTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The 4776 failures a PDC emulator logged when another DC retried a wrong password there (NTLM pass-through keeps the
    /// client's workstation, so the copy looks like a second attempt of the same source): a 4776 failure on a host of
    /// <paramref name="pdcHosts"/> with the same account, workstation and status as a 4776 failure on another host within
    /// <see cref="PdcRetryTolerance"/> either way. Each failure of another DC pairs with one PDC record at most (earliest
    /// first), so attempts made at the PDC emulator itself are kept. <paramref name="records"/> must be in
    /// <see cref="Prepare"/> order (the result is then the same for any input order).
    /// </summary>
    public static HashSet<EventLogRecord> PdcRetries(IEnumerable<EventLogRecord> records, IReadOnlySet<string> pdcHosts)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(pdcHosts);
        var retries = new HashSet<EventLogRecord>(ReferenceEqualityComparer.Instance);
        if (pdcHosts.Count == 0)
        {
            return retries;
        }

        var groups = new Dictionary<(string Account, string Workstation, long? Code), (List<EventLogRecord> Pdc, List<EventLogRecord> Others)>();
        foreach (var record in records)
        {
            if (record.EventId != AnalysisCodes.CredentialValidation || !AnalysisCodes.IsFailure(record)
                || AnalysisValues.AccountKey(record.Get("TargetUserName")) is not { } account
                || AnalysisValues.Computer(record.Get("Workstation")) is not { } workstation)
            {
                continue;
            }

            var key = (account, workstation, EventCodes.ResultOf(record)?.Value);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = ([], []);
            }

            (pdcHosts.Contains(AnalysisValues.Host(record.Host)) ? group.Pdc : group.Others).Add(record);
        }

        foreach (var (pdc, others) in groups.Values)
        {
            var next = 0;
            foreach (var copy in pdc)
            {
                while (next < others.Count && others[next].TimeCreated < copy.TimeCreated - PdcRetryTolerance)
                {
                    next++;
                }

                if (next < others.Count && others[next].TimeCreated <= copy.TimeCreated + PdcRetryTolerance)
                {
                    retries.Add(copy);
                    next++;
                }
            }
        }

        return retries;
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
