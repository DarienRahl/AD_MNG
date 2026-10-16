using System.Globalization;
using System.Text;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The weekly summary of a <c>logonHours</c> value ("pon–pt 7–17"): 21 bytes (42 hex characters, the canonical
/// form of section 9.4), one bit per hour from Sunday 00:00 UTC, shown in the configured zone's standard time with
/// days of identical hours grouped. All hours = "zawsze", none = "nigdy"; anything unparsable is returned as is.
/// </summary>
public static class LogonHoursSummary
{
    private const int Hours = 7 * 24;

    private static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>The summary of <paramref name="hex"/>.</summary>
    public static string Describe(string hex, NotificationText text, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(hex);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (!TryParse(hex, out var utcBits))
        {
            return hex;
        }

        var allowed = utcBits.Count(b => b);
        if (allowed == Hours)
        {
            return text.Get("Value.LogonHoursAlways");
        }

        if (allowed == 0)
        {
            return text.Get("Value.LogonHoursNever");
        }

        var offset = (int)Math.Round(timeZone.BaseUtcOffset.TotalHours);
        var local = new bool[Hours];
        for (var i = 0; i < Hours; i++)
        {
            local[(((i + offset) % Hours) + Hours) % Hours] = utcBits[i];
        }

        var groups = new List<(DayOfWeek First, DayOfWeek Last, string Ranges)>();
        foreach (var day in Week)
        {
            var ranges = Ranges(local, (int)day * 24);
            if (ranges.Length == 0)
            {
                continue;
            }

            if (groups.Count > 0 && groups[^1].Ranges == ranges && Next(groups[^1].Last) == day)
            {
                groups[^1] = groups[^1] with { Last = day };
            }
            else
            {
                groups.Add((day, day, ranges));
            }
        }

        return string.Join("; ", groups.Select(g => DayLabel(g.First, g.Last, text) + " " + g.Ranges));
    }

    /// <summary>Parses 42 hex characters into 168 hour bits (bit 0 of byte 0 = Sunday 00:00–01:00 UTC).</summary>
    public static bool TryParse(string hex, out bool[] bits)
    {
        bits = new bool[Hours];
        var trimmed = hex.Trim();
        if (trimmed.Length != Hours / 4)
        {
            return false;
        }

        for (var i = 0; i < Hours / 8; i++)
        {
            if (!byte.TryParse(trimmed.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            for (var bit = 0; bit < 8; bit++)
            {
                bits[(i * 8) + bit] = (value & (1 << bit)) != 0;
            }
        }

        return true;
    }

    private static string Ranges(bool[] local, int start)
    {
        var builder = new StringBuilder();
        var hour = 0;
        while (hour < 24)
        {
            if (!local[start + hour])
            {
                hour++;
                continue;
            }

            var from = hour;
            while (hour < 24 && local[start + hour])
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

    private static DayOfWeek Next(DayOfWeek day) => (DayOfWeek)(((int)day + 1) % 7);

    private static string DayLabel(DayOfWeek first, DayOfWeek last, NotificationText text)
    {
        var firstName = text.Get("Day." + first + ".Short");
        return first == last ? firstName : firstName + "–" + text.Get("Day." + last + ".Short");
    }
}
