namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>One structured query against a Security (or ForwardedEvents) log.</summary>
public sealed record SecurityEventQuery
{
    public required string LogName { get; init; }                 // "Security" | "ForwardedEvents"
    public IReadOnlyList<int> EventCodes { get; init; } = [];
    public long? AfterRecordId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>EventData equality filters, e.g. ("TargetLogonId", "0x30dc2").</summary>
    public IReadOnlyList<(string Name, string Value)> DataEquals { get; init; } = [];
    /// <summary>5136 AttributeLDAPDisplayName values to suppress (noise).</summary>
    public IReadOnlyList<string> SuppressAttributes { get; init; } = [];
    public bool NewestFirst { get; init; }                         // ReverseDirection
    public int MaxRecords { get; init; } = 1000;
}

public sealed record SecurityLogInfo(long? OldestRecordNumber, long? RecordCount, bool IsLogFull, long? FileSizeBytes);

/// <summary>Reads DC Security logs (Windows: EventLogSession/EventLogReader; elsewhere: unavailable).</summary>
public interface ISecurityEventSource
{
    /// <summary>False on non-Windows hosts or when attribution is disabled (NullSecurityEventSource).</summary>
    bool IsAvailable { get; }
    Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken);
    /// <summary>Raw event XML (EventRecord.ToXml()) in query order. Throws SecurityLogAccessException (Win32 error) on RPC/ACL failures.</summary>
    IAsyncEnumerable<string> ReadAsync(string host, SecurityEventQuery query, CancellationToken cancellationToken);
}

/// <summary>A DC log could not be read (RPC unavailable, access denied, …).</summary>
public sealed class SecurityLogAccessException(string host, int? win32Error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Host { get; } = host;
    public int? Win32Error { get; } = win32Error;     // 5 access denied, 1722 RPC unavailable, …
}
