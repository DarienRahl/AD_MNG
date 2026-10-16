using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Storage.Migrations;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Storage;

public sealed class MigrationRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FreshDatabase_GetsWalIncrementalAutoVacuumAndTheLatestSchema_WithoutABackup()
    {
        using var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);

        var result = Runner(database).Run();

        Assert.Equal(0, result.FromVersion);
        Assert.Equal(Migration.LatestVersion, result.ToVersion);
        Assert.Null(result.BackupPath);
        Assert.Equal(Migration.LatestVersion, Pragma<long>(database, "user_version"));
        Assert.Equal("wal", Pragma<string>(database, "journal_mode"));
        Assert.Equal(2, Pragma<long>(database, "auto_vacuum"));   // INCREMENTAL
        Assert.False(System.IO.Directory.Exists(database.BackupDirectory));
    }

    [Fact]
    public void SecondRun_IsANoOp()
    {
        using var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);
        Runner(database).Run();

        var again = Runner(database).Run();

        Assert.False(again.Migrated);
        Assert.Null(again.BackupPath);
    }

    [Fact]
    public async Task Upgrade_WritesAPreMigrationBackupWithTheOldData_ThenApplies()
    {
        await using var store = TestStore.Create();
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        var next = new Migration(2, "0002_test", "CREATE TABLE test_extra (id INTEGER PRIMARY KEY, note TEXT NOT NULL);");
        store.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 2, 21, 5, 9, TimeSpan.Zero));

        var result = new MigrationRunner(store.Database, store.Clock, NullLogger<MigrationRunner>.Instance, [.. Migration.All, next]).Run();

        Assert.Equal(1, result.FromVersion);
        Assert.Equal(2, result.ToVersion);
        Assert.NotNull(result.BackupPath);
        Assert.Equal("pre-migration-1-20261102210509.db", Path.GetFileName(result.BackupPath));
        Assert.Equal(2, Pragma<long>(store.Database, "user_version"));
        Assert.True(File.Exists(result.BackupPath));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={result.BackupPath};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM events;";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, (long)version.ExecuteScalar()!);
        }
    }

    [Fact]
    public void NewerDatabase_IsRefused()
    {
        using var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);
        Runner(database).Run();
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<DatabaseVersionTooNewException>(() => Runner(database).Run());

        Assert.Equal(99, error.DatabaseVersion);
        Assert.Equal(Migration.LatestVersion, error.SupportedVersion);
    }

    private static MigrationRunner Runner(SqliteDatabase database) =>
        new(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance);

    private static T Pragma<T>(SqliteDatabase database, string name)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return (T)command.ExecuteScalar()!;
    }
}
