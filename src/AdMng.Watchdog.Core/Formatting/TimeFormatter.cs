using System.Globalization;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Times in notifications (section 14.1): the configured time zone; Polish <c>dd.MM.yyyy HH:mm:ss</c>, English
/// <c>yyyy-MM-dd HH:mm:ss</c>, followed by the UTC offset; the e-mail body also shows UTC.
/// </summary>
public sealed class TimeFormatter
{
    private readonly bool _polish;

    public TimeFormatter(TimeZoneInfo timeZone, bool polish)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
        _polish = polish;
    }

    public TimeZoneInfo TimeZone { get; }

    private string DateFormat => _polish ? "dd.MM.yyyy" : "yyyy-MM-dd";

    /// <summary><paramref name="instant"/> in the configured zone.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>"14.10.2026 14:32:05 (UTC+02:00)".</summary>
    public string Local(DateTimeOffset instant)
    {
        var local = ToLocal(instant);
        return local.ToString(DateFormat + " HH:mm:ss", CultureInfo.InvariantCulture) + " (" + Offset(local.Offset) + ")";
    }

    /// <summary>"14.10.2026 14:32:05 (UTC+02:00) · 12:32:05 UTC".</summary>
    public string LocalAndUtc(DateTimeOffset instant) =>
        Local(instant) + " · " + instant.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>"14.10.2026".</summary>
    public string Date(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>The local date of <paramref name="instant"/>.</summary>
    public string LocalDate(DateTimeOffset instant) => ToLocal(instant).ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>"10:42" (local).</summary>
    public string LocalTime(DateTimeOffset instant) => ToLocal(instant).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"14.10.2026 10:42" (local, no offset).</summary>
    public string LocalShort(DateTimeOffset instant) => ToLocal(instant).ToString(DateFormat + " HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"UTC+02:00" / "UTC−05:30" / "UTC".</summary>
    public static string Offset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "UTC+00:00";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}");
    }
}
