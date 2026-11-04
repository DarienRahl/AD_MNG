using AdMng.Core.EventLogs;

namespace AdMng.Core.Tests.EventLogs.Analysis;

/// <summary>
/// Security records as the reader parses them (EventData by name, <c>-</c> and empty values omitted): 4740, 4767, 4771,
/// 4776, 4625, 4768, 4624 of the domain FIRMA (firma.test) with Polish accounts and computers. Record IDs grow per builder.
/// </summary>
internal sealed class SecurityRecords
{
    public const string Pdc = "dc01.firma.test";
    public const string Dc2 = "dc02.firma.test";
    public const string Dc3 = "dc03.firma.test";
    public const string KowalskiSid = "S-1-5-21-1004336348-1177238915-682003330-1105";
    public const string NowakSid = "S-1-5-21-1004336348-1177238915-682003330-1106";

    public static readonly DateTimeOffset T0 = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    private long _recordId = 41_000;

    public EventLogRecord Record(string host, int eventId, DateTimeOffset time, bool failure, params (string Name, string? Value)[] data) => new()
    {
        Host = host,
        Computer = host,
        Channel = "Security",
        Provider = "Microsoft-Windows-Security-Auditing",
        EventId = eventId,
        RecordId = ++_recordId,
        TimeCreated = time,
        Keywords = failure ? EventLogRecord.AuditFailureKeyword : EventLogRecord.AuditSuccessKeyword,
        Data = [.. data.Where(d => !string.IsNullOrEmpty(d.Value) && d.Value != "-").Select(d => KeyValuePair.Create(d.Name, d.Value!))],
    };

    /// <summary>4740: the account was locked out; <c>TargetDomainName</c> is the caller computer.</summary>
    public EventLogRecord Lockout(string host, DateTimeOffset time, string account, string? sid, string? caller) =>
        Record(host, 4740, time, false,
            ("TargetUserName", account), ("TargetDomainName", caller), ("TargetSid", sid),
            ("SubjectUserSid", "S-1-5-18"), ("SubjectUserName", AnalysisHostName(host) + "$"), ("SubjectDomainName", "FIRMA"), ("SubjectLogonId", "0x3e7"));

    /// <summary>4767: the account was unlocked by <paramref name="by"/>.</summary>
    public EventLogRecord Unlock(string host, DateTimeOffset time, string account, string? sid, string by) =>
        Record(host, 4767, time, false,
            ("TargetUserName", account), ("TargetDomainName", "FIRMA"), ("TargetSid", sid),
            ("SubjectUserSid", "S-1-5-21-1004336348-1177238915-682003330-1201"), ("SubjectUserName", by), ("SubjectDomainName", "FIRMA"),
            ("SubjectLogonId", "0x5c2f1"));

    /// <summary>4771: Kerberos pre-authentication failed.</summary>
    public EventLogRecord Kerberos(string host, DateTimeOffset time, string account, string? sid, string? ip, string status = "0x18") =>
        Record(host, 4771, time, true,
            ("TargetUserName", account), ("TargetSid", sid), ("ServiceName", "krbtgt/FIRMA"), ("TicketOptions", "0x40810010"),
            ("Status", status), ("PreAuthType", "2"), ("IpAddress", ip), ("IpPort", "51234"));

    /// <summary>4776: NTLM credential validation (a failure unless the status is 0x0).</summary>
    public EventLogRecord Ntlm(string host, DateTimeOffset time, string account, string? workstation, string status = "0xc000006a") =>
        Record(host, 4776, time, status != "0x0",
            ("PackageName", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0"), ("TargetUserName", account), ("Workstation", workstation), ("Status", status));

    /// <summary>4625: an account failed to log on (default: bad password behind the generic status).</summary>
    public EventLogRecord LogonFailure(string host, DateTimeOffset time, string account, string? ip, string? workstation,
        string status = "0xc000006d", string? subStatus = "0xc000006a", int logonType = 3) =>
        Record(host, 4625, time, true,
            ("SubjectUserSid", "S-1-0-0"), ("SubjectLogonId", "0x0"), ("TargetUserSid", "S-1-0-0"), ("TargetUserName", account),
            ("TargetDomainName", "FIRMA"), ("Status", status), ("FailureReason", "Unknown user name or bad password."), ("SubStatus", subStatus),
            ("LogonType", logonType.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("LogonProcessName", "NtLmSsp "),
            ("AuthenticationPackageName", "NTLM"), ("WorkstationName", workstation), ("TransmittedServices", "-"), ("LmPackageName", "-"),
            ("KeyLength", "0"), ("ProcessId", "0x0"), ("ProcessName", "-"), ("IpAddress", ip), ("IpPort", "49832"));

    /// <summary>4768: a TGT was requested (a failure unless the status is 0x0).</summary>
    public EventLogRecord Tgt(string host, DateTimeOffset time, string account, string? ip, string status = "0x0", string? sid = null) =>
        Record(host, 4768, time, status != "0x0",
            ("TargetUserName", account), ("TargetDomainName", "FIRMA.TEST"), ("TargetSid", sid ?? (status == "0x0" ? KowalskiSid : "S-1-0-0")),
            ("ServiceName", "krbtgt"), ("ServiceSid", "S-1-5-21-1004336348-1177238915-682003330-502"), ("TicketOptions", "0x40810010"),
            ("Status", status), ("TicketEncryptionType", status == "0x0" ? "0x12" : "0xffffffff"), ("PreAuthType", status == "0x0" ? "2" : "-"),
            ("IpAddress", ip), ("IpPort", "50122"));

    /// <summary>4624: an account was successfully logged on.</summary>
    public EventLogRecord LogonSuccess(string host, DateTimeOffset time, string account, string? ip, string? workstation, int logonType = 3) =>
        Record(host, 4624, time, false,
            ("SubjectUserSid", "S-1-0-0"), ("TargetUserSid", KowalskiSid), ("TargetUserName", account), ("TargetDomainName", "FIRMA"),
            ("TargetLogonId", "0x8a1f2c"), ("LogonType", logonType.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("LogonProcessName", "Kerberos"), ("AuthenticationPackageName", "Kerberos"), ("WorkstationName", workstation),
            ("IpAddress", ip), ("IpPort", "50311"));

    /// <summary>A covered Security log (from an hour before <paramref name="from"/>).</summary>
    public static EventLogCoverage Covered(string host, DateTimeOffset from) => new()
    {
        Host = host,
        Channel = "Security",
        Oldest = from - TimeSpan.FromHours(1),
        Newest = from + TimeSpan.FromDays(1),
        RecordCount = 1_250_000,
        FileSizeBytes = 1L << 30,
        MaximumSizeBytes = 1L << 30,
        Mode = EventLogMode.Circular,
        IsFull = true,
    };

    private static string AnalysisHostName(string host) => host.Split('.')[0].ToUpperInvariant();
}
