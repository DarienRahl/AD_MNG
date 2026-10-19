using System.Runtime.CompilerServices;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// IEventStore: events (AUTOINCREMENT ids), rule evaluation, attribution updates, acknowledgement, long-poll.
public sealed partial class InMemoryWatchdogStore
{
    private const int MaxPageSize = 500;

    private readonly SortedDictionary<long, StoredEventRow> _events = [];
    private readonly HashSet<Guid> _eventGuids = [];
    private long _lastEventId;
    private TaskCompletionSource _eventAppended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        cancellationToken.ThrowIfCancellationRequested();
        long id;
        lock (_gate)
        {
            id = InsertEvent(watchEvent).Id;
        }

        SignalAppended(1);
        return Task.FromResult(id);
    }

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = new EventQueryFilter(query);
        var limit = Math.Clamp(query.Limit, 1, MaxPageSize);
        lock (_gate)
        {
            IEnumerable<StoredEventRow> rows = query.After is { } after
                ? _events.Values.Where(r => r.Id > after)
                : _events.Values.Reverse().Where(r => query.Before is not { } before || r.Id < before);
            var matching = rows.Where(r => filter.Matches(r.Event)).Take(limit + 1).ToList();
            return Task.FromResult(new EventPage
            {
                Items = [.. matching.Take(limit).Select(r => RoundTrip(r.Event))],
                HasMore = matching.Count > limit,
                LatestId = LatestEventId(),
            });
        }
    }

    public async Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task appended;
        lock (_gate)
        {
            if (LatestEventId() > afterId)
            {
                return;
            }

            appended = _eventAppended.Task;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, _timeProvider, timer.Token);
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(appended, delay).ConfigureAwait(false);
                if (completed == delay)
                {
                    await delay.ConfigureAwait(false);   // throws when cancelled
                    return;
                }

                lock (_gate)
                {
                    if (LatestEventId() > afterId)
                    {
                        return;
                    }

                    appended = _eventAppended.Task;
                }
            }
        }
        finally
        {
            await timer.CancelAsync().ConfigureAwait(false);
        }
    }

    public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.TryGetValue(id, out var row) ? RoundTrip(row.Event) : null);
        }
    }

    public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var row = _events.Values.FirstOrDefault(r => r.Event.EventId == eventId);
            return Task.FromResult(row is null ? null : RoundTrip(row.Event));
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<WatchEvent> result = changeSetId == Guid.Empty
                ? []
                : [.. _events.Values.Where(r => r.Event.ChangeSetId == changeSetId).Select(r => RoundTrip(r.Event))];
            return Task.FromResult(result);
        }
    }

    public async IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<WatchEvent> result;
        lock (_gate)
        {
            result = [.. _events.Values.Where(r => r.Event.DetectedAt >= from && r.Event.DetectedAt < to).Select(r => RoundTrip(r.Event))];
        }

        foreach (var watchEvent in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return watchEvent;
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<WatchEvent> result =
            [
                .. _events.Values
                    .Where(r => r.Event.Attribution.Status == AttributionStatus.Pending && r.NextAttributionAt is { } next && next <= dueBefore)
                    .OrderBy(r => r.NextAttributionAt).ThenBy(r => r.Id)
                    .Take(Math.Max(0, limit))
                    .Select(r => RoundTrip(r.Event)),
            ];
            return Task.FromResult(result);
        }
    }

    public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (_events.TryGetValue(id, out var row))
            {
                var current = row.Event;
                row.Event = RoundTrip(EventSeverities.WithAttribution(current, update));
                row.NextAttributionAt = update.Attribution.Status == AttributionStatus.Pending ? update.NextAttemptAt : null;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<WatchEvent> result =
                [.. _events.Values.Where(r => !r.Evaluated).Take(Math.Max(0, limit)).Select(r => RoundTrip(r.Event))];
            return Task.FromResult(result);
        }
    }

    public Task CommitEvaluationAsync(
        IReadOnlyList<EventEvaluation> evaluations,
        IReadOnlyList<PendingNotification> notifications,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        ArgumentNullException.ThrowIfNull(notifications);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var evaluation in evaluations)
            {
                if (_events.TryGetValue(evaluation.EventId, out var row))
                {
                    row.Evaluated = true;
                    row.Event = row.Event with { Severity = evaluation.Severity, MatchedRules = [.. evaluation.MatchedRules] };
                }
            }

            InsertPending(notifications);
        }

        return Task.CompletedTask;
    }

    public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(by);
        lock (_gate)
        {
            if (!_events.TryGetValue(id, out var row))
            {
                return Task.FromResult(false);
            }

            Acknowledge(row, by, comment, at);
            return Task.FromResult(true);
        }
    }

    public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(by);
        lock (_gate)
        {
            var count = 0;
            foreach (var id in ids.Distinct())
            {
                if (_events.TryGetValue(id, out var row) && Acknowledge(row, by, comment, at))
                {
                    count++;
                }
            }

            return Task.FromResult(count);
        }
    }

    public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.Values.Count(r =>
                r.Event.Object.ObjectGuid == objectGuid && r.Event.Kind == kind && r.Event.DetectedAt >= since));
        }
    }

    public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var recent = _events.Values.Select(r => r.Event).Where(e => e.DetectedAt >= since).ToList();
            return Task.FromResult(new EventCountsDto
            {
                Total = recent.Count,
                BySeverity = recent.GroupBy(e => e.Severity).ToDictionary(g => g.Key, g => g.Count()),
                Unacknowledged = recent.Count(e => e.AcknowledgedAt is null),
                UnacknowledgedHighOrAbove = recent.Count(e => e.AcknowledgedAt is null && e.Severity >= Severity.High),
            });
        }
    }

    public Task<long> GetLatestIdAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(LatestEventId());
        }
    }

    /// <summary>Inserts one event (caller holds the lock): next AUTOINCREMENT id, unevaluated, attribution due at
    /// detection time when pending, own severity = severity unless given. The EventId is unique.</summary>
    private WatchEvent InsertEvent(WatchEvent watchEvent)
    {
        if (!_eventGuids.Add(watchEvent.EventId))
        {
            throw new InvalidOperationException("An event with this EventId is already stored (UNIQUE event_id).");
        }

        var stored = RoundTrip(watchEvent with { Id = ++_lastEventId, OwnSeverity = watchEvent.OwnSeverity ?? watchEvent.Severity });
        _events[stored.Id] = new StoredEventRow(stored);
        return RoundTrip(stored);
    }

    /// <summary>Wakes the long-poll waiters (outside the lock).</summary>
    private void SignalAppended(int count)
    {
        if (count == 0)
        {
            return;
        }

        TaskCompletionSource appended;
        lock (_gate)
        {
            appended = _eventAppended;
            _eventAppended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        appended.TrySetResult();
    }

    private long LatestEventId() => _events.Count == 0 ? 0 : _events.Keys.Max();

    private static bool Acknowledge(StoredEventRow row, string by, string? comment, DateTimeOffset at)
    {
        if (row.Event.AcknowledgedAt is not null)
        {
            return false;
        }

        row.Event = row.Event with { AcknowledgedAt = at, AcknowledgedBy = by, AcknowledgeComment = comment };
        return true;
    }
}
