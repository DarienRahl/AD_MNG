using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Ingestion of one source (section 11.1): log info (health <c>eventlog.&lt;host&gt;</c> Ok / Error with the Win32
/// code), cursor (a new source starts at <c>now − ReplicationLatencyBound</c> – no history), gap checks (records
/// overwritten unread; a cursor regression after a cleared or recreated log rebases the cursor and reads the new log
/// from <c>now − ReplicationLatencyBound</c>, so the 1102 of the clear is ingested), pages of
/// <see cref="PageSize"/> records of the ingestion set with the noisy 5136 attributes suppressed, parse →
/// <see cref="EventSourceGuard"/> → event-log-only detections → <see cref="SecurityEventReducer"/> → store with the new
/// cursor in one call per page, until a short page or <c>MaxRecordsPerPoll</c> (then HealthEventLogGap "flooding"; the
/// cursor keeps moving). Dropped records still move the cursor.
/// </summary>
public sealed partial class SecurityLogIngestor(
    ISecurityEventSource source,
    ISecurityEventStore store,
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
        var events = new List<WatchEvent>();
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

        var query = new SecurityEventQuery
        {
            LogName = ingestion.LogName,
            EventCodes = SecurityEventCatalog.IngestionSet,
            SuppressAttributes = SuppressedAttributes,
            MaxRecords = PageSize,
        };
        var rebased = false;
        if (EventLogGapDetector.IsRegressed(cursor, info))
        {
            Add(events, gaps.Gap(host, EventLogGapDetector.Regressed, context.Domain));
            rebased = true;
            query = query with { From = now - context.Settings.ReplicationLatencyBound };
        }
        else if (cursor is null)
        {
            query = query with { From = now - context.Settings.ReplicationLatencyBound };
        }
        else
        {
            if (EventLogGapDetector.IsOverwritten(cursor, info))
            {
                tracker.RecordOverwritten(host);
                Add(events, gaps.Gap(host, EventLogGapDetector.Overwritten, context.Domain));
            }

            query = query with { AfterRecordId = cursor.LastRecordId };
        }

        var result = new IngestionResult();
        long? last = rebased ? null : cursor?.LastRecordId;
        try
        {
            while (true)
            {
                var page = await ReadPageAsync(host, query, context, events, cancellationToken).ConfigureAwait(false);
                result = result with
                {
                    Read = result.Read + page.Read,
                    Stored = result.Stored + page.Batch.Count,
                    Foreign = result.Foreign + page.Foreign,
                    Malformed = result.Malformed + page.Malformed,
                };
                if (page.MaxRecordId is { } max)
                {
                    last = Math.Max(last ?? 0, max);
                }

                if (page.Read > 0 || rebased)
                {
                    // A rebase without new records moves the cursor to the newest record of the new log.
                    var lastRecordId = last ?? EventLogGapDetector.NewestRecordId(info) ?? 0;
                    var newCursor = new EventLogCursor { Source = host, LastRecordId = lastRecordId, OldestRecordSeen = info.OldestRecordNumber, UpdatedAt = now };
                    await store.AppendAsync(page.Batch, newCursor, cancellationToken).ConfigureAwait(false);
                    rebased = false;
                }

                if (page.Read < PageSize)
                {
                    break;
                }

                if (result.Read >= context.Settings.MaxRecordsPerPoll)
                {
                    Add(events, gaps.Gap(host, EventLogGapDetector.Flooding, context.Domain));
                    break;
                }

                query = query with { AfterRecordId = last, From = null };
            }
        }
        catch (SecurityLogAccessException ex)
        {
            return Failed(host, ex, now) with { Events = events };
        }

        tracker.RecordRead(host, last);
        ReportHealth(host, result, now);
        return result with { Events = events };
    }

    private async Task<(int Read, int Foreign, int Malformed, long? MaxRecordId, List<SecurityEvent> Batch)> ReadPageAsync(
        string host, SecurityEventQuery query, IngestionContext context, List<WatchEvent> events, CancellationToken cancellationToken)
    {
        var batch = new List<SecurityEvent>();
        int read = 0, foreign = 0, malformed = 0;
        long? max = null;
        await foreach (var xml in source.ReadAsync(host, query, cancellationToken).ConfigureAwait(false))
        {
            read++;
            if (parser.Parse(xml, host) is not { } record)
            {
                malformed++;
                continue;
            }

            max = Math.Max(max ?? 0, record.Event.RecordId);
            if (!EventSourceGuard.Accept(record, context.IsKnownDomainController))
            {
                foreign++;
                continue;
            }

            tracker.RecordComputerName(HostNames.Normalize(record.Event.Computer), record.Event.Computer);
            if (EventLogGapDetector.ReasonOf(record.Event) is { } reason)
            {
                Add(events, gaps.Gap(HostNames.Normalize(record.Event.Computer), reason, context.Domain, record.Event));
            }

            if (await eventLogOnly.DetectAsync(record.Event, context.Domain, cancellationToken).ConfigureAwait(false) is { } detected
                && !context.DisabledKinds.Contains(detected.Kind))
            {
                events.Add(detected);
            }

            if (SecurityEventReducer.Reduce(record.Event, context.Reduction) is { } reduced)
            {
                batch.Add(reduced);
            }
        }

        return (read, foreign, malformed, max, batch);
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

    private static void Add(List<WatchEvent> events, WatchEvent? watchEvent)
    {
        if (watchEvent is not null)
        {
            events.Add(watchEvent);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log {Host}: {Foreign} foreign and {Malformed} malformed records dropped.")]
    private partial void LogDropped(string host, int foreign, int malformed);
}
