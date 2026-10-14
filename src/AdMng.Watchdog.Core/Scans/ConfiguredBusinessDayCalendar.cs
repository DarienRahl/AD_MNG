using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// <see cref="IBusinessDayCalendar"/> from the configuration: a business day is one of <c>BusinessHours.Days</c> and
/// not in <c>BusinessHours.ExtraHolidays</c>. Statutory holidays (<c>HolidayCalendar</c> "PL") are added by the
/// notification area's calendar when it registers one.
/// </summary>
public sealed class ConfiguredBusinessDayCalendar : IBusinessDayCalendar
{
    /// <summary>Upper bound of the backwards search (a configuration without any business day).</summary>
    private const int MaxLookBackDays = 366;

    private readonly IWatchdogConfigProvider _configuration;

    public ConfiguredBusinessDayCalendar(IWatchdogConfigProvider configuration)
    {
        _configuration = configuration;
    }

    public bool IsBusinessDay(DateOnly date)
    {
        var hours = _configuration.Current.Configuration.BusinessHours;
        return hours.Days.Contains(date.DayOfWeek) && !hours.ExtraHolidays.Contains(date);
    }

    public DateOnly LastBusinessDayOnOrBefore(DateOnly date)
    {
        for (var day = date; day.DayNumber > date.DayNumber - MaxLookBackDays; day = day.AddDays(-1))
        {
            if (IsBusinessDay(day))
            {
                return day;
            }
        }

        return date;
    }
}
