using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Retention;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The hourly part of retention (spec 16.3): enforces <c>Retention.MaxDatabaseMb</c> between the daily maintenance runs
/// (security events, then Dead outbox rows, oldest first, down to 90 %), purges the logon and failure records older than
/// <c>Retention.LogonRecordsHours</c> (collector mode, stage B design 7.7) and reports the health checks
/// <c>storage.size</c> and <c>storage.free</c> exactly as the retention job does (<see cref="StorageHealth"/> over the
/// same <see cref="IStoreMaintenance.GetStatusAsync"/>: database file + WAL against the cap, Warning from 80 %, Error
/// above it; Error below 1 GiB free) – two definitions of one check would make it flap every hour.
/// </summary>
public sealed partial class StorageGuardJob : IWatchdogJob
{
    /// <summary>Free space below which <c>storage.free</c> is Error (<see cref="StorageHealth.MinimumFreeBytes"/>).</summary>
    public const long MinimumFreeBytes = StorageHealth.MinimumFreeBytes;

    private readonly SqliteStoreMaintenance _maintenance;
    private readonly IWatchdogConfigProvider _configuration;
    private readonly IHealthRegistry _health;
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
        ArgumentNullException.ThrowIfNull(dataDirectory);      // the free space is measured by the store's own data folder
        _maintenance = store.Maintenance;
        _configuration = configuration;
        _health = health;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Name => "storage";

    public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    public IReadOnlyList<WatchdogSignal> WakeSignals => [WatchdogSignal.ConfigChanged];

    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var retention = _configuration.Current.Configuration.Retention ?? new RetentionSettings();
        var maxMb = retention.MaxDatabaseMb;
        var (securityEvents, outbox) = await _maintenance.EnforceSizeCapAsync(maxMb, cancellationToken).ConfigureAwait(false);
        if (securityEvents + outbox > 0)
        {
            Log.SizeCapPurged(_logger, securityEvents, outbox, maxMb);
        }

        var now = _timeProvider.GetUtcNow();
        var logons = await _maintenance.PurgeLogonRecordsAsync(retention.LogonRecordsHours, now, cancellationToken).ConfigureAwait(false);
        if (logons > 0)
        {
            Log.LogonRecordsPurged(_logger, logons, retention.LogonRecordsHours);
        }

        var status = await _maintenance.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var free = StorageHealth.FreeCheck(status, now);
        if (free.State != HealthState.Ok)
        {
            Log.LowDiskSpace(_logger, status.FreeBytes / (1024 * 1024));
        }

        _health.Report(StorageHealth.SizeCheck(status, retention, now));
        _health.Report(free);
        return new JobResult(null);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Warning,
            Message = "Database size cap ({MaxMb} MB) reached: purged {SecurityEvents} security event(s) and {Outbox} dead outbox message(s).")]
        public static partial void SizeCapPurged(ILogger logger, int securityEvents, int outbox, int maxMb);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Retention: purged {Count} logon record(s) older than {Hours} hours.")]
        public static partial void LogonRecordsPurged(ILogger logger, int count, int hours);

        [LoggerMessage(EventId = EventLogIds.StorageProblem, Level = LogLevel.Error, Message = "Low disk space on the data volume: {FreeMb} MiB free.")]
        public static partial void LowDiskSpace(ILogger logger, long freeMb);
    }
}
