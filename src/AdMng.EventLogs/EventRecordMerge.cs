using AdMng.Core.EventLogs;

namespace AdMng.EventLogs;

/// <summary>
/// The order of records read from several <c>.evtx</c> files (design note e-event-logs.md 3.1): every file is read
/// forward (oldest first – the only direction every file kind supports), the files are merged by time, and "newest
/// first" is applied after the read, keeping only the newest records it may return. Pure, tested on every platform.
/// </summary>
internal static class EventRecordMerge
{
    /// <summary>
    /// A k-way merge of <paramref name="sources"/> (each oldest first) by <see cref="EventLogRecord.TimeCreated"/>; equal
    /// times keep the order of the sources, then the record number. Lazy: one record per source is held, each source is
    /// opened on the first read and every enumerator is disposed when the merge ends or is abandoned. A source that is
    /// not in time order (a clock change) is still merged record by record.
    /// </summary>
    public static IEnumerable<EventLogRecord> OldestFirst(IReadOnlyList<IEnumerable<EventLogRecord>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return Merge(sources);
    }

    /// <summary>
    /// The newest <paramref name="limit"/> records of <paramref name="records"/>, newest first (a bounded heap: at most
    /// <paramref name="limit"/> records are held). Equal times: the later one in <paramref name="records"/> first.
    /// </summary>
    public static IReadOnlyList<EventLogRecord> NewestFirst(IEnumerable<EventLogRecord> records, int limit)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (limit == 0)
        {
            return [];
        }

        var newest = new PriorityQueue<EventLogRecord, (DateTimeOffset Time, long Sequence)>();
        var sequence = 0L;
        foreach (var record in records)
        {
            var key = (record.TimeCreated, sequence++);
            if (newest.Count < limit)
            {
                newest.Enqueue(record, key);
            }
            else
            {
                newest.EnqueueDequeue(record, key);
            }
        }

        var ordered = new List<EventLogRecord>(newest.Count);
        while (newest.TryDequeue(out var record, out _))
        {
            ordered.Add(record);
        }

        ordered.Reverse();
        return ordered;
    }

    /// <summary>
    /// The records of a file read in the requested order: oldest first streams the merge (bounded by
    /// <paramref name="maxRecords"/>); newest first keeps the newest <paramref name="maxRecords"/> – or, without a
    /// limit, <see cref="EventLogLimits.MaxRecordsInMemory"/> – and returns them once the files were read.
    /// </summary>
    public static IEnumerable<EventLogRecord> Order(IReadOnlyList<IEnumerable<EventLogRecord>> sources, bool newestFirst, int? maxRecords)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (maxRecords is <= 0)
        {
            return [];
        }

        var merged = OldestFirst(sources);
        return newestFirst
            ? NewestFirst(merged, maxRecords ?? EventLogLimits.MaxRecordsInMemory)
            : maxRecords is { } max ? merged.Take(max) : merged;
    }

    private static IEnumerable<EventLogRecord> Merge(IReadOnlyList<IEnumerable<EventLogRecord>> sources)
    {
        var readers = new List<IEnumerator<EventLogRecord>>(sources.Count);
        try
        {
            var next = new PriorityQueue<int, (DateTimeOffset Time, int Source, long RecordId)>();
            for (var index = 0; index < sources.Count; index++)
            {
                var reader = sources[index].GetEnumerator();
                readers.Add(reader);
                if (reader.MoveNext())
                {
                    next.Enqueue(index, Key(reader.Current, index));
                }
            }

            while (next.TryDequeue(out var index, out _))
            {
                var reader = readers[index];
                yield return reader.Current;
                if (reader.MoveNext())
                {
                    next.Enqueue(index, Key(reader.Current, index));
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static (DateTimeOffset, int, long) Key(EventLogRecord record, int source) => (record.TimeCreated, source, record.RecordId);
}
