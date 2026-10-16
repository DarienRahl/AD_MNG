using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Normalized Security-log records and per-source ingestion cursors (section 11).</summary>
public interface ISecurityEventStore
{
    Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken);
    /// <summary>Inserts events (duplicates by (Computer, RecordId) ignored) and moves the cursor – one transaction.</summary>
    Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken);  // coverage
}
