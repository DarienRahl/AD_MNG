using System.Globalization;

namespace AdMng.Core.EventLogs;

/// <summary>Standard event levels (<c>System/Level</c>).</summary>
public static class EventLogLevels
{
    public const byte LogAlways = 0;
    public const byte Critical = 1;
    public const byte Error = 2;
    public const byte Warning = 3;
    public const byte Information = 4;
    public const byte Verbose = 5;
}

/// <summary>
/// One event read from a Windows event log (design note e-event-logs.md 3.1): the <c>System</c> fields and the
/// <c>EventData</c> / <c>UserData</c> values by name. Held only in memory while shown (CLAUDE.md rule 1);
/// <see cref="ToString"/> carries no value read from the log.
/// </summary>
public sealed record EventLogRecord
{
    /// <summary>Audit success keyword (<c>0x0020000000000000</c>).</summary>
    public const long AuditSuccessKeyword = 0x0020000000000000;

    /// <summary>Audit failure keyword (<c>0x0010000000000000</c>).</summary>
    public const long AuditFailureKeyword = 0x0010000000000000;

    /// <summary>Where the record was read: the DC or computer FQDN, or the file name.</summary>
    public required string Host { get; init; }

    /// <summary><c>System/Computer</c> – the computer that logged the event.</summary>
    public required string Computer { get; init; }

    /// <summary><c>System/Channel</c> (<c>Security</c>, <c>Microsoft-Windows-GroupPolicy/Operational</c>, …).</summary>
    public required string Channel { get; init; }

    /// <summary>
    /// The channel of <see cref="Host"/> the record was read from when it is not <see cref="Channel"/> – a forwarded event
    /// keeps its source's channel in a collector's <c>ForwardedEvents</c> or another subscription log; null otherwise and
    /// for files. The record is read from there again for its message.
    /// </summary>
    public string? QueriedChannel { get; init; }

    /// <summary><c>System/Provider/@Name</c>.</summary>
    public string? Provider { get; init; }

    public required int EventId { get; init; }

    public int Version { get; init; }

    /// <summary><c>System/Level</c> (<see cref="EventLogLevels"/>), null when absent.</summary>
    public byte? Level { get; init; }

    public int? Task { get; init; }

    public int? Opcode { get; init; }

    /// <summary><c>System/Keywords</c> as a number.</summary>
    public long Keywords { get; init; }

    public required long RecordId { get; init; }

    /// <summary><c>System/TimeCreated/@SystemTime</c> (UTC).</summary>
    public required DateTimeOffset TimeCreated { get; init; }

    public Guid? ActivityId { get; init; }

    /// <summary><c>System/Security/@UserID</c>.</summary>
    public string? UserSid { get; init; }

    /// <summary>
    /// <c>EventData/Data</c> (or the children of <c>UserData/*</c>) by name in document order; unnamed values as
    /// <c>Data1</c>, <c>Data2</c>, …; <c>-</c> and empty values omitted; documented <c>%%NNNN</c> codes decoded.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Data { get; init; } = [];

    /// <summary>The value of <paramref name="name"/> (case-insensitive name match), or null.</summary>
    public string? Get(string name)
    {
        foreach (var (key, value) in Data)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The audit success keyword is set.</summary>
    public bool IsAuditSuccess => (Keywords & AuditSuccessKeyword) != 0;

    /// <summary>The audit failure keyword is set.</summary>
    public bool IsAuditFailure => (Keywords & AuditFailureKeyword) != 0;

    /// <summary>Never a value read from the log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Channel} {EventId} #{RecordId}");
}
