using System.Xml.Linq;
using AdMng.Core.EventLogs;

namespace AdMng.Core.Tests.EventLogs;

public class EventQueryXmlTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 3, 8, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset To = new(2026, 11, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void LockoutQuery_AllConditionsInOneSelect()
    {
        var xml = EventQueryXml.Build(new EventLogQuery
        {
            Channel = "Security",
            EventIds = [4740, 4771, 4776, 4625],
            Outcome = EventAuditOutcome.Failure,
            From = From,
            To = To,
            Data = [new EventDataCondition("TargetUserName", ["jan.kowalski"])],
        });

        var query = Assert.Single(XElement.Parse(xml).Elements("Query"));
        Assert.Equal("0", query.Attribute("Id")!.Value);
        Assert.Equal("Security", query.Attribute("Path")!.Value);
        var select = Assert.Single(query.Elements("Select"));
        Assert.Equal("Security", select.Attribute("Path")!.Value);
        Assert.Equal(
            "*[System[(EventID=4740 or EventID=4771 or EventID=4776 or EventID=4625) and band(Keywords,4503599627370496)"
            + " and TimeCreated[@SystemTime>='2026-11-03T07:00:00.000Z' and @SystemTime<='2026-11-03T09:30:00.000Z']]"
            + " and EventData[Data[@Name='TargetUserName']='jan.kowalski']]",
            select.Value);
        Assert.Contains("@SystemTime&gt;=", xml, StringComparison.Ordinal);
        Assert.Contains("@SystemTime&lt;=", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConditions_SelectsEverything()
    {
        var xml = EventQueryXml.Build(new EventLogQuery { Channel = "Microsoft-Windows-GroupPolicy/Operational" });

        var query = XElement.Parse(xml).Element("Query")!;
        Assert.Equal("Microsoft-Windows-GroupPolicy/Operational", query.Attribute("Path")!.Value);
        Assert.Equal("*", query.Element("Select")!.Value);
    }

    [Fact]
    public void MoreThanTwentyIds_AreSplitIntoSelectsOfAtMostTwenty_EachWithTheOtherConditions()
    {
        var ids = Enumerable.Range(4720, 45).ToList();
        var xml = EventQueryXml.Build(new EventLogQuery
        {
            Channel = "Security",
            EventIds = [.. ids, 4720, 4721],     // duplicates are dropped
            Levels = [EventLogLevels.LogAlways],
            From = From,
        });

        var selects = XElement.Parse(xml).Element("Query")!.Elements("Select").Select(s => s.Value).ToList();
        Assert.Equal(3, selects.Count);
        var counts = selects.Select(s => s.Split("EventID=").Length - 1).ToList();
        Assert.Equal([20, 20, 5], counts);
        Assert.All(selects, s => Assert.EndsWith("and (Level=0) and TimeCreated[@SystemTime>='2026-11-03T07:00:00.000Z']]]", s, StringComparison.Ordinal));
        Assert.StartsWith("*[System[(EventID=4720 or EventID=4721", selects[0], StringComparison.Ordinal);
        Assert.StartsWith("*[System[(EventID=4760 ", selects[2], StringComparison.Ordinal);
    }

    [Fact]
    public void LevelsProvidersAndSuccess()
    {
        var xml = EventQueryXml.Build(new EventLogQuery
        {
            Channel = "System",
            Levels = [EventLogLevels.Error, EventLogLevels.Warning],
            Providers = ["Service Control Manager", "Microsoft-Windows-Kerberos-Key-Distribution-Center"],
            Outcome = EventAuditOutcome.Success,
        });

        Assert.Equal(
            "*[System[(Level=2 or Level=3) and Provider[@Name='Service Control Manager' or @Name='Microsoft-Windows-Kerberos-Key-Distribution-Center']"
            + " and band(Keywords,9007199254740992)]]",
            XElement.Parse(xml).Element("Query")!.Element("Select")!.Value);
    }

    [Fact]
    public void DataValuesAreOred_ConditionsAnded()
    {
        var xml = EventQueryXml.Build(new EventLogQuery
        {
            Channel = "Security",
            Data =
            [
                new EventDataCondition("TargetUserName", ["zofia.zak", "Zofia.Zak", "zofia.zak"]),
                new EventDataCondition("IpAddress", ["::ffff:10.20.5.40"]),
            ],
        });

        Assert.Equal(
            "*[EventData[Data[@Name='TargetUserName']='zofia.zak' or Data[@Name='TargetUserName']='Zofia.Zak']"
            + " and EventData[Data[@Name='IpAddress']='::ffff:10.20.5.40']]",
            XElement.Parse(xml).Element("Query")!.Element("Select")!.Value);
    }

    [Theory]
    [InlineData("józef.żak", "'józef.żak'")]
    [InlineData("o'neill", "\"o'neill\"")]
    [InlineData("x' or '1'='1", "\"x' or '1'='1\"")]       // stays one literal – no injection
    [InlineData("x\"]|//*[\"", "'x\"]|//*[\"'")]
    [InlineData("<script>&amp;", "'<script>&amp;'")]
    public void DataValues_AreOneXPathLiteral_AndTheXmlIsEscaped(string value, string literal)
    {
        var xml = EventQueryXml.Build(new EventLogQuery { Channel = "Security", Data = [new EventDataCondition("TargetUserName", [value])] });

        Assert.Equal($"*[EventData[Data[@Name='TargetUserName']={literal}]]", XElement.Parse(xml).Element("Query")!.Element("Select")!.Value);
        Assert.DoesNotContain("<script>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("o'ne\"ill")]
    [InlineData("józef\u0001")]
    [InlineData("a\r\nb")]
    [InlineData("")]
    public void ValuesThatCannotBeALiteral_AreRejected_WithoutTheValueInTheMessage(string value)
    {
        var data = Assert.Throws<ArgumentException>(() =>
            EventQueryXml.Build(new EventLogQuery { Channel = "Security", Data = [new EventDataCondition("TargetUserName", [value])] }));
        var provider = Assert.Throws<ArgumentException>(() => EventQueryXml.Build(new EventLogQuery { Channel = "Security", Providers = [value] }));

        if (value.Length > 0)
        {
            Assert.DoesNotContain(value, data.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(value, provider.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TooLongValue_IsRejected() =>
        Assert.Throws<ArgumentException>(() => EventQueryXml.Build(new EventLogQuery
        {
            Channel = "Security",
            Data = [new EventDataCondition("TargetUserName", [new string('ż', XPathLiteral.MaxValueLength + 1)])],
        }));

    [Theory]
    [InlineData("Target']|//*['")]
    [InlineData("Łukasz")]
    [InlineData("a]b")]
    [InlineData("")]
    public void DataNamesOutsideTheAllowedCharacters_AreRejected(string name)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            EventQueryXml.Build(new EventLogQuery { Channel = "Security", Data = [new EventDataCondition(name, ["x"])] }));
        if (name.Length > 0)
        {
            Assert.DoesNotContain(name, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ADataConditionWithoutValues_IsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            EventQueryXml.Build(new EventLogQuery { Channel = "Security", Data = [new EventDataCondition("TargetUserName", [])] }));

    [Theory]
    [InlineData("Security")]
    [InlineData("Directory Service")]
    [InlineData("Microsoft-Windows-TerminalServices-LocalSessionManager/Operational")]
    [InlineData("Microsoft-Windows-Windows Defender/Operational")]
    [InlineData("ForwardedEvents")]
    public void Channels(string channel) => Assert.True(EventQueryXml.IsAllowedChannel(channel));

    [Theory]
    [InlineData("")]
    [InlineData(" Security")]
    [InlineData(@"file://\\atakujący\udział\log.evtx")]
    [InlineData(@"C:\Windows\System32\winevt\Logs\Security.evtx")]
    [InlineData("Security\"><Select Path=\"System")]
    [InlineData("Zabezpieczenia–dziennik")]
    [InlineData("Security\n")]
    public void ChannelsThatAreNotChannelNames_AreRejected(string channel)
    {
        Assert.False(EventQueryXml.IsAllowedChannel(channel));
        Assert.Throws<ArgumentException>(() => EventQueryXml.Build(new EventLogQuery { Channel = channel }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void EventIdsOutsideTheRange_AreRejected(int id) =>
        Assert.Throws<ArgumentException>(() => EventQueryXml.Build(new EventLogQuery { Channel = "Security", EventIds = [id] }));

    [Fact]
    public void FromAfterTo_IsRejected() =>
        Assert.Throws<ArgumentException>(() => EventQueryXml.Build(new EventLogQuery { Channel = "Security", From = To, To = From.AddMinutes(-1) }));

    [Fact]
    public void TimeBounds_AreWidenedToWholeMilliseconds()
    {
        var from = new DateTimeOffset(2026, 11, 3, 8, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);
        var to = new DateTimeOffset(2026, 11, 3, 9, 0, 0, 999, TimeSpan.Zero).AddTicks(1);

        var xml = EventQueryXml.Build(new EventLogQuery { Channel = "Security", From = from, To = to });

        Assert.Equal("*[System[TimeCreated[@SystemTime>='2026-11-03T08:00:00.123Z' and @SystemTime<='2026-11-03T09:00:01.000Z']]]",
            XElement.Parse(xml).Element("Query")!.Element("Select")!.Value);
        var (lower, upper) = EventQueryXml.TimeBounds(from, to);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 8, 0, 0, 123, TimeSpan.Zero), lower);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 1, 0, TimeSpan.Zero), upper);
        var (none, nothing) = EventQueryXml.TimeBounds(null, null);
        Assert.Null(none);
        Assert.Null(nothing);
        Assert.Equal(To, EventQueryXml.TimeBounds(null, To).To);       // whole milliseconds stay
    }

    [Fact]
    public void RawXPath_ReplacesTheGeneratedSelection()
    {
        const string raw = "*[System[(EventID=4625) and TimeCreated[timediff(@SystemTime) <= 86400000]] and EventData[Data[@Name='TargetUserName']='zofia.żak']]";

        var xml = EventQueryXml.Build(new EventLogQuery
        {
            Channel = "Security",
            EventIds = [4740],
            From = From,
            Data = [new EventDataCondition("TargetUserName", ["jan.kowalski"])],
            RawXPath = "  " + raw + "\n",
        });

        var select = Assert.Single(XElement.Parse(xml).Element("Query")!.Elements("Select"));
        Assert.Equal(raw, select.Value);
        Assert.Contains("&lt;= 86400000", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void RawXPath_ThatFailsTheCheck_IsRejectedWithoutTheText()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            EventQueryXml.Build(new EventLogQuery { Channel = "Security", RawXPath = "*[System[EventID=4625]] | //Tajne" }));

        Assert.DoesNotContain("Tajne", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*[System[EventID=4740]]")]
    [InlineData("Event[System[EventID=4740]]")]
    [InlineData("*[System[(EventID=4771 or EventID=4776) and (Level=0 or Level=4)]]")]
    [InlineData("*[System[Provider[@Name='Microsoft-Windows-Security-Auditing'] and band(Keywords,4503599627370496)]]")]
    [InlineData("*[System[TimeCreated[@SystemTime>='2026-11-03T00:00:00.000Z' and @SystemTime<='2026-11-03T23:59:59.999Z']]]")]
    [InlineData("*[System[TimeCreated[timediff(@SystemTime) <= 3600000]]]")]
    [InlineData("*[EventData[Data[@Name='TargetUserName']=\"o'neill\" and Data[@Name='Status']!='0x0']]")]
    [InlineData("*[UserData[LogFileCleared[SubjectUserName='jan.kowalski']]]")]
    [InlineData("*[System/EventID=1102]")]
    [InlineData("*[System[Provider/@Name='EventLog']]")]
    [InlineData("*[System[not(EventID=4624) and Band(Keywords, 0x10000000000000)]]")]
    [InlineData("*[EventData[Data[@Name='LogonType']>=10 and Data[@Name='KeyLength']<128.5]]")]
    [InlineData("*[System[(EventID=4625)]]\r\n")]
    public void RawXPath_Accepted(string xpath)
    {
        Assert.True(EventQueryXml.IsValidRawXPath(xpath, out var reason), reason);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("", EventQueryXml.RawXPathReasons.Empty)]
    [InlineData("   ", EventQueryXml.RawXPathReasons.Empty)]
    [InlineData("*[System[EventID=4740]", EventQueryXml.RawXPathReasons.Unbalanced)]
    [InlineData("*[System[(EventID=4740]]", EventQueryXml.RawXPathReasons.Unbalanced)]
    [InlineData("*[System[EventID=4740]])", EventQueryXml.RawXPathReasons.Unbalanced)]
    [InlineData("*[EventData[Data='abc]]", EventQueryXml.RawXPathReasons.UnterminatedLiteral)]
    [InlineData("*[System[EventID=4740]] | *[System[EventID=4741]]", EventQueryXml.RawXPathReasons.UnsupportedPath)]
    [InlineData("//Event", EventQueryXml.RawXPathReasons.UnsupportedPath)]
    [InlineData("*[System/descendant::EventID=1]", EventQueryXml.RawXPathReasons.UnsupportedPath)]
    [InlineData("*[../System]", EventQueryXml.RawXPathReasons.UnsupportedPath)]
    [InlineData("*[System[@*='x']]", EventQueryXml.RawXPathReasons.UnsupportedPath)]
    [InlineData("*[EventData[contains(Data, 'admin')]]", EventQueryXml.RawXPathReasons.UnsupportedFunction)]
    [InlineData("*[document('http://atakujący/x')]", EventQueryXml.RawXPathReasons.UnsupportedFunction)]
    [InlineData("*[System[position()=1]]", EventQueryXml.RawXPathReasons.UnsupportedFunction)]
    [InlineData("*[System[EventID=$id]]", EventQueryXml.RawXPathReasons.InvalidCharacter)]
    [InlineData("*[EventData[Data='a\u0001b']]", EventQueryXml.RawXPathReasons.InvalidCharacter)]
    [InlineData("*[System[EventID=4740]]\u0000", EventQueryXml.RawXPathReasons.InvalidCharacter)]
    [InlineData("*[Zdarzenie[Użytkownik='x']]", EventQueryXml.RawXPathReasons.InvalidCharacter)]
    [InlineData("System[EventID=4740]", EventQueryXml.RawXPathReasons.NotAnEventSelection)]
    [InlineData("EventID=4740", EventQueryXml.RawXPathReasons.NotAnEventSelection)]
    [InlineData("'tekst'", EventQueryXml.RawXPathReasons.NotAnEventSelection)]
    [InlineData("*[System[EventID=4740]] *[System[EventID=4741]]", EventQueryXml.RawXPathReasons.NotAnEventSelection)]
    [InlineData("*[System[EventID=4740]] and *", EventQueryXml.RawXPathReasons.NotAnEventSelection)]
    [InlineData("*[System[EventID==4740]]", EventQueryXml.RawXPathReasons.Syntax)]
    [InlineData("*[System[EventID=4740 and]]", EventQueryXml.RawXPathReasons.Syntax)]
    [InlineData("*[System[EventID=1=1]]", EventQueryXml.RawXPathReasons.Syntax)]
    [InlineData("*[System[EventID=0x]]", EventQueryXml.RawXPathReasons.Syntax)]
    [InlineData("*[]", EventQueryXml.RawXPathReasons.Syntax)]
    [InlineData("*[System[EventID=4740]]/@Name/x", EventQueryXml.RawXPathReasons.UnsupportedPath)]
    public void RawXPath_Rejected(string xpath, string expected)
    {
        Assert.False(EventQueryXml.IsValidRawXPath(xpath, out var reason));
        Assert.Equal(expected, reason);
        Assert.Contains(reason!, EventQueryXml.RawXPathReasons.All);
    }

    [Fact]
    public void RawXPath_TooLongOrTooDeep()
    {
        Assert.False(EventQueryXml.IsValidRawXPath("*[" + new string(' ', EventQueryXml.MaxRawXPathLength) + "]", out var tooLong));
        Assert.Equal(EventQueryXml.RawXPathReasons.TooLong, tooLong);

        var deep = "*" + string.Concat(Enumerable.Repeat("[System", 40)) + new string(']', 40);
        Assert.False(EventQueryXml.IsValidRawXPath(deep, out var tooDeep));
        Assert.Equal(EventQueryXml.RawXPathReasons.TooDeep, tooDeep);

        var parentheses = "*[" + new string('(', 40) + "EventID=1" + new string(')', 40) + "]";
        Assert.False(EventQueryXml.IsValidRawXPath(parentheses, out var tooDeepToo));
        Assert.Equal(EventQueryXml.RawXPathReasons.TooDeep, tooDeepToo);
    }

    [Fact]
    public void RawXPath_Null() => Assert.False(EventQueryXml.IsValidRawXPath(null, out _));

    [Fact]
    public void RawXPathReasons_AreDistinctStableKeys()
    {
        Assert.Equal(EventQueryXml.RawXPathReasons.All.Count, EventQueryXml.RawXPathReasons.All.Distinct().Count());
        Assert.All(EventQueryXml.RawXPathReasons.All, key => Assert.StartsWith("EventLogs.RawXPath.", key, StringComparison.Ordinal));
        Assert.Equal("EventLogs.RawXPath.Unbalanced", EventQueryXml.RawXPathReasons.Unbalanced);
    }
}
