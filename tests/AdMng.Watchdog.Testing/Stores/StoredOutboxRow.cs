using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>One row of the <c>outbox</c> table of <see cref="InMemoryWatchdogStore"/>: the message (payload set to
/// null after a successful send) plus the delivery columns.</summary>
internal sealed class StoredOutboxRow(OutboxMessage message)
{
    public OutboxMessage Message { get; set; } = message;

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastError { get; set; }

    /// <summary>The <c>subject</c> column: e-mail subject or card/webhook title (kept after the payload is cleared).</summary>
    public string? Subject { get; } = message.Email?.Subject ?? message.Http?.Title;

    /// <summary>The <c>outbox_events</c> links (removed with their events).</summary>
    public HashSet<long> EventIds { get; } = [.. message.EventIds];

    public long Id => Message.Id;

    /// <summary>The message as the store returns it: current attempts and links.</summary>
    public OutboxMessage ToMessage() => Message with { EventIds = [.. EventIds.Order()] };

    public bool IsWaiting => Status is OutboxStatus.Pending or OutboxStatus.Sending or OutboxStatus.Retrying;
}
