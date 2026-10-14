using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>Base for fault-injecting event stores: every member delegates to a <see cref="FakeEventStore"/> and can be
/// overridden.</summary>
internal abstract class DelegatingEventStore : IEventStore
{
    protected FakeEventStore Inner { get; } = new();

    public virtual Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken) => Inner.AppendAsync(watchEvent, cancellationToken);

    public virtual Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken) => Inner.QueryAsync(query, cancellationToken);

    public virtual Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken) =>
        Inner.WaitForNewerAsync(afterId, timeout, cancellationToken);

    public virtual Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken) => Inner.GetAsync(id, cancellationToken);

    public virtual Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken) => Inner.GetByEventIdAsync(eventId, cancellationToken);

    public virtual Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken) =>
        Inner.GetChangeSetAsync(changeSetId, cancellationToken);

    public virtual IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Inner.GetRangeAsync(from, to, cancellationToken);

    public virtual Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken) =>
        Inner.GetPendingAttributionAsync(dueBefore, limit, cancellationToken);

    public virtual Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken) =>
        Inner.UpdateAttributionAsync(id, update, cancellationToken);

    public virtual Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
        Inner.GetUnevaluatedAsync(limit, cancellationToken);

    public virtual Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) =>
        Inner.CommitEvaluationAsync(evaluations, notifications, cancellationToken);

    public virtual Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        Inner.AcknowledgeAsync(id, by, comment, at, cancellationToken);

    public virtual Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
        Inner.AcknowledgeManyAsync(ids, by, comment, at, cancellationToken);

    public virtual Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken) =>
        Inner.CountForObjectAsync(objectGuid, kind, since, cancellationToken);

    public virtual Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken) => Inner.CountAsync(since, cancellationToken);

    public virtual Task<long> GetLatestIdAsync(CancellationToken cancellationToken) => Inner.GetLatestIdAsync(cancellationToken);
}
