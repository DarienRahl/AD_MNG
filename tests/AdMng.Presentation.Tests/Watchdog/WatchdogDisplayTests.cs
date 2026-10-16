using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>Pure display helpers of the tab: health codes, durations, DN and SID checks, edit values.</summary>
public sealed class WatchdogDisplayTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Theory]
    [InlineData("directory.connection", "directory.connection")]
    [InlineData("eventlog.dc02.contoso.test", "eventlog")]
    [InlineData("eventlog.dc02.contoso.test.foreignRecords", "eventlog.foreignRecords")]
    [InlineData("permission.deletedObjects", "permission.deletedObjects")]
    [InlineData("permission.somethingNew", "permission")]
    [InlineData("channel.teams-it", "channel")]
    [InlineData("storage.free", "storage.free")]
    [InlineData("future.check", null)]
    public void HealthPrefix_IsTheLongestKnownPrefix(string code, string? expected) =>
        Assert.Equal(expected, WatchdogDisplay.HealthPrefix(code));

    [Fact]
    public void HealthText_PutsTheSubjectOfTheCodeIntoTheText()
    {
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal("Dziennik zabezpieczeń dc02.contoso.test", localizer.HealthText("eventlog.dc02.contoso.test"));
        Assert.Equal("Obce rekordy w dzienniku dc02.contoso.test", localizer.HealthText("eventlog.dc02.contoso.test.foreignRecords"));
        Assert.Equal("Odczyt kontenera Deleted Objects", localizer.HealthText("permission.deletedObjects"));
        Assert.StartsWith("Kolektor przekazuje zdarzenia", localizer.HealthRemedy("eventlog.dc02.contoso.test.foreignRecords"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(840, "840 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1,0 s")]
    [InlineData(12_345, "12,3 s")]
    public void FormatDuration_MillisecondsBelowOneSecond(int milliseconds, string expected) =>
        Assert.Equal(expected, WatchdogDisplay.FormatDuration(TimeSpan.FromMilliseconds(milliseconds), Polish));

    [Theory]
    [InlineData("OU=Kraków,OU=Firma,DC=contoso,DC=test", true)]
    [InlineData("CN=Kowalski\\, Jan,OU=IT,DC=contoso,DC=test", true)]
    [InlineData("to nie jest DN", false)]
    [InlineData("OU Kraków", false)]
    public void Dn_IsCheckedWithTheDistinguishedNameParser(string text, bool valid) =>
        Assert.Equal(valid, WatchdogDn.IsValid(text));

    [Fact]
    public void DnLines_SkipEmptyLines_AndReportInvalidOnes()
    {
        var text = "OU=Kraków,OU=Firma,DC=contoso,DC=test\r\n\r\n  zły wpis \nOU=Gdańsk,OU=Firma,DC=contoso,DC=test";

        Assert.Equal(["OU=Kraków,OU=Firma,DC=contoso,DC=test", "zły wpis", "OU=Gdańsk,OU=Firma,DC=contoso,DC=test"], WatchdogDn.Lines(text));
        Assert.Equal(["zły wpis"], WatchdogDn.InvalidLines(text));
        Assert.Empty(WatchdogDn.Lines(null));
    }

    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1105", true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("s-1-5-18", false)]
    [InlineData("CONTOSO\\jan.kowalski", false)]
    [InlineData("S-1-5-21-abc", false)]
    [InlineData("", false)]
    public void Sid_AcceptsSddlSidsOnly(string text, bool valid) =>
        Assert.Equal(valid, WatchdogSid.IsValid(text));

    [Fact]
    public void EditValues_KeepTheStoredOrder_ThenAppendNewOnes()
    {
        Assert.Equal(["helpdesk", "it-admins", "kierownicy"], WatchdogEditValues.KeepOrder(["helpdesk", "usunieta", "it-admins"], ["kierownicy", "it-admins", "helpdesk", "kierownicy"]));
        Assert.Equal(["B"], WatchdogEditValues.KeepOrder(["b"], ["B"], StringComparer.OrdinalIgnoreCase).Select(s => s.ToUpperInvariant()));
        Assert.Empty(WatchdogEditValues.KeepOrder<string>(["a"], []));
    }

    [Fact]
    public void EditValues_KeepTheStoredPrecision_WhileTheTextIsUnchanged()
    {
        var stored = TimeSpan.FromSeconds(90);
        static string Minutes(TimeSpan t) => ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        static TimeSpan Parse(string t) => TimeSpan.FromMinutes(int.Parse(t, CultureInfo.InvariantCulture));

        Assert.Equal(stored, WatchdogEditValues.KeepUnchanged(stored, " 1 ", Minutes, Parse));
        Assert.Equal(TimeSpan.FromMinutes(2), WatchdogEditValues.KeepUnchanged(stored, "2", Minutes, Parse));
    }

    [Fact]
    public void StageAndChannelNames_AreLocalized()
    {
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal("Odpowiedź HTTP", localizer.StageName(DeliveryStage.HttpResponse));
        Assert.NotEqual(nameof(DeliveryStage.Authenticate), localizer.StageName(DeliveryStage.Authenticate));
        Assert.Equal("E-mail", localizer.ChannelTypeName(DeliveryChannelType.Email));
        Assert.Equal("Powiadomienie", localizer.DeliveryKindName(DeliveryKind.Alert));
    }
}
