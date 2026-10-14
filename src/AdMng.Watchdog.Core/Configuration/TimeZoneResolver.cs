using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>
/// Resolves <c>General.TimeZone</c> (an IANA id such as <c>Europe/Warsaw</c> or a Windows id such as
/// <c>Central European Standard Time</c>) to a <see cref="TimeZoneInfo"/> on every host – also on Windows hosts in NLS
/// mode (Windows Server 2019 without ICU), where the system knows only Windows ids and cannot convert IANA ids.
/// </summary>
/// <remarks>
/// Order: (1) the id as given; (2) IANA ↔ Windows conversion of the runtime (needs ICU); (3) the built-in map
/// Europe/Warsaw ↔ Central European Standard Time, Europe/Berlin ↔ W. Europe Standard Time, Europe/London ↔ GMT
/// Standard Time, UTC/Etc/UTC ↔ UTC (case-insensitive); (4) <see cref="TimeZoneInfo.Utc"/> for "UTC"/"Etc/UTC".
/// Leading/trailing white space is ignored; ids containing control characters are never resolved.
/// </remarks>
public static class TimeZoneResolver
{
    private static readonly FrozenDictionary<string, string> IanaToWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Warsaw"] = "Central European Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/London"] = "GMT Standard Time",
        ["Etc/UTC"] = "UTC",
        ["UTC"] = "UTC",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> WindowsToIana = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Central European Standard Time"] = "Europe/Warsaw",
        ["W. Europe Standard Time"] = "Europe/Berlin",
        ["GMT Standard Time"] = "Europe/London",
        ["UTC"] = "Etc/UTC",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves <paramref name="id"/>; false (and null) when it is empty or unknown on this host.</summary>
    public static bool TryResolve(string? id, [NotNullWhen(true)] out TimeZoneInfo? timeZone) =>
        TryResolve(id, FindSystemTimeZone, convertWithRuntime: true, out timeZone);

    /// <summary>Resolves <paramref name="id"/> or throws.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null, empty or white space.</exception>
    /// <exception cref="TimeZoneNotFoundException">The id is not known on this host (also not through the map).</exception>
    public static TimeZoneInfo Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return TryResolve(id, out var timeZone)
            ? timeZone
            : throw new TimeZoneNotFoundException($"The time zone '{id.Trim()}' is not known on this host (IANA or Windows id expected).");
    }

    /// <summary>Testable core: <paramref name="findSystemTimeZone"/> stands in for the system lookup and
    /// <paramref name="convertWithRuntime"/> = false simulates a host without ICU (NLS mode).</summary>
    internal static bool TryResolve(
        string? id, Func<string, TimeZoneInfo?> findSystemTimeZone, bool convertWithRuntime, [NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmed = id.Trim();
        if (trimmed.Any(char.IsControl))
        {
            return false;                                               // e.g. "Europe/Warsaw\0" (the Linux lookup stops at NUL)
        }

        timeZone = findSystemTimeZone(trimmed);
        if (timeZone is not null)
        {
            return true;
        }

        if (convertWithRuntime)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windowsId) && (timeZone = findSystemTimeZone(windowsId)) is not null)
            {
                return true;
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var ianaId) && (timeZone = findSystemTimeZone(ianaId)) is not null)
            {
                return true;
            }
        }

        if (IanaToWindows.TryGetValue(trimmed, out var mappedWindowsId) && (timeZone = FindEither(mappedWindowsId)) is not null)
        {
            return true;
        }

        if (WindowsToIana.TryGetValue(trimmed, out var mappedIanaId) && (timeZone = FindEither(mappedIanaId)) is not null)
        {
            return true;
        }

        if (trimmed.Equals("UTC", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        timeZone = null;
        return false;

        // A mapped id may be known in the other form only (e.g. the map yields "UTC" on a host that knows "Etc/UTC").
        TimeZoneInfo? FindEither(string mapped) =>
            findSystemTimeZone(mapped)
            ?? (WindowsToIana.TryGetValue(mapped, out var iana) ? findSystemTimeZone(iana) : null)
            ?? (IanaToWindows.TryGetValue(mapped, out var windows) ? findSystemTimeZone(windows) : null);
    }

    // Configuration input is arbitrary text: a malformed or unreadable zone is "not found", never an exception.
    private static TimeZoneInfo? FindSystemTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var timeZone) ? timeZone : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidTimeZoneException or IOException
                                      or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
