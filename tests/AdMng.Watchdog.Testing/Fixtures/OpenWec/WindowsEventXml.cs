using System.Globalization;
using System.Security;
using System.Text;

namespace AdMng.Watchdog.Testing.Fixtures.OpenWec;

/// <summary>
/// Security records in the shape a Windows Server 2022/2025 DC forwards them with WEF <c>ContentFormat = Raw</c>
/// (EvtRender of the event, no RenderingInfo): single-quoted attributes, the full <c>System</c> element (provider GUID,
/// task, keywords, correlation, execution, channel, computer) and every <c>EventData</c> field of the event's manifest in
/// manifest order (versions as on Windows Server 2022: 4624 v2, 4625 v0, 4720 v0, 4738 v0, 5136 v0). The values follow
/// Microsoft's documented examples; tests only choose the identities, ids and times.
/// </summary>
public static class WindowsEventXml
{
    public const string SecurityAuditingGuid = "{54849625-5478-4994-a5ba-3e3b0328c30d}";
    public const string EventLogGuid = "{fc65ddd8-d6ef-4962-83d5-6e5cfe9ce148}";
    public const string AuditSuccess = "0x8020000000000000";
    public const string AuditFailure = "0x8010000000000000";

    /// <summary>A Security-auditing record.</summary>
    public static string Event(int eventId, int version, int task, string keywords, long recordId, DateTimeOffset timeCreated, string computer,
        IEnumerable<(string Name, string? Value)> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var builder = new StringBuilder();
        builder.Append("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>");
        builder.Append(CultureInfo.InvariantCulture,
            $"<Provider Name='Microsoft-Windows-Security-Auditing' Guid='{SecurityAuditingGuid}'/><EventID>{eventId}</EventID><Version>{version}</Version><Level>0</Level><Task>{task}</Task><Opcode>0</Opcode><Keywords>{keywords}</Keywords>");
        AppendTail(builder, recordId, timeCreated, computer, 740, 4180);
        builder.Append("<EventData>");
        foreach (var (name, value) in data)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<Data Name='{name}'>{SecurityElement.Escape(value ?? "-")}</Data>");
        }

        return builder.Append("</EventData></Event>").ToString();
    }

    /// <summary>4624 (version 2) of a network (3) or RemoteInteractive (10) logon.</summary>
    public static string Logon(long recordId, DateTimeOffset timeCreated, string computer, string targetUserSid, string targetUserName,
        string targetLogonId, int logonType, string? ipAddress, string? workstationName, string authenticationPackage = "Kerberos") =>
        Event(4624, 2, 12544, AuditSuccess, recordId, timeCreated, computer,
        [
            ("SubjectUserSid", logonType == 10 ? "S-1-5-18" : "S-1-0-0"), ("SubjectUserName", logonType == 10 ? "DC01$" : "-"),
            ("SubjectDomainName", logonType == 10 ? "CONTOSO" : "-"), ("SubjectLogonId", logonType == 10 ? "0x3e7" : "0x0"),
            ("TargetUserSid", targetUserSid), ("TargetUserName", targetUserName), ("TargetDomainName", "CONTOSO"),
            ("TargetLogonId", targetLogonId), ("LogonType", logonType.ToString(CultureInfo.InvariantCulture)),
            ("LogonProcessName", logonType == 10 ? "User32 " : authenticationPackage == "NTLM" ? "NtLmSsp " : "Kerberos"),
            ("AuthenticationPackageName", logonType == 10 ? "Negotiate" : authenticationPackage), ("WorkstationName", workstationName),
            ("LogonGuid", "{7C1D9F3A-2B44-8E21-5D0A-9E7F3B2C1A44}"), ("TransmittedServices", "-"),
            ("LmPackageName", authenticationPackage == "NTLM" ? "NTLM V2" : "-"), ("KeyLength", authenticationPackage == "NTLM" ? "128" : "0"),
            ("ProcessId", logonType == 10 ? "0x1d8c" : "0x0"), ("ProcessName", logonType == 10 ? "C:\\Windows\\System32\\svchost.exe" : "-"),
            ("IpAddress", ipAddress), ("IpPort", ipAddress is null ? "-" : "52417"), ("ImpersonationLevel", "%%1833"),
            ("RestrictedAdminMode", logonType == 10 ? "%%1843" : "-"), ("TargetOutboundUserName", "-"), ("TargetOutboundDomainName", "-"),
            ("VirtualAccount", "%%1843"), ("TargetLinkedLogonId", "0x0"), ("ElevatedToken", "%%1842"),
        ]);

    /// <summary>4625 (version 0) failed network logon.</summary>
    public static string LogonFailed(long recordId, DateTimeOffset timeCreated, string computer, string targetUserName, string? ipAddress,
        string? workstationName, string status = "0xc000006d", string subStatus = "0xc000006a") =>
        Event(4625, 0, 12544, AuditFailure, recordId, timeCreated, computer,
        [
            ("SubjectUserSid", "S-1-0-0"), ("SubjectUserName", "-"), ("SubjectDomainName", "-"), ("SubjectLogonId", "0x0"),
            ("TargetUserSid", "S-1-0-0"), ("TargetUserName", targetUserName), ("TargetDomainName", "CONTOSO"), ("Status", status),
            ("FailureReason", "%%2313"), ("SubStatus", subStatus), ("LogonType", "3"), ("LogonProcessName", "NtLmSsp "),
            ("AuthenticationPackageName", "NTLM"), ("WorkstationName", workstationName), ("TransmittedServices", "-"),
            ("LmPackageName", "-"), ("KeyLength", "0"), ("ProcessId", "0x0"), ("ProcessName", "-"), ("IpAddress", ipAddress),
            ("IpPort", ipAddress is null ? "-" : "0"),
        ]);

    /// <summary>4771 (version 0) Kerberos pre-authentication failed (0x18 = bad password).</summary>
    public static string KerberosPreAuthFailed(long recordId, DateTimeOffset timeCreated, string computer, string targetUserName, string targetSid,
        string ipAddress, string status = "0x18") =>
        Event(4771, 0, 14339, AuditFailure, recordId, timeCreated, computer,
        [
            ("TargetUserName", targetUserName), ("TargetSid", targetSid), ("ServiceName", "krbtgt/CONTOSO"), ("TicketOptions", "0x40810010"),
            ("Status", status), ("PreAuthType", "2"), ("IpAddress", "::ffff:" + ipAddress), ("IpPort", "51340"), ("CertIssuerName", null),
            ("CertSerialNumber", null), ("CertThumbprint", null),
        ]);

    /// <summary>4776 (version 0) NTLM credential validation (0xc000006a = bad password).</summary>
    public static string CredentialValidation(long recordId, DateTimeOffset timeCreated, string computer, string targetUserName, string workstation,
        string status = "0xc000006a") =>
        Event(4776, 0, 14336, status == "0x0" ? AuditSuccess : AuditFailure, recordId, timeCreated, computer,
        [
            ("PackageName", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0"), ("TargetUserName", targetUserName), ("Workstation", workstation),
            ("Status", status),
        ]);

    /// <summary>The subject fields of an account-management or directory-service record.</summary>
    public static IEnumerable<(string Name, string? Value)> Subject(string sid, string userName, string logonId) =>
        [("SubjectUserSid", sid), ("SubjectUserName", userName), ("SubjectDomainName", "CONTOSO"), ("SubjectLogonId", logonId)];

    /// <summary>4720 (version 0) user created – all SAM fields as a DC logs a new user.</summary>
    public static string UserCreated(long recordId, DateTimeOffset timeCreated, string computer, string targetSid, string samAccountName,
        IEnumerable<(string Name, string? Value)> subject) =>
        Event(4720, 0, 13824, AuditSuccess, recordId, timeCreated, computer,
        [
            ("TargetUserName", samAccountName), ("TargetDomainName", "CONTOSO"), ("TargetSid", targetSid), .. subject, ("PrivilegeList", "-"),
            ("SamAccountName", samAccountName), ("DisplayName", "%%1793"), ("UserPrincipalName", samAccountName + "@contoso.test"),
            ("HomeDirectory", "%%1793"), ("HomePath", "%%1793"), ("ScriptPath", "%%1793"), ("ProfilePath", "%%1793"),
            ("UserWorkstations", "%%1793"), ("PasswordLastSet", "%%1794"), ("AccountExpires", "%%1794"), ("PrimaryGroupId", "513"),
            ("AllowedToDelegateTo", "-"), ("OldUacValue", "0x0"), ("NewUacValue", "0x15"), ("UserAccountControl", "\r\n\t\t%%2080\r\n\t\t%%2082\r\n\t\t%%2084"),
            ("UserParameters", "%%1793"), ("SidHistory", "-"), ("LogonHours", "%%1797"),
        ]);

    /// <summary>A 47xx record of the user/computer family with the TargetSid key (4722, 4724, 4725, 4726, 4740 …).</summary>
    public static string Account(int eventId, long recordId, DateTimeOffset timeCreated, string computer, string targetSid, string targetUserName,
        IEnumerable<(string Name, string? Value)> subject, params (string Name, string? Value)[] extra) =>
        Event(eventId, 0, 13824, AuditSuccess, recordId, timeCreated, computer,
            [("TargetUserName", targetUserName), ("TargetDomainName", "CONTOSO"), ("TargetSid", targetSid), .. subject, .. extra]);

    /// <summary>4740 account locked out: TargetDomainName carries the caller computer; the subject is the DC.</summary>
    public static string LockedOut(long recordId, DateTimeOffset timeCreated, string computer, string targetSid, string targetUserName, string? callerComputer) =>
        Event(4740, 0, 13824, AuditSuccess, recordId, timeCreated, computer,
        [
            ("TargetUserName", targetUserName), ("TargetDomainName", callerComputer), ("TargetSid", targetSid),
            .. Subject("S-1-5-18", computer.Split('.')[0].ToUpperInvariant() + "$", "0x3e7"),
        ]);

    /// <summary>4728/4732/4756 member added (4729/4733/4757 removed).</summary>
    public static string Membership(int eventId, long recordId, DateTimeOffset timeCreated, string computer, string groupSid, string groupName,
        string memberSid, string memberDn, IEnumerable<(string Name, string? Value)> subject) =>
        Event(eventId, 0, eventId is 4732 or 4733 ? 13826 : 13826, AuditSuccess, recordId, timeCreated, computer,
        [
            ("MemberName", memberDn), ("MemberSid", memberSid), ("TargetUserName", groupName), ("TargetDomainName", "CONTOSO"),
            ("TargetSid", groupSid), .. subject, ("PrivilegeList", "-"),
        ]);

    /// <summary>5136 (version 0) directory service object modified.</summary>
    public static string DirectoryModified(long recordId, DateTimeOffset timeCreated, string computer, Guid objectGuid, string objectDn, string objectClass,
        string attribute, string? value, bool added, IEnumerable<(string Name, string? Value)> subject, Guid? correlation = null) =>
        Event(5136, 0, 14081, AuditSuccess, recordId, timeCreated, computer,
        [
            ("OpCorrelationID", (correlation ?? Guid.NewGuid()).ToString("B", CultureInfo.InvariantCulture)), ("AppCorrelationID", "-"),
            .. subject, ("DSName", "contoso.test"), ("DSType", "%%14676"), ("ObjectDN", objectDn),
            ("ObjectGUID", objectGuid.ToString("B", CultureInfo.InvariantCulture)), ("ObjectClass", objectClass),
            ("AttributeLDAPDisplayName", attribute), ("AttributeSyntaxOID", "2.5.5.12"), ("AttributeValue", value),
            ("OperationType", added ? "%%14674" : "%%14675"),
        ]);

    /// <summary>5137 created / 5138 undeleted / 5141 deleted.</summary>
    public static string DirectoryObject(int eventId, long recordId, DateTimeOffset timeCreated, string computer, Guid objectGuid, string objectDn,
        string objectClass, IEnumerable<(string Name, string? Value)> subject) =>
        Event(eventId, 0, 14081, AuditSuccess, recordId, timeCreated, computer,
        [
            ("OpCorrelationID", Guid.NewGuid().ToString("B", CultureInfo.InvariantCulture)), ("AppCorrelationID", "-"), .. subject,
            ("DSName", "contoso.test"), ("DSType", "%%14676"), ("ObjectDN", objectDn),
            ("ObjectGUID", objectGuid.ToString("B", CultureInfo.InvariantCulture)), ("ObjectClass", objectClass),
            .. eventId == 5141 ? new (string, string?)[] { ("TreeDelete", "%%14679") } : [],
        ]);

    /// <summary>1102 the audit log was cleared (provider Microsoft-Windows-Eventlog, UserData/LogFileCleared).</summary>
    public static string LogCleared(long recordId, DateTimeOffset timeCreated, string computer, string subjectSid, string subjectUserName, string subjectLogonId)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Eventlog' Guid='{EventLogGuid}'/><EventID>1102</EventID><Version>0</Version><Level>4</Level><Task>104</Task><Opcode>0</Opcode><Keywords>0x4020000000000000</Keywords>");
        AppendTail(builder, recordId, timeCreated, computer, 1148, 6020);
        builder.Append(CultureInfo.InvariantCulture,
            $"<UserData><LogFileCleared xmlns='http://manifests.microsoft.com/win/2004/08/windows/eventlog'><SubjectUserSid>{subjectSid}</SubjectUserSid><SubjectUserName>{SecurityElement.Escape(subjectUserName)}</SubjectUserName><SubjectDomainName>CONTOSO</SubjectDomainName><SubjectLogonId>{subjectLogonId}</SubjectLogonId></LogFileCleared></UserData></Event>");
        return builder.ToString();
    }

    private static void AppendTail(StringBuilder builder, long recordId, DateTimeOffset timeCreated, string computer, int processId, int threadId) =>
        builder.Append(CultureInfo.InvariantCulture,
            $"<TimeCreated SystemTime='{timeCreated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)}'/><EventRecordID>{recordId}</EventRecordID><Correlation ActivityID='{{6A3D2B8F-7A1D-0003-8F2B-3C551D7ADC01}}'/><Execution ProcessID='{processId}' ThreadID='{threadId}'/><Channel>Security</Channel><Computer>{SecurityElement.Escape(computer)}</Computer><Security/></System>");
}
