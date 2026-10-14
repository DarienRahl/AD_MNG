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
/// <c>incremental_vacuum</c> + <c>wal_checkpoint(TRUNCATE)</c> + <c>optimize</c> (with <c>secure_delete</c> the freed
/// pages and the truncated WAL no longer hold purged values), <c>VACUUM INTO</c> backups and their rotation.
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
        var notices = await DeleteInBatchesAsync(
            "expiry_notices",
            "(object_guid, kind, expires_raw, threshold_days)",
            "expires_raw < $cutoff",
            (now - NoticeRetention).ToFileTime(),
            cancellationToken).ConfigureAwait(false);
        var audit = await DeleteInBatchesAsync("config_audit", "rowid", "at < $cutoff", Cutoff(now, retention.ConfigAuditDays), cancellationToken).ConfigureAwait(false);
        await DeleteInBatchesAsync("digest_runs", "rowid", "created_at < $cutoff", SqliteValues.ToUnixMs(now - DigestRunRetention), cancellationToken).ConfigureAwait(false);

        var cap = await EnforceSizeCapAsync(retention.MaxDatabaseMb, cancellationToken).ConfigureAwait(false);
        await CompactAsync(cancellationToken).ConfigureAwait(false);
        _backups.RotatePreMigrationBackups(TimeSpan.FromDays(Math.Max(0, retention.PreMigrationBackupDays)), now);
        return new PurgeResult(events, securityEvents + cap.SecurityEvents, outbox + cap.OutboxMessages, snapshots, notices, audit);
    }

    public Task<string> BackupAsync(CancellationToken cancellationToken) =>
        _database.ExclusiveAsync(
            connection =>
            {
                _backups.EnsureDirectory();
                var now = _timeProvider.GetUtcNow();
                var path = _backups.NewRetentionBackupPath(now);
                SqliteValues.Execute(connection, null, "VACUUM INTO $path;", ("$path", path));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                MetaRows.Set(connection, null, MetaRows.LastBackupAt, SqliteValues.ToUnixMs(now).ToString(CultureInfo.InvariantCulture));
                _backups.RotateRetentionBackups(Volatile.Read(ref _backupsToKeep));
                return Path.GetFileName(path);
            },
            cancellationToken);

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
            FreeBytes = FreeSpace(_database.DataDirectory),
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

    /// <summary><c>PRAGMA incremental_vacuum; wal_checkpoint(TRUNCATE); optimize;</c> behind the writer gate.</summary>
    public Task CompactAsync(CancellationToken cancellationToken) =>
        _database.ExclusiveAsync(
            connection =>
            {
                SqliteValues.Execute(connection, null, "PRAGMA incremental_vacuum;");
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
            cancellationToken);

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
