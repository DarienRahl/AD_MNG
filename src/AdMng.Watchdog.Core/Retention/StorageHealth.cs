using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Retention;

/// <summary>
/// Storage thresholds of section 16.3: the database reaching 80 % of <c>Retention.MaxDatabaseMb</c> or the data volume
/// having less than 1 GiB free is a <c>HealthStorage</c> problem; above the cap itself the store purges
/// <c>security_events</c> oldest-first (also checked hourly).
/// </summary>
public static class StorageHealth
{
    /// <summary>Health check code of the database size.</summary>
    public const string SizeCode = "storage.size";

    /// <summary>Health check code of the free space on the data volume.</summary>
    public const string FreeCode = "storage.free";

    /// <summary>Share of <c>MaxDatabaseMb</c> at which <c>HealthStorage</c> fires.</summary>
    public const double WarningShare = 0.8;

    /// <summary>Free space below which <c>HealthStorage</c> fires.</summary>
    public const long MinimumFreeBytes = 1L << 30;

    private const long BytesPerMb = 1024 * 1024;

    /// <summary>The size limit in bytes (at least 1 MB).</summary>
    public static long LimitBytes(RetentionSettings retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        return Math.Max(1, retention.MaxDatabaseMb) * BytesPerMb;
    }

    /// <summary>The database is above the cap: the store has to purge now.</summary>
    public static bool IsOverCap(StorageStatusDto status, RetentionSettings retention)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.DatabaseBytes > LimitBytes(retention);
    }

    /// <summary>The size check: Warning from 80 % of the cap, Error above it.</summary>
    public static HealthCheckDto SizeCheck(StorageStatusDto status, RetentionSettings retention, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        var limit = LimitBytes(retention);
        var state = status.DatabaseBytes > limit ? HealthState.Error : status.DatabaseBytes >= limit * WarningShare ? HealthState.Warning : HealthState.Ok;
        return new HealthCheckDto
        {
            Code = SizeCode,
            State = state,
            Detail = state == HealthState.Ok ? null : "Database size reached the configured share of Retention.MaxDatabaseMb.",
            Args = [Mb(status.DatabaseBytes), Mb(limit)],
            CheckedAt = now,
        };
    }

    /// <summary>The free-space check: Error below 1 GiB.</summary>
    public static HealthCheckDto FreeCheck(StorageStatusDto status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        var state = status.FreeBytes < MinimumFreeBytes ? HealthState.Error : HealthState.Ok;
        return new HealthCheckDto
        {
            Code = FreeCode,
            State = state,
            Detail = state == HealthState.Ok ? null : "Less than 1 GiB free on the data volume.",
            Args = [Mb(status.FreeBytes)],
            CheckedAt = now,
        };
    }

    /// <summary>Megabytes as an invariant integer string.</summary>
    public static string Mb(long bytes) => (bytes / BytesPerMb).ToString(CultureInfo.InvariantCulture);
}
