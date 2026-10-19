using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
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
        var current = Migration.LatestVersion;
        var next = new Migration(current + 1, "9999_test", "CREATE TABLE test_extra (id INTEGER PRIMARY KEY, note TEXT NOT NULL);");
        store.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 2, 21, 5, 9, TimeSpan.Zero));

        var result = new MigrationRunner(store.Database, store.Clock, NullLogger<MigrationRunner>.Instance, [.. Migration.All, next]).Run();

        Assert.Equal(current, result.FromVersion);
        Assert.Equal(current + 1, result.ToVersion);
        Assert.NotNull(result.BackupPath);
        Assert.Equal($"pre-migration-{current}-20261102210509.db", Path.GetFileName(result.BackupPath));
        Assert.Equal(current + 1, Pragma<long>(store.Database, "user_version"));
        Assert.True(File.Exists(result.BackupPath));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={result.BackupPath};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM events;";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal((long)current, (long)version.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task SchemaOne_KeepsTheSeverityAsTheOwnSeverity_AndAlertLinksWithoutAPerEventState()
    {
        // Schema 1 stored only the evaluated severity (rule overrides included); its own severity is not known any more,
        // so it keeps the current value – the input of later matches (dispatch re-match, re-evaluation).
        // Its alert links have no per-event state (schema 3): the message's severity and pending flag stand for them.
        var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);
        new MigrationRunner(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance, [Migration.All[0]]).Run();
        var evaluated = TestData.Event(WatchEventKind.KeyCredentialChanged) with { Severity = Severity.High, MatchedRules = ["konta-vip"] };
        InsertSchemaOneEvent(database, evaluated);
        using (var connection = database.Open())
        {
            using var alert = connection.CreateCommand();
            alert.CommandText = """
                INSERT INTO outbox (message_id, channel_type, channel_id, kind, rule_id, severity, status, attempts, created_at,
                  next_attempt_at, target_key, sent_with_pending)
                VALUES ('0192f3a45b6c7d8e9f00112233445566', $email, 'email', $alert, 'konta-vip', $high, $sent, 1, 0, 0, 'email|0', 1);
                INSERT INTO outbox_events (outbox_id, event_id) VALUES (last_insert_rowid(), (SELECT max(id) FROM events));
                """;
            alert.Parameters.AddWithValue("$email", (int)AdMng.Watchdog.Contracts.Api.DeliveryChannelType.Email);
            alert.Parameters.AddWithValue("$alert", (int)AdMng.Watchdog.Contracts.Api.DeliveryKind.Alert);
            alert.Parameters.AddWithValue("$high", (int)Severity.High);
            alert.Parameters.AddWithValue("$sent", (int)AdMng.Watchdog.Contracts.Api.OutboxStatus.Sent);
            alert.ExecuteNonQuery();
        }

        await using var store = TestStore.Create(directory);
        var loaded = Assert.Single((await store.Store.QueryAsync(new AdMng.Watchdog.Contracts.Api.EventQuery { Limit = 10 }, Ct)).Items);

        Assert.Equal(Migration.LatestVersion, Pragma<long>(store.Database, "user_version"));
        Assert.Equal((Severity.High, Severity.High), (loaded.OwnSeverity, loaded.Severity));
        Assert.Equal(["konta-vip"], loaded.MatchedRules);
        var message = Assert.Single(await store.Store.GetMessagesForEventAsync(loaded.Id, Ct));
        Assert.Equal([loaded.Id], message.EventIds);
        Assert.Equal((Severity.High, true), (message.Severity, message.SentWithPendingAttribution));
        Assert.Empty(message.EventStates);

        // The migration changes the schema only – no row of the events table is rewritten at service start (a full-table
        // UPDATE would copy the table into the WAL); the row is read with its payload severity.
        Assert.Null(OwnSeverityColumn(store.Database, loaded.Id));

        // A later evaluation keeps that own severity (the payload now carries it), an attribution update stores it.
        await store.Store.CommitEvaluationAsync([new EventEvaluation { EventId = loaded.Id, Severity = Severity.Critical, MatchedRules = ["konta-vip", "przeglad"] }], [], Ct);
        var reevaluated = (await store.Store.GetAsync(loaded.Id, Ct))!;
        Assert.Equal((Severity.High, Severity.Critical), (reevaluated.OwnSeverity, reevaluated.Severity));
        Assert.Null(OwnSeverityColumn(store.Database, loaded.Id));
        await store.Store.UpdateAttributionAsync(
            loaded.Id,
            new AttributionUpdate { Attribution = new AdMng.Watchdog.Contracts.Events.Attribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.NoMatch } },
            Ct);
        var attributed = (await store.Store.GetAsync(loaded.Id, Ct))!;
        Assert.Equal((Severity.High, Severity.Critical), (attributed.OwnSeverity, attributed.Severity));
        Assert.Equal((long)Severity.High, OwnSeverityColumn(store.Database, loaded.Id));
    }

    [Fact]
    public void AMigrationFailingHalfway_IsRolledBackWhole_AndTheNextStartAppliesIt()
    {
        // A crash or an error in the middle of a migration (here: its second statement) must leave the database at the
        // previous version with none of the migration's changes, so the next start simply applies it again.
        using var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);
        new MigrationRunner(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance, [Migration.All[0]]).Run();
        InsertSchemaOneEvent(database, TestData.Event(WatchEventKind.UserCreated) with { Severity = Severity.Medium });

        var failing = new Migration(2, "0002_failing", Migration0002OwnSeverity.Sql + "\nINSERT INTO no_such_table VALUES (1);");
        var runner = new MigrationRunner(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance, [Migration.All[0], failing]);

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => runner.Run());
        Assert.Equal(1L, Pragma<long>(database, "user_version"));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM pragma_table_info('events') WHERE name = 'own_severity';"));

        // The service restarts later (the install script's recovery waits 60 s), so its pre-migration backup gets a new name.
        var result = new MigrationRunner(database, new FakeTimeProvider(TestStore.Start.AddMinutes(1)), NullLogger<MigrationRunner>.Instance).Run();

        Assert.Equal((1, Migration.LatestVersion), (result.FromVersion, result.ToVersion));
        Assert.Equal(1L, Scalar(database, "SELECT count(*) FROM pragma_table_info('events') WHERE name = 'own_severity';"));
        Assert.Equal(2L, Scalar(database, "SELECT count(*) FROM pragma_table_info('outbox_events') WHERE name IN ('severity', 'pending');"));
        Assert.Equal(1L, Scalar(database, "SELECT count(*) FROM events;"));
    }

    /// <summary>One evaluated event row as schema 1 stored it (no own severity anywhere).</summary>
    private static void InsertSchemaOneEvent(SqliteDatabase database, WatchEvent watchEvent)
    {
        using var connection = database.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO events (event_id, kind, severity, object_kind, object_dn, object_dn_norm, search_text, detected_at, source,
              actor_status, rules_state, matched_rules, payload)
            VALUES ($eventId, $kind, $severity, $objectKind, $dn, $dn, '', $detectedAt, $source, $actorStatus, 1, $rules, $payload);
            """;
        insert.Parameters.AddWithValue("$eventId", watchEvent.EventId.ToByteArray());
        insert.Parameters.AddWithValue("$kind", (int)watchEvent.Kind);
        insert.Parameters.AddWithValue("$severity", (int)watchEvent.Severity);
        insert.Parameters.AddWithValue("$objectKind", (int)watchEvent.Object.Kind);
        insert.Parameters.AddWithValue("$dn", watchEvent.Object.Dn ?? string.Empty);
        insert.Parameters.AddWithValue("$detectedAt", watchEvent.DetectedAt.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$source", (int)watchEvent.Source);
        insert.Parameters.AddWithValue("$actorStatus", (int)watchEvent.Attribution.Status);
        insert.Parameters.AddWithValue("$rules", watchEvent.MatchedRules.Count == 0 ? DBNull.Value : string.Join('|', watchEvent.MatchedRules));
        insert.Parameters.AddWithValue("$payload", BlobCodec.EncodeContract(watchEvent with { Id = 0, OwnSeverity = null }));
        insert.ExecuteNonQuery();
    }

    private static long? OwnSeverityColumn(SqliteDatabase database, long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT own_severity FROM events WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long value ? value : null;
    }

    private static long Scalar(SqliteDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
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
