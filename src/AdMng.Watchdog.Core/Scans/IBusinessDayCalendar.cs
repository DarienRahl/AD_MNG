namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Business days for the expiry scanners (section 10.14: a threshold whose day is not a business day fires on the
/// previous business day). The default <see cref="ConfiguredBusinessDayCalendar"/> uses the configured business-hour
/// days and extra holidays; the notification area may register an implementation that adds the statutory holiday
/// calendar (<c>BusinessHoursSettings.HolidayCalendar</c>).
/// </summary>
public interface IBusinessDayCalendar
{
    /// <summary>True when <paramref name="date"/> is a business day.</summary>
    bool IsBusinessDay(DateOnly date);

    /// <summary>The last business day on or before <paramref name="date"/>.</summary>
    DateOnly LastBusinessDayOnOrBefore(DateOnly date);
}
