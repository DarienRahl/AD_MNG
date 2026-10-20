using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Storage.Migrations;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Collector;

/// <summary>Migration 0004 and the hourly purge of logon records (stage B design 7.7).</summary>
public sealed class SecurityEventsV2StorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration0004_KeepsTheRows_AndChangesUniquenessAndIndexes()
    {
        using var directory = new TestDataDirectory();
        var database = new SqliteDatabase(directory.Path);
        new MigrationRunner(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance, [.. Migration.All.Take(3)]).Run();
        var record = new SecurityEvent
        {
            Source = "dc01.contoso.test",
            Computer = "dc01.contoso.test",
            RecordId = 42,
            EventCode = 4724,
            TimeCreated = TestStore.Start,
            Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["TargetUserName"] = "zofia.żak", ["SubjectLogonId"] = "0x5a3f1" },
        };
        Execute(database, "INSERT INTO security_events (computer, record_id, source, event_code, time_created, target_user, subject_logon_id, data) "
            + "VALUES ('dc01.contoso.test', 42, 'dc01.contoso.test', 4724, $time, 'zofia.żak', '0x5a3f1', $data);",
            ("$time", TestStore.Start.ToUnixTimeMilliseconds()), ("$data", BlobCodec.EncodeSecurityEvent(record)));

        var result = new MigrationRunner(database, new FakeTimeProvider(TestStore.Start), NullLogger<MigrationRunner>.Instance).Run();

        Assert.Equal((3, Migration0004SecurityEventsV2.Version), (result.FromVersion, result.ToVersion));
        Assert.NotNull(result.BackupPath);   // the pre-migration backup of spec 16.1
        Assert.Equal(1L, Scalar(database, "SELECT count(*) FROM pragma_table_info('security_events') WHERE name = 'logon_id';"));
        Assert.Equal(2L, Scalar(database, "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('ix_sec_logon', 'ix_sec_user_code');"));
        Assert.Equal(4L, Scalar(database, "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('ix_sec_target', 'ix_sec_object', 'ix_sec_code', 'ix_sec_time');"));

        var store = new SqliteSecurityEventStore(database);
        var kept = Assert.Single(await store.FindAsync(new SecurityEventCriteria { From = TestStore.Start, To = TestStore.Start }, Ct));
        Assert.Equal("zofia.żak", kept.Get("TargetUserName"));

        // The same record number at another time (a cleared log) is another record now.
        await store.AppendAsync([record, record with { EventCode = 4725, TimeCreated = TestStore.Start.AddHours(1) }], new EventLogCursor { Source = "x" }, Ct);
        Assert.Equal(2L, Scalar(database, "SELECT count(*) FROM security_events;"));
    }

    [Fact]
    public async Task TheLogonIdColumn_HoldsOnlyTheTargetLogonIdOf4624()
    {
        await using var test = TestStore.Create();
        var store = test.Store;
        await store.AppendAsync(
        [
            Record(1, 4624, ("TargetLogonId", "0x0005A3F1"), ("SubjectLogonId", "0x0")),
            Record(2, 4724, ("SubjectLogonId", "0x5a3f1")),
        ], new EventLogCursor { Source = "wec:DC01@CONTOSO.TEST" }, Ct);

        Assert.Equal(1L, Scalar(test.Database, "SELECT count(*) FROM security_events WHERE logon_id = '0x5a3f1';"));
        Assert.Equal(1L, Scalar(test.Database, "SELECT count(*) FROM security_events WHERE logon_id IS NULL;"));
    }

    [Fact]
    public async Task StorageGuard_PurgesLogonRecordsAfterLogonRecordsHours_ButNotChanges()
    {
        await using var test = TestStore.Create();
        var now = TestStore.Start;
        await test.Store.AppendAsync(
        [
            Record(1, 4624, now.AddHours(-49)), Record(2, 4625, now.AddHours(-49)), Record(3, 4771, now.AddHours(-60)),
            Record(4, 4776, now.AddHours(-47)), Record(5, 4724, now.AddHours(-60)), Record(6, 5136, now.AddDays(-20)),
        ], new EventLogCursor { Source = "wec:DC01@CONTOSO.TEST" }, Ct);

        var purged = await test.Store.Maintenance.PurgeLogonRecordsAsync(new RetentionSettings().LogonRecordsHours, now, Ct);

        Assert.Equal(3, purged);
        Assert.Equal([4, 5, 6], (await test.Store.FindAsync(new SecurityEventCriteria { From = now.AddDays(-30), To = now }, Ct)).Select(e => e.RecordId).Order());
    }

    private static SecurityEvent Record(long id, int code, params (string Name, string Value)[] data) => Record(id, code, TestStore.Start, data);

    private static SecurityEvent Record(long id, int code, DateTimeOffset time, params (string Name, string Value)[] data) => new()
    {
        Source = "dc01.contoso.test",
        Computer = "dc01.contoso.test",
        RecordId = id,
        EventCode = code,
        TimeCreated = time,
        Data = data.ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal),
    };

    private static void Execute(SqliteDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
