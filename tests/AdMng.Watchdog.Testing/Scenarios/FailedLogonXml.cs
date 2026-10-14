using System.Globalization;
using System.Security;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>Event XML of failed logons as <c>EventRecord.ToXml()</c> returns it (4771 Kerberos pre-authentication, 4776
/// NTLM credential validation), for the password-spray scenarios.</summary>
public static class FailedLogonXml
{
    private const string Namespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>4771 with status 0x18 (wrong password) from <paramref name="ipAddress"/> (IPv4-mapped like Windows logs it).</summary>
    public static string Kerberos(string userName, string ipAddress, DateTimeOffset time, string status = "0x18", long recordId = 1) =>
        Event(4771, time, recordId,
            ("TargetUserName", userName), ("TargetSid", "S-1-5-21-3623811015-3361044348-30300820-1107"), ("ServiceName", "krbtgt/CONTOSO.TEST"),
            ("TicketOptions", "0x40810010"), ("Status", status), ("PreAuthType", "2"), ("IpAddress", "::ffff:" + ipAddress), ("IpPort", "51234"),
            ("CertIssuerName", string.Empty), ("CertSerialNumber", string.Empty), ("CertThumbprint", string.Empty));

    /// <summary>4776 with status 0xC000006A (wrong password) from <paramref name="workstation"/>.</summary>
    public static string Ntlm(string userName, string workstation, DateTimeOffset time, string status = "0xc000006a", long recordId = 1) =>
        Event(4776, time, recordId,
            ("PackageName", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0"), ("TargetUserName", userName), ("Workstation", workstation), ("Status", status));

    private static string Event(int eventId, DateTimeOffset time, long recordId, params (string Name, string Value)[] data)
    {
        var fields = string.Concat(data.Select(d => d.Value.Length == 0
            ? $"<Data Name='{d.Name}' />"
            : $"<Data Name='{d.Name}'>{SecurityElement.Escape(d.Value)}</Data>"));
        var created = time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return $"<Event xmlns='{Namespace}'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{{54849625-5478-4994-a5ba-3e3b0328c30d}}' />"
            + $"<EventID>{eventId.ToString(CultureInfo.InvariantCulture)}</EventID><Version>0</Version><Level>0</Level><Task>14339</Task><Opcode>0</Opcode>"
            + $"<Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='{created}' />"
            + $"<EventRecordID>{recordId.ToString(CultureInfo.InvariantCulture)}</EventRecordID><Correlation /><Execution ProcessID='716' ThreadID='1220' />"
            + "<Channel>Security</Channel><Computer>dc01.contoso.test</Computer><Security /></System>"
            + $"<EventData>{fields}</EventData></Event>";
    }
}
