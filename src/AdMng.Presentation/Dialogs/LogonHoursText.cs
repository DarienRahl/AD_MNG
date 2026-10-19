using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// Localized texts of logon hours: the days in the week order of the UI culture (Polish: Monday first, like the Polish
/// ADUC; English: Sunday first), hour labels, the UTC offset and the one-line summary of a schedule in local time
/// ("pon–pt 8–16; sob 9–13" - days with the same hours grouped, like the Watchdog's summary).
/// </summary>
public static class LogonHoursText
{
    /// <summary>The seven days starting with the culture's first day of the week.</summary>
    public static IReadOnlyList<DayOfWeek> Week(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var first = (int)culture.DateTimeFormat.FirstDayOfWeek;
        return [.. Enumerable.Range(0, 7).Select(i => (DayOfWeek)((first + i) % 7))];
    }

    /// <summary>"Poniedziałek".</summary>
    public static string DayName(ILocalizer localizer, DayOfWeek day) => localizer[DisplayNames.LogonHoursDayKey(day)];

    /// <summary>"pon".</summary>
    public static string ShortDayName(ILocalizer localizer, DayOfWeek day) => localizer[DisplayNames.LogonHoursShortDayKey(day)];

    /// <summary>"08:00" (24 → "24:00", the end of the day).</summary>
    public static string Hour(int hour) => hour.ToString("00", CultureInfo.InvariantCulture) + ":00";

    /// <summary>"+02:00" / "-05:00" for a whole-hour UTC offset.</summary>
    public static string Offset(int hours) =>
        (hours < 0 ? "-" : "+") + Math.Abs(hours).ToString("00", CultureInfo.InvariantCulture) + ":00";

    /// <summary>The summary of <paramref name="hours"/> in the local time of <paramref name="zone"/> at <paramref name="now"/>.</summary>
    public static string Describe(LogonHours hours, ILocalizer localizer, TimeZoneInfo zone, DateTimeOffset now) =>
        Describe(hours, localizer, LogonHours.OffsetHours(zone, now));

    /// <summary>The summary of <paramref name="hours"/> shown with the UTC offset <paramref name="offsetHours"/>.</summary>
    public static string Describe(LogonHours hours, ILocalizer localizer, int offsetHours)
    {
        ArgumentNullException.ThrowIfNull(hours);
        ArgumentNullException.ThrowIfNull(localizer);
        if (hours.IsAllPermitted)
        {
            return localizer[L.LogonHours.Always];
        }

        if (hours.IsNonePermitted)
        {
            return localizer[L.LogonHours.Never];
        }

        var local = hours.ToLocalGrid(offsetHours);
        var groups = new List<(DayOfWeek First, DayOfWeek Last, string Ranges)>();
        foreach (var day in Week(localizer.Culture))
        {
            var ranges = Ranges(local, day);
            if (ranges.Length == 0)
            {
                continue;
            }

            if (groups.Count > 0 && groups[^1].Ranges == ranges && (DayOfWeek)(((int)groups[^1].Last + 1) % 7) == day)
            {
                groups[^1] = groups[^1] with { Last = day };
            }
            else
            {
                groups.Add((day, day, ranges));
            }
        }

        return string.Join("; ", groups.Select(g =>
            (g.First == g.Last ? ShortDayName(localizer, g.First) : ShortDayName(localizer, g.First) + "–" + ShortDayName(localizer, g.Last))
            + " " + g.Ranges));
    }

    /// <summary>"8–16, 18–20": the permitted ranges of one local day (end hour exclusive).</summary>
    private static string Ranges(LogonHoursGrid local, DayOfWeek day)
    {
        var builder = new StringBuilder();
        var hour = 0;
        while (hour < 24)
        {
            if (!local[day, hour])
            {
                hour++;
                continue;
            }

            var from = hour;
            while (hour < 24 && local[day, hour])
            {
                hour++;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(from.ToString(CultureInfo.InvariantCulture)).Append('–').Append(hour.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
