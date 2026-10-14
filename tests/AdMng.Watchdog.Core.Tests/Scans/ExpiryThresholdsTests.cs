using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Scans;

namespace AdMng.Watchdog.Core.Tests.Scans;

public class ExpiryThresholdsTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    [Fact]
    public void AccountEndOfDay_IsTheDayBeforeTheStoredLocalMidnight()
    {
        // ADUC "End of: 21.10.2026" stores 22.10.2026 00:00 local (CEST, UTC+2).
        var stored = new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 21), ExpiryThresholds.LastValidDay(stored, ExpiryKind.Account, Warsaw));
    }

    [Fact]
    public void AccountEndOfDay_AcrossTheAutumnClockChange()
    {
        // "End of: 25.10.2026" (the day clocks go back) stores 26.10.2026 00:00 CET = 25.10 23:00 UTC.
        var stored = new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 25), ExpiryThresholds.LastValidDay(stored, ExpiryKind.Account, Warsaw));
    }

    [Fact]
    public void PasswordExpiry_IsTheLocalDateOfTheMoment()
    {
        var moment = new DateTimeOffset(2026, 10, 21, 22, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 22), ExpiryThresholds.LastValidDay(moment, ExpiryKind.Password, Warsaw));
    }

    [Fact]
    public void DaysLeft_CountsCalendarDays()
    {
        Assert.Equal(7, ExpiryThresholds.DaysLeft(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 14)));
        Assert.Equal(0, ExpiryThresholds.DaysLeft(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 14)));
    }

    [Fact]
    public void OneDayThreshold_FiresTheDayBefore_AndOnTheLastBusinessDay()
    {
        var sunday = new DateOnly(2026, 10, 25);
        var calendar = new WeekdayCalendar();

        Assert.Equal(new DateOnly(2026, 10, 24), ExpiryThresholds.FireDay(sunday, 1, null));
        Assert.Equal(new DateOnly(2026, 10, 23), ExpiryThresholds.FireDay(sunday, 1, calendar));
    }

    [Fact]
    public void Due_ReturnsEveryPassedThresholdLargestFirst()
    {
        var lastDay = new DateOnly(2026, 10, 21);

        Assert.Equal([14, 7], ExpiryThresholds.Due([1, 7, 14], lastDay, new DateOnly(2026, 10, 14), null));
        Assert.Empty(ExpiryThresholds.Due([1, 7, 14], lastDay, new DateOnly(2026, 10, 6), null));
        Assert.Equal([14, 7, 1], ExpiryThresholds.Due([14, 7, 1, 7], lastDay, new DateOnly(2026, 10, 20), null));
    }

    private sealed class WeekdayCalendar : IBusinessDayCalendar
    {
        public bool IsBusinessDay(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

        public DateOnly LastBusinessDayOnOrBefore(DateOnly date)
        {
            while (!IsBusinessDay(date))
            {
                date = date.AddDays(-1);
            }

            return date;
        }
    }
}
