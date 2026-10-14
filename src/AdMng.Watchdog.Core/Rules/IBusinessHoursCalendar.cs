using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>Business-time check (section 13.5) as the rule preview needs it. Implemented by the notification side
/// (<c>BusinessCalendar</c> with Polish holidays); without an implementation the preview treats every moment as
/// business time (an upper bound of the messages).</summary>
public interface IBusinessHoursCalendar
{
    /// <summary>True when <paramref name="instant"/> is business time under <paramref name="configuration"/>
    /// (configured days, hours, time zone and holidays).</summary>
    bool IsBusinessTime(DateTimeOffset instant, WatchdogConfiguration configuration);
}
