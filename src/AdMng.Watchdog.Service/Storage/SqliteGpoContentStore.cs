using System.Globalization;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Gpo;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="IGpoContentStore"/> over <c>gpo_content</c> and <c>gpo_content_queue</c> (migration 0005, stage B design 8.8)
/// and the <c>gpo_content.*</c> keys of <c>meta</c>. Inventory and settings are deflated JSON
/// (<see cref="WatchdogStorageJsonContext"/>); masked settings have no value, so no secret reaches the database
/// (ADR-0008/0009). Content commits append their events in the same transaction (<see cref="EventRows.Insert"/>).
/// </summary>
public sealed class SqliteGpoContentStore : IGpoContentStore
{
    private const string Columns = "gpo_guid, dc, gpc_version, gpt_version, read_at, state, inventory, settings, deleted_at";
    private const string QueueColumns = "gpo_guid, reason, due_at, attempts, object_guid, change_set_id, enqueued_at, first_attempt_at";
    private const string CursorKey = "gpo_content.event_cursor";
    private const string BaselineKey = "gpo_content.baseline_completed_at";
    private const string ReconciliationKey = "gpo_content.last_reconciliation_at";
    private const string LastReadKey = "gpo_content.last_read_at";

    private readonly SqliteDatabase _database;
    private readonly EventAppendedNotifier _notifier;

    internal SqliteGpoContentStore(SqliteDatabase database, EventAppendedNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(notifier);
        _database = database;
        _notifier = notifier;
    }

    public Task<StoredGpoContent?> GetGpoContentAsync(Guid gpoGuid, CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => SqliteValues.Query(c, null, $"SELECT {Columns} FROM gpo_content WHERE gpo_guid = $guid;",
            r => ReadContent(r, includeSettings: true), ("$guid", SqliteValues.ToBlob(gpoGuid))).FirstOrDefault()));

    public Task<IReadOnlyList<StoredGpoContent>> ListGpoContentAsync(bool includeSettings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredGpoContent>>(_database.Read(c => SqliteValues.Query(c, null, $"SELECT {Columns} FROM gpo_content;",
            r => ReadContent(r, includeSettings))).OrderBy(s => s.Snapshot.GpoGuid).ToList());

    public async Task<IReadOnlyList<WatchEvent>> CommitGpoContentAsync(GpoContentSnapshot snapshot, IReadOnlyList<WatchEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);
        var stored = await _database.WriteAsync(
            (connection, transaction) =>
            {
                SqliteValues.Execute(
                    connection,
                    transaction,
                    $"""
                    INSERT OR REPLACE INTO gpo_content ({Columns})
                    VALUES ($guid, $dc, $gpc, $gpt, $readAt, $state, $inventory, $settings, NULL);
                    """,
                    ("$guid", SqliteValues.ToBlob(snapshot.GpoGuid)),
                    ("$dc", snapshot.Dc),
                    ("$gpc", OrNull(snapshot.GpcVersion)),
                    ("$gpt", OrNull(snapshot.GptVersion)),
                    ("$readAt", SqliteValues.ToUnixMs(snapshot.ReadAt)),
                    ("$state", (int)snapshot.State),
                    ("$inventory", BlobCodec.Encode(snapshot.Inventory.ToList(), WatchdogStorageJsonContext.Default.ListGpoInventoryEntry)),
                    ("$settings", BlobCodec.Encode(snapshot.Settings.ToList(), WatchdogStorageJsonContext.Default.ListGpoSetting)));
                return Complete(connection, transaction, snapshot.GpoGuid, events);
            },
            cancellationToken).ConfigureAwait(false);
        if (stored.Count > 0)
        {
            _notifier.Notify();
        }

        return stored;
    }

    public async Task<IReadOnlyList<WatchEvent>> CompleteGpoQueueEntryAsync(Guid gpoGuid, IReadOnlyList<WatchEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var stored = await _database.WriteAsync((connection, transaction) => Complete(connection, transaction, gpoGuid, events), cancellationToken)
            .ConfigureAwait(false);
        if (stored.Count > 0)
        {
            _notifier.Notify();
        }

        return stored;
    }

    public Task MarkGpoContentDeletedAsync(Guid gpoGuid, DateTimeOffset deletedAt, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                SqliteValues.Execute(connection, transaction, "UPDATE gpo_content SET deleted_at = $at WHERE gpo_guid = $guid AND deleted_at IS NULL;",
                    ("$at", SqliteValues.ToUnixMs(deletedAt)), ("$guid", SqliteValues.ToBlob(gpoGuid)));
                RemoveQueued(connection, transaction, gpoGuid);
            },
            cancellationToken);

    public Task EnqueueGpoContentAsync(GpoQueueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                var existing = SqliteValues.Query(connection, transaction, $"SELECT {QueueColumns} FROM gpo_content_queue WHERE gpo_guid = $guid;",
                    ReadQueue, ("$guid", SqliteValues.ToBlob(entry.GpoGuid))).FirstOrDefault();
                WriteQueue(connection, transaction, existing is null ? entry : existing.Merge(entry));
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<GpoQueueEntry>> GetGpoQueueAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GpoQueueEntry>>(_database.Read(c => SqliteValues.Query(c, null,
            $"SELECT {QueueColumns} FROM gpo_content_queue;", ReadQueue)).OrderBy(e => e.DueAt).ThenBy(e => e.GpoGuid).ToList());

    public Task UpdateGpoQueueEntryAsync(GpoQueueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                var exists = SqliteValues.Scalar<long>(connection, transaction, "SELECT COUNT(*) FROM gpo_content_queue WHERE gpo_guid = $guid;",
                    ("$guid", SqliteValues.ToBlob(entry.GpoGuid))) > 0;
                if (exists)
                {
                    WriteQueue(connection, transaction, entry);
                }
            },
            cancellationToken);
    }

    public Task RemoveGpoQueueEntryAsync(Guid gpoGuid, CancellationToken cancellationToken) =>
        _database.WriteAsync((connection, transaction) => RemoveQueued(connection, transaction, gpoGuid), cancellationToken);

    public Task<GpoContentJobState> GetGpoContentStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_database.Read(c => new GpoContentJobState
        {
            EventCursor = long.TryParse(MetaRows.Get(c, null, CursorKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cursor) ? cursor : null,
            BaselineCompletedAt = Time(MetaRows.Get(c, null, BaselineKey)),
            LastReconciliationAt = Time(MetaRows.Get(c, null, ReconciliationKey)),
            LastReadAt = Time(MetaRows.Get(c, null, LastReadKey)),
        }));

    public Task SaveGpoContentStateAsync(GpoContentJobState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                Set(connection, transaction, CursorKey, state.EventCursor?.ToString(CultureInfo.InvariantCulture));
                Set(connection, transaction, BaselineKey, Text(state.BaselineCompletedAt));
                Set(connection, transaction, ReconciliationKey, Text(state.LastReconciliationAt));
                Set(connection, transaction, LastReadKey, Text(state.LastReadAt));
            },
            cancellationToken);
    }

    /// <summary>Removes snapshots of GPOs deleted before <paramref name="cutoff"/> (unix ms); returns the count.</summary>
    internal static int PurgeDeleted(SqliteConnection connection, SqliteTransaction transaction, long cutoff) =>
        SqliteValues.Execute(connection, transaction, "DELETE FROM gpo_content WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff;", ("$cutoff", cutoff));

    private static List<WatchEvent> Complete(SqliteConnection connection, SqliteTransaction transaction, Guid gpoGuid, IReadOnlyList<WatchEvent> events)
    {
        var stored = events.Select(e => EventRows.Insert(connection, transaction, e)).ToList();
        RemoveQueued(connection, transaction, gpoGuid);
        return stored;
    }

    private static void RemoveQueued(SqliteConnection connection, SqliteTransaction transaction, Guid gpoGuid) =>
        SqliteValues.Execute(connection, transaction, "DELETE FROM gpo_content_queue WHERE gpo_guid = $guid;", ("$guid", SqliteValues.ToBlob(gpoGuid)));

    private static void WriteQueue(SqliteConnection connection, SqliteTransaction transaction, GpoQueueEntry entry) =>
        SqliteValues.Execute(
            connection,
            transaction,
            $"""
            INSERT OR REPLACE INTO gpo_content_queue ({QueueColumns})
            VALUES ($guid, $reason, $due, $attempts, $object, $changeSet, $enqueued, $first);
            """,
            ("$guid", SqliteValues.ToBlob(entry.GpoGuid)),
            ("$reason", (int)entry.Reason),
            ("$due", SqliteValues.ToUnixMs(entry.DueAt)),
            ("$attempts", entry.Attempts),
            ("$object", SqliteValues.ToBlobOrNull(entry.ObjectGuid)),
            ("$changeSet", SqliteValues.ToBlobOrNull(entry.ChangeSetId)),
            ("$enqueued", SqliteValues.ToUnixMs(entry.EnqueuedAt)),
            ("$first", SqliteValues.ToUnixMsOrNull(entry.FirstAttemptAt)));

    private static GpoQueueEntry ReadQueue(SqliteDataReader reader) => new()
    {
        GpoGuid = SqliteValues.GuidFrom(reader, 0),
        Reason = (GpoQueueReason)reader.GetInt32(1),
        DueAt = SqliteValues.FromUnixMs(reader.GetInt64(2)),
        Attempts = reader.GetInt32(3),
        ObjectGuid = SqliteValues.GuidOrNull(reader, 4),
        ChangeSetId = SqliteValues.GuidOrNull(reader, 5) ?? Guid.Empty,
        EnqueuedAt = SqliteValues.FromUnixMs(reader.GetInt64(6)),
        FirstAttemptAt = SqliteValues.FromUnixMsOrNull(reader, 7),
    };

    private static StoredGpoContent ReadContent(SqliteDataReader reader, bool includeSettings) => new()
    {
        Snapshot = new GpoContentSnapshot
        {
            GpoGuid = SqliteValues.GuidFrom(reader, 0),
            Dc = reader.GetString(1),
            GpcVersion = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            GptVersion = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            ReadAt = SqliteValues.FromUnixMs(reader.GetInt64(4)),
            State = (GpoContentState)reader.GetInt32(5),
            Inventory = BlobCodec.Decode((byte[])reader.GetValue(6), WatchdogStorageJsonContext.Default.ListGpoInventoryEntry),
            Settings = includeSettings ? BlobCodec.Decode((byte[])reader.GetValue(7), WatchdogStorageJsonContext.Default.ListGpoSetting) : [],
        },
        DeletedAt = SqliteValues.FromUnixMsOrNull(reader, 8),
    };

    private static object OrNull(int? value) => value is { } v ? v : DBNull.Value;

    private static void Set(SqliteConnection connection, SqliteTransaction transaction, string key, string? value)
    {
        if (value is null)
        {
            SqliteValues.Execute(connection, transaction, "DELETE FROM meta WHERE key = $key;", ("$key", key));
        }
        else
        {
            MetaRows.Set(connection, transaction, key, value);
        }
    }

    private static string? Text(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
