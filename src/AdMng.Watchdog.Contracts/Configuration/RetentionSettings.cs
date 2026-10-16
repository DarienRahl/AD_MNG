namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Retention and maintenance of the service store (section 16.3).</summary>
public sealed record RetentionSettings
{
    public int EventsDays { get; init; } = 365;
    public int SecurityEventsDays { get; init; } = 30;
    public int OutboxSentDays { get; init; } = 30;
    public int OutboxDeadDays { get; init; } = 90;
    public int DeletedSnapshotDays { get; init; } = 180;
    public int ConfigAuditDays { get; init; } = 1095;
    public int BackupsToKeep { get; init; } = 7;
    /// <summary>Pre-migration backups older than this are deleted (the newest one is always kept).</summary>
    public int PreMigrationBackupDays { get; init; } = 30;
    /// <summary>Size cap of watchdog.db; security_events are purged oldest-first when reached, HealthStorage at 80 %.</summary>
    public int MaxDatabaseMb { get; init; } = 10_240;
    public TimeOnly MaintenanceTime { get; init; } = new(3, 15);
}
