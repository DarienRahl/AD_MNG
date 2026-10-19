using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Event-log gaps (sections 7, 11.1, 11.6): records overwritten before they were read
/// (<c>OldestRecordNumber &gt; cursor + 1</c>), a cursor regression (the log was cleared or recreated – newest record id
/// below the cursor), the ingestion budget exceeded ("flooding"), and the event-log service records 1102 (log cleared),
/// 1104 (log full), 1100 (service shut down). Each becomes one HealthEventLogGap event per DC and reason, repeated at
/// most every <see cref="RepeatAfter"/> while the condition persists. The gap of a record has the record's stable id
/// (<see cref="RecordEventIds"/>).
/// </summary>
public sealed class EventLogGapDetector(TimeProvider timeProvider)
{
    public const string Overwritten = "overwritten";
    public const string Regressed = "cursorRegression";
    public const string Flooding = "flooding";
    public const string LogCleared = "logCleared";
    public const string LogFull = "logFull";
    public const string ServiceShutdown = "serviceShutdown";

    /// <summary>Minimum distance between two gap events of one DC and reason.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Host, string Reason), DateTimeOffset> _raised = [];

    /// <summary>True when records between the cursor and the oldest record were overwritten unread.</summary>
    public static bool IsOverwritten(EventLogCursor? cursor, SecurityLogInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return cursor is not null && info.OldestRecordNumber is { } oldest && oldest > cursor.LastRecordId + 1;
    }

    /// <summary>True when the newest record id is below the cursor (log cleared or recreated).</summary>
    public static bool IsRegressed(EventLogCursor? cursor, SecurityLogInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (cursor is null || cursor.LastRecordId <= 0)
        {
            return false;
        }

        return (NewestRecordId(info) ?? 0) < cursor.LastRecordId;
    }

    /// <summary>Newest record id of a log (oldest + count − 1); null for an empty log.</summary>
    public static long? NewestRecordId(SecurityLogInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.OldestRecordNumber is { } oldest && info.RecordCount is { } count and > 0 ? oldest + count - 1 : null;
    }

    /// <summary>The gap reason of an event-log service record (1102/1104/1100); null for other records.</summary>
    public static string? ReasonOf(SecurityEvent record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.EventCode switch
        {
            SecurityEventCatalog.SecurityLogCleared => LogCleared,
            SecurityEventCatalog.SecurityLogFull => LogFull,
            SecurityEventCatalog.EventLogServiceShutdown => ServiceShutdown,
            _ => null,
        };
    }

    /// <summary>The HealthEventLogGap event for <paramref name="host"/> and <paramref name="reason"/>; null when one was
    /// raised less than <see cref="RepeatAfter"/> ago.</summary>
    public WatchEvent? Gap(string host, string reason, string? domain, SecurityEvent? record = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var now = timeProvider.GetUtcNow();
        var key = (HostNames.Normalize(host), reason);
        lock (_gate)
        {
            if (_raised.TryGetValue(key, out var last) && now - last < RepeatAfter)
            {
                return null;
            }

            _raised[key] = now;
        }

        var id = record is null ? Guid.CreateVersion7(now) : RecordEventIds.Create(record, reason);
        return new WatchEvent
        {
            EventId = id,
            ChangeSetId = id,
            Kind = WatchEventKind.HealthEventLogGap,
            Severity = EventKindCatalog.DefaultSeverity(WatchEventKind.HealthEventLogGap, WatchObjectKind.Watchdog),
            Object = new ObjectRef { Dn = string.Empty, Name = key.Item1, Kind = WatchObjectKind.Watchdog },
            Changes = [new AttributeChange { Attribute = "eventLogGap", ValueKind = AttributeValueKind.Text, NewValues = [reason], NewCount = 1 }],
            Domain = domain,
            DetectedAt = now,
            OriginatingTime = record?.TimeCreated,
            OriginatingDc = key.Item1,
            Source = DetectionSource.Health,
            Attribution = EventAttribution.NotApplicable,
        };
    }
}
