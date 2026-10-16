namespace AdMng.Watchdog.Core.Model;

/// <summary>Rows removed by one retention run.</summary>
public sealed record PurgeResult(int Events, int SecurityEvents, int OutboxMessages, int Snapshots, int Notices, int AuditEntries);
