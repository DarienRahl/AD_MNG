using System.Globalization;
using AdMng.Watchdog.Service.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// Brings the database to the newest schema (spec 16.1): a new file gets <c>auto_vacuum = INCREMENTAL</c> and
/// <c>journal_mode = WAL</c> before its first table; an existing non-empty database is copied with
/// <c>VACUUM INTO &lt;data&gt;/backup/pre-migration-&lt;old&gt;-&lt;timestamp&gt;.db</c> before any migration; each migration runs
/// in one transaction followed by <c>PRAGMA user_version</c>. A database newer than this build is refused
/// (<see cref="DatabaseVersionTooNewException"/>, exit code 3).
/// </summary>
public sealed partial class MigrationRunner
{
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner(SqliteDatabase database, TimeProvider timeProvider, ILogger<MigrationRunner> logger)
        : this(database, timeProvider, logger, Migration.All)
    {
    }

    /// <summary>Tests: a custom migration list (ascending versions).</summary>
    internal MigrationRunner(SqliteDatabase database, TimeProvider timeProvider, ILogger<MigrationRunner> logger, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0)
        {
            throw new ArgumentException("At least one migration is required.", nameof(migrations));
        }

        _database = database;
        _timeProvider = timeProvider;
        _logger = logger;
        _migrations = migrations;
    }

    /// <summary>The newest schema version of this build.</summary>
    public int LatestVersion => _migrations[^1].Version;

    /// <summary>Migrates the database; returns the schema versions before and after and the pre-migration backup.</summary>
    /// <exception cref="DatabaseVersionTooNewException">The database is newer than this build.</exception>
    public MigrationResult Run()
    {
        using var connection = _database.Open();
        var current = UserVersion(connection);
        if (current > LatestVersion)
        {
            throw new DatabaseVersionTooNewException(current, LatestVersion);
        }

        if (current == LatestVersion)
        {
            EnsureWal(connection);
            return new MigrationResult(current, current, null);
        }

        string? backup = null;
        if (current == 0 && !HasTables(connection))
        {
            // Must precede the first table: auto_vacuum cannot be switched on later without a full VACUUM.
            SqliteValues.Execute(connection, null, "PRAGMA auto_vacuum = INCREMENTAL;");
        }
        else
        {
            backup = BackupBeforeMigration(connection, current);
        }

        EnsureWal(connection);
        foreach (var migration in _migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            SqliteValues.Execute(connection, transaction, migration.Sql);
            SqliteValues.Execute(connection, transaction, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};"));
            transaction.Commit();
            Log.MigrationApplied(_logger, migration.Name, migration.Version);
        }

        return new MigrationResult(current, LatestVersion, backup);
    }

    private string BackupBeforeMigration(SqliteConnection connection, int current)
    {
        var files = new BackupFiles(_database.BackupDirectory);
        files.EnsureDirectory();
        var path = files.NewPreMigrationBackupPath(current, _timeProvider.GetUtcNow());
        SqliteValues.Execute(connection, null, "VACUUM INTO $path;", ("$path", path));
        Log.PreMigrationBackup(_logger, current, Path.GetFileName(path));
        return path;
    }

    private static void EnsureWal(SqliteConnection connection)
    {
        // Persistent; a no-op when the file is already in WAL mode.
        using var command = SqliteValues.Command(connection, null, "PRAGMA journal_mode = WAL;");
        command.ExecuteScalar();
    }

    private static int UserVersion(SqliteConnection connection) =>
        (int)(SqliteValues.Scalar<long>(connection, null, "PRAGMA user_version;"));

    private static bool HasTables(SqliteConnection connection) =>
        SqliteValues.Scalar<long>(connection, null, "SELECT count(*) FROM sqlite_master WHERE type = 'table';") > 0;

    private static partial class Log
    {
        [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "Database migration {Name} applied (schema version {Version}).")]
        public static partial void MigrationApplied(ILogger logger, string name, int version);

        [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Pre-migration backup of schema version {Version} written to {FileName}.")]
        public static partial void PreMigrationBackup(ILogger logger, int version, string fileName);
    }
}
