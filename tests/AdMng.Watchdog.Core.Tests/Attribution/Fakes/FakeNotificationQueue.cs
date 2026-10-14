using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>Records follow-up rows added by attribution; the flush members (W-notify's) are not needed and throw.</summary>
internal sealed class FakeNotificationQueue : INotificationQueueStore
{
    private readonly Lock _gate = new();
    private readonly List<PendingNotification> _added = [];

    public IReadOnlyList<PendingNotification> Added
    {
        get
        {
            lock (_gate)
            {
                return [.. _added];
            }
        }
    }

    public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _added.AddRange(notifications);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(Added);

    public Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken) => throw new NotSupportedException();
}
