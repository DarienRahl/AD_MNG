using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// INotificationQueueStore + IOutboxStore: pending_notifications, outbox, outbox_events.
public sealed partial class InMemoryWatchdogStore
{
    private readonly SortedDictionary<long, PendingNotification> _pending = [];
    private readonly SortedDictionary<long, StoredOutboxRow> _outbox = [];
    private long _lastPendingId;
    private long _lastOutboxId;

    // ---------------------------------------------------------------- INotificationQueueStore

    public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<PendingNotification> result = [.. _pending.Values.Select(Copy)];
            return Task.FromResult(result);
        }
    }

    public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            InsertPending(notifications);
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (message is not null)
            {
                InsertOutbox(message);
            }

            foreach (var id in pendingIds)
            {
                _pending.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        lock (_gate)
        {
            foreach (var id in pendingIds)
            {
                _pending.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        lock (_gate)
        {
            foreach (var id in pendingIds)
            {
                if (_pending.TryGetValue(id, out var row))
                {
                    _pending[id] = row with { DueAt = dueAt };
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Inserts pending rows (caller holds the lock); duplicates by (event, target key, follow-up) – stored or
    /// within the batch – are ignored; an unknown event id violates the foreign key.</summary>
    private void InsertPending(IReadOnlyList<PendingNotification> notifications)
    {
        foreach (var notification in notifications)
        {
            if (!_events.ContainsKey(notification.EventId))
            {
                throw new InvalidOperationException("A pending notification references an event that is not stored (foreign key).");
            }
        }

        foreach (var notification in notifications)
        {
            var key = notification.Target.Key;
            if (_pending.Values.Any(p => p.EventId == notification.EventId && p.IsFollowUp == notification.IsFollowUp && p.Target.Key == key))
            {
                continue;
            }

            var id = ++_lastPendingId;
            _pending[id] = Copy(notification) with
            {
                Id = id,
                CreatedAt = notification.CreatedAt == default ? UtcNow : notification.CreatedAt,
            };
        }
    }

    // ---------------------------------------------------------------- IOutboxStore

    public Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(InsertOutbox(message));
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var due = _outbox.Values
                .Where(r => (r.Status is OutboxStatus.Pending or OutboxStatus.Retrying && r.Message.NextAttemptAt <= now)
                    || (r.Status == OutboxStatus.Sending && r.LeaseUntil is { } until && until <= now))
                .OrderBy(r => r.Message.NextAttemptAt).ThenBy(r => r.Id)
                .Take(Math.Max(0, limit))
                .ToList();
            foreach (var row in due)
            {
                row.Status = OutboxStatus.Sending;
                row.LeaseUntil = now + lease;
            }

            IReadOnlyList<OutboxMessage> result = [.. due.Select(r => Copy(r.ToMessage()))];
            return Task.FromResult(result);
        }
    }

    public Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_outbox.TryGetValue(id, out var row))
            {
                row.Status = OutboxStatus.Sent;
                row.SentAt = at;
                row.LeaseUntil = null;
                row.Message = row.Message with { Attempts = row.Message.Attempts + 1, Email = null, Http = null };
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errorCode);
        lock (_gate)
        {
            if (_outbox.TryGetValue(id, out var row))
            {
                row.Status = OutboxStatus.Retrying;
                row.LeaseUntil = null;
                row.LastErrorCode = errorCode;
                row.LastError = error;
                row.Message = row.Message with
                {
                    NextAttemptAt = nextAttemptAt,
                    Attempts = row.Message.Attempts + (countAttempt ? 1 : 0),
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errorCode);
        lock (_gate)
        {
            if (_outbox.TryGetValue(id, out var row))
            {
                row.Status = OutboxStatus.Dead;
                row.LeaseUntil = null;
                row.LastErrorCode = errorCode;
                row.LastError = error;
                row.Message = row.Message with { Attempts = row.Message.Attempts + 1 };
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_outbox.TryGetValue(id, out var row) || row.Status != OutboxStatus.Dead)
            {
                return Task.FromResult(false);
            }

            row.Status = OutboxStatus.Pending;
            row.LeaseUntil = null;
            row.Message = row.Message with { NextAttemptAt = at, Attempts = 0 };
            return Task.FromResult(true);
        }
    }

    public Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, MaxPageSize);
        lock (_gate)
        {
            var rows = _outbox.Values.Reverse()
                .Where(r => query.Before is not { } before || r.Id < before)
                .Where(r => query.Status is not { } status || r.Status == status)
                .Where(r => query.ChannelId is null || string.Equals(r.Message.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
                .Take(limit + 1)
                .ToList();
            return Task.FromResult(new OutboxPage
            {
                Items = [.. rows.Take(limit).Select(ToItem)],
                HasMore = rows.Count > limit,
            });
        }
    }

    public Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<EventDeliveryDto> result =
            [
                .. _outbox.Values.Where(r => r.EventIds.Contains(eventId)).Select(r => new EventDeliveryDto
                {
                    OutboxId = r.Id,
                    ChannelType = r.Message.ChannelType,
                    ChannelId = r.Message.ChannelId,
                    Kind = r.Message.Kind,
                    Status = r.Status,
                    CreatedAt = r.Message.CreatedAt,
                    SentAt = r.SentAt,
                    LastErrorCode = r.LastErrorCode,
                }),
            ];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxMessage> result =
                [.. _outbox.Values.Where(r => r.EventIds.Contains(eventId)).Select(r => Copy(r.ToMessage()))];
            return Task.FromResult(result);
        }
    }

    public Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        lock (_gate)
        {
            var notified = _outbox.Values.Any(r =>
                r.Message.Kind is DeliveryKind.Alert or DeliveryKind.FollowUp or DeliveryKind.Health
                && string.Equals(r.Message.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)
                && r.Message.CreatedAt >= since
                && r.EventIds.Any(id => _events.TryGetValue(id, out var row) && row.Event.Object.ObjectGuid == objectGuid));
            return Task.FromResult(notified);
        }
    }

    public Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        lock (_gate)
        {
            return Task.FromResult(_outbox.Values.Count(r =>
                r.Status == OutboxStatus.Sent && r.SentAt >= since
                && string.Equals(r.Message.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                && (ruleId is null || string.Equals(r.Message.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))));
        }
    }

    public Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, int> result = _outbox.Values
                .Where(r => r.IsWaiting)
                .GroupBy(r => r.Message.ChannelId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(result);
        }
    }

    /// <summary>Inserts one message (caller holds the lock): next id, Pending, unique MessageId, links to its stored
    /// events.</summary>
    private long InsertOutbox(OutboxMessage message)
    {
        if (_outbox.Values.Any(r => string.Equals(r.Message.MessageId, message.MessageId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("An outbox message with this MessageId is already stored (UNIQUE message_id).");
        }

        if (message.EventIds.Any(e => !_events.ContainsKey(e)))
        {
            throw new InvalidOperationException("An outbox message references an event that is not stored (foreign key).");
        }

        var id = ++_lastOutboxId;
        _outbox[id] = new StoredOutboxRow(Copy(message) with { Id = id });
        return id;
    }

    private static OutboxItemDto ToItem(StoredOutboxRow row) => new()
    {
        Id = row.Id,
        MessageId = row.Message.MessageId,
        ChannelType = row.Message.ChannelType,
        ChannelId = row.Message.ChannelId,
        Kind = row.Message.Kind,
        Status = row.Status,
        Attempts = row.Message.Attempts,
        CreatedAt = row.Message.CreatedAt,
        NextAttemptAt = row.IsWaiting ? row.Message.NextAttemptAt : null,
        SentAt = row.SentAt,
        LastErrorCode = row.LastErrorCode,
        LastError = row.LastError,
        Subject = row.Subject,
        EventCount = row.EventIds.Count,
        RecipientCount = row.Message.Recipients.Count,
    };
}
