using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Retention;

/// <summary>
/// <see cref="IWatchdogJob"/> "retention" (section 16.3, job side): daily at <c>Retention.MaintenanceTime</c> (local
/// time of the configured zone) <see cref="IStoreMaintenance.PurgeAsync"/> then <see cref="IStoreMaintenance.BackupAsync"/>
/// – a maintenance missed during a downtime runs at start-up; every hour the storage status is checked: above
/// <c>MaxDatabaseMb</c> the purge runs at once (the store enforces the cap), at ≥ 80 % of the cap or with less than
/// 1 GiB free the health checks turn Warning/Error and one <c>HealthStorage</c> event is stored per Ok → problem
/// transition.
/// </summary>
public sealed partial class RetentionJob : IWatchdogJob
{
    /// <summary>Health check code of the daily maintenance.</summary>
    public const string MaintenanceCode = "storage.maintenance";

    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.ConfigChanged];

    private readonly IStoreMaintenance _maintenance;
    private readonly IEventStore _events;
    private readonly IHealthRegistry _health;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly BusinessCalendarProvider _calendars;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionJob> _logger;
    private bool _started;
    private TimeOnly? _scheduledTime;
    private DateTimeOffset? _nextMaintenance;
    private bool _storageProblem;

    public RetentionJob(IStoreMaintenance maintenance, IEventStore events, IHealthRegistry health, IWatchdogConfigProvider configProvider,
        BusinessCalendarProvider calendars, IWatchdogSignals signals, TimeProvider timeProvider, ILogger<RetentionJob> logger)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(calendars);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _maintenance = maintenance;
        _events = events;
        _health = health;
        _configProvider = configProvider;
        _calendars = calendars;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "retention";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromHours(1);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <summary>When the next daily maintenance runs (null before the first iteration).</summary>
    public DateTimeOffset? NextMaintenanceAt => _nextMaintenance;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var configuration = _configProvider.Current.Configuration;
            var retention = configuration.Retention ?? new RetentionSettings();
            var timeZone = _calendars.For(configuration).TimeZone;
            var status = await _maintenance.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var due = MaintenanceDue(retention, timeZone, status, now);
            if (now >= due)
            {
                await MaintainAsync(retention, now, backup: true, cancellationToken).ConfigureAwait(false);
                _nextMaintenance = Next(retention, timeZone, now);
                status = await _maintenance.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (StorageHealth.IsOverCap(status, retention))
            {
                LogOverCap(StorageHealth.Mb(status.DatabaseBytes), retention.MaxDatabaseMb);
                await MaintainAsync(retention, now, backup: false, cancellationToken).ConfigureAwait(false);
                status = await _maintenance.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }

            await CheckStorageAsync(status, retention, now, cancellationToken).ConfigureAwait(false);
            var wait = _nextMaintenance is { } next ? next - now : DefaultInterval;
            return new JobResult(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > DefaultInterval ? DefaultInterval : wait);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "retention");
            _health.Report(new HealthCheckDto
            {
                Code = MaintenanceCode,
                State = HealthState.Error,
                Args = [SafeExceptionLog.ErrorCode(ex)],
                CheckedAt = _timeProvider.GetUtcNow(),
            });
            return new JobResult(DefaultInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    /// <summary>The next maintenance time; at start-up a maintenance missed since the last backup is due at once.</summary>
    private DateTimeOffset MaintenanceDue(RetentionSettings retention, TimeZoneInfo timeZone, StorageStatusDto status, DateTimeOffset now)
    {
        if (_started && _scheduledTime == retention.MaintenanceTime && _nextMaintenance is { } scheduled)
        {
            return scheduled;
        }

        var firstRun = !_started;
        _started = true;
        _scheduledTime = retention.MaintenanceTime;
        var next = Next(retention, timeZone, now);
        var previous = Schedule.PreviousOccurrence(retention.MaintenanceTime, Enum.GetValues<DayOfWeek>(), timeZone, now);
        _nextMaintenance = firstRun && previous is { } missed && (status.LastBackupAt is null || status.LastBackupAt < missed) ? now : next;
        return _nextMaintenance.Value;
    }

    private static DateTimeOffset Next(RetentionSettings retention, TimeZoneInfo timeZone, DateTimeOffset now) =>
        Schedule.NextOccurrence(retention.MaintenanceTime, Enum.GetValues<DayOfWeek>(), timeZone, now) ?? now.AddDays(1);

    private async Task MaintainAsync(RetentionSettings retention, DateTimeOffset now, bool backup, CancellationToken cancellationToken)
    {
        var purged = await _maintenance.PurgeAsync(retention, now, cancellationToken).ConfigureAwait(false);
        LogPurged(purged.Events, purged.SecurityEvents, purged.OutboxMessages, purged.Snapshots, purged.Notices, purged.AuditEntries);
        if (backup)
        {
            var file = await _maintenance.BackupAsync(cancellationToken).ConfigureAwait(false);
            LogBackup(Path.GetFileName(file));
        }

        _health.Report(new HealthCheckDto { Code = MaintenanceCode, State = HealthState.Ok, CheckedAt = now });
    }

    private async Task CheckStorageAsync(StorageStatusDto status, RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var size = StorageHealth.SizeCheck(status, retention, now);
        var free = StorageHealth.FreeCheck(status, now);
        _health.Report(size);
        _health.Report(free);
        var problem = size.State != HealthState.Ok || free.State != HealthState.Ok;
        if (problem && !_storageProblem)
        {
            var code = free.State != HealthState.Ok ? StorageHealth.FreeCode : StorageHealth.SizeCode;
            await _events.AppendAsync(HealthEvents.Create(WatchEventKind.HealthStorage, now,
                ("code", code),
                ("databaseMb", StorageHealth.Mb(status.DatabaseBytes)),
                ("limitMb", StorageHealth.Mb(StorageHealth.LimitBytes(retention))),
                ("freeMb", StorageHealth.Mb(status.FreeBytes))), cancellationToken).ConfigureAwait(false);
            _signals.Raise(WatchdogSignal.EventsStored);
            LogStorageProblem(code);
        }

        _storageProblem = problem;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention purged {Events} events, {SecurityEvents} security events, {Outbox} outbox messages, {Snapshots} snapshots, {Notices} notices, {Audit} audit entries.")]
    private partial void LogPurged(int events, int securityEvents, int outbox, int snapshots, int notices, int audit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database backup written: {File}.")]
    private partial void LogBackup(string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database ({SizeMb} MB) exceeds Retention.MaxDatabaseMb ({LimitMb} MB); purging now.")]
    private partial void LogOverCap(string sizeMb, int limitMb);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage needs attention ({Code}).")]
    private partial void LogStorageProblem(string code);
}
