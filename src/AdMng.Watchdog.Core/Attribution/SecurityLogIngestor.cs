using System.Globalization;
using System.Xml.Linq;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Ingestion of one source (section 11.1): log info (health <c>eventlog.&lt;host&gt;</c> Ok / Error with the Win32
/// code), cursor, records of the ingestion set with the noisy 5136 attributes suppressed, parse →
/// <see cref="EventSourceGuard"/> → event-log-only detections and record gaps → <see cref="SecurityEventReducer"/> → store
/// with the new cursor in one call per page, until a short page or <c>MaxRecordsPerPoll</c> (then HealthEventLogGap
/// "flooding"; the cursor keeps moving). Dropped records still move the cursor.
/// <list type="bullet">
/// <item><b>Security log of a DC</b>: the cursor is the DC's own <c>EventRecordID</c>. A new source starts at
/// <c>now − ReplicationLatencyBound</c> (no history); records overwritten unread raise a gap; a cursor regression after a
/// cleared or recreated log rebases the cursor and reads the new log from <c>now − ReplicationLatencyBound</c>, so the
/// 1102 of the clear is ingested. After the last (short) page the cursor moves to the newest record of the log even when
/// no record of the ingestion set arrived – the filtered query proves there is none up to it, so a busy DC whose log
/// wraps between account changes reports no false "overwritten" gap.</item>
/// <item><b>ForwardedEvents log of a WEF collector</b>: the records keep their source DC's <c>EventRecordID</c>, which is
/// neither comparable across DCs nor with the collector's own numbering – the cursor is time based instead: the newest
/// <c>TimeCreated</c> read (capped at now) is kept in <see cref="EventLogCursor.BookmarkXml"/>
/// (<c>&lt;TimeCursor SystemTime="…"/&gt;</c>), every poll reads from that time minus <c>ReplicationLatencyBound</c>
/// (records delivered late by WEF), and the collector's record numbers are never checked for overwrites or
/// regressions. Records read again are ignored by the store (computer, record id) and by the detections (stable ids).</item>
/// </list>
/// Detections are appended to the event store <b>before</b> the cursor of their page is committed, under an id derived
/// from the record (<see cref="RecordEventIds"/>) and only when that id is not stored yet – an interrupted pass re-reads
/// the page and neither loses nor repeats a SecurityLogCleared. A 1102/1104 or a cursor regression marks the DC's log as
/// overwritten from that time (11.7 LOG_OVERWRITTEN).
/// </summary>
public sealed partial class SecurityLogIngestor(
    ISecurityEventSource source,
    ISecurityEventStore store,
    IEventStore events,
    SecurityEventParser parser,
    EventLogGapDetector gaps,
    EventLogOnlyDetector eventLogOnly,
    DcLogHealthTracker tracker,
    IHealthRegistry health,
    TimeProvider timeProvider,
    ILogger<SecurityLogIngestor> logger)
{
    /// <summary>Records per read (11.1).</summary>
    public const int PageSize = 1000;

    /// <summary>The log a WEF collector receives the DC records in (<see cref="IngestionSource.LogName"/>).</summary>
    public const string ForwardedEventsLog = "ForwardedEvents";

    /// <summary>5136 attributes suppressed in the query (noise, 9.3).</summary>
    public static IReadOnlyList<string> SuppressedAttributes { get; } =
    [
        "dSCorePropagationData", "lastLogonTimestamp", "lastLogon", "logonCount", "badPwdCount", "badPasswordTime",
        "msDS-LastSuccessfulInteractiveLogonTime", "msDS-LastFailedInteractiveLogonTime", "msDS-FailedInteractiveLogonCount",
        "msDS-KeyVersionNumber", "dnsRecord", "dNSTombstoned",
    ];

    /// <summary>Ingests new records of <paramref name="ingestion"/>.</summary>
    public async Task<IngestionResult> IngestAsync(IngestionSource ingestion, IngestionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(context);
        var host = ingestion.Host;
        var now = timeProvider.GetUtcNow();
        SecurityLogInfo info;
        EventLogCursor? cursor;
        try
        {
            info = await source.GetLogInfoAsync(host, ingestion.LogName, cancellationToken).ConfigureAwait(false);
            cursor = await store.GetCursorAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SecurityLogAccessException ex)
        {
            return Failed(host, ex, now);
        }

        var pass = new Pass(host, context, now);
        long? last;
        try
        {
            last = IsForwarded(ingestion)
                ? await ForwardedAsync(pass, ingestion, cursor, cancellationToken).ConfigureAwait(false)
                : await SecurityLogAsync(pass, ingestion, info, cursor, cancellationToken).ConfigureAwait(false);
        }
        catch (SecurityLogAccessException ex)
        {
            return Failed(host, ex, now) with { Events = pass.Appended };
        }

        tracker.RecordRead(host, last);
        ReportHealth(host, pass.Result, now);
        return pass.Result with { Events = pass.Appended };
    }

    /// <summary>True for a WEF collector's ForwardedEvents log.</summary>
    public static bool IsForwarded(IngestionSource ingestion)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        return string.Equals(ingestion.LogName, ForwardedEventsLog, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The time cursor of a ForwardedEvents source (<see cref="EventLogCursor.BookmarkXml"/>); null when none.</summary>
    public static DateTimeOffset? ReadTimeCursor(EventLogCursor? cursor)
    {
        if (cursor?.BookmarkXml is not { Length: > 0 } xml)
        {
            return null;
        }

        try
        {
            var element = XElement.Parse(xml);
            return element.Name.LocalName == "TimeCursor"
                && DateTimeOffset.TryParse((string?)element.Attribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime()
                : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;   // a bookmark of another kind (or of an older version): start like a new source
        }
    }

    /// <summary>The <see cref="EventLogCursor.BookmarkXml"/> form of a ForwardedEvents time cursor.</summary>
    public static string WriteTimeCursor(DateTimeOffset time) =>
        new XElement("TimeCursor", new XAttribute("SystemTime", time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))).ToString(SaveOptions.DisableFormatting);

    private SecurityEventQuery BaseQuery(IngestionSource ingestion) => new()
    {
        LogName = ingestion.LogName,
        EventCodes = SecurityEventCatalog.IngestionSet,
        SuppressAttributes = SuppressedAttributes,
        MaxRecords = PageSize,
    };

    private async Task<long?> SecurityLogAsync(Pass pass, IngestionSource ingestion, SecurityLogInfo info, EventLogCursor? cursor,
        CancellationToken cancellationToken)
    {
        var host = pass.Host;
        var settings = pass.Context.Settings;
        var query = BaseQuery(ingestion);
        var rebased = false;
        long? last;
        if (EventLogGapDetector.IsRegressed(cursor, info))
        {
            tracker.RecordOverwritten(host);
            await AddAsync(pass, gaps.Gap(host, EventLogGapDetector.Regressed, pass.Context.Domain), cancellationToken).ConfigureAwait(false);
            rebased = true;
            last = null;
            query = query with { From = pass.Now - settings.ReplicationLatencyBound };
        }
        else if (cursor is null)
        {
            last = null;
            query = query with { From = pass.Now - settings.ReplicationLatencyBound };
        }
        else
        {
            if (EventLogGapDetector.IsOverwritten(cursor, info))
            {
                tracker.RecordOverwritten(host);
                await AddAsync(pass, gaps.Gap(host, EventLogGapDetector.Overwritten, pass.Context.Domain), cancellationToken).ConfigureAwait(false);
            }

            last = cursor.LastRecordId;
            query = query with { AfterRecordId = cursor.LastRecordId };
        }

        var stored = cursor?.LastRecordId;
        while (true)
        {
            var page = await ReadPageAsync(host, query, cancellationToken).ConfigureAwait(false);
            var processed = await ProcessAsync(pass, page, cancellationToken).ConfigureAwait(false);
            if (processed.MaxRecordId is { } max)
            {
                last = Math.Max(last ?? 0, max);
            }

            var complete = page.Count < PageSize;
            var cursorId = complete
                ? Math.Max(last ?? 0, EventLogGapDetector.NewestRecordId(info) ?? 0)   // nothing of the ingestion set up to the newest record
                : last ?? 0;
            if (page.Count > 0 || rebased || (cursorId != stored && cursorId > 0))   // no cursor 0 for a log without numbers
            {
                var newCursor = new EventLogCursor { Source = host, LastRecordId = cursorId, OldestRecordSeen = info.OldestRecordNumber, UpdatedAt = pass.Now };
                await store.AppendAsync(processed.Batch, newCursor, cancellationToken).ConfigureAwait(false);
                stored = cursorId;
                rebased = false;
            }

            if (complete)
            {
                return stored ?? last;
            }

            if (pass.Result.Read >= settings.MaxRecordsPerPoll)
            {
                await AddAsync(pass, gaps.Gap(host, EventLogGapDetector.Flooding, pass.Context.Domain), cancellationToken).ConfigureAwait(false);
                return stored ?? last;
            }

            query = query with { AfterRecordId = last, From = null };
        }
    }

    private async Task<long?> ForwardedAsync(Pass pass, IngestionSource ingestion, EventLogCursor? cursor, CancellationToken cancellationToken)
    {
        var host = pass.Host;
        var settings = pass.Context.Settings;
        var known = ReadTimeCursor(cursor);
        var highWater = known;
        var query = BaseQuery(ingestion) with
        {
            From = (known ?? pass.Now) - settings.ReplicationLatencyBound,
            MaxRecords = Math.Max(1, settings.MaxRecordsPerPoll),
        };

        // One read of the whole window in log (arrival) order – records of different DCs interleave in time, so the window
        // cannot be paged by time; the stream is stored in pages of PageSize.
        var chunk = new List<string>(PageSize);
        var read = 0;
        await foreach (var xml in source.ReadAsync(host, query, cancellationToken).ConfigureAwait(false))
        {
            chunk.Add(xml);
            read++;
            if (chunk.Count == PageSize)
            {
                highWater = await StoreForwardedAsync(pass, chunk, highWater, cancellationToken).ConfigureAwait(false);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            await StoreForwardedAsync(pass, chunk, highWater, cancellationToken).ConfigureAwait(false);
        }

        if (read >= query.MaxRecords)
        {
            await AddAsync(pass, gaps.Gap(host, EventLogGapDetector.Flooding, pass.Context.Domain), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<DateTimeOffset?> StoreForwardedAsync(Pass pass, IReadOnlyList<string> chunk, DateTimeOffset? highWater, CancellationToken cancellationToken)
    {
        var processed = await ProcessAsync(pass, chunk, cancellationToken).ConfigureAwait(false);
        if (processed.MaxTimeCreated is { } newest)
        {
            // A DC with a clock ahead must not push the window past the records of the others.
            var capped = newest > pass.Now ? pass.Now : newest;
            highWater = highWater is { } known && known > capped ? known : capped;
        }

        var cursor = new EventLogCursor
        {
            Source = pass.Host,
            BookmarkXml = highWater is { } time ? WriteTimeCursor(time) : null,
            UpdatedAt = pass.Now,
        };
        await store.AppendAsync(processed.Batch, cursor, cancellationToken).ConfigureAwait(false);
        return highWater;
    }

    /// <summary>Appends a detection or gap unless an event with its id is stored already.</summary>
    private async Task AddAsync(Pass pass, WatchEvent? watchEvent, CancellationToken cancellationToken)
    {
        if (watchEvent is null || await events.GetByEventIdAsync(watchEvent.EventId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        await events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
        pass.Appended.Add(watchEvent);
    }

    /// <summary>Parses, guards, detects (appending detections and record gaps at once) and reduces a page.</summary>
    private async Task<ProcessedPage> ProcessAsync(Pass pass, IReadOnlyList<string> page, CancellationToken cancellationToken)
    {
        var context = pass.Context;
        var batch = new List<SecurityEvent>();
        int foreign = 0, malformed = 0;
        long? maxRecordId = null;
        DateTimeOffset? maxTime = null;
        foreach (var xml in page)
        {
            if (parser.Parse(xml, pass.Host) is not { } record)
            {
                malformed++;
                continue;
            }

            maxRecordId = Math.Max(maxRecordId ?? 0, record.Event.RecordId);
            maxTime = maxTime is { } known && known > record.Event.TimeCreated ? known : record.Event.TimeCreated;
            if (!EventSourceGuard.Accept(record, context.IsKnownDomainController))
            {
                foreign++;
                continue;
            }

            var computer = HostNames.Normalize(record.Event.Computer);
            tracker.RecordComputerName(computer, record.Event.Computer);
            if (EventLogGapDetector.ReasonOf(record.Event) is { } reason)
            {
                if (reason is EventLogGapDetector.LogCleared or EventLogGapDetector.LogFull)
                {
                    tracker.RecordOverwritten(computer, record.Event.TimeCreated);
                }
                else if (reason == EventLogGapDetector.ServiceShutdown)
                {
                    tracker.RecordShutdown(computer, record.Event.TimeCreated);   // logon ids restart (LogonSourceResolver cache)
                }

                await AddAsync(pass, gaps.Gap(computer, reason, context.Domain, record.Event), cancellationToken).ConfigureAwait(false);
            }

            if (await eventLogOnly.DetectAsync(record.Event, context.Domain, cancellationToken).ConfigureAwait(false) is { } detected
                && !context.DisabledKinds.Contains(detected.Kind))
            {
                await AddAsync(pass, detected, cancellationToken).ConfigureAwait(false);
            }

            if (SecurityEventReducer.Reduce(record.Event, context.Reduction) is { } reduced)
            {
                batch.Add(reduced);
            }
        }

        pass.Result = pass.Result with
        {
            Read = pass.Result.Read + page.Count,
            Stored = pass.Result.Stored + batch.Count,
            Foreign = pass.Result.Foreign + foreign,
            Malformed = pass.Result.Malformed + malformed,
        };
        return new ProcessedPage(batch, maxRecordId, maxTime);
    }

    private async Task<List<string>> ReadPageAsync(string host, SecurityEventQuery query, CancellationToken cancellationToken)
    {
        var page = new List<string>();
        await foreach (var xml in source.ReadAsync(host, query, cancellationToken).ConfigureAwait(false))
        {
            page.Add(xml);
        }

        return page;
    }

    private IngestionResult Failed(string host, SecurityLogAccessException ex, DateTimeOffset now)
    {
        var code = ex.Win32Error?.ToString(CultureInfo.InvariantCulture) ?? SafeExceptionLog.ErrorCode(ex);
        SafeExceptionLog.LogWarning(logger, ex, "security log read");
        tracker.RecordFailure(host, code);
        health.Report(new HealthCheckDto
        {
            Code = "eventlog." + host,
            State = HealthState.Error,
            Detail = "The Security log cannot be read (RPC: firewall / Remote Event Log Management; 5: Event Log Readers).",
            Args = [host, code],
            CheckedAt = now,
        });
        return new IngestionResult { Failed = true };
    }

    private void ReportHealth(string host, IngestionResult result, DateTimeOffset now)
    {
        health.Report(new HealthCheckDto { Code = "eventlog." + host, State = HealthState.Ok, Args = [host], CheckedAt = now });
        if (result.Foreign > 0 || result.Malformed > 0)
        {
            LogDropped(host, result.Foreign, result.Malformed);
        }

        health.Report(new HealthCheckDto
        {
            Code = "eventlog." + host + ".foreignRecords",
            State = result.Foreign > 0 ? HealthState.Warning : HealthState.Ok,
            Detail = result.Foreign > 0 ? "Records of unknown computers or providers were dropped." : null,
            Args = [host, result.Foreign.ToString(CultureInfo.InvariantCulture)],
            CheckedAt = now,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log {Host}: {Foreign} foreign and {Malformed} malformed records dropped.")]
    private partial void LogDropped(string host, int foreign, int malformed);

    /// <summary>The records of one page, parsed and reduced.</summary>
    private sealed record ProcessedPage(List<SecurityEvent> Batch, long? MaxRecordId, DateTimeOffset? MaxTimeCreated);

    /// <summary>One ingestion of one source: counters and the watch events appended so far.</summary>
    private sealed class Pass(string host, IngestionContext context, DateTimeOffset now)
    {
        public string Host => host;

        public IngestionContext Context => context;

        public DateTimeOffset Now => now;

        public IngestionResult Result { get; set; } = new();

        public List<WatchEvent> Appended { get; } = [];
    }
}
