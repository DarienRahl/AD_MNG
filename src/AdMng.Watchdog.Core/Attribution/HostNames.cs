namespace AdMng.Watchdog.Core.Attribution;

/// <summary>DNS host names as attribution compares them: trimmed, lower-case, without a trailing dot.</summary>
public static class HostNames
{
    /// <summary>The normalized form of <paramref name="host"/>.</summary>
    public static string Normalize(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>True when both names denote the same host (case-insensitive, trailing dot ignored).</summary>
    public static bool AreEqual(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
