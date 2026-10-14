using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class BusinessCalendarTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneResolver.Resolve("Europe/Warsaw");

    private static readonly BusinessCalendar Default = new(new BusinessHoursSettings(), Warsaw);

    [Theory]
    [InlineData("2026-10-14T05:00:00Z", true)]    // Wednesday 07:00 CEST – start is inclusive
    [InlineData("2026-10-14T04:59:59Z", false)]
    [InlineData("2026-10-14T14:59:59Z", true)]
    [InlineData("2026-10-14T15:00:00Z", false)]   // 17:00 CEST – end is exclusive
    [InlineData("2026-10-17T08:00:00Z", false)]   // Saturday
    [InlineData("2026-11-11T08:00:00Z", false)]   // Narodowe Święto Niepodległości
    [InlineData("2026-12-24T08:00:00Z", false)]   // Wigilia (since 2025)
    [InlineData("2026-12-23T07:00:00Z", true)]    // 08:00 CET
    [InlineData("2026-12-23T06:00:00Z", true)]    // 07:00 CET
    [InlineData("2026-12-23T05:59:00Z", false)]
    public void IsBusinessTime_UsesTheLocalTimeDaysAndHolidays(string instant, bool expected) =>
        Assert.Equal(expected, Default.IsBusinessTime(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void ExtraHolidays_AndTheNoneCalendar_AreHonoured()
    {
        var settings = new BusinessHoursSettings { HolidayCalendar = "None", ExtraHolidays = [new DateOnly(2026, 10, 14)] };
        var calendar = new BusinessCalendar(settings, Warsaw);

        Assert.False(calendar.IsBusinessDay(new DateOnly(2026, 10, 14)));
        Assert.True(calendar.IsBusinessDay(new DateOnly(2026, 11, 11)));
        Assert.True(calendar.IsHoliday(new DateOnly(2026, 10, 14)));
    }

    [Fact]
    public void PreviousBusinessDay_SkipsWeekendsAndHolidays()
    {
        Assert.Equal(new DateOnly(2026, 10, 16), Default.PreviousBusinessDay(new DateOnly(2026, 10, 19)));   // Monday → Friday
        Assert.Equal(new DateOnly(2026, 12, 23), Default.PreviousBusinessDay(new DateOnly(2026, 12, 28)));   // 24–27 Dec off
        Assert.Equal(new DateOnly(2026, 4, 3), Default.PreviousBusinessDay(new DateOnly(2026, 4, 7)));       // Easter Monday
        Assert.Equal(new DateOnly(2026, 10, 13), Default.PreviousBusinessDay(new DateOnly(2026, 10, 14)));
    }

    [Fact]
    public void LastBusinessDayOnOrBefore_KeepsABusinessDay()
    {
        Assert.Equal(new DateOnly(2026, 10, 14), Default.LastBusinessDayOnOrBefore(new DateOnly(2026, 10, 14)));
        Assert.Equal(new DateOnly(2026, 10, 16), Default.LastBusinessDayOnOrBefore(new DateOnly(2026, 10, 18)));   // Sunday expiry → Friday
    }

    [Fact]
    public void WithoutBusinessDays_ThePreviousBusinessDayIsTheDateItself()
    {
        var calendar = new BusinessCalendar(new BusinessHoursSettings { Days = [] }, Warsaw);

        Assert.Equal(new DateOnly(2026, 10, 14), calendar.PreviousBusinessDay(new DateOnly(2026, 10, 14)));
        Assert.False(calendar.IsBusinessTime(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void HoursWrappingMidnight_BelongToTheDayTheyStart()
    {
        var settings = new BusinessHoursSettings { Start = new TimeOnly(22, 0), End = new TimeOnly(6, 0) };
        var calendar = new BusinessCalendar(settings, TimeZoneInfo.Utc);

        Assert.True(calendar.IsBusinessTime(new DateTimeOffset(2026, 10, 16, 23, 0, 0, TimeSpan.Zero)));   // Friday night
        Assert.True(calendar.IsBusinessTime(new DateTimeOffset(2026, 10, 17, 5, 0, 0, TimeSpan.Zero)));    // Saturday early = Friday's shift
        Assert.False(calendar.IsBusinessTime(new DateTimeOffset(2026, 10, 17, 23, 0, 0, TimeSpan.Zero)));  // Saturday night
        Assert.False(calendar.IsBusinessTime(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void EqualStartAndEnd_MeanTheWholeBusinessDay()
    {
        var calendar = new BusinessCalendar(new BusinessHoursSettings { Start = new TimeOnly(0, 0), End = new TimeOnly(0, 0) }, Warsaw);

        Assert.True(calendar.IsBusinessTime(new DateTimeOffset(2026, 10, 14, 22, 30, 0, TimeSpan.Zero)));   // 00:30 Thursday
    }

    [Fact]
    public void For_ResolvesTheConfiguredTimeZone_AndFallsBackToUtc()
    {
        var warsaw = BusinessCalendar.For(new WatchdogConfiguration { General = new GeneralSettings { TimeZone = "Central European Standard Time" } });
        var broken = BusinessCalendar.For(new WatchdogConfiguration { General = new GeneralSettings { TimeZone = "Mars/Olympus" } });

        Assert.Equal(TimeSpan.FromHours(2), warsaw.TimeZone.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Same(TimeZoneInfo.Utc, broken.TimeZone);
        Assert.Equal(new DateOnly(2026, 10, 15), warsaw.LocalDate(new DateTimeOffset(2026, 10, 14, 22, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Provider_CachesPerConfigurationInstance()
    {
        var provider = new BusinessCalendarProvider();
        var configuration = new WatchdogConfiguration();

        Assert.Same(provider.For(configuration), provider.For(configuration));
        Assert.NotSame(provider.For(configuration), provider.For(new WatchdogConfiguration()));
        Assert.True(provider.IsBusinessTime(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), configuration));
    }
}
