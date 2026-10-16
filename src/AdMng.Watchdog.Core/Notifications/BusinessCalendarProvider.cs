using System.Runtime.CompilerServices;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// The <see cref="BusinessCalendar"/> of a configuration, cached per configuration instance (the configuration
/// provider hands out one instance per saved version). It is the rules area's <see cref="IBusinessHoursCalendar"/> (rule
/// preview) and, through <c>ConfiguredBusinessDayCalendar</c>, the business-day calendar of the expiry scanners.
/// </summary>
public sealed class BusinessCalendarProvider : IBusinessHoursCalendar
{
    private readonly ConditionalWeakTable<WatchdogConfiguration, BusinessCalendar> _calendars = [];

    /// <summary>The calendar of <paramref name="configuration"/>.</summary>
    public BusinessCalendar For(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return _calendars.GetValue(configuration, BusinessCalendar.For);
    }

    /// <summary>True when <paramref name="instant"/> is business time under <paramref name="configuration"/>
    /// (configured days, hours, time zone and holidays).</summary>
    public bool IsBusinessTime(DateTimeOffset instant, WatchdogConfiguration configuration) => For(configuration).IsBusinessTime(instant);
}
