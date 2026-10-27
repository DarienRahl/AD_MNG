using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="IStoreMaintenance"/> (spec 16.3): retention purge in batches of 5 000 rows per transaction (the writer gate
/// is released between batches; event deletes cascade to pending notifications and outbox links), the size cap (oldest
/// Security events, then dead outbox messages, until below 90 % of <see cref="RetentionSettings.MaxDatabaseMb"/>),
/// <c>incremental_vacuum</c> in steps (the gate is released between them) + <c>wal_checkpoint(TRUNCATE)</c> +
/// <c>optimize</c> (with <c>secure_delete</c> the freed pages and the truncated WAL no longer hold purged values),
/// <c>VACUUM INTO</c> backups outside the writer gate and their rotation.
/// </summary>
public sealed class SqliteStoreMaintenance : IStoreMaintenance
{
    /// <summary>Rows deleted per transaction.</summary>
    public const int BatchSize = 5000;

    /// <summary>Rows deleted per transaction by the size cap (finer, so it stops close to 90 %).</summary>
    public const int TrimBatchSize = 500;

    /// <summary>Expiry notices are kept this long after the expiry passed.</summary>
    public static readonly TimeSpan NoticeRetention = TimeSpan.FromDays(30);

    /// <summary>Digest runs are kept for a year.</summary>
    public static readonly TimeSpan DigestRunRetention = TimeSpan.FromDays(365);

    /// <summary>Free pages released per <c>incremental_vacuum</c> step (8 MiB with 4 KiB pages – milliseconds behind the
    /// writer gate).</summary>
    public const int VacuumStepPages = 2048;

    private readonly SemaphoreSlim _backupGate = new(1, 1);
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly BackupFiles _backups;
    private int _backupsToKeep = new RetentionSettings().BackupsToKeep;

    internal SqliteStoreMaintenance(SqliteDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
        _backups = new BackupFiles(database.BackupDirectory);
    }

    /// <summary>
    /// Free bytes on the volume of a folder (−1 when it cannot be measured) for <see cref="GetStatusAsync"/>. Tests: a
    /// fixed value – the real volume is shared with every other process, so two measurements may differ.
    /// </summary>
    internal Func<string, long> FreeSpaceProbe { get; set; } = FreeSpace;

    public async Task<PurgeResult> PurgeAsync(RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        Volatile.Write(ref _backupsToKeep, Math.Max(1, retention.BackupsToKeep));

        var events = await DeleteInBatchesAsync("events", "rowid", "detected_at < $cutoff", Cutoff(now, retention.EventsDays), cancellationToken).ConfigureAwait(false);
        var securityEvents = await DeleteInBatchesAsync("security_events", "rowid", "time_created < $cutoff", Cutoff(now, retention.SecurityEventsDays), cancellationToken).ConfigureAwait(false);
        var outbox = await DeleteInBatchesAsync(
            "outbox",
            "rowid",
            string.Create(CultureInfo.InvariantCulture, $"status = {(int)OutboxStatus.Sent} AND sent_at < $cutoff"),
            Cutoff(now, retention.OutboxSentDays),
            cancellationToken).ConfigureAwait(false);
        outbox += await DeleteInBatchesAsync(
            "outbox",
            "rowid",
            string.Create(CultureInfo.InvariantCulture, $"status IN ({(int)OutboxStatus.Dead}, {(int)OutboxStatus.Cancelled}) AND created_at < $cutoff"),
            Cutoff(now, retention.OutboxDeadDays),
            cancellationToken).ConfigureAwait(false);
        var snapshots = await PurgeDeletedSnapshotsAsync(Cutoff(now, retention.DeletedSnapshotDays), cancellationToken).ConfigureAwait(false);
        snapshots += await _database.WriteAsync(
            (connection, transaction) => SqliteGpoContentStore.PurgeDeleted(connection, transaction, Cutoff(now, retention.DeletedSnapshotDays)),
            cancellationToken).ConfigureAwait(false);
        var notices = await DeleteInBatchesAsync(
            "expiry_notices",
            "(object_guid, kind, expires_raw, threshold_days)",
            "expires_raw < $cutoff",
            (now - NoticeRetention).ToFileTime(),
            cancellationToken).ConfigureAwait(false);
        var audit = await DeleteInBatchesAsync("config_audit", "rowid", "at < $cutoff", Cutoff(now, retention.ConfigAuditDays), cancellationToken).ConfigureAwait(false);
        await DeleteInBatchesAsync("digest_runs", "rowid", "created_at < $cutoff", SqliteValues.ToUnixMs(now - DigestRunRetention), cancellationToken).ConfigureAwait(false);
        await DeleteInBatchesAsync("user_messages", "rowid", "submitted_at < $cutoff", Cutoff(now, retention.UserMessageAuditDays), cancellationToken).ConfigureAwait(false);

        var cap = await EnforceSizeCapAsync(retention.MaxDatabaseMb, cancellationToken).ConfigureAwait(false);
        await CompactAsync(cancellationToken).ConfigureAwait(false);
        _backups.RotatePreMigrationBackups(TimeSpan.FromDays(Math.Max(0, retention.PreMigrationBackupDays)), now);
        return new PurgeResult(events, securityEvents + cap.SecurityEvents, outbox + cap.OutboxMessages, snapshots, notices, audit);
    }

    /// <summary>
    /// <c>VACUUM INTO</c> a new backup file on its own connection, outside the single-writer gate: in WAL mode it is a read
    /// transaction on a consistent snapshot, so outbox, detection and API writes go on while a multi-GB database is copied.
    /// Only the <c>LastBackupAt</c> update takes the gate; backups themselves are serialized.
    /// </summary>
    public async Task<string> BackupAsync(CancellationToken cancellationToken)
    {
        await _backupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _backups.EnsureDirectory();
            var now = _timeProvider.GetUtcNow();
            var path = _backups.NewRetentionBackupPath(now);
            await Task.Run(() => _database.Read(c => SqliteValues.Execute(c, null, "VACUUM INTO $path;", ("$path", path))), cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await _database.WriteAsync(
                (c, t) => MetaRows.Set(c, t, MetaRows.LastBackupAt, SqliteValues.ToUnixMs(now).ToString(CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);
            _backups.RotateRetentionBackups(Volatile.Read(ref _backupsToKeep));
            return Path.GetFileName(path);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public Task<StorageStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (count, oldest, lastBackup) = _database.Read(c => (
            SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM events;"),
            SqliteValues.Scalar<long?>(c, null, "SELECT min(detected_at) FROM events;"),
            MetaRows.Get(c, null, MetaRows.LastBackupAt)));
        return Task.FromResult(new StorageStatusDto
        {
            DataDirectory = _database.DataDirectory,
            DatabaseBytes = FileSize(_database.DatabasePath) + FileSize(_database.DatabasePath + "-wal"),
            FreeBytes = FreeSpaceProbe(_database.DataDirectory),
            EventCount = count,
            OldestEventAt = oldest is { } o ? SqliteValues.FromUnixMs(o) : null,
            LastBackupAt = long.TryParse(lastBackup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? SqliteValues.FromUnixMs(ms) : null,
        });
    }

    /// <summary>Bytes held by live pages (<c>(page_count − freelist_count) × page_size</c>).</summary>
    public long UsedBytes() => _database.Read(UsedBytes);

    /// <summary>
    /// The size cap (also run hourly by the storage guard): when the live data exceeds <paramref name="maxDatabaseMb"/>,
    /// the oldest Security events and then dead outbox messages are deleted until it is below 90 % of the cap.
    /// </summary>
    public async Task<(int SecurityEvents, int OutboxMessages)> EnforceSizeCapAsync(int maxDatabaseMb, CancellationToken cancellationToken)
    {
        var limit = (long)Math.Max(1, maxDatabaseMb) * 1024 * 1024;
        if (UsedBytes() <= limit)
        {
            return (0, 0);
        }

        var target = limit * 9 / 10;
        var securityEvents = await TrimOldestAsync("security_events", "time_created", "1 = 1", target, cancellationToken).ConfigureAwait(false);
        var outbox = await TrimOldestAsync("outbox", "created_at", string.Create(CultureInfo.InvariantCulture, $"status = {(int)OutboxStatus.Dead}"), target, cancellationToken)
            .ConfigureAwait(false);
        return (securityEvents, outbox);
    }

    /// <summary><c>PRAGMA incremental_vacuum(N)</c> in steps of <see cref="VacuumStepPages"/> pages – the writer gate is
    /// released between the steps, so deliveries are not held up by a large freelist – then
    /// <c>wal_checkpoint(TRUNCATE); optimize;</c> behind the gate.</summary>
    public async Task CompactAsync(CancellationToken cancellationToken)
    {
        while (await _database.ExclusiveAsync(
            connection =>
            {
                var before = FreePages(connection);
                if (before == 0)
                {
                    return false;
                }

                SqliteValues.Execute(connection, null, string.Create(CultureInfo.InvariantCulture, $"PRAGMA incremental_vacuum({VacuumStepPages});"));
                return FreePages(connection) < before;      // no progress (auto_vacuum off): stop
            },
            cancellationToken).ConfigureAwait(false))
        {
        }

        await _database.ExclusiveAsync(
            connection =>
            {
                using (var checkpoint = SqliteValues.Command(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);"))
                {
                    using var reader = checkpoint.ExecuteReader();
                    while (reader.Read())
                    {
                    }
                }

                SqliteValues.Execute(connection, null, "PRAGMA optimize;");
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static long FreePages(SqliteConnection connection) => SqliteValues.Scalar<long>(connection, null, "PRAGMA freelist_count;");

    private async Task<int> TrimOldestAsync(string table, string orderColumn, string where, long targetBytes, CancellationToken cancellationToken)
    {
        var total = 0;
        while (UsedBytes() > targetBytes)
        {
            var deleted = await _database.WriteAsync(
                (c, t) => SqliteValues.Execute(
                    c,
                    t,
                    $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {where} ORDER BY {orderColumn} LIMIT {TrimBatchSize});"),
                cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted == 0)
            {
                break;
            }
        }

        return total;
    }

    private async Task<int> DeleteInBatchesAsync(string table, string key, string where, long cutoff, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var deleted = await _database.WriteAsync(
                (c, t) => SqliteValues.Execute(
                    c,
                    t,
                    $"DELETE FROM {table} WHERE {key} IN (SELECT {key.Trim('(', ')')} FROM {table} WHERE {where} LIMIT {BatchSize});",
                    ("$cutoff", cutoff)),
                cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < BatchSize)
            {
                return total;
            }
        }
    }

    private async Task<int> PurgeDeletedSnapshotsAsync(long cutoff, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var deleted = await _database.WriteAsync(
                (connection, transaction) =>
                {
                    var guids = SqliteValues.Query(
                        connection,
                        transaction,
                        $"SELECT object_guid FROM snapshot_objects WHERE is_deleted = 1 AND deleted_at < $cutoff LIMIT {BatchSize};",
                        r => (byte[])r.GetValue(0),
                        ("$cutoff", cutoff));
                    foreach (var guid in guids)
                    {
                        SqliteValues.Execute(connection, transaction, "DELETE FROM snapshot_members WHERE group_guid = $guid;", ("$guid", guid));
                        SqliteValues.Execute(connection, transaction, "DELETE FROM snapshot_objects WHERE object_guid = $guid;", ("$guid", guid));
                    }

                    return guids.Count;
                },
                cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < BatchSize)
            {
                return total;
            }
        }
    }

    private static long UsedBytes(SqliteConnection connection)
    {
        var pages = SqliteValues.Scalar<long>(connection, null, "PRAGMA page_count;");
        var free = SqliteValues.Scalar<long>(connection, null, "PRAGMA freelist_count;");
        var size = SqliteValues.Scalar<long>(connection, null, "PRAGMA page_size;");
        return (pages - free) * size;
    }

    /// <summary>
    /// Collector mode (stage B design 7.7): the logon and failure records (4624, 4625, 4771, 4776) older than
    /// <paramref name="hours"/> – run hourly by <see cref="StorageGuardJob"/>, in batches of <see cref="BatchSize"/> (with
    /// <c>secure_delete</c>). Returns the deleted rows.
    /// </summary>
    public Task<int> PurgeLogonRecordsAsync(int hours, DateTimeOffset now, CancellationToken cancellationToken) =>
        DeleteInBatchesAsync(
            "security_events",
            "rowid",
            string.Create(CultureInfo.InvariantCulture,
                $"event_code IN ({string.Join(", ", AdMng.Watchdog.Core.Attribution.SecurityEventCatalog.LogonRecordCodes)}) AND time_created < $cutoff"),
            SqliteValues.ToUnixMs(now - TimeSpan.FromHours(Math.Max(1, hours))),
            cancellationToken);

    private static long Cutoff(DateTimeOffset now, int days) => SqliteValues.ToUnixMs(now.AddDays(-Math.Max(0, days)));

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long FreeSpace(string directory)
    {
        try
        {
            // Windows: the volume root; Unix: statvfs of the folder itself (it may be its own mount).
            return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(directory) ?? directory : directory).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }
}
