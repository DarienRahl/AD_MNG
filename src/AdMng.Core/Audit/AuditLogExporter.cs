using System.Globalization;
using AdMng.Core.Csv;

namespace AdMng.Core.Audit;

/// <summary>
/// Writes audit entries to CSV (explicit operator export of the session log).
/// Columns: Timestamp, Operator, Server, DN, Action, Attribute, Old value, New value, Result, Error.
/// </summary>
/// <remarks>
/// Timestamps are written in the local time zone with the offset (<c>yyyy-MM-dd HH:mm:ss zzz</c>), so they are both
/// readable and unambiguous. Result is <see cref="ResultSucceeded"/> or <see cref="ResultFailed"/>.
/// </remarks>
public sealed class AuditLogExporter
{
    public const string ResultSucceeded = "Succeeded";
    public const string ResultFailed = "Failed";
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss zzz";

    private static readonly string[] Columns =
        ["Timestamp", "Operator", "Server", "DN", "Action", "Attribute", "Old value", "New value", "Result", "Error"];

    private readonly ICsvCodec _csv;

    public AuditLogExporter(ICsvCodec csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        _csv = csv;
    }

    /// <summary>Writes all entries of the log.</summary>
    public void Write(TextWriter writer, IAuditLog log, char delimiter = ';')
    {
        ArgumentNullException.ThrowIfNull(log);
        Write(writer, log.Entries, delimiter);
    }

    /// <summary>Writes the given entries (e.g. a filtered view).</summary>
    public void Write(TextWriter writer, IEnumerable<AuditEntry> entries, char delimiter = ';')
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entries);
        _csv.Write(writer, Columns, entries.Select(ToRow), delimiter, protectFormulas: true);
    }

    /// <summary>Formats a timestamp the way the export writes it.</summary>
    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static string[] ToRow(AuditEntry entry) =>
    [
        FormatTimestamp(entry.Timestamp),
        entry.Operator,
        entry.Server,
        entry.DistinguishedName,
        entry.Action,
        entry.Attribute ?? string.Empty,
        entry.OldValue ?? string.Empty,
        entry.NewValue ?? string.Empty,
        entry.Succeeded ? ResultSucceeded : ResultFailed,
        entry.Error ?? string.Empty,
    ];
}
