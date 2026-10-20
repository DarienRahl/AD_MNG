namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Retention and maintenance of the service store (section 16.3; defaults of the Linux service: stage B design 14.2).</summary>
public sealed record RetentionSettings
{
    /// <summary>Watch events (who / what / when / from where / old → new): 3 years. Never below the root-owned bootstrap floor
    /// <c>Watchdog:Retention:MinEventsDays</c> (validator code <c>retentionBelowFloor</c>, stage B design 14.2).</summary>
    public int EventsDays { get; init; } = 1095;

    /// <summary>Change-related DC log records (the ingestion set of 11.2).</summary>
    public int SecurityEventsDays { get; init; } = 30;

    /// <summary>Logon and failure records 4624/4625/4771/4776 (collector mode, stage B design 7.7): range 12–720 and at least
    /// <c>Attribution.LogonLookback</c> + the last <c>Attribution.PendingRetries</c> step (validator code
    /// <c>logonRetentionTooShort</c>); purged hourly.</summary>
    public int LogonRecordsHours { get; init; } = 48;

    public int OutboxSentDays { get; init; } = 30;
    public int OutboxDeadDays { get; init; } = 90;
    public int DeletedSnapshotDays { get; init; } = 180;
    public int ConfigAuditDays { get; init; } = 1095;
    public int BackupsToKeep { get; init; } = 7;
    /// <summary>Pre-migration backups older than this are deleted (the newest one is always kept).</summary>
    public int PreMigrationBackupDays { get; init; } = 30;
    /// <summary>Size cap of watchdog.db; security_events are purged oldest-first when reached, HealthStorage at 80 %.
    /// 4096 so that the database and <see cref="BackupsToKeep"/> nightly copies fit the recommended 40 GB <c>/var</c>
    /// (stage B design 14.2; spec 5.4 had 10 240).</summary>
    public int MaxDatabaseMb { get; init; } = 4096;
    public TimeOnly MaintenanceTime { get; init; } = new(3, 15);
}
