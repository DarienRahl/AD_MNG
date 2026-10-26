using System.Runtime.CompilerServices;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>
/// In-memory <see cref="IEventStore"/> for the rules and attribution tests (the shared InMemoryWatchdogStore is
/// W-notify's): ids from 1, rules state, next attribution attempt (= detection time on insert for pending events),
/// recorded evaluations, notifications and attribution updates.
/// </summary>
internal sealed class FakeEventStore : IEventStore
{
    private readonly Lock _gate = new();
    private readonly List<StoredEvent> _events = [];

    public List<EventEvaluation> Evaluations { get; } = [];

    public List<PendingNotification> Notifications { get; } = [];

    public List<(long Id, AttributionUpdate Update)> AttributionUpdates { get; } = [];

    public int CommitCalls { get; private set; }

    /// <summary>Fault injection: an exception returned for an event id is thrown by <see cref="UpdateAttributionAsync"/>
    /// before anything is stored (a store error or a stop in the middle of an attribution pass).</summary>
    public Func<long, Exception?>? UpdateAttributionFailure { get; set; }

    public IReadOnlyList<WatchEvent> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _events.Select(e => e.Event)];
            }
        }
    }

    public WatchEvent Get(long id)
    {
        lock (_gate)
        {
            return _events.Single(e => e.Event.Id == id).Event;
        }
    }

    public DateTimeOffset? NextAttributionAt(long id)
    {
        lock (_gate)
        {
            return _events.Single(e => e.Event.Id == id).NextAttributionAt;
        }
    }

    /// <summary>Adds an event and returns it with its id.</summary>
    public WatchEvent Add(WatchEvent watchEvent, bool evaluated = false)
    {
        lock (_gate)
        {
            var stored = watchEvent with { Id = _events.Count + 1, OwnSeverity = watchEvent.OwnSeverity ?? watchEvent.Severity };
            _events.Add(new StoredEvent
            {
                Event = stored,
                Evaluated = evaluated,
                NextAttributionAt = stored.Attribution.Status == AttributionStatus.Pending ? stored.DetectedAt : null,
            });
            return stored;
        }
    }

    public Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken) => Task.FromResult(Add(watchEvent).Id);

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var items = _events.Select(e => e.Event)
                .Where(e => query.After is null || e.Id > query.After)
                .Where(e => query.After is not null || query.Before is null || e.Id < query.Before)
                .Where(e => query.Kinds.Count == 0 || query.Kinds.Contains(e.Kind))
                .Where(e => query.ObjectGuid is null || e.Object.ObjectGuid == query.ObjectGuid)
                .Where(e => query.From is null || e.DetectedAt >= query.From)
                .Where(e => query.To is null || e.DetectedAt <= query.To)
                .OrderBy(e => query.After is null ? -e.Id : e.Id)
                .Take(query.Limit)
                .ToList();
            return Task.FromResult(new EventPage { Items = items, LatestId = _events.Count });
        }
    }

    public Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.FirstOrDefault(e => e.Event.Id == id)?.Event);
        }
    }

    public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.FirstOrDefault(e => e.Event.EventId == eventId)?.Event);
        }
    }

    public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WatchEvent>>([.. _events.Select(e => e.Event).Where(e => e.ChangeSetId == changeSetId)]);
        }
    }

    public async IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<WatchEvent> snapshot;
        lock (_gate)
        {
            snapshot = [.. _events.Select(e => e.Event).Where(e => e.DetectedAt >= from && e.DetectedAt <= to).OrderBy(e => e.Id)];
        }

        foreach (var watchEvent in snapshot)
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
            return Task.FromResult<IReadOnlyList<WatchEvent>>([.. _events
                .Where(e => e.Event.Attribution.Status == AttributionStatus.Pending && e.NextAttributionAt <= dueBefore)
                .OrderBy(e => e.NextAttributionAt).ThenBy(e => e.Event.Id)
                .Take(limit)
                .Select(e => e.Event)]);
        }
    }

    public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken)
    {
        if (UpdateAttributionFailure?.Invoke(id) is { } failure)
        {
            return Task.FromException(failure);
        }

        // As SqliteDatabase.WriteAsync: a cancelled token fails the write.
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var stored = _events.Single(e => e.Event.Id == id);
            var watchEvent = stored.Event;
            stored.Event = EventSeverities.WithAttribution(watchEvent, update);
            stored.NextAttributionAt = update.NextAttemptAt;
            AttributionUpdates.Add((id, update));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WatchEvent>>([.. _events.Where(e => !e.Evaluated).Take(limit).Select(e => e.Event)]);
        }
    }

    public Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CommitCalls++;
            foreach (var evaluation in evaluations)
            {
                var stored = _events.Single(e => e.Event.Id == evaluation.EventId);
                stored.Evaluated = true;
                stored.Event = stored.Event with { Severity = evaluation.Severity, MatchedRules = evaluation.MatchedRules };
                Evaluations.Add(evaluation);
            }

            Notifications.AddRange(notifications);
        }

        return Task.CompletedTask;
    }

    public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(AcknowledgeOne(id, by, comment, at));

    public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(ids.Count(id => AcknowledgeOne(id, by, comment, at)));

    public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.Count(e => e.Event.Object.ObjectGuid == objectGuid && e.Event.Kind == kind && e.Event.DetectedAt >= since));
        }
    }

    public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var recent = _events.Select(e => e.Event).Where(e => e.DetectedAt >= since).ToList();
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
            return Task.FromResult((long)_events.Count);
        }
    }

    private bool AcknowledgeOne(long id, string by, string? comment, DateTimeOffset at)
    {
        lock (_gate)
        {
            var stored = _events.FirstOrDefault(e => e.Event.Id == id);
            if (stored is null || stored.Event.AcknowledgedAt is not null)
            {
                return false;
            }

            stored.Event = stored.Event with { AcknowledgedAt = at, AcknowledgedBy = by, AcknowledgeComment = comment };
            return true;
        }
    }

    private sealed class StoredEvent
    {
        public required WatchEvent Event { get; set; }

        public bool Evaluated { get; set; }

        public DateTimeOffset? NextAttributionAt { get; set; }
    }
}
