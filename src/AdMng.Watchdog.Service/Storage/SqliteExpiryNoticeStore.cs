using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary><see cref="IExpiryNoticeStore"/> over <c>expiry_notices</c> (dedup per object × kind × expiry × threshold) and
/// <c>upcoming_expiries</c> (replaced per kind, listed by expiry).</summary>
public sealed class SqliteExpiryNoticeStore : IExpiryNoticeStore
{
    private readonly SqliteDatabase _database;

    internal SqliteExpiryNoticeStore(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return _database.WriteAsync(
            (c, t) => SqliteValues.Execute(
                c,
                t,
                """
                INSERT OR IGNORE INTO expiry_notices (object_guid, kind, expires_raw, threshold_days, notified_at)
                VALUES ($guid, $kind, $expires, $threshold, $notified);
                """,
                ("$guid", SqliteValues.ToBlob(notice.ObjectGuid)),
                ("$kind", (int)notice.Kind),
                ("$expires", notice.ExpiresAtFileTime),
                ("$threshold", notice.ThresholdDays),
                ("$notified", SqliteValues.ToUnixMs(notice.NotifiedAt))) == 1,
            cancellationToken);
    }

    public Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upcoming);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                SqliteValues.Execute(connection, transaction, "DELETE FROM upcoming_expiries WHERE kind = $kind;", ("$kind", (int)kind));
                foreach (var item in upcoming)
                {
                    SqliteValues.Execute(
                        connection,
                        transaction,
                        "INSERT OR REPLACE INTO upcoming_expiries (kind, object_guid, expires_at, payload) VALUES ($kind, $guid, $expires, $payload);",
                        ("$kind", (int)kind),
                        ("$guid", SqliteValues.ToBlob(item.Object.ObjectGuid ?? Guid.Empty)),
                        ("$expires", SqliteValues.ToUnixMs(item.ExpiresAt)),
                        ("$payload", BlobCodec.Encode(item with { Kind = kind }, WatchdogStorageJsonContext.Default.UpcomingExpiry)));
                }
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UpcomingExpiry>>(_database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT payload FROM upcoming_expiries ORDER BY expires_at, kind;",
            r =>
            {
                var item = BlobCodec.Decode((byte[])r.GetValue(0), WatchdogStorageJsonContext.Default.UpcomingExpiry);
                return item with { Tags = item.Tags ?? [] };
            })));
}
