using System.Globalization;
using System.Text;
using System.Xml;
using AdMng.Core.EventLogs;

namespace AdMng.Testing.EventLogs;

/// <summary>
/// Polish sample records for the event log features (design note e-event-logs.md 6): the DCs, accounts, computers and
/// addresses of the sample company and builders for the Security records the lockout and failed-logon analyses read –
/// 4740, 4771, 4776, 4625, 4624, 4768 – with the EventData names and value formats Windows writes. Each record is written
/// as event XML in the <c>EventRecord.ToXml()</c> shape (<see cref="SecurityXml"/>) and parsed by
/// <see cref="EventXmlParser"/>, so the samples hold exactly what a real read would.
/// </summary>
public static class EventLogSamples
{
    public const string Dc01 = "DC01.contoso.test";
    public const string Dc02 = "DC02.contoso.test";

    public const string SecurityChannel = "Security";
    public const string SystemChannel = "System";
    public const string GroupPolicyChannel = "Microsoft-Windows-GroupPolicy/Operational";
    public const string SecurityAuditing = "Microsoft-Windows-Security-Auditing";

    public const string NetBiosDomain = "CONTOSO";
    public const string DnsDomain = "CONTOSO.TEST";

    public const string JanKowalski = "jan.kowalski";
    public const string ZofiaZak = "zofia.zak";
    public const string LukaszWisniewski = "lukasz.wisniewski";
    public const string MalgorzataWojcik = "malgorzata.wojcik";
    public const string GrazynaDabrowska = "grazyna.dabrowska";
    public const string PawelLewandowski = "pawel.lewandowski";
    public const string ZanetaSzymanska = "zaneta.szymanska";
    public const string BartlomiejKozlowski = "bartlomiej.kozlowski";

    public const string WawPc001 = "WAW-PC001";
    public const string WawPc001Ip = "10.10.1.21";
    public const string WawPc002 = "WAW-PC002";
    public const string WawPc002Ip = "10.10.1.22";
    public const string KrkLt001 = "KRK-LT001";
    public const string KrkLt001Ip = "10.20.5.40";
    public const string SrvPliki01 = "SRV-PLIKI01";
    public const string SrvPliki01Ip = "10.10.0.15";

    /// <summary>A VPN concentrator address many accounts fail from (password spraying).</summary>
    public const string VpnIp = "10.99.0.66";

    /// <summary>The audit success keywords as Windows writes them (classic | audit success).</summary>
    public const string AuditSuccessKeywords = "0x8020000000000000";

    /// <summary>The audit failure keywords (classic | audit failure).</summary>
    public const string AuditFailureKeywords = "0x8010000000000000";

    private const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const string SecurityAuditingGuid = "{54849625-5478-4994-A5BA-3E3B0328C30D}";

    /// <summary>The time the samples start (Tuesday morning, UTC).</summary>
    public static DateTimeOffset Morning { get; } = new(2026, 11, 3, 7, 0, 0, TimeSpan.Zero);

    /// <summary>The domain SID of the fake directory.</summary>
    public static string DomainSid => FakeDirectorySession.DefaultDomainSid;

    /// <summary>The 10 sample accounts (with <see cref="Sid"/> RIDs 1104…).</summary>
    public static IReadOnlyList<string> Accounts { get; } =
    [
        JanKowalski, ZofiaZak, LukaszWisniewski, MalgorzataWojcik, GrazynaDabrowska, PawelLewandowski, ZanetaSzymanska,
        BartlomiejKozlowski, "zbigniew.kazmierczak", "jozef.zak",
    ];

    /// <summary>The SID of a sample account (unknown names get RID 9999).</summary>
    public static string Sid(string account)
    {
        var index = Accounts.ToList().FindIndex(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase));
        return string.Create(CultureInfo.InvariantCulture, $"{DomainSid}-{(index < 0 ? 9999 : 1104 + index)}");
    }

    /// <summary>4740 – <paramref name="account"/> locked out on <paramref name="dc"/>; the caller computer is logged as
    /// <c>TargetDomainName</c> (empty when the client did not say).</summary>
    public static EventLogRecord AccountLockedOut(string dc, long recordId, DateTimeOffset time, string account, string? callerComputer) =>
        Security(dc, 4740, recordId, time, success: true, task: 13824,
        [
            ("TargetUserName", account), ("TargetDomainName", callerComputer ?? string.Empty), ("TargetSid", Sid(account)),
            ("SubjectUserSid", "S-1-5-18"), ("SubjectUserName", DcAccount(dc)), ("SubjectDomainName", NetBiosDomain), ("SubjectLogonId", "0x3e7"),
        ]);

    /// <summary>4771 – Kerberos pre-authentication failed (<c>Status 0x18</c> = bad password); the address carries the
    /// IPv4-mapped prefix like on a real DC.</summary>
    public static EventLogRecord KerberosPreAuthFailed(string dc, long recordId, DateTimeOffset time, string account, string ipAddress,
        string status = "0x18", string preAuthType = "2") =>
        Security(dc, 4771, recordId, time, success: false, task: 14339,
        [
            ("TargetUserName", account), ("TargetSid", Sid(account)), ("ServiceName", "krbtgt/" + DnsDomain), ("TicketOptions", "0x40810010"),
            ("Status", status), ("PreAuthType", preAuthType), ("IpAddress", "::ffff:" + ipAddress), ("IpPort", "51340"),
            ("CertIssuerName", string.Empty), ("CertSerialNumber", string.Empty), ("CertThumbprint", string.Empty),
        ]);

    /// <summary>4776 – NTLM credential validation on a DC (<c>Status 0xc000006a</c> = bad password, <c>0x0</c> = success).</summary>
    public static EventLogRecord CredentialValidation(string dc, long recordId, DateTimeOffset time, string account, string? workstation,
        string status = "0xc000006a") =>
        Security(dc, 4776, recordId, time, success: status is "0x0", task: 14336,
        [
            ("PackageName", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0"), ("TargetUserName", account), ("Workstation", workstation ?? string.Empty),
            ("Status", status),
        ]);

    /// <summary>4625 – a failed logon (default: network logon, NTLM, <c>0xc000006d</c>/<c>0xc000006a</c> bad password).</summary>
    public static EventLogRecord LogonFailed(string computer, long recordId, DateTimeOffset time, string account, string? ipAddress,
        string? workstation, string status = "0xc000006d", string subStatus = "0xc000006a", int logonType = 3, string failureReason = "%%2313") =>
        Security(computer, 4625, recordId, time, success: false, task: 12544,
        [
            ("SubjectUserSid", "S-1-0-0"), ("SubjectUserName", "-"), ("SubjectDomainName", "-"), ("SubjectLogonId", "0x0"),
            ("TargetUserSid", "S-1-0-0"), ("TargetUserName", account), ("TargetDomainName", NetBiosDomain), ("Status", status),
            ("FailureReason", failureReason), ("SubStatus", subStatus), ("LogonType", logonType.ToString(CultureInfo.InvariantCulture)),
            ("LogonProcessName", "NtLmSsp "), ("AuthenticationPackageName", "NTLM"), ("WorkstationName", workstation ?? "-"),
            ("TransmittedServices", "-"), ("LmPackageName", "-"), ("KeyLength", "0"), ("ProcessId", "0x0"), ("ProcessName", "-"),
            ("IpAddress", ipAddress ?? "-"), ("IpPort", ipAddress is null ? "-" : "0"),
        ]);

    /// <summary>4624 (version 2) – a successful logon (default: network logon with Kerberos).</summary>
    public static EventLogRecord LogonSucceeded(string computer, long recordId, DateTimeOffset time, string account, string? ipAddress,
        string? workstation, int logonType = 3, string authenticationPackage = "Kerberos") =>
        Security(computer, 4624, recordId, time, success: true, task: 12544, version: 2, data:
        [
            ("SubjectUserSid", "S-1-0-0"), ("SubjectUserName", "-"), ("SubjectDomainName", "-"), ("SubjectLogonId", "0x0"),
            ("TargetUserSid", Sid(account)), ("TargetUserName", account), ("TargetDomainName", NetBiosDomain),
            ("TargetLogonId", string.Create(CultureInfo.InvariantCulture, $"0x{0x30000 + recordId:x}")),
            ("LogonType", logonType.ToString(CultureInfo.InvariantCulture)), ("LogonProcessName", authenticationPackage),
            ("AuthenticationPackageName", authenticationPackage), ("WorkstationName", workstation ?? "-"),
            ("LogonGuid", "{00000000-0000-0000-0000-000000000000}"), ("TransmittedServices", "-"),
            ("LmPackageName", authenticationPackage == "NTLM" ? "NTLM V2" : "-"), ("KeyLength", authenticationPackage == "NTLM" ? "128" : "0"),
            ("ProcessId", "0x0"), ("ProcessName", "-"), ("IpAddress", ipAddress ?? "-"), ("IpPort", ipAddress is null ? "-" : "49827"),
            ("ImpersonationLevel", "%%1833"), ("RestrictedAdminMode", "-"), ("TargetOutboundUserName", "-"), ("TargetOutboundDomainName", "-"),
            ("VirtualAccount", "%%1843"), ("TargetLinkedLogonId", "0x0"), ("ElevatedToken", "%%1842"),
        ]);

    /// <summary>4768 – a Kerberos TGT request (<c>Status 0x0</c> success; <c>0x6</c> unknown account, <c>0x18</c>…
    /// failures with encryption type <c>0xffffffff</c>).</summary>
    public static EventLogRecord TicketGranted(string dc, long recordId, DateTimeOffset time, string account, string ipAddress,
        string status = "0x0", string encryptionType = "0x12", string preAuthType = "2") =>
        Security(dc, 4768, recordId, time, success: status is "0x0", task: 14339,
        [
            ("TargetUserName", account), ("TargetDomainName", DnsDomain), ("TargetSid", status is "0x6" ? "S-1-0-0" : Sid(account)),
            ("ServiceName", "krbtgt"), ("ServiceSid", DomainSid + "-502"), ("TicketOptions", "0x40810010"), ("Status", status),
            ("TicketEncryptionType", status is "0x0" ? encryptionType : "0xffffffff"), ("PreAuthType", status is "0x0" ? preAuthType : "-"),
            ("IpAddress", "::ffff:" + ipAddress), ("IpPort", "50122"), ("CertIssuerName", string.Empty), ("CertSerialNumber", string.Empty),
            ("CertThumbprint", string.Empty),
        ]);

    /// <summary>
    /// Lockout of <see cref="JanKowalski"/> by an old password on <see cref="WawPc001"/> (a mapped drive): 4771 0x18 from
    /// its address on <see cref="Dc01"/> (PDC emulator) and <see cref="Dc02"/>, 4776 0xc000006a from the workstation on
    /// <see cref="Dc02"/>, then the 4740 on <see cref="Dc01"/> naming the caller; one unrelated success of
    /// <see cref="ZofiaZak"/>. Times from <see cref="Morning"/>.
    /// </summary>
    public static IReadOnlyList<EventLogRecord> LockoutScenario()
    {
        var at = Morning.AddMinutes(30);
        return
        [
            KerberosPreAuthFailed(Dc01, 9_001, at, JanKowalski, WawPc001Ip),
            KerberosPreAuthFailed(Dc02, 5_001, at.AddMinutes(1), JanKowalski, WawPc001Ip),
            KerberosPreAuthFailed(Dc01, 9_002, at.AddMinutes(2), JanKowalski, WawPc001Ip),
            CredentialValidation(Dc02, 5_002, at.AddMinutes(3), JanKowalski, WawPc001),
            KerberosPreAuthFailed(Dc01, 9_003, at.AddMinutes(4), JanKowalski, WawPc001Ip),
            AccountLockedOut(Dc01, 9_004, at.AddMinutes(4).AddSeconds(1), JanKowalski, WawPc001),
            TicketGranted(Dc02, 5_003, at.AddMinutes(5), ZofiaZak, KrkLt001Ip),
        ];
    }

    /// <summary>
    /// Failed logons for the analyses: password spraying from <see cref="VpnIp"/> (each of the ten
    /// <see cref="Accounts"/> once, 4771 0x18, one minute apart), brute force against <see cref="ZofiaZak"/> from
    /// <see cref="KrkLt001"/> (12 × 4776 0xc000006a, then a successful 4768 from its address), account enumeration from
    /// <see cref="SrvPliki01Ip"/> (4768 0x6 and 4625 0xc0000064 for names that do not exist). All on <see cref="Dc01"/>
    /// and <see cref="Dc02"/>, times from <see cref="Morning"/>.
    /// </summary>
    public static IReadOnlyList<EventLogRecord> FailedLogonScenario()
    {
        var records = new List<EventLogRecord>();
        var id = 20_000L;
        for (var i = 0; i < Accounts.Count; i++)
        {
            records.Add(KerberosPreAuthFailed(i % 2 == 0 ? Dc01 : Dc02, id++, Morning.AddMinutes(i), Accounts[i], VpnIp));
        }

        for (var i = 0; i < 12; i++)
        {
            records.Add(CredentialValidation(Dc02, id++, Morning.AddHours(1).AddSeconds(i * 10), ZofiaZak, KrkLt001));
        }

        records.Add(TicketGranted(Dc02, id++, Morning.AddHours(1).AddMinutes(3), ZofiaZak, KrkLt001Ip));
        foreach (var name in new[] { "administrator1", "admin", "kowalski.jan", "zofia.żak" })
        {
            records.Add(TicketGranted(Dc01, id++, Morning.AddHours(2), name, SrvPliki01Ip, status: "0x6"));
            records.Add(LogonFailed(Dc01, id++, Morning.AddHours(2).AddSeconds(5), name, SrvPliki01Ip, SrvPliki01,
                subStatus: "0xc0000064"));
        }

        return records;
    }

    /// <summary>A Security record as <c>EventRecord.ToXml()</c> writes it (values XML-escaped; null written as <c>-</c>).</summary>
    public static string SecurityXml(string computer, int eventId, long recordId, DateTimeOffset time, bool success, int task,
        IEnumerable<(string Name, string? Value)> data, int version = 0) =>
        EventXml(computer, SecurityChannel, SecurityAuditing, SecurityAuditingGuid, eventId, recordId, time, level: 0, task,
            success ? AuditSuccessKeywords : AuditFailureKeywords, data, version);

    /// <summary>A record of any channel in the <c>EventRecord.ToXml()</c> shape with named <c>EventData</c>.</summary>
    public static string EventXml(string computer, string channel, string provider, string? providerGuid, int eventId, long recordId,
        DateTimeOffset time, byte level, int task, string keywords, IEnumerable<(string Name, string? Value)> data, int version = 0,
        Guid? activityId = null, string? userSid = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("Event", EventNamespace);
            writer.WriteStartElement("System", EventNamespace);
            writer.WriteStartElement("Provider", EventNamespace);
            writer.WriteAttributeString("Name", provider);
            if (providerGuid is not null)
            {
                writer.WriteAttributeString("Guid", providerGuid);
            }

            writer.WriteEndElement();
            writer.WriteElementString("EventID", EventNamespace, eventId.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Version", EventNamespace, version.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Level", EventNamespace, level.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Task", EventNamespace, task.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Opcode", EventNamespace, "0");
            writer.WriteElementString("Keywords", EventNamespace, keywords);
            writer.WriteStartElement("TimeCreated", EventNamespace);
            writer.WriteAttributeString("SystemTime", time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff00'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteElementString("EventRecordID", EventNamespace, recordId.ToString(CultureInfo.InvariantCulture));
            writer.WriteStartElement("Correlation", EventNamespace);
            if (activityId is { } activity)
            {
                writer.WriteAttributeString("ActivityID", activity.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant());
            }

            writer.WriteEndElement();
            writer.WriteStartElement("Execution", EventNamespace);
            writer.WriteAttributeString("ProcessID", "780");
            writer.WriteAttributeString("ThreadID", "1520");
            writer.WriteEndElement();
            writer.WriteElementString("Channel", EventNamespace, channel);
            writer.WriteElementString("Computer", EventNamespace, computer);
            writer.WriteStartElement("Security", EventNamespace);
            if (userSid is not null)
            {
                writer.WriteAttributeString("UserID", userSid);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("EventData", EventNamespace);
            foreach (var (name, value) in data)
            {
                writer.WriteStartElement("Data", EventNamespace);
                writer.WriteAttributeString("Name", name);
                writer.WriteString(value ?? "-");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return builder.ToString();
    }

    /// <summary>A record of any channel (see <see cref="EventXml"/>), parsed; <see cref="EventLogRecord.Host"/> =
    /// <paramref name="computer"/>.</summary>
    public static EventLogRecord Record(string computer, string channel, string provider, int eventId, long recordId, DateTimeOffset time,
        byte level, IEnumerable<(string Name, string? Value)> data, string keywords = "0x8000000000000000", Guid? activityId = null,
        string? userSid = null) =>
        Parse(EventXml(computer, channel, provider, null, eventId, recordId, time, level, task: 0, keywords, data, activityId: activityId, userSid: userSid),
            computer);

    private static EventLogRecord Security(string computer, int eventId, long recordId, DateTimeOffset time, bool success, int task,
        IEnumerable<(string Name, string? Value)> data, int version = 0) =>
        Parse(SecurityXml(computer, eventId, recordId, time, success, task, data, version), computer);

    private static EventLogRecord Parse(string xml, string host) =>
        EventXmlParser.Parse(xml, host) ?? throw new InvalidOperationException("A sample record did not parse.");

    private static string DcAccount(string dc) => dc.Split('.')[0].ToUpperInvariant() + "$";
}
