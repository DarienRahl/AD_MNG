using AdMng.Core.EventLogs;
using AdMng.Testing.EventLogs;

namespace AdMng.Core.Tests.EventLogs;

public class EventXmlParserTests
{
    [Fact]
    public void Parses4625_SystemFieldsAndEventDataByNameInDocumentOrder()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.LogonFailed4625, "waw-pc001.contoso.test")!;

        Assert.Equal("waw-pc001.contoso.test", record.Host);
        Assert.Equal("WAW-PC001.contoso.test", record.Computer);
        Assert.Equal("Security", record.Channel);
        Assert.Equal("Microsoft-Windows-Security-Auditing", record.Provider);
        Assert.Equal(4625, record.EventId);
        Assert.Equal(0, record.Version);
        Assert.Equal(EventLogLevels.LogAlways, record.Level);
        Assert.Equal(12544, record.Task);
        Assert.Equal(0, record.Opcode);
        Assert.Equal(1182, record.RecordId);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 7, 13, 55, TimeSpan.Zero).AddTicks(4378386), record.TimeCreated);
        Assert.Equal(TimeSpan.Zero, record.TimeCreated.Offset);
        Assert.Null(record.ActivityId);
        Assert.Null(record.UserSid);
        Assert.True(record.IsAuditFailure);
        Assert.False(record.IsAuditSuccess);
        Assert.Equal(unchecked((long)0x8010000000000000UL), record.Keywords);

        Assert.Equal(
        [
            "SubjectUserSid", "SubjectUserName", "SubjectDomainName", "SubjectLogonId", "TargetUserSid", "TargetUserName", "TargetDomainName",
            "Status", "FailureReason", "SubStatus", "LogonType", "LogonProcessName", "AuthenticationPackageName", "WorkstationName", "KeyLength",
            "ProcessId", "ProcessName", "IpAddress", "IpPort",
        ], record.Data.Select(d => d.Key));
        Assert.Equal("jan.kowalski", record.Get("TargetUserName"));
        Assert.Equal("jan.kowalski", record.Get("targetusername"));     // Get is case-insensitive
        Assert.Equal("0xc0000234", record.Get("Status"));
        Assert.Equal("%%2307", record.Get("FailureReason"));             // not a decoder code – the catalog has its key
        Assert.Equal("User32", record.Get("LogonProcessName"));          // trimmed
        Assert.Equal(@"C:\Windows\System32\winlogon.exe", record.Get("ProcessName"));
        Assert.Null(record.Get("TransmittedServices"));                  // "-" omitted
        Assert.Equal("Security 4625 #1182", record.ToString());
    }

    [Fact]
    public void Parses4740_CompactSingleQuotedToXmlShape()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.AccountLockedOut4740, "DC01.contoso.test")!;

        Assert.Equal(4740, record.EventId);
        Assert.Equal(13824, record.Task);
        Assert.True(record.IsAuditSuccess);
        Assert.Equal("zofia.zak", record.Get("TargetUserName"));
        Assert.Equal("KRK-LT001", record.Get("TargetDomainName"));      // the caller computer
        Assert.Equal("S-1-5-21-3623811015-3361044348-30300820-1105", record.Get("TargetSid"));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 8, 6, 8, TimeSpan.Zero).AddTicks(5768875), record.TimeCreated);
    }

    [Fact]
    public void Parses4771_EmptyCertificateFieldsAreOmitted()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.KerberosPreAuthFailed4771, "dc02.contoso.test")!;

        Assert.Equal(4771, record.EventId);
        Assert.Equal("łukasz.wiśniewski", record.Get("TargetUserName"));
        Assert.Equal("0x18", record.Get("Status"));
        Assert.Equal("2", record.Get("PreAuthType"));
        Assert.Equal("::ffff:10.20.5.40", record.Get("IpAddress"));     // normalized by EventCodes, not by the parser
        Assert.Null(record.Get("CertIssuerName"));
        Assert.Equal(8, record.Data.Count);
    }

    [Fact]
    public void Parses4776()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.CredentialValidation4776, "dc01.contoso.test")!;

        Assert.Equal(4776, record.EventId);
        Assert.Equal(14336, record.Task);
        Assert.Equal("grażyna.dąbrowska", record.Get("TargetUserName"));
        Assert.Equal(@"\\WAW-PC002", record.Get("Workstation"));
        Assert.Equal("0xc000006a", record.Get("Status"));
        Assert.True(record.IsAuditFailure);
    }

    [Fact]
    public void Parses1102_TheChildrenOfUserData()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.LogCleared1102, "dc02.contoso.test")!;

        Assert.Equal("Microsoft-Windows-Eventlog", record.Provider);
        Assert.Equal(1102, record.EventId);
        Assert.Equal(EventLogLevels.Information, record.Level);
        Assert.Equal(104, record.Task);
        Assert.True(record.IsAuditSuccess);
        Assert.Equal(
            [
                new KeyValuePair<string, string>("SubjectUserSid", "S-1-5-21-3623811015-3361044348-30300820-1104"),
                new KeyValuePair<string, string>("SubjectUserName", "jan.kowalski"),
                new KeyValuePair<string, string>("SubjectDomainName", "CONTOSO"),
                new KeyValuePair<string, string>("SubjectLogonId", "0x55cd1d"),
            ],
            record.Data);
    }

    [Fact]
    public void ParsesGroupPolicyOperational_ActivityIdUserIdLevelAndOpcode()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.GroupPolicyUserProcessed8001, "WAW-PC001.contoso.test")!;

        Assert.Equal("Microsoft-Windows-GroupPolicy/Operational", record.Channel);
        Assert.Equal("Microsoft-Windows-GroupPolicy", record.Provider);
        Assert.Equal(8001, record.EventId);
        Assert.Equal(EventLogLevels.Information, record.Level);
        Assert.Equal(2, record.Opcode);
        Assert.Equal(Guid.Parse("5B1C3F2A-8E4D-4C0B-9A47-1D2E3F405162"), record.ActivityId);
        Assert.Equal("S-1-5-18", record.UserSid);
        Assert.Equal(0x4000000000000000, record.Keywords);
        Assert.False(record.IsAuditSuccess);
        Assert.False(record.IsAuditFailure);
        Assert.Equal(@"CONTOSO\jan.kowalski", record.Get("PrincipalSamName"));
        Assert.Equal("3", record.Get("PolicyElaspedTimeInSeconds"));
        Assert.Equal("0", record.Get("ErrorCode"));
    }

    [Fact]
    public void MultiLineValuesAndEscapedMarkupSurvive()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.GroupPolicyApplicable5312, "WAW-PC001.contoso.test")!;

        Assert.Equal("Domyślne zasady domeny\r\nWAW – Stacje robocze", record.Get("DescriptionString"));
        Assert.Equal("<GPO ID=\"{31B2F340-016D-11D2-945F-00C04FB984F9}\"><Name>Default Domain Policy</Name></GPO>", record.Get("GPOInfoList"));
        Assert.Equal(record.ActivityId, EventXmlParser.Parse(EventXmlFixtures.GroupPolicyUserProcessed8001, "x")!.ActivityId);
    }

    [Fact]
    public void ClassicRecord_QualifiersIgnored_UnnamedDataNumberedByPosition_BinarySkipped()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.ClassicApplication11707, "srv-pliki01.contoso.test")!;

        Assert.Equal(11707, record.EventId);
        Assert.Equal("MsiInstaller", record.Provider);
        Assert.Equal("Application", record.Channel);
        Assert.Equal("S-1-5-21-3623811015-3361044348-30300820-1104", record.UserSid);
        Assert.Equal(
            [
                new KeyValuePair<string, string>("Data1", "Produkt: Narzędzia administracyjne – instalacja zakończona pomyślnie."),
                new KeyValuePair<string, string>("Data2", "(NULL)"),
                new KeyValuePair<string, string>("Data5", "(NULL)"),    // Data3 "-" and Data4 empty omitted, numbering kept
            ],
            record.Data);
    }

    [Fact]
    public void RenderingInfoIsIgnored_DocumentedCodesDecoded()
    {
        var record = EventXmlParser.Parse(EventXmlFixtures.ForwardedWithRenderingInfo4624, "srv-pliki01.contoso.test")!;

        Assert.Equal(Guid.Empty, record.ActivityId);
        Assert.Equal(2, record.Version);
        Assert.Equal(["TargetUserName", "LogonType", "ImpersonationLevel", "ElevatedToken"], record.Data.Select(d => d.Key));
        Assert.Equal("%%1833", record.Get("ImpersonationLevel"));          // not in the decoder: kept as written
        Assert.DoesNotContain(record.Data, d => d.Value.Contains("pomyślnie", StringComparison.Ordinal));
    }

    [Fact]
    public void DecoderCodesAreDecoded()
    {
        var xml = EventLogSamples.SecurityXml("DC01.contoso.test", 4720, 7, EventLogSamples.Morning, success: true, task: 13824,
            [("TargetUserName", "zofia.zak"), ("PasswordLastSet", "%%1794"), ("LogonHours", "%%1793"), ("HomeDirectory", "-")]);

        var record = EventXmlParser.Parse(xml, "dc01")!;

        Assert.Equal(InsertionStringDecoder.Never, record.Get("PasswordLastSet"));
        Assert.Equal(InsertionStringDecoder.ValueNotSet, record.Get("LogonHours"));
        Assert.Null(record.Get("HomeDirectory"));
    }

    [Fact]
    public void PolishAndMarkupCharactersInValuesSurviveEscaping()
    {
        var xml = EventLogSamples.SecurityXml("DC01.contoso.test", 4738, 9, EventLogSamples.Morning, success: true, task: 13824,
            [("DisplayName", "Łukasz <Wiśniewski> & Żaneta \"Źdźbło\" 'ąćęłńóśźż'")]);

        Assert.Equal("Łukasz <Wiśniewski> & Żaneta \"Źdźbło\" 'ąćęłńóśźż'", EventXmlParser.Parse(xml, "dc01")!.Get("DisplayName"));
    }

    [Theory]
    [InlineData("x&#x1;")]
    [InlineData("x\u0001")]
    [InlineData("x&#x1F;")]
    [InlineData("x\uFFFE")]
    public void InvalidXmlCharactersInAValue_AreReplaced_TheRecordIsKept(string displayName)
    {
        var xml = EventLogSamples.SecurityXml("DC01.contoso.test", 4742, 9, EventLogSamples.Morning, success: true, task: 13825,
            [("TargetSid", "S-1-5-21-1-2-3-3101"), ("DisplayName", "ZASTĘPCZA"), ("AllowedToDelegateTo", "cifs/dc01.contoso.test")])
            .Replace("ZASTĘPCZA", displayName, StringComparison.Ordinal);

        var record = EventXmlParser.Parse(xml, "dc01")!;

        Assert.Equal("cifs/dc01.contoso.test", record.Get("AllowedToDelegateTo"));
        Assert.Equal("x\uFFFD", record.Get("DisplayName"));
        Assert.DoesNotContain(record.Data, d => d.Value.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')));
    }

    [Fact]
    public void AnUnpairedSurrogateReference_NeverThrows()
    {
        var xml = EventLogSamples.SecurityXml("DC01.contoso.test", 4742, 9, EventLogSamples.Morning, success: true, task: 13825,
            [("DisplayName", "ZASTĘPCZA")]).Replace("ZASTĘPCZA", "x&#xD800;y", StringComparison.Ordinal);

        if (EventXmlParser.TryParse(xml, "dc01", out var record))
        {
            Assert.DoesNotContain(record.Get("DisplayName")!, char.IsSurrogate);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<Event")]
    [InlineData("nie jest to XML")]
    [InlineData("<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4720</EventID></System></Event>")]
    [InlineData("<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4720</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"wczoraj\"/></System></Event>")]
    [InlineData("<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>-1</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"2026-11-03T06:00:00Z\"/></System></Event>")]
    [InlineData("<!DOCTYPE Event [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>&x;</EventID></System></Event>")]
    [InlineData("<!DOCTYPE Event [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><Event><System><EventID>&b;</EventID></System></Event>")]
    [InlineData("<Events><Event><System><EventID>1</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"2026-11-03T06:00:00Z\"/></System></Event></Events>")]
    [InlineData("<Event xmlns=\"urn:inna\"><System><EventID>1</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"2026-11-03T06:00:00Z\"/></System></Event>")]
    public void MalformedOrIncompleteRecords_YieldFalse(string xml)
    {
        Assert.False(EventXmlParser.TryParse(xml, "dc01", out var record));
        Assert.Null(record);
        Assert.Null(EventXmlParser.Parse(xml, "dc01"));
    }

    [Fact]
    public void Null_YieldsFalse() => Assert.False(EventXmlParser.TryParse(null, "dc01", out _));

    [Fact]
    public void ARecordWithoutNamespace_ComputerOrChannel_IsStillRead()
    {
        const string xml = "<Event><System><EventID>4624</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"2026-11-03T06:00:00Z\"/></System></Event>";

        var record = EventXmlParser.Parse(xml, "plik.evtx")!;

        Assert.Equal(4624, record.EventId);
        Assert.Equal(string.Empty, record.Computer);
        Assert.Equal(string.Empty, record.Channel);
        Assert.Null(record.Provider);
        Assert.Null(record.Level);
        Assert.Null(record.Task);
        Assert.Equal(0, record.Keywords);
        Assert.Empty(record.Data);
    }

    [Fact]
    public void OversizedInput_YieldsFalse()
    {
        var xml = EventXmlFixtures.AccountLockedOut4740.Replace("zofia.zak", new string('a', EventXmlParser.MaxCharacters), StringComparison.Ordinal);

        Assert.False(EventXmlParser.TryParse(xml, "dc01", out _));
    }

    [Fact]
    public void DeeplyNestedInput_YieldsFalse_WithoutRecursingIntoIt()
    {
        var nested = string.Concat(Enumerable.Repeat("<x>", 100_000)) + "a" + string.Concat(Enumerable.Repeat("</x>", 100_000));
        var xml = EventXmlFixtures.AccountLockedOut4740.Replace("<Data Name='TargetUserName'>zofia.zak</Data>",
            "<Data Name='TargetUserName'>" + nested + "</Data>", StringComparison.Ordinal);

        Assert.False(EventXmlParser.TryParse(xml, "dc01", out _));
    }

    [Fact]
    public void NestedUserData_IsFlattenedWithDottedNames()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="Microsoft-Windows-TerminalServices-LocalSessionManager"/><EventID>21</EventID><Version>0</Version>
                <Level>4</Level><EventRecordID>311</EventRecordID><TimeCreated SystemTime="2026-11-03T07:00:00.0000000Z"/>
                <Channel>Microsoft-Windows-TerminalServices-LocalSessionManager/Operational</Channel><Computer>SRV-APP02.contoso.test</Computer>
              </System>
              <UserData>
                <EventXML xmlns="Event_NS">
                  <User>CONTOSO\zofia.zak</User>
                  <SessionID>2</SessionID>
                  <Address>10.20.5.40</Address>
                  <Client><Name>KRK-LT001</Name><Build>26100</Build></Client>
                  <Empty/>
                </EventXML>
              </UserData>
            </Event>
            """;

        var record = EventXmlParser.Parse(xml, "srv-app02.contoso.test")!;

        Assert.Equal(
            [
                new KeyValuePair<string, string>("User", @"CONTOSO\zofia.zak"),
                new KeyValuePair<string, string>("SessionID", "2"),
                new KeyValuePair<string, string>("Address", "10.20.5.40"),
                new KeyValuePair<string, string>("Client.Name", "KRK-LT001"),
                new KeyValuePair<string, string>("Client.Build", "26100"),
            ],
            record.Data);
    }

    [Fact]
    public void EventDataWinsOverUserData()
    {
        const string xml = "<Event><System><EventID>1</EventID><EventRecordID>1</EventRecordID><TimeCreated SystemTime=\"2026-11-03T06:00:00Z\"/></System>"
            + "<EventData><Data Name=\"A\">1</Data></EventData><UserData><X><B>2</B></X></UserData></Event>";

        Assert.Equal([new KeyValuePair<string, string>("A", "1")], EventXmlParser.Parse(xml, "h")!.Data);
    }

    [Fact]
    public void HostIsRequired()
    {
        Assert.Throws<ArgumentException>(() => EventXmlParser.TryParse(EventXmlFixtures.AccountLockedOut4740, " ", out _));
        Assert.ThrowsAny<ArgumentException>(() => EventXmlParser.Parse(EventXmlFixtures.AccountLockedOut4740, null!));
    }

    [Theory]
    [InlineData("2015-08-20T16:22:02.759912000Z", 2015, 7599120)]
    [InlineData("2026-10-14T06:00:00Z", 2026, 0)]
    [InlineData("2026-10-14T06:00:00.5Z", 2026, 5000000)]
    [InlineData("2026-10-14T06:00:00.123456789123Z", 2026, 1234567)]
    public void SystemTime_MoreThanSevenFractionalDigitsAreTruncated(string value, int year, long fractionTicks)
    {
        Assert.True(EventXmlParser.TryParseSystemTime(value, out var utc));
        Assert.Equal(year, utc.Year);
        Assert.Equal(fractionTicks, utc.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
    }

    [Theory]
    [InlineData("2026-10-14T08:00:00+02:00", 6)]
    [InlineData("2026-10-14T06:00:00", 6)]
    public void SystemTime_IsUtc(string value, int hour)
    {
        Assert.True(EventXmlParser.TryParseSystemTime(value, out var utc));
        Assert.Equal(hour, utc.Hour);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wczoraj")]
    public void SystemTime_Invalid(string? value) => Assert.False(EventXmlParser.TryParseSystemTime(value, out _));

    [Fact]
    public void TheSamplesParse_AsTheirXml()
    {
        var records = EventLogSamples.LockoutScenario().Concat(EventLogSamples.FailedLogonScenario()).ToList();

        Assert.All(records, r => Assert.Equal(EventLogSamples.SecurityChannel, r.Channel));
        var lockout = Assert.Single(records, r => r.EventId == 4740);
        Assert.Equal(EventLogSamples.WawPc001, lockout.Get("TargetDomainName"));
        Assert.Equal(EventLogSamples.Sid(EventLogSamples.JanKowalski), lockout.Get("TargetSid"));
        var unknown = records.First(r => r.EventId == 4768 && r.Get("Status") == "0x6");
        Assert.Equal("0xffffffff", unknown.Get("TicketEncryptionType"));
        Assert.Null(unknown.Get("PreAuthType"));
        Assert.True(unknown.IsAuditFailure);
    }
}
