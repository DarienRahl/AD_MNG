using System.Text;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;

namespace AdMng.Watchdog.Core.Tests.Attribution.Collector;

/// <summary>RawJson lines of the pinned OpenWEC (stage B design 7.6): the metadata, the raw event, malformed and partial
/// lines.</summary>
public sealed class WecRecordReaderTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 20, 8, 15, 31, 123, TimeSpan.Zero);

    // One line as OpenWEC 86de07f writes it (raw_json.rs), Polish diacritics in the event kept as UTF-8.
    private const string UpstreamLine =
        """{"meta":{"IpAddress":"10.0.1.10","TimeReceived":"2026-10-20T08:15:31.123000000+00:00","Principal":"DC01$@CONTOSO.TEST","Client":"DC01$@CONTOSO.TEST","Subscription":{"Uuid":"4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10","Version":"d2fa1803-b284-f1b2-d2fa-1803b284f1b2","Name":"admng-dc-security","Uri":"/wsman/SubscriptionManager/WEC","ClientRevision":"1","ServerRevision":"2"},"Node":"wec-1"},"data":"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>4724</EventID><Computer>DC01.contoso.test</Computer></System><EventData><Data Name='TargetUserName'>łucja.żak</Data></EventData></Event>"}""";

    [Fact]
    public void AnUpstreamLine_GivesTheMetadataAndTheEvent()
    {
        var record = WecRecordReader.Read(Encoding.UTF8.GetBytes(UpstreamLine), out var resynchronized);

        Assert.NotNull(record);
        Assert.False(resynchronized);
        Assert.Equal("10.0.1.10", record.IpAddress);
        Assert.Equal(Received, record.TimeReceived);
        Assert.Equal("DC01$@CONTOSO.TEST", record.Client);
        Assert.Equal("DC01$@CONTOSO.TEST", record.EffectiveClient);
        Assert.Equal(WecRawJson.SubscriptionUuid, record.SubscriptionUuid);
        Assert.Equal("admng-dc-security", record.SubscriptionName);
        Assert.Contains("łucja.żak", record.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixtureWriter_ProducesWhatTheReaderReads()
    {
        var xml = WindowsEventXml.LogCleared(5, Received, "DC01.contoso.test", "S-1-5-21-1-2-3-500", "adm\"x\\y", "0x3e7");
        var line = WecRawJson.Line(xml, "DC01$@CONTOSO.TEST", "10.0.1.10", Received.AddTicks(4567));

        var record = WecRecordReader.Read(Encoding.UTF8.GetBytes(line.TrimEnd('\n')));

        Assert.Equal(xml, record!.Data);
        Assert.Equal(Received.AddTicks(4567), record.TimeReceived);
        Assert.Equal("2026-10-20T08:15:31.123456700+00:00", WecRawJson.Rfc3339(Received.AddTicks(4567)));
    }

    [Fact]
    public void Principal_IsTheFallbackOnlyWithoutClient()
    {
        var line = """{"meta":{"IpAddress":"10.0.1.10","Principal":"DC02$@CONTOSO.TEST","Subscription":{"Uuid":"4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"}},"data":"<Event/>"}""";

        var record = WecRecordReader.Read(Encoding.UTF8.GetBytes(line))!;

        Assert.Null(record.Client);
        Assert.Equal("DC02$@CONTOSO.TEST", record.EffectiveClient);
        Assert.Null(record.TimeReceived);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"meta":{}}""")]                                         // no data
    [InlineData("""{"meta":{},"data":""}""")]                               // empty event
    [InlineData("""{"meta":{},"data":42}""")]                               // data of another type
    [InlineData("""{"meta":{"Client":5},"data":"<Event/>"}""")]             // metadata of another type
    [InlineData("""[{"meta":{},"data":"<Event/>"}]""")]                     // not an object
    [InlineData("""{"meta":{},"data":"<Event/>"} trailing""")]               // trailing content
    [InlineData("""{"meta":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":1}}}}}}}},"data":"<Event/>"}""")]   // deeper than 8
    public void AMalformedLine_IsNoRecord(string line) => Assert.Null(WecRecordReader.Read(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void APartialRecordFollowedByAWholeOne_IsResynchronizedAtTheLastRecordStart()
    {
        // openwecd died in the middle of a write; after the restart its next record was appended to the partial line.
        var whole = UpstreamLine;
        var partial = whole[..137];

        var record = WecRecordReader.Read(Encoding.UTF8.GetBytes(partial + whole), out var resynchronized);

        Assert.True(resynchronized);
        Assert.Equal("DC01$@CONTOSO.TEST", record!.Client);
        Assert.StartsWith(WecRecordReader.RecordStart, whole, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidSubscriptionUuid_IsKeptAsMissing()
    {
        var line = """{"meta":{"Client":"DC01$@CONTOSO.TEST","Subscription":{"Uuid":"nie-uuid"}},"data":"<Event/>"}""";

        Assert.Null(WecRecordReader.Read(Encoding.UTF8.GetBytes(line))!.SubscriptionUuid);
    }
}
