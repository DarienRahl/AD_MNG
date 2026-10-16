using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Threshold arithmetic of the expiry scanners (section 10.14), in calendar days of the configured time zone:
/// the displayed last valid day, days left, the (business-day adjusted) day a threshold fires and the thresholds due
/// today. The dedup key of a notice is (objectGuid, kind, raw expiry value, threshold) – a changed expiration date
/// re-arms every threshold.
/// </summary>
public static class ExpiryThresholds
{
    /// <summary>
    /// The last valid day shown to people: accounts <c>(local(expires) − 1 tick).Date</c> (ADUC "End of: D" stores the
    /// local midnight that starts D+1); passwords the local date of the exact expiry moment.
    /// </summary>
    public static DateOnly LastValidDay(DateTimeOffset expiresAt, ExpiryKind kind, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var moment = kind == ExpiryKind.Account ? expiresAt.AddTicks(-1) : expiresAt;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, timeZone).DateTime);
    }

    /// <summary>Calendar days from <paramref name="today"/> to the last valid day (0 = the last valid day is today).</summary>
    public static int DaysLeft(DateOnly lastValidDay, DateOnly today) => lastValidDay.DayNumber - today.DayNumber;

    /// <summary>The day a threshold fires: <c>lastValidDay − threshold</c>, moved to the last business day on or
    /// before it when <paramref name="calendar"/> is given (FireOnBusinessDays).</summary>
    public static DateOnly FireDay(DateOnly lastValidDay, int thresholdDays, IBusinessDayCalendar? calendar)
    {
        var day = lastValidDay.AddDays(-thresholdDays);
        return calendar?.LastBusinessDayOnOrBefore(day) ?? day;
    }

    /// <summary>The configured thresholds whose fire day is today or earlier, largest first.</summary>
    public static IReadOnlyList<int> Due(IEnumerable<int> thresholds, DateOnly lastValidDay, DateOnly today, IBusinessDayCalendar? calendar)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        return thresholds.Where(t => t > 0).Distinct().OrderDescending().Where(t => today >= FireDay(lastValidDay, t, calendar)).ToList();
    }
}
