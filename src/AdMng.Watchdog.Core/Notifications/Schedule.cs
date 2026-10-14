namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Occurrences of a local time of day on selected weekdays (digest, maintenance, section 13.4). DST: a local time that
/// does not exist (spring forward) moves to the next valid minute; an ambiguous one (fall back) uses its first
/// occurrence. Results are UTC instants.
/// </summary>
public static class Schedule
{
    private const int DaysToSearch = 8;

    /// <summary>The first occurrence strictly after <paramref name="after"/>; null when <paramref name="days"/> is empty.</summary>
    public static DateTimeOffset? NextOccurrence(TimeOnly time, IReadOnlyCollection<DayOfWeek> days, TimeZoneInfo timeZone, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(timeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, timeZone).DateTime);
        for (var i = -1; i <= DaysToSearch; i++)
        {
            var date = today.AddDays(i);
            if (days.Contains(date.DayOfWeek) && OnDate(date, time, timeZone) is var occurrence && occurrence > after)
            {
                return occurrence;
            }
        }

        return null;
    }

    /// <summary>The latest occurrence at or before <paramref name="atOrBefore"/>; null when <paramref name="days"/> is empty.</summary>
    public static DateTimeOffset? PreviousOccurrence(TimeOnly time, IReadOnlyCollection<DayOfWeek> days, TimeZoneInfo timeZone, DateTimeOffset atOrBefore)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(timeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(atOrBefore, timeZone).DateTime);
        for (var i = 1; i >= -DaysToSearch; i--)
        {
            var date = today.AddDays(i);
            if (days.Contains(date.DayOfWeek) && OnDate(date, time, timeZone) is var occurrence && occurrence <= atOrBefore)
            {
                return occurrence;
            }
        }

        return null;
    }

    /// <summary>The instant of <paramref name="time"/> on the local <paramref name="date"/> (DST rules above), UTC.</summary>
    public static DateTimeOffset OnDate(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        for (var guard = 0; guard < 24 * 60 && timeZone.IsInvalidTime(local); guard++)
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
