using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary><see cref="INotificationQueueStore"/> over <c>pending_notifications</c>; completing a bucket deletes its rows
/// and enqueues the rendered message in the same transaction; a re-planned target updates its waiting row in place
/// (<c>ON CONFLICT (event_id, target_key, is_follow_up) DO UPDATE</c>).</summary>
public sealed class SqliteNotificationQueueStore : INotificationQueueStore
{
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    internal SqliteNotificationQueueStore(SqliteDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingNotification>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT {PendingRows.Columns} FROM pending_notifications ORDER BY id;",
            PendingRows.Read)));

    public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        return _database.WriteAsync((c, t) => PendingRows.Insert(c, t, notifications, _timeProvider.GetUtcNow()), cancellationToken);
    }

    public Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                if (message is not null)
                {
                    OutboxRows.Insert(connection, transaction, message);
                }

                DeleteRows(connection, transaction, pendingIds);
            },
            cancellationToken);
    }

    public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        return _database.WriteAsync((c, t) => DeleteRows(c, t, pendingIds), cancellationToken);
    }

    public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = SqliteValues.Command(connection, transaction, "UPDATE pending_notifications SET due_at = $due WHERE id = $id;");
                command.Parameters.AddWithValue("$due", SqliteValues.ToUnixMs(dueAt));
                var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                foreach (var pendingId in pendingIds)
                {
                    id.Value = pendingId;
                    command.ExecuteNonQuery();
                }
            },
            cancellationToken);
    }

    public Task ReplanAsync(IReadOnlyList<PendingNotification> notifications, bool insertMissing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        return _database.WriteAsync((c, t) => PendingRows.Replan(c, t, notifications, insertMissing, _timeProvider.GetUtcNow()), cancellationToken);
    }

    private static void DeleteRows(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, IReadOnlyList<long> ids)
    {
        using var command = SqliteValues.Command(connection, transaction, "DELETE FROM pending_notifications WHERE id = $id;");
        var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var pendingId in ids)
        {
            id.Value = pendingId;
            command.ExecuteNonQuery();
        }
    }
}
