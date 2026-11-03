using AdMng.Core.Directory;

namespace AdMng.Core.EventLogs;

/// <summary>Audit outcome filter of a query.</summary>
public enum EventAuditOutcome
{
    Success,
    Failure,
}

/// <summary>An <c>EventData</c> condition: the value of <see cref="Name"/> is one of <see cref="Values"/> (OR-ed).</summary>
public sealed record EventDataCondition(string Name, IReadOnlyList<string> Values)
{
    /// <summary>Never a value (values are typed by the operator or come from the directory).</summary>
    public override string ToString() => $"{Name} ({Values.Count})";
}

/// <summary>
/// What to read from one log (design note e-event-logs.md 3.1). Conditions are AND-ed; <see cref="EventIds"/>,
/// <see cref="Levels"/>, <see cref="Providers"/> and the values of one <see cref="EventDataCondition"/> are OR-ed.
/// <see cref="RawXPath"/> (expert mode) replaces the generated selection. A file reader ignores <see cref="Channel"/>.
/// </summary>
public sealed record EventLogQuery
{
    public required string Channel { get; init; }

    /// <summary>Inclusive lower bound (UTC), null = from the oldest record.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Inclusive upper bound (UTC), null = up to now.</summary>
    public DateTimeOffset? To { get; init; }

    public IReadOnlyList<int> EventIds { get; init; } = [];

    /// <summary><see cref="EventLogLevels"/> values.</summary>
    public IReadOnlyList<byte> Levels { get; init; } = [];

    public IReadOnlyList<string> Providers { get; init; } = [];

    public EventAuditOutcome? Outcome { get; init; }

    public IReadOnlyList<EventDataCondition> Data { get; init; } = [];

    /// <summary>An XPath selection typed by the operator (checked before it is sent; see EventQueryXml).</summary>
    public string? RawXPath { get; init; }

    /// <summary>Newest first (live logs: read in reverse and stop at <see cref="From"/>).</summary>
    public bool NewestFirst { get; init; } = true;

    /// <summary>Stop after this many records, null = no limit (the caller still bounds what it keeps).</summary>
    public int? MaxRecords { get; init; }

    /// <summary>Never a value of a condition.</summary>
    public override string ToString() => $"{Channel}: {EventIds.Count} IDs, {Data.Count} data conditions";
}

/// <summary>How a log keeps its records when full.</summary>
public enum EventLogMode
{
    Unknown,
    Circular,
    AutoBackup,
    Retain,
}

/// <summary>How far one log reaches (design note 3.2).</summary>
public sealed record EventLogCoverage
{
    public required string Host { get; init; }

    public required string Channel { get; init; }

    public DateTimeOffset? Oldest { get; init; }

    public DateTimeOffset? Newest { get; init; }

    public long? RecordCount { get; init; }

    public long? FileSizeBytes { get; init; }

    public long? MaximumSizeBytes { get; init; }

    public EventLogMode Mode { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsFull { get; init; }

    /// <summary>The log holds records from <paramref name="from"/> on (false when it starts later or is unknown).</summary>
    public bool Covers(DateTimeOffset from) => Oldest is { } oldest && oldest <= from;

    public override string ToString() => $"{Channel} coverage";
}

/// <summary>Why a log could not be read.</summary>
public enum EventLogAccessError
{
    /// <summary>Not available here (not Windows, or a window without a Windows logon – simple bind).</summary>
    NotSupported,

    /// <summary>The host does not answer (name not resolved, no route).</summary>
    Unreachable,

    /// <summary>The RPC endpoint is not reachable (firewall group "Remote Event Log Management").</summary>
    RpcUnavailable,

    /// <summary>The account may not read the log (group "Event Log Readers", channel access).</summary>
    AccessDenied,

    ChannelNotFound,

    /// <summary>The target rejected the query (raw XPath, too complex).</summary>
    InvalidQuery,

    FileUnreadable,

    Failed,
}

/// <summary>A read failed; the message carries no value read from a log, a host name or an account.</summary>
public sealed class EventLogAccessException : Exception
{
    public EventLogAccessException(EventLogAccessError error, Exception? innerException = null)
        : base("Event log access failed: " + error, innerException)
    {
        Error = error;
    }

    public EventLogAccessException()
        : this(EventLogAccessError.Failed)
    {
    }

    public EventLogAccessException(string message)
        : base(message)
    {
        Error = EventLogAccessError.Failed;
    }

    public EventLogAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = EventLogAccessError.Failed;
    }

    public EventLogAccessError Error { get; }

    /// <summary>The Windows error code when known (for the classification tests and the details line).</summary>
    public int? NativeErrorCode { get; init; }
}

/// <summary>Opens event logs of computers (MS-EVEN6, Kerberos, the window's credential) or of operator-chosen files.</summary>
public interface IEventLogConnector
{
    /// <summary>Remote and file reads are possible here (Windows).</summary>
    bool IsSupported { get; }

    /// <summary>Opens <paramref name="host"/> (FQDN) with the logon of <paramref name="session"/> (rule 2).</summary>
    /// <exception cref="EventLogAccessException">The host cannot be opened.</exception>
    Task<IEventLogReader> OpenComputerAsync(string host, IDirectorySession session, CancellationToken cancellationToken);

    /// <summary>A reader over <c>.evtx</c> files the operator chose (read forward; <c>EventLogQuery.Channel</c> ignored).</summary>
    IEventLogReader OpenFiles(IReadOnlyList<string> paths);
}

/// <summary>One open source: a computer or a set of files.</summary>
public interface IEventLogReader : IAsyncDisposable
{
    /// <summary>The computer FQDN or the files' display name.</summary>
    string Host { get; }

    Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken cancellationToken);

    Task<EventLogCoverage> GetCoverageAsync(string channel, CancellationToken cancellationToken);

    /// <summary>Streams the records of <paramref name="query"/> (batches read off the calling thread).</summary>
    /// <exception cref="EventLogAccessException">The log cannot be read or the query was rejected.</exception>
    IAsyncEnumerable<EventLogRecord> ReadAsync(EventLogQuery query, CancellationToken cancellationToken);

    /// <summary>The rendered message of one record (publisher metadata of the source), null when it cannot be rendered.</summary>
    Task<string?> FormatMessageAsync(EventLogRecord record, CancellationToken cancellationToken);
}

/// <summary>Limits of the event log features (design note 2).</summary>
public static class EventLogLimits
{
    /// <summary>Records held in memory by one view; aggregates keep counting past it.</summary>
    public const int MaxRecordsInMemory = 50_000;

    /// <summary>Hosts read at the same time.</summary>
    public const int MaxParallelHosts = 4;

    /// <summary>Records per batch of a remote read.</summary>
    public const int BatchSize = 512;

    /// <summary>Default time window of a query.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
}
