using System.Globalization;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Storage;

/// <summary><see cref="IDigestRunStore"/> over <c>digest_runs</c> (period, event count and the outbox ids of each digest).</summary>
public sealed class SqliteDigestRunStore : IDigestRunStore
{
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    internal SqliteDigestRunStore(SqliteDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken)
    {
        var last = _database.Read(c => SqliteValues.Scalar<long?>(c, null, "SELECT max(period_end) FROM digest_runs;"));
        return Task.FromResult(last is { } value ? SqliteValues.FromUnixMs(value) : (DateTimeOffset?)null);
    }

    public Task RecordAsync(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int eventCount,
        IReadOnlyList<long> outboxIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxIds);
        return _database.WriteAsync(
            (c, t) => SqliteValues.Execute(
                c,
                t,
                """
                INSERT INTO digest_runs (period_start, period_end, created_at, event_count, outbox_ids)
                VALUES ($start, $end, $created, $count, $ids);
                """,
                ("$start", SqliteValues.ToUnixMs(periodStart)),
                ("$end", SqliteValues.ToUnixMs(periodEnd)),
                ("$created", SqliteValues.ToUnixMs(_timeProvider.GetUtcNow())),
                ("$count", eventCount),
                ("$ids", string.Join(',', outboxIds.Select(id => id.ToString(CultureInfo.InvariantCulture))))),
            cancellationToken);
    }
}
