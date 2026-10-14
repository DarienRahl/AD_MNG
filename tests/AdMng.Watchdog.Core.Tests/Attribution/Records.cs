using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Tests.Rules;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

/// <summary>Security records of the CONTOSO test domain for the attribution tests.</summary>
internal static class Records
{
    public const string Dc1 = "dc01.contoso.test";
    public const string Dc2 = "dc02.contoso.test";
    public const string AdminLogonId = "0x5a3f1";
    public const string OtherAdminSid = TestEvents.DomainSid + "-1105";
    public const string UserSid = TestEvents.DomainSid + "-6609";

    public static readonly IEnumerable<(string, string?)> Admin = SecurityEventXml.Subject(TestEvents.AdminSid, "adm.jkowalska", "CONTOSO", AdminLogonId);
    public static readonly IEnumerable<(string, string?)> OtherAdmin = SecurityEventXml.Subject(OtherAdminSid, "adm.zzak", "CONTOSO", "0x77aa1");
    public static readonly IEnumerable<(string, string?)> System = SecurityEventXml.Subject("S-1-5-18", "DC01$", "CONTOSO", "0x3e7");

    private static readonly SecurityEventParser Parser = new();
    private static long _recordId = 1000;

    public static long NextId() => Interlocked.Increment(ref _recordId);

    public static SecurityEvent Parse(string xml, string source = Dc1) => Parser.Parse(xml, source)!.Event;

    public static SecurityEvent Account(int code, DateTimeOffset at, IEnumerable<(string, string?)> subject, string targetSid = UserSid,
        string computer = Dc1, params (string Name, string? Value)[] extra) =>
        Parse(SecurityEventXml.Account(code, NextId(), at, computer, targetSid, "lwisniewski", subject, extra), computer);

    public static SecurityEvent Modified(DateTimeOffset at, IEnumerable<(string, string?)> subject, string attribute, string? value, bool added,
        Guid? objectGuid = null, string computer = Dc1, string dn = TestEvents.UserDn) =>
        Parse(SecurityEventXml.DirectoryModified(NextId(), at, computer, objectGuid ?? TestEvents.UserGuid, dn, "user", attribute, value, added, subject), computer);
}
