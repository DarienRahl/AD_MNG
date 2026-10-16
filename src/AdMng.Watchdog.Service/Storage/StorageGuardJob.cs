using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The hourly part of retention (spec 16.3): enforces <c>Retention.MaxDatabaseMb</c> between the daily maintenance runs
/// (security events, then Dead outbox rows, oldest first, down to 90 %) and reports the health checks
/// <c>storage.size</c> (Warning at ≥ 80 % of the cap) and <c>storage.free</c> (Warning below 1 GiB free on the volume).
/// </summary>
public sealed partial class StorageGuardJob : IWatchdogJob
{
    public const long MinimumFreeBytes = 1024L * 1024 * 1024;

    private readonly SqliteStoreMaintenance _maintenance;
    private readonly IWatchdogConfigProvider _configuration;
    private readonly IHealthRegistry _health;
    private readonly DataDirectory _dataDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageGuardJob> _logger;

    public StorageGuardJob(
        SqliteWatchdogStore store,
        IWatchdogConfigProvider configuration,
        IHealthRegistry health,
        DataDirectory dataDirectory,
        TimeProvider timeProvider,
        ILogger<StorageGuardJob> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _maintenance = store.Maintenance;
        _configuration = configuration;
        _health = health;
        _dataDirectory = dataDirectory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Name => "storage";

    public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    public IReadOnlyList<WatchdogSignal> WakeSignals => [WatchdogSignal.ConfigChanged];

    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var maxMb = _configuration.Current.Configuration.Retention.MaxDatabaseMb;
        var (securityEvents, outbox) = await _maintenance.EnforceSizeCapAsync(maxMb, cancellationToken).ConfigureAwait(false);
        if (securityEvents + outbox > 0)
        {
            Log.SizeCapPurged(_logger, securityEvents, outbox, maxMb);
        }

        var used = _maintenance.UsedBytes();
        var cap = maxMb * 1024L * 1024L;
        var share = cap > 0 ? (int)(used * 100 / cap) : 0;
        Report("storage.size", share >= 80 ? HealthState.Warning : HealthState.Ok, "Database size relative to Retention.MaxDatabaseMb (percent).", share);

        var free = FreeBytes();
        if (free is { } bytes)
        {
            var low = bytes < MinimumFreeBytes;
            if (low)
            {
                Log.LowDiskSpace(_logger, bytes / (1024 * 1024));
            }

            Report("storage.free", low ? HealthState.Warning : HealthState.Ok, "Free space on the data volume (MiB).", bytes / (1024 * 1024));
        }

        return new JobResult(null);
    }

    private long? FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(_dataDirectory.Path);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void Report(string code, HealthState state, string detail, long value) =>
        _health.Report(new HealthCheckDto
        {
            Code = code,
            State = state,
            Detail = detail,
            Args = [value.ToString(CultureInfo.InvariantCulture)],
            CheckedAt = _timeProvider.GetUtcNow(),
        });

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Warning,
            Message = "Database size cap ({MaxMb} MB) reached: purged {SecurityEvents} security event(s) and {Outbox} dead outbox message(s).")]
        public static partial void SizeCapPurged(ILogger logger, int securityEvents, int outbox, int maxMb);

        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Error, Message = "Low disk space on the data volume: {FreeMb} MiB free.")]
        public static partial void LowDiskSpace(ILogger logger, long freeMb);
    }
}
