using System.Xml.Linq;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class SecurityEventQueryBuilderTests
{
    [Fact]
    public void Ingestion_EventIdsInSelectsOfAtMost20_WithTheRecordCursorAndSuppressions()
    {
        var xml = SecurityEventQueryBuilder.Build(new SecurityEventQuery
        {
            LogName = "Security",
            EventCodes = SecurityEventCatalog.IngestionSet,
            AfterRecordId = 12345,
            SuppressAttributes = ["dSCorePropagationData", "lastLogonTimestamp"],
        });

        var root = XElement.Parse(xml);
        var query = Assert.Single(root.Elements("Query"));
        Assert.Equal("Security", query.Attribute("Path")!.Value);
        var selects = query.Elements("Select").ToList();
        Assert.Equal((SecurityEventCatalog.IngestionSet.Count + 19) / 20, selects.Count);
        Assert.All(selects, s =>
        {
            Assert.Equal("Security", s.Attribute("Path")!.Value);
            Assert.InRange(s.Value.Split(" or ").Length, 1, SecurityEventQueryBuilder.MaxEventIdsPerSelect);
            Assert.EndsWith("and EventRecordID > 12345]]", s.Value, StringComparison.Ordinal);
        });
        Assert.StartsWith("*[System[(EventID=4720 or EventID=4722", selects[0].Value, StringComparison.Ordinal);
        var suppress = query.Elements("Suppress").Select(s => s.Value).ToList();
        Assert.Equal(
        [
            "*[System[EventID=5136] and EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]",
            "*[System[EventID=5136] and EventData[Data[@Name='AttributeLDAPDisplayName']='lastLogonTimestamp']]",
        ], suppress);
        Assert.Contains("EventRecordID &gt; 12345", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void OnDemandLookup_TimeBoundsAndDataFilter()
    {
        var xml = SecurityEventQueryBuilder.Build(new SecurityEventQuery
        {
            LogName = "Security",
            EventCodes = [4624],
            From = new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.FromHours(2)),
            To = new DateTimeOffset(2026, 10, 14, 5, 30, 0, TimeSpan.Zero),
            DataEquals = [("TargetLogonId", "0x30dc2")],
            NewestFirst = true,
            MaxRecords = 1,
        });

        var select = XElement.Parse(xml).Element("Query")!.Element("Select")!.Value;
        Assert.Equal(
            "*[System[(EventID=4624) and TimeCreated[@SystemTime>='2026-10-14T05:00:00.0000000Z'] and TimeCreated[@SystemTime<='2026-10-14T05:30:00.0000000Z']]"
            + " and EventData[Data[@Name='TargetLogonId']='0x30dc2']]",
            select);
        Assert.Contains("&gt;=", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConditions_SelectsEverything()
    {
        var xml = SecurityEventQueryBuilder.Build(new SecurityEventQuery { LogName = "ForwardedEvents" });

        Assert.Equal("*", XElement.Parse(xml).Element("Query")!.Element("Select")!.Value);
        Assert.Contains("Path=\"ForwardedEvents\"", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0x1' or '1'='1")]
    [InlineData("a]b")]
    [InlineData("Łukasz")]
    [InlineData("x\"y")]
    [InlineData("")]
    public void ValuesOutsideTheAllowedCharacters_AreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => SecurityEventQueryBuilder.Build(new SecurityEventQuery { LogName = "Security", DataEquals = [("TargetUserName", value)] }));
        Assert.Throws<ArgumentException>(() => SecurityEventQueryBuilder.Build(new SecurityEventQuery { LogName = "Security", SuppressAttributes = [value] }));
        Assert.Throws<ArgumentException>(() => SecurityEventQueryBuilder.Build(new SecurityEventQuery { LogName = value }));
    }

    [Theory]
    [InlineData("jkowalska")]
    [InlineData("WS-KRK-0042$")]
    [InlineData("{4FE80A66-5F93-4F73-B215-68678058E613}")]
    [InlineData("S-1-5-21-1-2-3-1104")]
    [InlineData("Jan Kowalski")]
    [InlineData("10.0.0.15")]
    public void TypicalValues_AreAllowed(string value) => Assert.True(SecurityEventQueryBuilder.IsAllowedValue(value));
}
