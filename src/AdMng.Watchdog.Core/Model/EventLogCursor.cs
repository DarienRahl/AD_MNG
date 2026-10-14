namespace AdMng.Watchdog.Core.Model;

/// <summary>Ingestion position in one Security log (per DC or collector).</summary>
public sealed record EventLogCursor
{
    public required string Source { get; init; }
    public long LastRecordId { get; init; }
    public string? BookmarkXml { get; init; }
    public long? OldestRecordSeen { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
