using System.Globalization;
using System.Text;
using System.Xml;

namespace AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

/// <summary>
/// Builds well-formed Security-log records in the <c>EventRecord.ToXml()</c> shape (the Appendix A layout) for tests:
/// any EventID with named EventData, the event-log service records (1100/1102/1104 with UserData) and shortcuts for the
/// records attribution reads. Values are XML-escaped by an <see cref="XmlWriter"/>.
/// </summary>
public static class SecurityEventXml
{
    public const string SecurityAuditing = "Microsoft-Windows-Security-Auditing";
    public const string EventLogService = "Microsoft-Windows-Eventlog";

    private const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const string EventLogNamespace = "http://manifests.microsoft.com/win/2004/08/windows/eventlog";

    /// <summary>A record with EventData (names in the given order; null values are written as "-").</summary>
    public static string Create(int eventCode, long recordId, DateTimeOffset timeCreated, string computer,
        IEnumerable<(string Name, string? Value)> data, string provider = SecurityAuditing, int version = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(eventCode, recordId, timeCreated, computer, provider, version, writer =>
        {
            writer.WriteStartElement("EventData", EventNamespace);
            foreach (var (name, value) in data)
            {
                writer.WriteStartElement("Data", EventNamespace);
                writer.WriteAttributeString("Name", name);
                writer.WriteString(value ?? "-");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }

    /// <summary>1102 "The audit log was cleared" (provider Microsoft-Windows-Eventlog, fields under UserData).</summary>
    public static string LogCleared(long recordId, DateTimeOffset timeCreated, string computer, string subjectSid, string subjectUserName,
        string subjectDomainName, string subjectLogonId) =>
        Write(1102, recordId, timeCreated, computer, EventLogService, 0, writer =>
        {
            writer.WriteStartElement("UserData", EventNamespace);
            writer.WriteStartElement("LogFileCleared", EventLogNamespace);
            writer.WriteElementString("SubjectUserSid", EventLogNamespace, subjectSid);
            writer.WriteElementString("SubjectUserName", EventLogNamespace, subjectUserName);
            writer.WriteElementString("SubjectDomainName", EventLogNamespace, subjectDomainName);
            writer.WriteElementString("SubjectLogonId", EventLogNamespace, subjectLogonId);
            writer.WriteEndElement();
            writer.WriteEndElement();
        });

    /// <summary>1100 (event logging service shut down) or 1104 (security log full) without data.</summary>
    public static string EventLogServiceRecord(int eventCode, long recordId, DateTimeOffset timeCreated, string computer) =>
        Write(eventCode, recordId, timeCreated, computer, EventLogService, 0, writer =>
        {
            writer.WriteStartElement("UserData", EventNamespace);
            writer.WriteStartElement(eventCode == 1104 ? "FileIsFull" : "ServiceShutdown", EventLogNamespace);
            writer.WriteEndElement();
            writer.WriteEndElement();
        });

    /// <summary>The subject fields of an account-management or directory-service record.</summary>
    public static IEnumerable<(string Name, string? Value)> Subject(string sid, string userName, string domainName, string logonId) =>
    [
        ("SubjectUserSid", sid), ("SubjectUserName", userName), ("SubjectDomainName", domainName), ("SubjectLogonId", logonId),
    ];

    /// <summary>A 47xx account-management record about <paramref name="targetSid"/>.</summary>
    public static string Account(int eventCode, long recordId, DateTimeOffset timeCreated, string computer, string targetSid,
        string targetUserName, IEnumerable<(string Name, string? Value)> subject, params (string Name, string? Value)[] extra) =>
        Create(eventCode, recordId, timeCreated, computer,
            [("TargetUserName", targetUserName), ("TargetDomainName", "CONTOSO"), ("TargetSid", targetSid), .. subject, .. extra]);

    /// <summary>A 4728-family membership record: <paramref name="memberSid"/> added to/removed from group
    /// <paramref name="groupSid"/>.</summary>
    public static string Membership(int eventCode, long recordId, DateTimeOffset timeCreated, string computer, string groupSid,
        string groupName, string memberSid, string memberDn, IEnumerable<(string Name, string? Value)> subject) =>
        Create(eventCode, recordId, timeCreated, computer,
            [("MemberName", memberDn), ("MemberSid", memberSid), ("TargetUserName", groupName), ("TargetDomainName", "CONTOSO"),
             ("TargetSid", groupSid), .. subject, ("PrivilegeList", "-")]);

    /// <summary>A 5136 record (OperationType 14674 Value Added / 14675 Value Deleted).</summary>
    public static string DirectoryModified(long recordId, DateTimeOffset timeCreated, string computer, Guid objectGuid, string objectDn,
        string objectClass, string attribute, string? value, bool added, IEnumerable<(string Name, string? Value)> subject, Guid? opCorrelationId = null) =>
        Create(5136, recordId, timeCreated, computer,
        [
            ("OpCorrelationID", (opCorrelationId ?? Guid.NewGuid()).ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant()),
            ("AppCorrelationID", "-"), .. subject, ("DSName", "contoso.test"), ("DSType", "%%14676"), ("ObjectDN", objectDn),
            ("ObjectGUID", objectGuid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant()), ("ObjectClass", objectClass),
            ("AttributeLDAPDisplayName", attribute), ("AttributeSyntaxOID", "2.5.5.12"), ("AttributeValue", value),
            ("OperationType", added ? "%%14674" : "%%14675"),
        ]);

    /// <summary>A 5137 (created), 5138 (undeleted) or 5141 (deleted) record.</summary>
    public static string DirectoryObject(int eventCode, long recordId, DateTimeOffset timeCreated, string computer, Guid objectGuid,
        string objectDn, string objectClass, IEnumerable<(string Name, string? Value)> subject) =>
        Create(eventCode, recordId, timeCreated, computer,
        [
            ("OpCorrelationID", Guid.NewGuid().ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant()), ("AppCorrelationID", "-"),
            .. subject, ("DSName", "contoso.test"), ("DSType", "%%14676"), ("ObjectDN", objectDn),
            ("ObjectGUID", objectGuid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant()), ("ObjectClass", objectClass),
        ]);

    /// <summary>A 4624 successful logon (on-demand source lookup).</summary>
    public static string Logon(long recordId, DateTimeOffset timeCreated, string computer, string targetLogonId, int logonType,
        string? ipAddress, string? workstationName, string targetUserSid, string targetUserName, string authenticationPackage = "Kerberos") =>
        Create(4624, recordId, timeCreated, computer,
        [
            ("SubjectUserSid", "S-1-0-0"), ("SubjectUserName", "-"), ("SubjectDomainName", "-"), ("SubjectLogonId", "0x0"),
            ("TargetUserSid", targetUserSid), ("TargetUserName", targetUserName), ("TargetDomainName", "CONTOSO"),
            ("TargetLogonId", targetLogonId), ("LogonType", logonType.ToString(CultureInfo.InvariantCulture)),
            ("LogonProcessName", "Kerberos"), ("AuthenticationPackageName", authenticationPackage), ("WorkstationName", workstationName),
            ("IpAddress", ipAddress), ("IpPort", ipAddress is null ? null : "50321"),
        ], version: 2);

    /// <summary>A 4771 Kerberos pre-authentication failure (Status 0x18 = bad password).</summary>
    public static string KerberosPreAuthFailed(long recordId, DateTimeOffset timeCreated, string computer, string targetUserName,
        string ipAddress, string status = "0x18") =>
        Create(4771, recordId, timeCreated, computer,
        [
            ("TargetUserName", targetUserName), ("TargetSid", "S-1-5-21-0-0-0-0"), ("ServiceName", "krbtgt/CONTOSO"), ("TicketOptions", "0x40810010"),
            ("Status", status), ("PreAuthType", "2"), ("IpAddress", "::ffff:" + ipAddress), ("IpPort", "51340"),
        ]);

    /// <summary>A 4776 NTLM credential validation (Status 0xc000006a = bad password).</summary>
    public static string CredentialValidation(long recordId, DateTimeOffset timeCreated, string computer, string targetUserName,
        string workstation, string status = "0xc000006a") =>
        Create(4776, recordId, timeCreated, computer,
        [
            ("PackageName", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0"), ("TargetUserName", targetUserName), ("Workstation", workstation),
            ("Status", status),
        ]);

    /// <summary>A 4625 failed logon.</summary>
    public static string LogonFailed(long recordId, DateTimeOffset timeCreated, string computer, string targetUserName,
        string? ipAddress, string? workstationName) =>
        Create(4625, recordId, timeCreated, computer,
        [
            ("SubjectUserSid", "S-1-0-0"), ("TargetUserSid", "S-1-0-0"), ("TargetUserName", targetUserName), ("TargetDomainName", "CONTOSO"),
            ("Status", "0xc000006d"), ("FailureReason", "%%2313"), ("SubStatus", "0xc000006a"), ("LogonType", "3"),
            ("WorkstationName", workstationName), ("IpAddress", ipAddress), ("IpPort", "0"),
        ]);

    private static string Write(int eventCode, long recordId, DateTimeOffset timeCreated, string computer, string provider, int version,
        Action<XmlWriter> body)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("Event", EventNamespace);
            writer.WriteStartElement("System", EventNamespace);
            writer.WriteStartElement("Provider", EventNamespace);
            writer.WriteAttributeString("Name", provider);
            writer.WriteEndElement();
            writer.WriteElementString("EventID", EventNamespace, eventCode.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Version", EventNamespace, version.ToString(CultureInfo.InvariantCulture));
            writer.WriteStartElement("TimeCreated", EventNamespace);
            writer.WriteAttributeString("SystemTime", timeCreated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff00'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteElementString("EventRecordID", EventNamespace, recordId.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Channel", EventNamespace, "Security");
            writer.WriteElementString("Computer", EventNamespace, computer);
            writer.WriteEndElement();
            body(writer);
            writer.WriteEndElement();
        }

        return builder.ToString();
    }
}
