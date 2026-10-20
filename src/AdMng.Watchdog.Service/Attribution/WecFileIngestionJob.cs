using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>
/// The WEF tailer of the Linux service (stage B design 7.5; job "wec-ingestion", every 2 s, at once while data is
/// waiting): one logical source per OpenWEC client directory below <c>Watchdog:Collector:OutputDirectory</c>, its cursor
/// in <c>eventlog_cursors</c> (<see cref="WecFileCursor"/>). Files are consumed in order – rotated files by their stamp,
/// then the active file – from the cursor's file (found by device, inode and first-line hash) and offset; only complete
/// lines are read, a line over 1 MiB is skipped and counted. Per batch (≤ 1000 lines): the attempt is recorded in its own
/// transaction (poison input – after <see cref="MaxAttempts"/> the batch is processed line by line and a line that fails
/// again is skipped and counted as malformed), the lines are parsed, checked and reduced (<see cref="CollectorIngestor"/>),
/// the detections appended, and the records are stored together with the new cursor in one transaction
/// (at-least-once: a crash before the commit reads the batch again, the store and the detections ignore what they have).
/// After every commit <c>wec-progress.json</c> is rewritten for <c>admng-wec-rotate</c>, which deletes only files listed
/// complete. A client directory of no writable DC of the domain is not read at all (identityMismatch; a DC added since the
/// last DC list refresh is read once the list knows it), and nothing is read while the DC list is unknown. Health per DC:
/// <c>wec.&lt;dc&gt;.*</c> of 7.9, the Status section „Kolektor WEF” and the DC's log state for
/// attribution (a silent DC reads as unreachable – DC_UNREACHABLE in its collector variant).
/// The tailer only reads below the output directory; it never renames or deletes there.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class WecFileIngestionJob : IWatchdogJob, ISecurityEventCollector
{
    /// <summary>Attempts at one offset before the batch is processed line by line (7.5).</summary>
    public const int MaxAttempts = 3;

    /// <summary>Batches per client directory and run (the job runs again at once when more data is waiting).</summary>
    public const int MaxBatchesPerDirectory = 20;

    /// <summary>Unconsumed bytes of one DC above which <c>wec.&lt;dc&gt;.backlog</c> is Warning (7.9).</summary>
    public const long BacklogWarningBytes = 256L * 1024 * 1024;

    /// <summary>Interval while no data is waiting (7.5: inotify or every 2 s).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A rotated file unconsumed for longer than this is <c>wec.&lt;dc&gt;.backlog</c> Warning (7.9).</summary>
    public static readonly TimeSpan BacklogWarningAge = TimeSpan.FromHours(1);

    /// <summary>Median delivery lag above which <c>wec.&lt;dc&gt;.lag</c> is Warning (7.9).</summary>
    public static readonly TimeSpan LagWarning = TimeSpan.FromMinutes(5);

    /// <summary>No file written by any DC for this long (with the directory reachable) is <c>wec.service</c> Error (7.9).</summary>
    public static readonly TimeSpan ServiceSilence = TimeSpan.FromMinutes(10);

    /// <summary>A rotated file whose last line has no line feed and that was not written for this long gets its tail
    /// consumed as a malformed line (OpenWEC died mid-write before the rotation).</summary>
    public static readonly TimeSpan StaleTail = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly IWatchdogConfigProvider _configuration;
    private readonly ISecurityEventStore _store;
    private readonly CollectorIngestor _ingestor;
    private readonly SecurityEventReductionFactory _reductions;
    private readonly ICollectorDomainControllerSource _domainControllers;
    private readonly IConfigTokenSource _tokens;
    private readonly EventLogGapDetector _gaps;
    private readonly IEventStore _events;
    private readonly DcLogHealthTracker _tracker;
    private readonly IHealthRegistry _health;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WecFileIngestionJob> _logger;
    private readonly CollectorLogonBudget _budget;
    private readonly DateTimeOffset _startedAt;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DirectoryState> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DcStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(ulong Device, ulong Inode, long Offset)> _marked = [];

    public WecFileIngestionJob(
        IOptionsMonitor<ServiceOptions> options,
        IWatchdogConfigProvider configuration,
        ISecurityEventStore store,
        CollectorIngestor ingestor,
        SecurityEventReductionFactory reductions,
        ICollectorDomainControllerSource domainControllers,
        IConfigTokenSource tokens,
        EventLogGapDetector gaps,
        IEventStore events,
        DcLogHealthTracker tracker,
        IHealthRegistry health,
        IWatchdogSignals signals,
        TimeProvider timeProvider,
        ILogger<WecFileIngestionJob> logger)
    {
        _options = options;
        _configuration = configuration;
        _store = store;
        _ingestor = ingestor;
        _reductions = reductions;
        _domainControllers = domainControllers;
        _tokens = tokens;
        _gaps = gaps;
        _events = events;
        _tracker = tracker;
        _health = health;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
        _budget = new CollectorLogonBudget(options.CurrentValue.Collector.MaxLogonRecordsPerDcPerMinute);
        _startedAt = timeProvider.GetUtcNow();
    }

    /// <summary>Test seam: called at the named steps of a batch ("attempt", "prepared", "detected", "committed",
    /// "progress") with the batch's lines; an exception thrown there stands for a crash at that step.</summary>
    internal Action<string, IReadOnlyList<WecLine>>? FaultPoint { get; set; }

    /// <inheritdoc/>
    public string Name => "wec-ingestion";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval => PollInterval;

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals { get; } = [WatchdogSignal.ConfigChanged];

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration.Current.Configuration;
        var settings = configuration.Attribution ?? new AttributionSettings();
        var collector = _options.CurrentValue.Collector;
        if (!settings.Enabled || settings.Source != AttributionSourceKind.OpenWec)
        {
            _health.ReportCollector(new CollectorStatusDto { State = HealthState.Disabled, Kind = collector.Kind.ToString(), SubscriptionUuid = collector.SubscriptionUuid });
            return new JobResult(TimeSpan.FromSeconds(30));
        }

        var now = _timeProvider.GetUtcNow();
        var domainControllers = await _domainControllers.GetAsync(cancellationToken).ConfigureAwait(false);
        if (domainControllers.Count == 0)
        {
            // The DC list was never read (LDAP down at start): without it every record would fail check 2 and be dropped –
            // the files wait instead.
            _health.ReportCollector(new CollectorStatusDto
            {
                State = HealthState.Unknown,
                Kind = collector.Kind.ToString(),
                SubscriptionUuid = collector.SubscriptionUuid,
            });
            return new JobResult(TimeSpan.FromSeconds(30));
        }

        var domain = Domain();
        var identity = new CollectorIdentityPolicy(collector.SubscriptionUuid, Realm(domain), domainControllers, AdditionalAddresses(collector));
        var context = new CollectorIngestionContext
        {
            Ingestion = new IngestionContext
            {
                Settings = settings,
                Reduction = await _reductions.CreateAsync(configuration, cancellationToken).ConfigureAwait(false),
                IsKnownDomainController = computer => domainControllers.Any(d => HostNames.AreEqual(d.HostName, computer)),
                Domain = domain,
                DisabledKinds = configuration.Directory?.DisabledKinds ?? [],
            },
            Identity = identity,
            Budget = _budget,
        };

        var appended = 0;
        var more = false;
        string? failure = null;
        foreach (var directory in ClientDirectories(collector.OutputDirectory))
        {
            try
            {
                var (added, pending) = await IngestDirectoryAsync(directory, collector, context, cancellationToken).ConfigureAwait(false);
                appended += added;
                more |= pending;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                failure = SafeExceptionLog.ErrorCode(ex);
                SafeExceptionLog.LogError(_logger, ex, "WEF file ingestion");
                Stats(KeyOf(identity, Path.GetFileName(directory))).ErrorCode = failure;
            }
        }

        WriteProgress(collector);
        ReportHealth(identity, settings, collector);
        if (appended > 0)
        {
            _signals.Raise(WatchdogSignal.EventsStored);
        }

        return failure is null
            ? new JobResult(more ? TimeSpan.Zero : PollInterval)
            : new JobResult(PollInterval, Failed: true, FailureCode: failure);
    }

    /// <summary>The client directories (no symbolic links, names of OpenWEC's character set) in name order.</summary>
    public static IReadOnlyList<string> ClientDirectories(string outputDirectory)
    {
        try
        {
            return [.. System.IO.Directory.EnumerateDirectories(outputDirectory)
                .Where(d => PosixPermissionCheck.Stat(d) is { IsDirectory: true, IsSymbolicLink: false })
                .Where(d => Path.GetFileName(d) is { Length: > 0 } name && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@'))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<(int Appended, bool More)> IngestDirectoryAsync(string path, CollectorBootstrapOptions collector, CollectorIngestionContext context,
        CancellationToken cancellationToken)
    {
        var client = Path.GetFileName(path);
        var key = KeyOf(context.Identity, client);
        var stats = Stats(key);
        stats.Client = client;
        stats.ErrorCode = null;
        if (context.Identity.ForDirectory(client) is null)
        {
            // Not the directory of a writable DC of the domain (yet – a new DC appears with the next DC list refresh): its
            // files are left unread (no record is dropped for it) and reported as identityMismatch.
            stats.UnknownClient = true;
            return (0, false);
        }

        stats.UnknownClient = false;
        var files = WecFiles.List(path, collector.FileName);
        var state = Directory(client);
        if (files.Count > 0)
        {
            stats.LastFileWriteAt = files.Max(f => f.ModifiedAt);
        }

        var cursor = WecFileCursor.Parse(await _store.GetCursorAsync(WecFileCursor.SourceOf(client), cancellationToken).ConfigureAwait(false));
        var appended = await DetectLossAsync(state, files, cursor, key, context, cancellationToken).ConfigureAwait(false);
        var index = 0;
        var offset = 0L;
        if (cursor is not null && IndexOf(files, cursor) is var found and >= 0)
        {
            index = found;
            offset = cursor.Offset <= files[found].Size ? cursor.Offset : 0;   // a shorter file: replaced in place – read it again
        }

        state.Cursor = cursor;
        state.Files = files;
        if (index < files.Count)
        {
            state.Remember(files, files[index], offset);
        }

        var batches = 0;
        var more = false;
        var now = _timeProvider.GetUtcNow();
        for (; index < files.Count; index++, offset = 0)
        {
            var file = files[index];
            if (file.FirstLineSha256 is null)
            {
                break;   // no complete line yet
            }

            var consumedFile = false;
            while (true)
            {
                if (batches >= MaxBatchesPerDirectory)
                {
                    more = true;
                    break;
                }

                if (WecFiles.Read(file, offset) is not { } read)
                {
                    break;   // renamed or replaced since the listing: the next run lists again
                }

                if (read.ConsumedTo == offset)
                {
                    if (file.IsRotated && read.Length > offset && file.ModifiedAt <= now - StaleTail && index < files.Count - 1)
                    {
                        // The tail of a rotated file that will never get its line feed (openwecd died mid-write).
                        cursor = WecFileCursor.At(file, read.Length);
                        await CommitAsync(client, [], cursor, collector, [], cancellationToken).ConfigureAwait(false);
                        stats.AddMalformed(now, 1);
                        offset = read.Length;
                        consumedFile = true;
                    }
                    else
                    {
                        consumedFile = read.Length == offset;
                    }

                    break;
                }

                (cursor, var added) = await ProcessBatchAsync(client, key, file, offset, read, cursor, context, collector, cancellationToken).ConfigureAwait(false);
                appended += added;
                offset = cursor.Offset;
                batches++;
            }

            state.Remember(files, file, offset);
            if (more || !consumedFile || !file.IsRotated)
            {
                break;
            }
        }

        if (index >= files.Count && files.Count > 0)
        {
            state.Remember(files, files[^1], Math.Max(files[^1].Size, offset));
        }

        return (appended, more);
    }

    /// <summary>One batch: normal (attempt recorded, all lines, one commit) or – after <see cref="MaxAttempts"/> at this
    /// offset, or while a poison window is open – line by line up to the poison window's end.</summary>
    private async Task<(WecFileCursor Cursor, int Appended)> ProcessBatchAsync(string client, string key, WecFileInfo file, long offset,
        WecReadResult read, WecFileCursor? cursor, CollectorIngestionContext context, CollectorBootstrapOptions collector, CancellationToken cancellationToken)
    {
        var same = cursor is not null && cursor.Matches(file);
        var attempts = same ? cursor!.AttemptsAt(offset) : 0;
        long? poisonEnd = same && cursor!.PoisonEnd is { } end && offset < end ? end : null;
        if (poisonEnd is null && attempts >= MaxAttempts)
        {
            poisonEnd = read.ConsumedTo;
            LogPoison(client, attempts);
        }

        return poisonEnd is { } windowEnd
            ? await ProcessLineByLineAsync(client, key, file, read, cursor!, windowEnd, context, collector, cancellationToken).ConfigureAwait(false)
            : await ProcessWholeBatchAsync(client, key, file, offset, read, attempts, context, collector, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(WecFileCursor Cursor, int Appended)> ProcessWholeBatchAsync(string client, string key, WecFileInfo file, long offset,
        WecReadResult read, int attempts, CollectorIngestionContext context, CollectorBootstrapOptions collector, CancellationToken cancellationToken)
    {
        var marked = WecFileCursor.At(file, offset) with { Size = read.Length, Attempts = attempts + 1, AttemptOffset = offset };
        await MarkAsync(client, marked, read.Lines, cancellationToken).ConfigureAwait(false);

        var batch = _ingestor.Prepare(client, [.. read.Lines.Select(l => l.Bytes)], context);
        FaultPoint?.Invoke("prepared", read.Lines);
        var detections = await _ingestor.DetectAsync(batch, context, cancellationToken).ConfigureAwait(false);
        FaultPoint?.Invoke("detected", read.Lines);

        var next = WecFileCursor.At(file, read.ConsumedTo) with { Size = read.Length };
        await CommitAsync(client, batch.Records, next, collector, read.Lines, cancellationToken).ConfigureAwait(false);
        Unmark(file, offset);
        Account(key, client, batch, read.SkippedLongLines);
        FaultPoint?.Invoke("progress", read.Lines);
        return (next, detections.Count);
    }

    private async Task<(WecFileCursor Cursor, int Appended)> ProcessLineByLineAsync(string client, string key, WecFileInfo file, WecReadResult read,
        WecFileCursor cursor, long windowEnd, CollectorIngestionContext context, CollectorBootstrapOptions collector, CancellationToken cancellationToken)
    {
        var appended = 0;
        foreach (var line in read.Lines.Where(l => l.Start < windowEnd))
        {
            // A line whose attempt an earlier process recorded in the poison window crashed that process: skip it. A marker
            // of this process means an exception (a store failure) – retried, never skipped.
            var failedBefore = cursor.Matches(file) && cursor.PoisonEnd is not null && cursor.AttemptsAt(line.Start) >= 1
                && !MarkedHere(file, line.Start);
            var batch = new CollectorBatch();
            if (failedBefore)
            {
                batch.Malformed = 1;   // it crashed the process alone: skipped
                LogPoisonLineSkipped(client, line.Start);
            }
            else
            {
                var marked = WecFileCursor.At(file, line.Start) with { Size = read.Length, Attempts = 1, AttemptOffset = line.Start, PoisonEnd = windowEnd };
                await MarkAsync(client, marked, [line], cancellationToken).ConfigureAwait(false);
                cursor = marked;
                try
                {
                    batch = _ingestor.Prepare(client, [line.Bytes], context);
                    FaultPoint?.Invoke("prepared", [line]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    SafeExceptionLog.LogWarning(_logger, ex, "WEF line");
                    batch = new CollectorBatch { Malformed = 1 };
                    LogPoisonLineSkipped(client, line.Start);
                }

                appended += (await _ingestor.DetectAsync(batch, context, cancellationToken).ConfigureAwait(false)).Count;
            }

            var next = WecFileCursor.At(file, line.End) with { Size = read.Length, PoisonEnd = line.End < windowEnd ? windowEnd : null };
            await CommitAsync(client, batch.Records, next, collector, [line], cancellationToken).ConfigureAwait(false);
            Unmark(file, line.Start);
            Account(key, client, batch, 0);
            cursor = next;
        }

        return (cursor, appended);
    }

    /// <summary>Records the attempt at the marker's offset in its own transaction before the lines are processed.</summary>
    private async Task MarkAsync(string client, WecFileCursor marked, IReadOnlyList<WecLine> lines, CancellationToken cancellationToken)
    {
        await _store.AppendAsync([], marked.ToEventLogCursor(client, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _marked.Add((marked.Device, marked.Inode, marked.AttemptOffset ?? marked.Offset));
        }

        FaultPoint?.Invoke("attempt", lines);
    }

    /// <summary>The batch at <paramref name="offset"/> is committed: its attempt marker is no longer needed.</summary>
    private void Unmark(WecFileInfo file, long offset)
    {
        lock (_gate)
        {
            _marked.Remove((file.Device, file.Inode, offset));
        }
    }

    private bool MarkedHere(WecFileInfo file, long offset)
    {
        lock (_gate)
        {
            return _marked.Contains((file.Device, file.Inode, offset));
        }
    }

    /// <summary>Records and cursor in one transaction, then the progress file.</summary>
    private async Task CommitAsync(string client, IReadOnlyList<SecurityEvent> records, WecFileCursor cursor, CollectorBootstrapOptions collector,
        IReadOnlyList<WecLine> lines, CancellationToken cancellationToken)
    {
        await _store.AppendAsync(records, cursor.ToEventLogCursor(client, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        FaultPoint?.Invoke("committed", lines);
        var state = Directory(client);
        state.Cursor = cursor;
        if (state.Files.FirstOrDefault(cursor.Matches) is { } file)
        {
            state.Remember(state.Files, file, cursor.Offset);
        }

        WriteProgress(collector);
    }

    /// <summary>
    /// A file lost before it was consumed (7.5, HealthEventLogGap reason <c>collectorFileLost</c>): the cursor's file is gone
    /// although its committed offset was below its size, or a file this process saw with unread bytes vanished. A file
    /// that was consumed completely and then deleted by <c>admng-wec-rotate</c> is not a loss.
    /// </summary>
    private async Task<int> DetectLossAsync(DirectoryState state, IReadOnlyList<WecFileInfo> files, WecFileCursor? cursor, string key,
        CollectorIngestionContext context, CancellationToken cancellationToken)
    {
        var lost = false;
        if (cursor is not null && !files.Any(cursor.Matches) && cursor.Offset < cursor.Size && state.ReportedLost != (cursor.Device, cursor.Inode))
        {
            state.ReportedLost = (cursor.Device, cursor.Inode);
            lost = true;
        }

        foreach (var (identity, unread) in state.Unconsumed)
        {
            if (unread && !files.Any(f => f.Device == identity.Device && f.Inode == identity.Inode))
            {
                lost = true;
            }
        }

        if (!lost)
        {
            return 0;
        }

        var stats = Stats(key);
        stats.LostAt = _timeProvider.GetUtcNow();
        LogFileLost(stats.Client ?? key);
        if (_gaps.Gap(key, EventLogGapDetector.CollectorFileLost, context.Ingestion.Domain) is { } gap
            && await _events.GetByEventIdAsync(gap.EventId, cancellationToken).ConfigureAwait(false) is null)
        {
            await _events.AppendAsync(gap, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        return 0;
    }

    private void Account(string key, string client, CollectorBatch batch, int skippedLongLines)
    {
        var now = _timeProvider.GetUtcNow();
        var stats = Stats(key);
        stats.Client = client;
        stats.AddStored(now, batch.Records.Count);
        stats.AddMalformed(now, batch.Malformed + skippedLongLines);
        stats.AddRejected(now, batch.RejectedTotal);
        stats.AddOverBudget(now, batch.OverBudget);
        foreach (var (check, count) in batch.Rejected)
        {
            LogRejected(client, check.ToString(), count);
        }

        if (batch.Accepted == 0)
        {
            return;
        }

        stats.LastDeliveryAt = Later(stats.LastDeliveryAt, batch.NewestReceivedAt ?? now);
        stats.LastRecordAt = Later(stats.LastRecordAt, batch.NewestRecordAt);
        stats.LastReceivedAt = Later(stats.LastReceivedAt, batch.NewestReceivedAt);
        stats.AddLags(batch.Lags, now);
        if (batch.Dc is { } dc)
        {
            _tracker.RecordRead(dc.HostName, batch.MaxRecordId);
        }
    }

    /// <summary>The checks of 7.9, the Status section and the DCs' log state for attribution.</summary>
    private void ReportHealth(CollectorIdentityPolicy identity, AttributionSettings settings, CollectorBootstrapOptions collector)
    {
        var now = _timeProvider.GetUtcNow();
        var rows = new List<CollectorDcStatusDto>();
        var writable = identity.DomainControllers.Select(d => d.HostName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dc in identity.DomainControllers.OrderBy(d => d.HostName, StringComparer.Ordinal))
        {
            rows.Add(ReportDc(dc.HostName, settings, now));
        }

        List<KeyValuePair<string, DcStats>> foreign;
        lock (_gate)
        {
            foreign = [.. _stats.Where(s => !writable.Contains(s.Key))];
        }

        foreach (var (key, stats) in foreign)
        {
            var rejected = stats.Rejected24h(now);
            Report(key, HealthCheckCodes.WecIdentityMismatch, rejected > 0 || stats.UnknownClient ? HealthState.Error : HealthState.Ok, now, rejected);
        }

        var lastWrite = Snapshot().Select(s => s.LastFileWriteAt).Max();
        var serviceSilent = now - _startedAt >= ServiceSilence && (lastWrite is null || now - lastWrite >= ServiceSilence)
            && _health.Directory.State == HealthState.Ok && identity.DomainControllers.Count > 0;
        _health.Report(new HealthCheckDto
        {
            Code = HealthCheckCodes.WecService,
            State = serviceSilent ? HealthState.Error : HealthState.Ok,
            Detail = serviceSilent ? "No DC has written an OpenWEC file for 10 minutes while the directory is reachable." : null,
            CheckedAt = now,
        });

        var worst = rows.Select(r => r.State).Append(serviceSilent ? HealthState.Error : HealthState.Ok).Max();
        _health.ReportCollector(new CollectorStatusDto
        {
            State = worst,
            Kind = collector.Kind.ToString(),
            SubscriptionUuid = collector.SubscriptionUuid,
            BacklogBytes = rows.Sum(r => r.BacklogBytes),
            DomainControllers = rows,
        });
    }

    private CollectorDcStatusDto ReportDc(string dc, AttributionSettings settings, DateTimeOffset now)
    {
        var stats = Stats(dc);
        var delivered = stats.Client is not null;
        var silentSince = stats.LastDeliveryAt ?? _startedAt;
        var silence = now - silentSince;
        var silentState = silence >= settings.CollectorSilenceError ? HealthState.Error
            : silence >= settings.CollectorSilenceWarning ? HealthState.Warning
            : HealthState.Ok;
        if (silentState == HealthState.Error)
        {
            // Unreachable for attribution (DC_UNREACHABLE, collector variant) – the engine's monitor raises
            // HealthAttributionDegraded on the transition.
            _tracker.RecordFailure(dc, delivered ? HealthCheckCodes.WecSilent : HealthCheckCodes.WecNotForwarding);
        }

        var lag = stats.MedianLag(now);
        var rejected = stats.Rejected24h(now);
        var malformed = stats.Malformed24h(now);
        var (backlog, oldest) = Backlog(stats.Client);
        var backlogWarning = backlog > BacklogWarningBytes || (oldest is { } rotated && now - rotated > BacklogWarningAge);
        var lost = stats.LostAt is { } lostAt && now - lostAt < Day;

        Report(dc, HealthCheckCodes.WecNotForwarding, delivered ? HealthState.Ok : HealthState.Error, now);
        Report(dc, HealthCheckCodes.WecSilent, delivered ? silentState : HealthState.Ok, now);
        Report(dc, HealthCheckCodes.WecLag, lag > LagWarning ? HealthState.Warning : HealthState.Ok, now);
        Report(dc, HealthCheckCodes.WecIdentityMismatch, rejected > 0 ? HealthState.Error : HealthState.Ok, now, rejected);
        Report(dc, HealthCheckCodes.WecMalformedLines, malformed > 0 ? HealthState.Warning : HealthState.Ok, now, malformed);
        Report(dc, HealthCheckCodes.WecBacklog, backlogWarning ? HealthState.Warning : HealthState.Ok, now);
        Report(dc, HealthCheckCodes.WecCollectorFileLost, lost ? HealthState.Error : HealthState.Ok, now);

        var states = new[]
        {
            delivered ? silentState : HealthState.Error,
            lag > LagWarning || malformed > 0 || backlogWarning ? HealthState.Warning : HealthState.Ok,
            rejected > 0 || lost || stats.ErrorCode is not null ? HealthState.Error : HealthState.Ok,
        };
        return new CollectorDcStatusDto
        {
            Dc = dc,
            State = states.Max(),
            Client = stats.Client,
            LastRecordAt = stats.LastRecordAt,
            LastReceivedAt = stats.LastReceivedAt,
            MedianLag = lag,
            BacklogBytes = backlog,
            OldestUnconsumedFileAt = oldest,
            RecordsStored24h = stats.Stored24h(now),
            RejectedRecords24h = (int)Math.Min(int.MaxValue, rejected),
            MalformedLines24h = (int)Math.Min(int.MaxValue, malformed),
            DroppedLogonRecords24h = stats.OverBudget24h(now),
            ErrorCode = stats.ErrorCode ?? (lost ? HealthCheckCodes.WecCollectorFileLost : null),
        };
    }

    private void Report(string dc, string check, HealthState state, DateTimeOffset now, long count = 0) =>
        _health.Report(new HealthCheckDto
        {
            Code = HealthCheckCodes.Wec(dc, check),
            State = state,
            Args = count > 0 ? [dc, count.ToString(CultureInfo.InvariantCulture)] : [dc],
            CheckedAt = now,
        });

    /// <summary>Unconsumed bytes of a client directory and the rotation time of its oldest unconsumed rotated file.</summary>
    private (long Bytes, DateTimeOffset? OldestRotated) Backlog(string? client)
    {
        if (client is null)
        {
            return (0, null);
        }

        var state = Directory(client);
        long bytes = 0;
        DateTimeOffset? oldest = null;
        foreach (var file in state.Files)
        {
            var consumed = state.ConsumedOf(file);
            if (consumed < file.Size)
            {
                bytes += file.Size - consumed;
                if (file.RotatedAt is { } rotated && (oldest is null || rotated < oldest))
                {
                    oldest = rotated;
                }
            }
        }

        return (bytes, oldest);
    }

    private void WriteProgress(CollectorBootstrapOptions collector)
    {
        List<WecProgressEntry> entries;
        lock (_gate)
        {
            entries = [.. _directories.OrderBy(d => d.Key, StringComparer.Ordinal).SelectMany(d => d.Value.Progress(d.Key))];
        }

        try
        {
            WecProgressFile.Write(collector.ProgressFile, entries, _timeProvider.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "WEF progress file");
        }
    }

    private string? Domain() =>
        DistinguishedName.TryParse(_tokens.Current.DomainDn, out var dn) && !dn.IsRoot ? dn.DnsDomain : null;

    private string Realm(string? domain) =>
        _options.CurrentValue.Kerberos.Realm is { Length: > 0 } realm ? realm : (domain ?? "LOCALDOMAIN").ToUpperInvariant();

    private static IEnumerable<IPAddress> AdditionalAddresses(CollectorBootstrapOptions collector) =>
        (collector.AdditionalSourceAddresses ?? []).Select(a => IPAddress.TryParse(a?.Trim(), out var address) ? address : null).OfType<IPAddress>();

    /// <summary>The status key of a client directory: its DC's host name, or a host-like name of the directory
    /// (<c>SRV01@CONTOSO.COM</c> → <c>srv01.contoso.com</c>) for a client that is not a writable DC.</summary>
    private static string KeyOf(CollectorIdentityPolicy identity, string client)
    {
        if (identity.ForDirectory(client)?.HostName is { } host)
        {
            return host;
        }

        var pseudo = new string([.. client.ToLowerInvariant().Select(c => c is '@' ? '.' : c is '_' ? '-' : c)]).Trim('.', '-');
        return pseudo.Length > 0 ? pseudo : "unknown";
    }

    private static int IndexOf(IReadOnlyList<WecFileInfo> files, WecFileCursor cursor)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (cursor.Matches(files[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private DirectoryState Directory(string client)
    {
        lock (_gate)
        {
            if (!_directories.TryGetValue(client, out var state))
            {
                state = new DirectoryState();
                _directories[client] = state;
            }

            return state;
        }
    }

    private DcStats Stats(string key)
    {
        lock (_gate)
        {
            if (!_stats.TryGetValue(key, out var stats))
            {
                stats = new DcStats();
                _stats[key] = stats;
            }

            return stats;
        }
    }

    private List<DcStats> Snapshot()
    {
        lock (_gate)
        {
            return [.. _stats.Values];
        }
    }

    [LoggerMessage(EventId = EventLogIds.CollectorProblem, Level = LogLevel.Error, Message = "WEF collector: {Count} record(s) in {Client} failed the identity check {Check}.")]
    private partial void LogRejected(string client, string check, int count);

    [LoggerMessage(EventId = EventLogIds.CollectorProblem, Level = LogLevel.Warning, Message = "WEF collector: the batch at the cursor of {Client} failed {Attempts} times; it is processed line by line.")]
    private partial void LogPoison(string client, int attempts);

    [LoggerMessage(EventId = EventLogIds.CollectorProblem, Level = LogLevel.Warning, Message = "WEF collector: a line of {Client} at offset {Offset} failed again and was skipped.")]
    private partial void LogPoisonLineSkipped(string client, long offset);

    [LoggerMessage(EventId = EventLogIds.CollectorProblem, Level = LogLevel.Error, Message = "WEF collector: a file of {Client} was deleted before it was read completely.")]
    private partial void LogFileLost(string client);

    /// <summary>The files of one client directory as last seen and how far each is consumed (progress file, backlog,
    /// loss detection).</summary>
    private sealed class DirectoryState
    {
        private readonly Dictionary<(ulong Device, ulong Inode), long> _consumed = [];

        public IReadOnlyList<WecFileInfo> Files { get; set; } = [];

        public WecFileCursor? Cursor { get; set; }

        public (ulong Device, ulong Inode)? ReportedLost { get; set; }

        /// <summary>Files with unread bytes (true) at the last pass.</summary>
        public IEnumerable<((ulong Device, ulong Inode) Identity, bool Unread)> Unconsumed =>
            Files.Select(f => ((f.Device, f.Inode), ConsumedOf(f) < f.Size));

        /// <summary>Files before <paramref name="current"/> are consumed, <paramref name="current"/> up to
        /// <paramref name="offset"/>.</summary>
        public void Remember(IReadOnlyList<WecFileInfo> files, WecFileInfo current, long offset)
        {
            Files = files;
            foreach (var file in files)
            {
                if (file == current)
                {
                    _consumed[(file.Device, file.Inode)] = offset;
                    break;
                }

                _consumed[(file.Device, file.Inode)] = file.Size;
            }

            foreach (var gone in _consumed.Keys.Where(k => !files.Any(f => f.Device == k.Device && f.Inode == k.Inode)).ToList())
            {
                _consumed.Remove(gone);
            }
        }

        public long ConsumedOf(WecFileInfo file) => _consumed.GetValueOrDefault((file.Device, file.Inode));

        public IEnumerable<WecProgressEntry> Progress(string client) =>
            Files.Where(f => f.FirstLineSha256 is not null).Select(f =>
            {
                var consumed = ConsumedOf(f);
                var size = Math.Max(f.Size, consumed);
                return new WecProgressEntry(client, f.Name, f.Device, f.Inode, f.FirstLineSha256!, consumed, size, f.IsRotated && consumed >= size);
            });
    }

    /// <summary>Per-DC counters of the last 24 hours (hourly buckets) and delivery facts.</summary>
    private sealed class DcStats
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, long[]> _hours = [];
        private readonly Queue<(DateTimeOffset At, TimeSpan Lag)> _lags = new();

        public string? Client { get; set; }

        public DateTimeOffset? LastDeliveryAt { get; set; }

        public DateTimeOffset? LastRecordAt { get; set; }

        public DateTimeOffset? LastReceivedAt { get; set; }

        public DateTimeOffset? LastFileWriteAt { get; set; }

        public DateTimeOffset? LostAt { get; set; }

        public string? ErrorCode { get; set; }

        /// <summary>A client directory of no writable DC of the domain: left unread.</summary>
        public bool UnknownClient { get; set; }

        public void AddStored(DateTimeOffset now, long count) => Add(now, 0, count);

        public void AddRejected(DateTimeOffset now, long count) => Add(now, 1, count);

        public void AddMalformed(DateTimeOffset now, long count) => Add(now, 2, count);

        public void AddOverBudget(DateTimeOffset now, long count) => Add(now, 3, count);

        public long Stored24h(DateTimeOffset now) => Sum(now, 0);

        public long Rejected24h(DateTimeOffset now) => Sum(now, 1);

        public long Malformed24h(DateTimeOffset now) => Sum(now, 2);

        public long OverBudget24h(DateTimeOffset now) => Sum(now, 3);

        public void AddLags(IEnumerable<(DateTimeOffset ReceivedAt, TimeSpan Lag)> lags, DateTimeOffset now)
        {
            lock (_gate)
            {
                foreach (var (at, lag) in lags)
                {
                    _lags.Enqueue((at, lag));
                }

                while (_lags.Count > 10_000 || (_lags.Count > 0 && now - _lags.Peek().At > TimeSpan.FromHours(1)))
                {
                    _lags.Dequeue();
                }
            }
        }

        /// <summary>Median delivery lag of the last hour; null without samples.</summary>
        public TimeSpan? MedianLag(DateTimeOffset now)
        {
            lock (_gate)
            {
                var recent = _lags.Where(l => now - l.At <= TimeSpan.FromHours(1)).Select(l => l.Lag).Order().ToList();
                return recent.Count == 0 ? null : recent[recent.Count / 2];
            }
        }

        private void Add(DateTimeOffset now, int counter, long count)
        {
            if (count <= 0)
            {
                return;
            }

            var hour = now.ToUnixTimeSeconds() / 3600;
            lock (_gate)
            {
                if (!_hours.TryGetValue(hour, out var buckets))
                {
                    buckets = new long[4];
                    _hours[hour] = buckets;
                    foreach (var old in _hours.Keys.Where(h => h <= hour - 24).ToList())
                    {
                        _hours.Remove(old);
                    }
                }

                buckets[counter] += count;
            }
        }

        private long Sum(DateTimeOffset now, int counter)
        {
            var hour = now.ToUnixTimeSeconds() / 3600;
            lock (_gate)
            {
                return _hours.Where(h => h.Key > hour - 24).Sum(h => h.Value[counter]);
            }
        }
    }
}
