using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class PolishHolidaysTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void EasterSunday_FollowsTheGregorianComputus(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), PolishHolidays.EasterSunday(year));

    [Fact]
    public void Holidays2026_AreTheFourteenStatutoryDays()
    {
        DateOnly[] expected =
        [
            new(2026, 1, 1), new(2026, 1, 6), new(2026, 4, 5), new(2026, 4, 6), new(2026, 5, 1), new(2026, 5, 3),
            new(2026, 5, 24), new(2026, 6, 4), new(2026, 8, 15), new(2026, 11, 1), new(2026, 11, 11), new(2026, 12, 24),
            new(2026, 12, 25), new(2026, 12, 26),
        ];

        Assert.Equal(expected.Order(), PolishHolidays.Get(2026).Order());
    }

    [Fact]
    public void Holidays2027_FollowEaster()
    {
        var holidays = PolishHolidays.Get(2027);

        Assert.Contains(new DateOnly(2027, 3, 28), holidays);   // Wielkanoc
        Assert.Contains(new DateOnly(2027, 3, 29), holidays);   // Poniedziałek Wielkanocny
        Assert.Contains(new DateOnly(2027, 5, 16), holidays);   // Zielone Świątki
        Assert.Contains(new DateOnly(2027, 5, 27), holidays);   // Boże Ciało
        Assert.Equal(14, holidays.Count);
    }

    [Fact]
    public void ChristmasEve_IsAHolidayFrom2025()
    {
        Assert.False(PolishHolidays.IsHoliday(new DateOnly(2024, 12, 24)));
        Assert.True(PolishHolidays.IsHoliday(new DateOnly(2025, 12, 24)));
        Assert.Equal(13, PolishHolidays.Get(2024).Count);
    }

    [Fact]
    public void OrdinaryDays_AreNotHolidays()
    {
        Assert.False(PolishHolidays.IsHoliday(new DateOnly(2026, 10, 14)));
        Assert.False(PolishHolidays.IsHoliday(new DateOnly(2026, 5, 2)));
    }

    [Fact]
    public void YearsBeforeTheGregorianCalendar_AreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PolishHolidays.EasterSunday(1500));
}
