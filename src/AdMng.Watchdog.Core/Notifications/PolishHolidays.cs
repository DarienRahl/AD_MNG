using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Polish statutory public holidays (section 13.5; Ustawa o dniach wolnych od pracy): 1 Jan, 6 Jan, Easter Sunday,
/// Easter Monday, 1 May, 3 May, Pentecost (Easter + 49), Corpus Christi (Easter + 60), 15 Aug, 1 Nov, 11 Nov,
/// 24 Dec (statutory since 2025), 25 Dec and 26 Dec. Easter is computed with the anonymous Gregorian algorithm.
/// </summary>
public static class PolishHolidays
{
    /// <summary>First year in which Christmas Eve (24 Dec) is a statutory holiday.</summary>
    public const int ChristmasEveSince = 2025;

    private static readonly ConcurrentDictionary<int, FrozenSet<DateOnly>> Cache = new();

    /// <summary>The holidays of <paramref name="year"/> (1583–9999).</summary>
    public static IReadOnlySet<DateOnly> Get(int year) => Cache.GetOrAdd(year, Build);

    /// <summary>True when <paramref name="date"/> is a Polish statutory holiday.</summary>
    public static bool IsHoliday(DateOnly date) => Get(date.Year).Contains(date);

    /// <summary>Easter Sunday of <paramref name="year"/> (anonymous Gregorian algorithm, "Meeus/Jones/Butcher").</summary>
    public static DateOnly EasterSunday(int year)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1583);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static FrozenSet<DateOnly> Build(int year)
    {
        var easter = EasterSunday(year);
        var days = new List<DateOnly>
        {
            new(year, 1, 1), new(year, 1, 6), easter, easter.AddDays(1), new(year, 5, 1), new(year, 5, 3),
            easter.AddDays(49), easter.AddDays(60), new(year, 8, 15), new(year, 11, 1), new(year, 11, 11),
            new(year, 12, 25), new(year, 12, 26),
        };
        if (year >= ChristmasEveSince)
        {
            days.Add(new DateOnly(year, 12, 24));
        }

        return days.ToFrozenSet();
    }
}
