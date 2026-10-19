using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class SecurityEventParserTests
{
    private readonly SecurityEventParser _parser = new();

    [Fact]
    public void Parses4720_SystemFieldsAndEventDataByName()
    {
        var record = _parser.Parse(SecurityEventFixtures.UserCreated4720, "dc01.contoso.local")!;
        var e = record.Event;

        Assert.Equal("Microsoft-Windows-Security-Auditing", record.Provider);
        Assert.Equal(4720, e.EventCode);
        Assert.Equal(0, e.Version);
        Assert.Equal(175408, e.RecordId);
        Assert.Equal("DC01.contoso.local", e.Computer);
        Assert.Equal("dc01.contoso.local", e.Source);
        Assert.Equal(new DateTimeOffset(2015, 8, 20, 16, 22, 2, TimeSpan.Zero).AddTicks(7599120), e.TimeCreated);
        Assert.Equal(TimeSpan.Zero, e.TimeCreated.Offset);
        Assert.Equal("S-1-5-21-3457937927-2839227994-823803824-6609", e.TargetSid);
        Assert.Equal("S-1-5-21-3457937927-2839227994-823803824-1104", e.SubjectSid);
        Assert.Equal("CONTOSO\\dadmin", e.SubjectAccount);
        Assert.Equal("0x30dc2", e.SubjectLogonId);
        Assert.Equal("Ken Smith", e.Get("DisplayName"));
        Assert.Equal("0x15", e.Get("NewUacValue"));
    }

    [Fact]
    public void DashAndEmptyValuesAreOmitted_AndPercentCodesDecoded()
    {
        var e = _parser.Parse(SecurityEventFixtures.UserCreated4720, "dc01")!.Event;

        Assert.Null(e.Get("HomeDirectory"));
        Assert.Null(e.Get("PrivilegeList"));
        Assert.Equal(InsertionStringDecoder.Never, e.Get("PasswordLastSet"));
        Assert.Equal(InsertionStringDecoder.Never, e.Get("AccountExpires"));
        Assert.Equal(InsertionStringDecoder.ValueNotSet, e.Get("LogonHours"));
        Assert.Equal("%%2080 %%2082 %%2084", e.Get("UserAccountControl"));
    }

    [Fact]
    public void Parses4738()
    {
        var e = _parser.Parse(SecurityEventFixtures.UserChanged4738, "dc01")!.Event;

        Assert.Equal(4738, e.EventCode);
        Assert.Equal(175413, e.RecordId);
        Assert.Null(e.Get("Dummy"));
        Assert.Null(e.Get("SamAccountName"));
        Assert.Equal("0x15", e.Get("OldUacValue"));
        Assert.Equal("0x211", e.Get("NewUacValue"));
    }

    [Fact]
    public void Parses4740_TheCallerComputerIsExposed()
    {
        var e = _parser.Parse(SecurityEventFixtures.AccountLockedOut4740, "dc01")!.Event;

        Assert.Equal("WIN81", e.Get(SecurityEventParser.CallerComputerName));
        Assert.Equal("S-1-5-18", e.SubjectSid);
        Assert.Equal("0x3e7", e.SubjectLogonId);
        Assert.Equal("Auditor", e.Get("TargetUserName"));
    }

    [Fact]
    public void Parses5136_GuidsAndOperationType()
    {
        var e = _parser.Parse(SecurityEventFixtures.DirectoryObjectModified5136, "dc01")!.Event;

        Assert.Equal(Guid.Parse("4FE80A66-5F93-4F73-B215-68678058E613"), e.ObjectGuid);
        Assert.Equal(Guid.Parse("02647639-8626-43CE-AFE6-7AA1AD657739"), e.OpCorrelationId);
        Assert.Equal(InsertionStringDecoder.ValueDeleted, e.Get("OperationType"));
        Assert.Equal("AD DS", e.Get("DSType"));
        Assert.Equal("userAccountControl", e.Get("AttributeLDAPDisplayName"));
        Assert.Equal("512", e.Get("AttributeValue"));
        Assert.Equal("CN=Sergey,CN=Builtin,DC=contoso,DC=local", e.Get("ObjectDN"));
        Assert.Equal("0x32004", e.SubjectLogonId);
    }

    [Fact]
    public void Parses1102_UserDataFields()
    {
        var xml = SecurityEventXml.LogCleared(500, new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero), "dc01.contoso.test",
            "S-1-5-21-1-2-3-1104", "jkowalska", "CONTOSO", "0x5a3f1");

        var record = _parser.Parse(xml, "dc01.contoso.test")!;

        Assert.Equal("Microsoft-Windows-Eventlog", record.Provider);
        Assert.Equal(1102, record.Event.EventCode);
        Assert.Equal("S-1-5-21-1-2-3-1104", record.Event.SubjectSid);
        Assert.Equal("CONTOSO\\jkowalska", record.Event.SubjectAccount);
    }

    [Fact]
    public void ValuesAreReadByNameNotByPosition()
    {
        var at = new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);
        var reordered = SecurityEventXml.Create(4724, 7, at, "dc01.contoso.test",
            [("SubjectLogonId", "0x1F"), ("Nowe pole wersji 3", "x"), ("TargetSid", "S-1-5-21-1-2-3-1500"), ("SubjectUserSid", "S-1-5-21-1-2-3-1104")], version: 3);

        var e = _parser.Parse(reordered, "dc01")!.Event;

        Assert.Equal("S-1-5-21-1-2-3-1500", e.TargetSid);
        Assert.Equal("0x1f", e.SubjectLogonId);
        Assert.Equal(3, e.Version);
        Assert.Equal("x", e.Get("Nowe pole wersji 3"));
    }

    [Fact]
    public void PolishValuesSurviveEscaping()
    {
        var xml = SecurityEventXml.Create(4738, 9, DateTimeOffset.UnixEpoch, "dc01.contoso.test",
            [("DisplayName", "Łukasz <Wiśniewski> & Żaneta \"Źdźbło\"")]);

        Assert.Equal("Łukasz <Wiśniewski> & Żaneta \"Źdźbło\"", _parser.Parse(xml, "dc01")!.Event.Get("DisplayName"));
    }

    [Theory]
    [InlineData("x&#x1;")]
    [InlineData("x\u0001")]
    [InlineData("x&#x1F;")]
    public void ControlCharactersInAValue_DoNotDropTheRecord(string displayName)
    {
        // attribution-rules-10: an attacker writes displayName "x\u0001" in the same modify as AllowedToDelegateTo;
        // EventRecord.ToXml() renders it – the one 4742 must still attribute the delegation change.
        var xml = SecurityEventXml.Create(4742, 9, DateTimeOffset.UnixEpoch, "dc01.contoso.test",
            [("TargetSid", "S-1-5-21-1-2-3-3101"), ("DisplayName", "ZASTĘPCZA"), ("AllowedToDelegateTo", "cifs/dc01.contoso.test")])
            .Replace("ZASTĘPCZA", displayName, StringComparison.Ordinal);

        var e = _parser.Parse(xml, "dc01")!.Event;

        Assert.Equal("cifs/dc01.contoso.test", e.Get("AllowedToDelegateTo"));
        Assert.Equal("x�", e.Get("DisplayName"));   // invalid XML 1.0 characters are replaced before storing
        Assert.DoesNotContain(e.Data.Values, v => v.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Event")]
    [InlineData("<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4720</EventID></System></Event>")]
    [InlineData("<!DOCTYPE Event [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>&x;</EventID></System></Event>")]
    [InlineData("<Event><System><EventID>4720</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"2026-10-14T06:00:00Z\"/><Computer>dc01</Computer></System></Event>")]
    public void MalformedOrIncompleteRecords_YieldNull(string xml)
    {
        Assert.Null(_parser.Parse(xml, "dc01"));
        Assert.False(_parser.TryParse(xml, "dc01", out _));
    }

    [Theory]
    [InlineData("2015-08-20T16:22:02.759912000Z", 2015, 7599120)]
    [InlineData("2026-10-14T06:00:00Z", 2026, 0)]
    [InlineData("2026-10-14T06:00:00.5Z", 2026, 5000000)]
    public void SystemTime_UpToNineFractionalDigits(string value, int year, long fractionTicks)
    {
        Assert.True(SecurityEventParser.TryParseSystemTime(value, out var utc));
        Assert.Equal(year, utc.Year);
        Assert.Equal(fractionTicks, utc.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.False(SecurityEventParser.TryParseSystemTime("wczoraj", out _));
    }
}
