using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Business days and hours in the configured time zone (section 13.5): quiet hours of
/// <see cref="DeliveryHours.BusinessHours"/>, the digest coverage of non-business days and the business-day rule of the
/// expiry scanners. Immutable; create one per configuration (<see cref="For"/>).
/// </summary>
/// <remarks>Hours are <c>Start ≤ local time &lt; End</c> on a business day. When <c>End</c> is earlier than
/// <c>Start</c> the hours wrap past midnight (a night shift belongs to the day it starts on); equal values mean the whole
/// day.</remarks>
public sealed class BusinessCalendar
{
    /// <summary>Holiday calendar name of <see cref="PolishHolidays"/> (<see cref="BusinessHoursSettings.HolidayCalendar"/>).</summary>
    public const string PolishCalendar = "PL";

    private const int MaxSearchDays = 366;

    private readonly HashSet<DayOfWeek> _days;
    private readonly HashSet<DateOnly> _extraHolidays;
    private readonly bool _polishHolidays;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;

    public BusinessCalendar(BusinessHoursSettings settings, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
        _days = [.. settings.Days ?? []];
        _extraHolidays = [.. settings.ExtraHolidays ?? []];
        _polishHolidays = string.Equals(settings.HolidayCalendar?.Trim(), PolishCalendar, StringComparison.OrdinalIgnoreCase);
        _start = settings.Start;
        _end = settings.End;
    }

    /// <summary>The configured time zone.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>The calendar of <paramref name="configuration"/>: its business hours in its time zone
    /// (<see cref="TimeZoneResolver"/>; an unresolvable zone falls back to UTC – validation rejects such a document).</summary>
    public static BusinessCalendar For(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new BusinessCalendar(configuration.BusinessHours ?? new BusinessHoursSettings(), ResolveTimeZone(configuration));
    }

    /// <summary>The configured time zone of <paramref name="configuration"/>, UTC when it cannot be resolved.</summary>
    public static TimeZoneInfo ResolveTimeZone(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return TimeZoneResolver.TryResolve(configuration.General?.TimeZone, out var timeZone) ? timeZone : TimeZoneInfo.Utc;
    }

    /// <summary><paramref name="instant"/> in the configured time zone.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>The local calendar date of <paramref name="instant"/>.</summary>
    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    /// <summary>True for a statutory (when the PL calendar is configured) or extra holiday.</summary>
    public bool IsHoliday(DateOnly date) => _extraHolidays.Contains(date) || (_polishHolidays && PolishHolidays.IsHoliday(date));

    /// <summary>A configured business weekday that is not a holiday.</summary>
    public bool IsBusinessDay(DateOnly date) => _days.Contains(date.DayOfWeek) && !IsHoliday(date);

    /// <summary>True when <paramref name="instant"/> falls into the business hours of a business day.</summary>
    public bool IsBusinessTime(DateTimeOffset instant)
    {
        var local = ToLocal(instant);
        var date = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (_start == _end)
        {
            return IsBusinessDay(date);
        }

        if (_start < _end)
        {
            return IsBusinessDay(date) && time >= _start && time < _end;
        }

        // Wrapping hours: the evening part belongs to today, the early-morning part to yesterday's shift.
        return time >= _start ? IsBusinessDay(date) : time < _end && IsBusinessDay(date.AddDays(-1));
    }

    /// <summary>The last business day strictly before <paramref name="date"/>; <paramref name="date"/> itself when none
    /// exists within a year (no business days configured).</summary>
    public DateOnly PreviousBusinessDay(DateOnly date)
    {
        for (var i = 1; i <= MaxSearchDays; i++)
        {
            var candidate = date.AddDays(-i);
            if (IsBusinessDay(candidate))
            {
                return candidate;
            }
        }

        return date;
    }

    /// <summary><paramref name="date"/> when it is a business day, else <see cref="PreviousBusinessDay"/> – the
    /// business-day rule of the expiry thresholds (10.14).</summary>
    public DateOnly LastBusinessDayOnOrBefore(DateOnly date) => IsBusinessDay(date) ? date : PreviousBusinessDay(date);
}
