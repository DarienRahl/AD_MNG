using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>A rendered e-mail (alert, digest, follow-up, test).</summary>
public sealed record RenderedEmail
{
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }
    public required string TextBody { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>A message to a user (<see cref="DeliveryKind.UserMessage"/>): its own header From, Reply-To, To and CC
    /// instead of <see cref="OutboxMessage.Recipients"/>. Kept only in the payload, which is dropped once sent.</summary>
    public UserMessageEnvelope? UserMessage { get; init; }
}

/// <summary>Addresses of a message to a user (README U1b) – the outbox payload only; the envelope sender is always the
/// SMTP channel's <c>smtp.from</c>.</summary>
public sealed record UserMessageEnvelope
{
    public required UserMessageMailbox From { get; init; }
    public UserMessageMailbox? ReplyTo { get; init; }
    public IReadOnlyList<UserMessageMailbox> To { get; init; } = [];
    public IReadOnlyList<UserMessageMailbox> Cc { get; init; } = [];
    public int RecipientCount => To.Count + Cc.Count;
    public override string ToString() => $"UserMessageEnvelope({RecipientCount} recipients)";
}

/// <summary>A checked address with an optional display name.</summary>
public sealed record UserMessageMailbox
{
    public required string Address { get; init; }
    public string? DisplayName { get; init; }
    public override string ToString() => "UserMessageMailbox";
}

/// <summary>A rendered HTTP payload (Teams card envelope or webhook JSON).</summary>
public sealed record RenderedHttpMessage
{
    public required byte[] Body { get; init; }              // exact bytes to send (signature covers them)
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public string? WebhookEventType { get; init; }
    public string? Title { get; init; }                     // for the delivery log
}

/// <summary>How one event was shown in an alert: its notification severity and whether its actor was still pending.</summary>
/// <param name="EventId">The event (store id).</param>
/// <param name="Severity">The severity the event was rendered with (an aggregated alert's events may differ).</param>
/// <param name="PendingAttribution">The event's attribution was still pending when the alert was rendered.</param>
public sealed record OutboxEventState(long EventId, Severity Severity, bool PendingAttribution);

/// <summary>One message to deliver over one channel (the outbox row).</summary>
public sealed record OutboxMessage
{
    public long Id { get; init; }
    public required string MessageId { get; init; }        // Guid v7 "N"; webhook-id / Message-ID local part
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }
    /// <summary>NotificationTarget.Key of the pending rows it was rendered from (follow-ups reuse the target).</summary>
    public string? TargetKey { get; init; }
    /// <summary>At least one event was rendered with attribution Pending (FollowUpPlanner input).</summary>
    public bool SentWithPendingAttribution { get; init; }
    public required DeliveryKind Kind { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public RenderedEmail? Email { get; init; }
    public RenderedHttpMessage? Http { get; init; }
    public string? RuleId { get; init; }
    public IReadOnlyList<long> EventIds { get; init; } = [];
    /// <summary>Alerts: per linked event, the severity it was shown with and whether its actor was still pending – what
    /// the recipients know about that event (13.2 #1 coverage of a later plan for the same event and target). Empty for
    /// other kinds and for alerts stored before schema 3: <see cref="Severity"/> and <see cref="SentWithPendingAttribution"/>
    /// then stand for every event.</summary>
    public IReadOnlyList<OutboxEventState> EventStates { get; init; } = [];
    /// <summary>The highest severity of the message's events.</summary>
    public Severity Severity { get; init; }
    public int Attempts { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset NextAttemptAt { get; init; }
    public override string ToString() => $"OutboxMessage({Id}, {ChannelType}, {ChannelId}, {Kind})";
}
