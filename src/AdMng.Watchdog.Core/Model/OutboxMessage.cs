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
}

/// <summary>A rendered HTTP payload (Teams card envelope or webhook JSON).</summary>
public sealed record RenderedHttpMessage
{
    public required byte[] Body { get; init; }              // exact bytes to send (signature covers them)
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public string? WebhookEventType { get; init; }
    public string? Title { get; init; }                     // for the delivery log
}

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
    public Severity Severity { get; init; }
    public int Attempts { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset NextAttemptAt { get; init; }
    public override string ToString() => $"OutboxMessage({Id}, {ChannelType}, {ChannelId}, {Kind})";
}
