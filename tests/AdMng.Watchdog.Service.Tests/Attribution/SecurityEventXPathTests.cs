using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Attribution;

namespace AdMng.Watchdog.Service.Tests.Attribution;

/// <summary>Structured Event Log queries: values can never change the query (spec 11).</summary>
public sealed class SecurityEventXPathTests
{
    [Fact]
    public void Query_CombinesCodesRecordIdTimeAndData()
    {
        var xml = SecurityEventXPath.Build(new SecurityEventQuery
        {
            LogName = "Security",
            EventCodes = [4624, 4771],
            AfterRecordId = 1234,
            From = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.FromHours(2)),
            To = new DateTimeOffset(2026, 10, 14, 8, 5, 0, TimeSpan.Zero),
            DataEquals = [("TargetLogonId", "0x30dc2")],
        });

        Assert.Equal(
            "<QueryList><Query Id=\"0\" Path=\"Security\"><Select Path=\"Security\">*[System[(EventID=4624 or EventID=4771) and (EventRecordID &gt; 1234) and "
            + "TimeCreated[@SystemTime &gt;= '2026-10-14T06:00:00.000Z' and @SystemTime &lt;= '2026-10-14T08:05:00.000Z']]][EventData[Data[@Name='TargetLogonId']='0x30dc2']]"
            + "</Select></Query></QueryList>",
            xml);
    }

    [Fact]
    public void Suppress_FiltersNoiseAttributes()
    {
        var xml = SecurityEventXPath.Build(new SecurityEventQuery { LogName = "ForwardedEvents", EventCodes = [5136], SuppressAttributes = ["lastLogonTimestamp"] });

        Assert.Contains("<Suppress Path=\"ForwardedEvents\">*[EventData[Data[@Name='AttributeLDAPDisplayName']='lastLogonTimestamp']]</Suppress>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_AreQuotedAndEscaped()
    {
        var xml = SecurityEventXPath.Build(new SecurityEventQuery
        {
            LogName = "Security",
            DataEquals = [("TargetUserName", "o'brien<script>&")],
        });

        Assert.Contains("[EventData[Data[@Name='TargetUserName']=\"o'brien&lt;script&gt;&amp;\"]]", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Security", "Target'User", "x")]
    [InlineData("Security", "TargetUserName", "both ' and \" quotes")]
    [InlineData("Security\"]", "TargetUserName", "x")]
    public void Injection_IsRefused(string log, string field, string value)
    {
        Assert.Throws<ArgumentException>(() => SecurityEventXPath.Build(new SecurityEventQuery { LogName = log, DataEquals = [(field, value)] }));
    }

    [Fact]
    public async Task NullSource_IsUnavailable()
    {
        var source = new NullSecurityEventSource();

        Assert.False(source.IsAvailable);
        await Assert.ThrowsAsync<SecurityLogAccessException>(() => source.GetLogInfoAsync("dc01.contoso.test", "Security", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecurityLogAccessException>(async () =>
        {
            await foreach (var _ in source.ReadAsync("dc01.contoso.test", new SecurityEventQuery { LogName = "Security" }, TestContext.Current.CancellationToken))
            {
            }
        });
    }
}
