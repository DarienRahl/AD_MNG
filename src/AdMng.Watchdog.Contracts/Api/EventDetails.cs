using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>One delivery (outbox message) that contained an event.</summary>
public sealed record EventDeliveryDto
{
    public required long OutboxId { get; init; }
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public required DeliveryKind Kind { get; init; }
    public required OutboxStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? LastErrorCode { get; init; }
}

/// <summary><c>GET /api/v1/events/{id}</c>.</summary>
public sealed record EventDetails
{
    public required WatchEvent Event { get; init; }
    public IReadOnlyList<WatchEvent> RelatedEvents { get; init; } = [];   // same ChangeSetId
    public IReadOnlyList<EventDeliveryDto> Deliveries { get; init; } = [];
}
