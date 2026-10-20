using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>What one collector batch needs besides its lines (stage B design 7.5–7.7).</summary>
public sealed record CollectorIngestionContext
{
    /// <summary>Settings, reduction, domain and disabled kinds – as for a DC log.</summary>
    public required IngestionContext Ingestion { get; init; }

    /// <summary>The identity checks of 7.6.</summary>
    public required CollectorIdentityPolicy Identity { get; init; }

    /// <summary>The per-DC ingest budget of logon and failure records (owned by the tailer, kept across batches).</summary>
    public required CollectorLogonBudget Budget { get; init; }
}

/// <summary>The outcome of <see cref="CollectorIngestor.Prepare"/> for one batch of lines of one client directory.</summary>
public sealed class CollectorBatch
{
    /// <summary>Reduced records to store with the batch's cursor.</summary>
    public List<SecurityEvent> Records { get; } = [];

    /// <summary>Accepted records of the change-related set whose detections (gaps, event-log-only kinds) are appended
    /// before the cursor is committed.</summary>
    public List<SecurityEvent> DetectionCandidates { get; } = [];

    /// <summary>The DC of the accepted records (null when none was accepted).</summary>
    public CollectorDomainController? Dc { get; set; }

    /// <summary>Lines read.</summary>
    public int Lines { get; set; }

    /// <summary>Records that passed the identity checks.</summary>
    public int Accepted { get; set; }

    /// <summary>Lines that are not records or whose event XML does not parse (<c>wec.&lt;dc&gt;.malformedLines</c>).</summary>
    public int Malformed { get; set; }

    /// <summary>Records found after a partial one in the same line (OpenWEC died mid-write; the partial one is counted
    /// in <see cref="Malformed"/>).</summary>
    public int Resynchronized { get; set; }

    /// <summary>Records refused by the identity checks, per check.</summary>
    public Dictionary<CollectorRejection, int> Rejected { get; } = [];

    /// <summary>Logon and failure records over the budget – counted, not stored.</summary>
    public int OverBudget { get; set; }

    /// <summary>Accepted records not stored: noise 5136, logon records of computer accounts, ids outside the sets.</summary>
    public int Filtered { get; set; }

    /// <summary><c>meta.TimeReceived − TimeCreated</c> of the accepted records (delivery lag, 7.9).</summary>
    public List<(DateTimeOffset ReceivedAt, TimeSpan Lag)> Lags { get; } = [];

    /// <summary>Newest <c>TimeCreated</c> of the accepted records.</summary>
    public DateTimeOffset? NewestRecordAt { get; set; }

    /// <summary>Newest <c>meta.TimeReceived</c> of the accepted records.</summary>
    public DateTimeOffset? NewestReceivedAt { get; set; }

    /// <summary>Highest <c>EventRecordID</c> of the accepted records.</summary>
    public long? MaxRecordId { get; set; }

    /// <summary>The budget was exceeded for <see cref="Dc"/> (HealthEventLogGap "flooding").</summary>
    public bool Flooded { get; set; }

    /// <summary>Total of <see cref="Rejected"/>.</summary>
    public int RejectedTotal => Rejected.Values.Sum();
}

/// <summary>
/// Ingest budget of logon and failure records per DC and minute of <c>TimeCreated</c> (stage B design 7.5,
/// <c>Watchdog:Collector:MaxLogonRecordsPerDcPerMinute</c>): beyond it 4624/4625/4771/4776 are counted, not stored;
/// change-related records are never limited. Thread-safe; the last minutes per DC are kept.
/// </summary>
public sealed class CollectorLogonBudget(int maxPerDcPerMinute)
{
    private const int KeptMinutes = 15;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Dc, long Minute), int> _used = [];

    public int MaxPerDcPerMinute { get; } = Math.Max(1, maxPerDcPerMinute);

    /// <summary>Takes one record of <paramref name="dc"/> logged at <paramref name="timeCreated"/>; false when the minute's
    /// budget is used up.</summary>
    public bool TryTake(string dc, DateTimeOffset timeCreated)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var minute = timeCreated.ToUnixTimeSeconds() / 60;
        var key = (HostNames.Normalize(dc), minute);
        lock (_gate)
        {
            var used = _used.GetValueOrDefault(key);
            if (used >= MaxPerDcPerMinute)
            {
                return false;
            }

            _used[key] = used + 1;
            if (_used.Count > 64 * KeptMinutes)
            {
                foreach (var old in _used.Keys.Where(k => k.Minute < minute - KeptMinutes).ToList())
                {
                    _used.Remove(old);
                }
            }

            return true;
        }
    }
}

/// <summary>
/// The Core part of the WEF tailer (stage B design 7.5–7.7): one batch of RawJson lines of one OpenWEC client directory →
/// <see cref="WecRecordReader"/> → identity checks 1, 2, 4 (<see cref="CollectorIdentityPolicy.MatchClient"/>) → the
/// unchanged <see cref="SecurityEventParser"/> → checks 3 and 5 → reduction: the change-related set as for a DC log
/// (<see cref="SecurityEventReducer.Reduce"/>; the noisy 5136 attributes the DC-log query suppresses are dropped here,
/// <see cref="SecurityLogIngestor.SuppressedAttributes"/>), logon and failure records within the budget
/// (<see cref="SecurityEventReducer.ReduceLogon"/>). <see cref="Prepare"/> is pure apart from the budget, so a line that
/// throws can be told apart from a store failure (poison input, 7.5); <see cref="DetectAsync"/> appends the gap and
/// event-log-only detections before the caller commits the batch with its cursor.
/// </summary>
public sealed class CollectorIngestor(SecurityLogIngestor ingestor, IEventStore events, EventLogGapDetector gaps, DcLogHealthTracker tracker)
{
    private static readonly IReadOnlySet<string> Suppressed = SecurityLogIngestor.SuppressedAttributes.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<int> ChangeSet = SecurityEventCatalog.IngestionSet.ToHashSet();

    private readonly SecurityEventParser _parser = new();

    /// <summary>Parses, checks and reduces <paramref name="lines"/> (each without its line feed) of the client directory
    /// <paramref name="clientDirectory"/>.</summary>
    public CollectorBatch Prepare(string clientDirectory, IReadOnlyList<ReadOnlyMemory<byte>> lines, CollectorIngestionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientDirectory);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(context);
        var batch = new CollectorBatch();
        foreach (var line in lines)
        {
            Add(batch, clientDirectory, line.Span, context);
        }

        return batch;
    }

    /// <summary>Appends the detections of <paramref name="batch"/> (gaps, event-log-only kinds, "flooding") that are not
    /// stored yet; their ids derive from the records, so a batch read again repeats none. Returns the appended events.</summary>
    public async Task<IReadOnlyList<WatchEvent>> DetectAsync(CollectorBatch batch, CollectorIngestionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(context);
        var appended = new List<WatchEvent>();
        foreach (var record in batch.DetectionCandidates)
        {
            foreach (var detected in await ingestor.DetectAsync(record, context.Ingestion, cancellationToken).ConfigureAwait(false))
            {
                await AddAsync(detected, appended, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Flooded && batch.Dc is { } dc)
        {
            await AddAsync(gaps.Gap(dc.HostName, EventLogGapDetector.Flooding, context.Ingestion.Domain), appended, cancellationToken).ConfigureAwait(false);
        }

        return appended;
    }

    private void Add(CollectorBatch batch, string clientDirectory, ReadOnlySpan<byte> line, CollectorIngestionContext context)
    {
        batch.Lines++;
        if (WecRecordReader.Read(line, out var resynchronized) is not { } wec)
        {
            batch.Malformed++;
            return;
        }

        if (resynchronized)
        {
            batch.Resynchronized++;
            batch.Malformed++;   // the partial record before it
        }

        var rejection = context.Identity.MatchClient(wec, clientDirectory, out var dc);
        if (rejection != CollectorRejection.None)
        {
            Reject(batch, rejection);
            return;
        }

        if (!_parser.TryParse(wec.Data, dc!.HostName, out var parsed))
        {
            batch.Malformed++;
            return;
        }

        rejection = CollectorIdentityPolicy.CheckRecord(parsed, dc);
        if (rejection != CollectorRejection.None)
        {
            Reject(batch, rejection);
            return;
        }

        var record = parsed.Event;
        batch.Dc ??= dc;
        batch.Accepted++;
        batch.MaxRecordId = Math.Max(batch.MaxRecordId ?? 0, record.RecordId);
        batch.NewestRecordAt = Max(batch.NewestRecordAt, record.TimeCreated);
        if (wec.TimeReceived is { } received)
        {
            batch.NewestReceivedAt = Max(batch.NewestReceivedAt, received);
            batch.Lags.Add((received, received - record.TimeCreated));
        }

        tracker.RecordComputerName(dc.HostName, record.Computer);
        if (SecurityEventCatalog.IsLogonRecord(record.EventCode))
        {
            if (!context.Budget.TryTake(dc.HostName, record.TimeCreated))
            {
                batch.OverBudget++;
                batch.Flooded = true;
            }
            else if (SecurityEventReducer.ReduceLogon(record, context.Ingestion.Settings) is { } logon)
            {
                batch.Records.Add(logon);
            }
            else
            {
                batch.Filtered++;
            }

            return;
        }

        if (!ChangeSet.Contains(record.EventCode)
            || (record.EventCode == SecurityEventCatalog.ObjectModified && record.Get("AttributeLDAPDisplayName") is { } attribute && Suppressed.Contains(attribute)))
        {
            batch.Filtered++;
            return;
        }

        batch.DetectionCandidates.Add(record);
        if (SecurityEventReducer.Reduce(record, context.Ingestion.Reduction) is { } reduced)
        {
            batch.Records.Add(reduced);
        }
        else
        {
            batch.Filtered++;
        }
    }

    private static void Reject(CollectorBatch batch, CollectorRejection rejection) =>
        batch.Rejected[rejection] = batch.Rejected.GetValueOrDefault(rejection) + 1;

    private static DateTimeOffset Max(DateTimeOffset? known, DateTimeOffset value) => known is { } k && k > value ? k : value;

    private async Task AddAsync(WatchEvent? watchEvent, List<WatchEvent> appended, CancellationToken cancellationToken)
    {
        if (watchEvent is null || await events.GetByEventIdAsync(watchEvent.EventId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        await events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
        appended.Add(watchEvent);
    }
}
