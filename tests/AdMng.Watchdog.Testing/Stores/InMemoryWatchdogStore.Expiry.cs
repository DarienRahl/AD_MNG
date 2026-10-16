using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// IExpiryNoticeStore + IDigestRunStore: expiry_notices, upcoming_expiries, digest_runs.
public sealed partial class InMemoryWatchdogStore
{
    private readonly Dictionary<(Guid ObjectGuid, ExpiryKind Kind, long ExpiresRaw, int ThresholdDays), DateTimeOffset> _notices = [];
    private readonly Dictionary<(ExpiryKind Kind, Guid ObjectGuid), UpcomingExpiry> _upcoming = [];
    private readonly List<StoredDigestRun> _digestRuns = [];

    // ---------------------------------------------------------------- IExpiryNoticeStore

    public Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_gate)
        {
            return Task.FromResult(_notices.TryAdd((notice.ObjectGuid, notice.Kind, notice.ExpiresAtFileTime, notice.ThresholdDays), notice.NotifiedAt));
        }
    }

    public Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upcoming);
        lock (_gate)
        {
            foreach (var key in _upcoming.Keys.Where(k => k.Kind == kind).ToList())
            {
                _upcoming.Remove(key);
            }

            foreach (var item in upcoming)
            {
                _upcoming[(kind, item.Object.ObjectGuid ?? Guid.Empty)] = Copy(item) with { Kind = kind };
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<UpcomingExpiry> result =
                [.. _upcoming.Values.OrderBy(u => u.ExpiresAt).ThenBy(u => u.Kind).Select(Copy)];
            return Task.FromResult(result);
        }
    }

    // ---------------------------------------------------------------- IDigestRunStore

    public Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_digestRuns.Count == 0 ? null : (DateTimeOffset?)_digestRuns.Max(r => r.PeriodEnd));
        }
    }

    public Task RecordAsync(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int eventCount,
        IReadOnlyList<long> outboxIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxIds);
        lock (_gate)
        {
            _digestRuns.Add(new StoredDigestRun(periodStart, periodEnd, UtcNow, eventCount, [.. outboxIds]));
        }

        return Task.CompletedTask;
    }
}
