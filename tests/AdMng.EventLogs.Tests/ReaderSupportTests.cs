using System.Xml.Linq;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs.Tests;

public class ReaderSupportTests
{
    private const string StructuredQuery =
        "<QueryList><Query Id=\"0\" Path=\"Application\">"
        + "<Select Path=\"Application\">*[System[(EventID=4740 or EventID=4771) and TimeCreated[@SystemTime&gt;='2026-11-02T07:30:00.000Z']]]</Select>"
        + "<Select Path=\"Application\">*[System[EventID=4625] and EventData[Data[@Name='TargetUserName']='żaneta.wiśniewska']]</Select>"
        + "<Suppress Path=\"Application\">*[System[EventID=4625] and EventData[Data[@Name='TargetUserName']='ANONYMOUS LOGON']]</Suppress>"
        + "</Query></QueryList>";

    [Fact]
    public void ForFile_PointsEveryPathAtTheFile_AndKeepsTheSelections()
    {
        const string file = @"C:\Logi\Archiwum 'DC01' & kopia\Dziennik zabezpieczeń.evtx";

        var query = XDocument.Parse(ReaderSupport.ForFile(StructuredQuery, file));
        var original = XDocument.Parse(StructuredQuery);

        var paths = query.Descendants().Where(e => e.Attribute("Path") is not null).Select(e => (string)e.Attribute("Path")!).ToList();
        Assert.Equal(4, paths.Count);
        Assert.All(paths, path => Assert.Equal("file://" + file, path));
        Assert.Equal(
            original.Descendants().Where(e => e.Name.LocalName is "Select" or "Suppress").Select(e => e.Value),
            query.Descendants().Where(e => e.Name.LocalName is "Select" or "Suppress").Select(e => e.Value));
    }

    [Fact]
    public void ForFile_AddsAMissingPath()
    {
        var query = XDocument.Parse(ReaderSupport.ForFile("<QueryList><Query Id=\"0\"><Select>*</Select></Query></QueryList>", "/srv/logi/dc02.evtx"));

        Assert.Equal("file:///srv/logi/dc02.evtx", (string?)query.Descendants("Select").Single().Attribute("Path"));
        Assert.Equal("file:///srv/logi/dc02.evtx", (string?)query.Descendants("Query").Single().Attribute("Path"));
    }

    [Theory]
    [InlineData("*[System[(EventID=4740)]]")]
    [InlineData("  *")]
    public void ForFile_LeavesABareXPathAlone(string query) =>
        Assert.Equal(query, ReaderSupport.ForFile(query, @"C:\Logi\dc01.evtx"));

    [Theory]
    [InlineData("<Query><Select Path=\"Security\">*</Select></Query>")]
    [InlineData("<!DOCTYPE QueryList [<!ENTITY x \"y\">]><QueryList><Query Id=\"0\"><Select Path=\"Security\">&x;</Select></Query></QueryList>")]
    [InlineData("<QueryList><Query Id=\"0\"><Select Path=\"Security\">*</Select>")]
    public void ForFile_RefusesOtherXml_SoNoFileQueryReachesAChannel(string query)
    {
        var error = Assert.Throws<EventLogAccessException>(() => ReaderSupport.ForFile(query, @"C:\Logi\dc01.evtx"));
        Assert.Equal(EventLogAccessError.InvalidQuery, error.Error);
    }

    [Fact]
    public void RecordQuery_IsNumbersOnly()
    {
        Assert.Equal("*[System[(EventRecordID=4740123)]]", ReaderSupport.RecordQuery(4740123));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderSupport.RecordQuery(-1));
    }

    [Fact]
    public void Channels_DropAnalyticAndDebug_DistinctAndSorted()
    {
        var channels = ReaderSupport.Channels(
        [
            "System",
            "Security",
            "Microsoft-Windows-GroupPolicy/Operational",
            "Microsoft-Windows-GroupPolicy/Debug",
            "Microsoft-Windows-Kerberos/Analytic",
            "security",
            null,
            " ",
            "Application",
        ]);

        Assert.Equal(["Application", "Microsoft-Windows-GroupPolicy/Operational", "Security", "System"], channels);
    }

    [Theory]
    [InlineData(@"C:\Logi\Dziennik zabezpieczeń – DC01.evtx", "Dziennik zabezpieczeń – DC01.evtx")]
    [InlineData(@"\\fs01.contoso.test\logi$\dc02.evtx", "dc02.evtx")]
    [InlineData("/srv/logi/łódź-dc03.evtx", "łódź-dc03.evtx")]
    [InlineData("archiwum.evtx", "archiwum.evtx")]
    public void FileHost_IsTheFileName(string path, string expected) => Assert.Equal(expected, ReaderSupport.FileHost(path));

    [Fact]
    public void FilesHost_JoinsTheNames() =>
        Assert.Equal("dc01.evtx, dc02.evtx", ReaderSupport.FilesHost([@"C:\Logi\dc01.evtx", @"D:\Kopie\dc02.evtx"]));

    [Theory]
    [InlineData(0, EventLogMode.Circular)]
    [InlineData(1, EventLogMode.AutoBackup)]
    [InlineData(2, EventLogMode.Retain)]
    [InlineData(3, EventLogMode.Unknown)]
    [InlineData(null, EventLogMode.Unknown)]
    public void Mode_FromTheNativeValue(int? native, EventLogMode expected) => Assert.Equal(expected, ReaderSupport.Mode(native));

    [Fact]
    public void ToUtc_LocalAndUtcTimes()
    {
        var utc = new DateTime(2026, 11, 3, 7, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTimeOffset(utc), ReaderSupport.ToUtc(utc));
        Assert.Equal(new DateTimeOffset(utc), ReaderSupport.ToUtc(utc.ToLocalTime()));
        Assert.Equal(TimeSpan.Zero, ReaderSupport.ToUtc(utc.ToLocalTime())!.Value.Offset);
        Assert.Null(ReaderSupport.ToUtc(null));
    }

    [Fact]
    public void SameEvent_NeedsTheIdComputerProviderAndTime()
    {
        var shown = Samples.Record("dc01.contoso.test", 912345, Samples.Morning);

        Assert.True(ReaderSupport.SameEvent(4740, "Microsoft-Windows-Security-Auditing", "dc01.CONTOSO.test", Samples.Morning.AddMilliseconds(400), shown));
        Assert.True(ReaderSupport.SameEvent(4740, null, null, Samples.Morning, shown));
        Assert.False(ReaderSupport.SameEvent(4771, "Microsoft-Windows-Security-Auditing", "DC01.contoso.test", Samples.Morning, shown));
        Assert.False(ReaderSupport.SameEvent(4740, "Microsoft-Windows-Eventlog", "DC01.contoso.test", Samples.Morning, shown));
        Assert.False(ReaderSupport.SameEvent(4740, "Microsoft-Windows-Security-Auditing", "DC02.contoso.test", Samples.Morning, shown));
        Assert.False(ReaderSupport.SameEvent(4740, "Microsoft-Windows-Security-Auditing", "DC01.contoso.test", Samples.Morning.AddSeconds(3), shown));
        Assert.False(ReaderSupport.SameEvent(4740, "Microsoft-Windows-Security-Auditing", "DC01.contoso.test", null, shown));
    }

    [Fact]
    public void Aggregates_OfOptionalValues()
    {
        var early = Samples.Morning;
        var late = Samples.Morning.AddHours(6);

        Assert.Equal(early, ReaderSupport.Earliest(late, early));
        Assert.Equal(late, ReaderSupport.Latest(late, early));
        Assert.Equal(early, ReaderSupport.Earliest(null, early));
        Assert.Equal(late, ReaderSupport.Latest(late, null));
        Assert.Null(ReaderSupport.Latest(null, null));
        Assert.Equal(30L, ReaderSupport.Sum([10, null, 20]));
        Assert.Null(ReaderSupport.Sum([null, null]));
    }
}
