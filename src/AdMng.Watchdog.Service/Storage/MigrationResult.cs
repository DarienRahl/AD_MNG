namespace AdMng.Watchdog.Service.Storage;

/// <summary>Outcome of <see cref="MigrationRunner.Run"/>.</summary>
/// <param name="FromVersion">Schema version found (0 = new database).</param>
/// <param name="ToVersion">Schema version after the run.</param>
/// <param name="BackupPath">The pre-migration backup, when one was written.</param>
public sealed record MigrationResult(int FromVersion, int ToVersion, string? BackupPath)
{
    public bool Migrated => ToVersion != FromVersion;
}
