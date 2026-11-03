using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs;

/// <summary>
/// The pure parts of the Windows readers (tested on every platform): the query text, the query of one file, the record
/// re-read for its message, channel names, file names, log modes and times.
/// </summary>
internal static class ReaderSupport
{
    /// <summary>
    /// The channel a file query is built for before its paths are replaced by the file (<see cref="ForFile"/>): a file reader
    /// ignores <see cref="EventLogQuery.Channel"/>, so whatever the caller passed there never reaches the query.
    /// </summary>
    public const string FileQueryChannel = "Application";

    private const string QueryListElement = "QueryList";

    private static readonly XmlReaderSettings QuerySettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
    };

    /// <summary>
    /// The structured query of <paramref name="query"/> (<see cref="EventQueryXml.Build"/>); a query the builder refuses
    /// (a name or value it cannot express, raw XPath that fails the structural check) is an
    /// <see cref="EventLogAccessError.InvalidQuery"/>.
    /// </summary>
    public static string BuildQuery(EventLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            return EventQueryXml.Build(query);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or XmlException)
        {
            throw new EventLogAccessException(EventLogAccessError.InvalidQuery);
        }
    }

    /// <summary>
    /// The query of one file: every <c>Path</c> of the structured query (<c>Query</c>, <c>Select</c>, <c>Suppress</c>) is set
    /// to <c>file://</c> + the file, as Event Viewer writes a filter of a saved log – a structured query that names a
    /// channel would read that channel of this computer, not the file. A bare XPath expression is returned as it is (the
    /// file path of the query applies to it); any other XML – not a <c>QueryList</c>, malformed, with a DTD – is refused,
    /// so no query of a file can reach a channel.
    /// </summary>
    /// <exception cref="EventLogAccessException"><see cref="EventLogAccessError.InvalidQuery"/>: XML that is not a structured query.</exception>
    public static string ForFile(string query, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!query.TrimStart().StartsWith('<'))
        {
            return query;
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(query), QuerySettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            throw new EventLogAccessException(EventLogAccessError.InvalidQuery);
        }

        if (document.Root is not { } root || root.Name.LocalName != QueryListElement)
        {
            throw new EventLogAccessException(EventLogAccessError.InvalidQuery);
        }

        var path = "file://" + fullPath;
        foreach (var element in root.Descendants())
        {
            if (element.Name.LocalName is "Query" or "Select" or "Suppress")
            {
                element.SetAttributeValue("Path", path);
            }
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The XPath of one record by its number (numbers only, nothing typed).</summary>
    public static string RecordQuery(long recordId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordId);
        return string.Create(CultureInfo.InvariantCulture, $"*[System[(EventRecordID={recordId})]]");
    }

    /// <summary>
    /// The channels worth offering: distinct, sorted, without the analytic and debug channels (<c>…/Analytic</c>,
    /// <c>…/Debug</c>) – they can be read only while disabled, only forward, and are off on almost every computer.
    /// Checking each channel's configuration would cost one round trip per channel (hundreds), so that is left to
    /// the coverage of the chosen channel.
    /// </summary>
    public static IReadOnlyList<string> Channels(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return
        [
            .. names
                .Where(name => !string.IsNullOrWhiteSpace(name)
                    && !name.EndsWith("/Analytic", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith("/Debug", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The host of a file's records: its file name (after the last <c>\</c> or <c>/</c>, on every platform).</summary>
    public static string FileHost(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.TrimEnd('\\', '/');
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>The display name of a set of files: the file names, comma-separated.</summary>
    public static string FilesHost(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return string.Join(", ", paths.Select(FileHost));
    }

    /// <summary>
    /// <c>EventLogConfiguration.LogMode</c> (<c>System.Diagnostics.Eventing.Reader.EventLogMode</c>: 0 circular, 1 auto
    /// backup, 2 retain) → <see cref="EventLogMode"/>.
    /// </summary>
    public static EventLogMode Mode(int? nativeMode) => nativeMode switch
    {
        0 => EventLogMode.Circular,
        1 => EventLogMode.AutoBackup,
        2 => EventLogMode.Retain,
        _ => EventLogMode.Unknown,
    };

    /// <summary>A time of the event log API (local or UTC <see cref="DateTime"/>) as UTC.</summary>
    public static DateTimeOffset? ToUtc(DateTime? time) => time switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } unspecified => new DateTimeOffset(DateTime.SpecifyKind(unspecified, DateTimeKind.Local).ToUniversalTime(), TimeSpan.Zero),
        { } known => new DateTimeOffset(known.ToUniversalTime(), TimeSpan.Zero),
    };

    /// <summary>
    /// The re-read record is the one shown: same event ID, provider, computer and time (within a second). Guards the
    /// message against a record number that means another event in the channel read again – a forwarded event keeps the
    /// number and the channel of its source.
    /// </summary>
    public static bool SameEvent(int eventId, string? provider, string? computer, DateTimeOffset? time, EventLogRecord expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return eventId == expected.EventId
            && (provider is null || expected.Provider is null || string.Equals(provider, expected.Provider, StringComparison.OrdinalIgnoreCase))
            && (computer is null || string.Equals(computer, expected.Computer, StringComparison.OrdinalIgnoreCase))
            && time is { } found && (found - expected.TimeCreated).Duration() <= TimeSpan.FromSeconds(1);
    }

    /// <summary>The newest of two optional times.</summary>
    public static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>The oldest of two optional times.</summary>
    public static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>The sum of optional sizes or counts (null only when every value is null).</summary>
    public static long? Sum(IEnumerable<long?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        long? total = null;
        foreach (var value in values)
        {
            if (value is { } known)
            {
                total = (total ?? 0) + known;
            }
        }

        return total;
    }
}
