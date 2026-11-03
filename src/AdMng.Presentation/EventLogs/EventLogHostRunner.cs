using AdMng.Core.Directory;
using AdMng.Core.EventLogs;

namespace AdMng.Presentation.EventLogs;

/// <summary>Where one source of a run is.</summary>
public enum EventLogHostState
{
    Waiting,
    Opening,
    Reading,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// One source of a run: a computer (<see cref="Host"/> = its FQDN) or operator-chosen <c>.evtx</c> files (<see cref="Files"/>;
/// <see cref="Host"/> = their display name), and the queries to run on it in order.
/// </summary>
/// <param name="Host">The FQDN, or the display name of the files.</param>
/// <param name="Queries">The queries (each one read of the source).</param>
public sealed record EventLogTarget(string Host, IReadOnlyList<EventLogQuery> Queries)
{
    /// <summary>The files of a file source (null: a computer).</summary>
    public IReadOnlyList<string>? Files { get; init; }

    public bool IsFiles => Files is { Count: > 0 };

    /// <summary>The kind only (hosts and paths are directory or operator values).</summary>
    public override string ToString() => IsFiles ? "files target" : "computer target";
}

/// <summary>The state of one source during and after a run (reported through the progress of <see cref="EventLogHostRunner.RunAsync"/>).</summary>
/// <param name="Host">The source's <see cref="EventLogTarget.Host"/>.</param>
/// <param name="State">Where it is.</param>
/// <param name="Records">Records read from it so far (also those beyond the memory limit).</param>
/// <param name="Coverage">The coverage of the channel of its first query, null when not read (yet).</param>
/// <param name="Error">Why it failed, null when it did not.</param>
/// <param name="NativeErrorCode">The Windows error code of the failure when known.</param>
public sealed record EventLogHostProgress(
    string Host,
    EventLogHostState State,
    int Records,
    EventLogCoverage? Coverage = null,
    EventLogAccessError? Error = null,
    int? NativeErrorCode = null)
{
    public override string ToString() => State.ToString();
}

/// <summary>What a run read: the records kept in memory (newest first), how many were dropped, the per-source result.</summary>
public sealed class EventLogRunResult
{
    /// <summary>The records kept, newest first (then by host and record ID descending).</summary>
    public required IReadOnlyList<EventLogRecord> Records { get; init; }

    /// <summary>Records read but not kept (beyond the memory limit; the oldest are dropped).</summary>
    public int Dropped { get; init; }

    /// <summary>Records read in total.</summary>
    public int Total => Records.Count + Dropped;

    /// <summary>The final state of every source, in the order of the targets.</summary>
    public required IReadOnlyList<EventLogHostProgress> Hosts { get; init; }

    /// <summary>The coverage of every source whose coverage could be read.</summary>
    public IReadOnlyList<EventLogCoverage> Coverage => [.. Hosts.Select(h => h.Coverage).OfType<EventLogCoverage>()];

    /// <summary>The sources that failed, by host.</summary>
    public IReadOnlyDictionary<string, EventLogAccessError> Failures =>
        Hosts.Where(h => h.Error is not null).ToDictionary(h => h.Host, h => h.Error!.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The run was cancelled: the records are those read until then.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Counts only.</summary>
    public override string ToString() => $"{Records.Count} records kept, {Dropped} dropped, {Hosts.Count} hosts";
}

/// <summary>
/// Runs queries on several computers and file sources (design note e-event-logs.md 2, rule 6): at most
/// <see cref="MaxParallelHosts"/> sources at once, each opened through <see cref="IEventLogConnector"/> with the window's
/// session, its coverage read for the channel of its first query, then its queries streamed off the UI thread. Progress per
/// source is reported through <see cref="IProgress{T}"/>; cancellation stops every source (the result then holds what was
/// read so far). Records are held in memory only: at most <see cref="MaxRecords"/>, the newest – the others are counted in
/// <see cref="EventLogRunResult.Dropped"/>. Never throws for a source that cannot be read: its failure is in the result.
/// </summary>
public sealed class EventLogHostRunner(IEventLogConnector connector)
{
    /// <summary>Records between two progress reports of a source.</summary>
    public const int ProgressEvery = 256;

    private readonly IEventLogConnector _connector = connector ?? throw new ArgumentNullException(nameof(connector));

    /// <summary>Records kept in memory (default <see cref="EventLogLimits.MaxRecordsInMemory"/>).</summary>
    public int MaxRecords { get; init; } = EventLogLimits.MaxRecordsInMemory;

    /// <summary>Sources read at the same time (default <see cref="EventLogLimits.MaxParallelHosts"/>).</summary>
    public int MaxParallelHosts { get; init; } = EventLogLimits.MaxParallelHosts;

    /// <summary>Reads every target; returns what was read (also after a cancellation).</summary>
    public async Task<EventLogRunResult> RunAsync(
        IDirectorySession session,
        IReadOnlyList<EventLogTarget> targets,
        IProgress<EventLogHostProgress>? progress,
        CancellationToken cancellationToken,
        int? maxRecords = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targets);
        var buffer = new RecordBuffer(Math.Max(0, maxRecords ?? MaxRecords));
        var results = new EventLogHostProgress[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            results[i] = new EventLogHostProgress(targets[i].Host, EventLogHostState.Waiting, 0);
            progress?.Report(results[i]);
        }

        using var gate = new SemaphoreSlim(Math.Max(1, MaxParallelHosts));
        var tasks = targets.Select((target, index) => Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = results[index] with { State = EventLogHostState.Cancelled };
                progress?.Report(results[index]);
                return;
            }

            try
            {
                results[index] = await ReadOneAsync(session, target, buffer, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            progress?.Report(results[index]);
        }, CancellationToken.None)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var (records, dropped) = buffer.Take();
        return new EventLogRunResult
        {
            Records = records,
            Dropped = dropped,
            Hosts = results,
            Cancelled = cancellationToken.IsCancellationRequested,
        };
    }

    private async Task<EventLogHostProgress> ReadOneAsync(
        IDirectorySession session,
        EventLogTarget target,
        RecordBuffer buffer,
        IProgress<EventLogHostProgress>? progress,
        CancellationToken cancellationToken)
    {
        var state = new EventLogHostProgress(target.Host, EventLogHostState.Opening, 0);
        progress?.Report(state);
        IEventLogReader? reader = null;
        var count = 0;
        try
        {
            reader = target.IsFiles
                ? _connector.OpenFiles(target.Files!)
                : await _connector.OpenComputerAsync(target.Host, session, cancellationToken).ConfigureAwait(false);
            EventLogCoverage? coverage = null;
            if (target.Queries.Count > 0)
            {
                try
                {
                    coverage = await reader.GetCoverageAsync(target.Queries[0].Channel, cancellationToken).ConfigureAwait(false);
                }
                catch (EventLogAccessException)
                {
                    // The read below reports the failure (a channel that cannot be described usually cannot be read either).
                    coverage = null;
                }
            }

            state = state with { State = EventLogHostState.Reading, Coverage = coverage };
            progress?.Report(state);
            foreach (var query in target.Queries)
            {
                await foreach (var record in reader.ReadAsync(query, cancellationToken).ConfigureAwait(false))
                {
                    buffer.Add(record);
                    if (++count % ProgressEvery == 0)
                    {
                        progress?.Report(state with { Records = count });
                    }
                }
            }

            return state with { State = EventLogHostState.Done, Records = count };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return state with { State = EventLogHostState.Cancelled, Records = count };
        }
        catch (EventLogAccessException ex)
        {
            return state with { State = EventLogHostState.Failed, Records = count, Error = ex.Error, NativeErrorCode = ex.NativeErrorCode };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unexpected failure of one source must not stop the others; nothing about it is logged (it may name a host).
            return state with { State = EventLogHostState.Failed, Records = count, Error = EventLogAccessError.Failed };
        }
        finally
        {
            if (reader is not null)
            {
                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is EventLogAccessException or ObjectDisposedException or InvalidOperationException)
                {
                    // Closing a reader that already failed.
                }
            }
        }
    }

    /// <summary>The newest records up to a limit (a min-heap by time), thread-safe.</summary>
    private sealed class RecordBuffer(int limit)
    {
        private readonly PriorityQueue<EventLogRecord, EventLogRecord> _heap = new(Comparer<EventLogRecord>.Create(Compare));
        private readonly Lock _gate = new();
        private int _dropped;

        public void Add(EventLogRecord record)
        {
            lock (_gate)
            {
                if (_heap.Count < limit)
                {
                    _heap.Enqueue(record, record);
                }
                else if (limit > 0 && Compare(record, _heap.Peek()) > 0)
                {
                    _heap.DequeueEnqueue(record, record);
                    _dropped++;
                }
                else
                {
                    _dropped++;
                }
            }
        }

        public (IReadOnlyList<EventLogRecord> Records, int Dropped) Take()
        {
            lock (_gate)
            {
                var records = _heap.UnorderedItems.Select(i => i.Element).ToList();
                records.Sort((a, b) => Compare(b, a));
                return (records, _dropped);
            }
        }

        /// <summary>Older first: time, host, record ID.</summary>
        private static int Compare(EventLogRecord a, EventLogRecord b)
        {
            var byTime = a.TimeCreated.CompareTo(b.TimeCreated);
            if (byTime != 0)
            {
                return byTime;
            }

            var byHost = string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
            return byHost != 0 ? byHost : a.RecordId.CompareTo(b.RecordId);
        }
    }
}
