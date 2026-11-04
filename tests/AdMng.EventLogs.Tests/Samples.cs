using AdMng.Core.EventLogs;

namespace AdMng.EventLogs.Tests;

/// <summary>Records with Polish accounts and computers (only what the tests compare is set).</summary>
internal static class Samples
{
    public static readonly DateTimeOffset Morning = new(2026, 11, 3, 7, 30, 0, TimeSpan.Zero);

    public static EventLogRecord Record(string host, long recordId, DateTimeOffset time, int eventId = 4740, string account = "żaneta.wiśniewska") => new()
    {
        Host = host,
        Computer = "DC01.contoso.test",
        Channel = "Security",
        Provider = "Microsoft-Windows-Security-Auditing",
        EventId = eventId,
        RecordId = recordId,
        TimeCreated = time,
        Keywords = EventLogRecord.AuditSuccessKeyword,
        Data =
        [
            new("TargetUserName", account),
            new("TargetDomainName", "KRK-PC101"),
        ],
    };

    /// <summary>A file's records, oldest first, <paramref name="minutes"/> after <see cref="Morning"/>.</summary>
    public static List<EventLogRecord> File(string host, params int[] minutes) =>
        [.. minutes.Select((minute, index) => Record(host, 1000 + index, Morning.AddMinutes(minute)))];
}
