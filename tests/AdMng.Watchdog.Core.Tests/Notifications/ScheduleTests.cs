using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneResolver.Resolve("Europe/Warsaw");

    private static readonly DayOfWeek[] EveryDay = Enum.GetValues<DayOfWeek>();

    [Fact]
    public void NextOccurrence_IsLaterTodayOrTomorrow_InLocalTime()
    {
        var before = Schedule.NextOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, Utc(2026, 10, 14, 5, 0));
        var after = Schedule.NextOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, Utc(2026, 10, 14, 5, 30));

        Assert.Equal(Utc(2026, 10, 14, 5, 30), before);   // 07:30 CEST
        Assert.Equal(Utc(2026, 10, 15, 5, 30), after);    // strictly after
    }

    [Fact]
    public void NextOccurrence_SkipsUnselectedDays()
    {
        var next = Schedule.NextOccurrence(new TimeOnly(7, 30), [DayOfWeek.Monday], Warsaw, Utc(2026, 10, 14, 12, 0));

        Assert.Equal(Utc(2026, 10, 19, 5, 30), next);
        Assert.Null(Schedule.NextOccurrence(new TimeOnly(7, 30), [], Warsaw, Utc(2026, 10, 14, 12, 0)));
    }

    [Fact]
    public void SpringForward_ANonExistentTimeMovesToTheNextValidMinute()
    {
        // 2026-03-29: 02:00 CET → 03:00 CEST in Europe/Warsaw.
        var occurrence = Schedule.OnDate(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), Warsaw);

        Assert.Equal(Utc(2026, 3, 29, 1, 0), occurrence);   // 03:00 CEST
        Assert.Equal(Utc(2026, 3, 28, 1, 30), Schedule.OnDate(new DateOnly(2026, 3, 28), new TimeOnly(2, 30), Warsaw));
        Assert.Equal(Utc(2026, 3, 30, 0, 30), Schedule.OnDate(new DateOnly(2026, 3, 30), new TimeOnly(2, 30), Warsaw));
    }

    [Fact]
    public void FallBack_AnAmbiguousTimeUsesTheFirstOccurrence()
    {
        // 2026-10-25: 03:00 CEST → 02:00 CET; 02:30 happens twice.
        var occurrence = Schedule.OnDate(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Warsaw);

        Assert.Equal(Utc(2026, 10, 25, 0, 30), occurrence);   // 02:30 CEST (+02:00)
    }

    [Fact]
    public void DailyOccurrences_AcrossTheFallBack_KeepTheLocalTime()
    {
        var saturday = Schedule.NextOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, Utc(2026, 10, 24, 0, 0));
        var sunday = Schedule.NextOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, saturday!.Value);
        var monday = Schedule.NextOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, sunday!.Value);

        Assert.Equal(Utc(2026, 10, 24, 5, 30), saturday);
        Assert.Equal(Utc(2026, 10, 25, 6, 30), sunday);   // 07:30 CET
        Assert.Equal(Utc(2026, 10, 26, 6, 30), monday);
        Assert.Equal(TimeSpan.FromHours(25), sunday.Value - saturday.Value);
    }

    [Fact]
    public void PreviousOccurrence_IsTheLatestAtOrBefore()
    {
        var exact = Schedule.PreviousOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, Utc(2026, 10, 14, 5, 30));
        var earlier = Schedule.PreviousOccurrence(new TimeOnly(7, 30), EveryDay, Warsaw, Utc(2026, 10, 14, 5, 29));
        var monday = Schedule.PreviousOccurrence(new TimeOnly(7, 30), [DayOfWeek.Monday], Warsaw, Utc(2026, 10, 14, 12, 0));

        Assert.Equal(Utc(2026, 10, 14, 5, 30), exact);
        Assert.Equal(Utc(2026, 10, 13, 5, 30), earlier);
        Assert.Equal(Utc(2026, 10, 12, 5, 30), monday);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
