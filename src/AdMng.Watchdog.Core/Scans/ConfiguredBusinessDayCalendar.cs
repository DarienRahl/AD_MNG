using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// <see cref="IBusinessDayCalendar"/> of the current configuration: the notification area's <see cref="BusinessCalendar"/>
/// (section 13.5) – a business day is one of <c>BusinessHours.Days</c>, not in <c>BusinessHours.ExtraHolidays</c> and,
/// with <c>HolidayCalendar</c> "PL", not a Polish statutory holiday (<see cref="PolishHolidays"/>). The same calendar
/// decides quiet hours and digest coverage, so the expiry scanners and the notifications never disagree about a day.
/// </summary>
public sealed class ConfiguredBusinessDayCalendar : IBusinessDayCalendar
{
    private readonly IWatchdogConfigProvider _configuration;
    private readonly BusinessCalendarProvider _calendars;

    public ConfiguredBusinessDayCalendar(IWatchdogConfigProvider configuration, BusinessCalendarProvider calendars)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(calendars);
        _configuration = configuration;
        _calendars = calendars;
    }

    public bool IsBusinessDay(DateOnly date) => Calendar().IsBusinessDay(date);

    public DateOnly LastBusinessDayOnOrBefore(DateOnly date) => Calendar().LastBusinessDayOnOrBefore(date);

    private BusinessCalendar Calendar() => _calendars.For(_configuration.Current.Configuration);
}
