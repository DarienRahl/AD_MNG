namespace AdMng.Watchdog.Contracts.Api;

/// <summary>Delivery health of one channel (SMTP, Teams, webhook).</summary>
public sealed record ChannelHealthDto
{
    public required string ChannelId { get; init; }          // "email" for SMTP
    public required DeliveryChannelType ChannelType { get; init; }
    public required HealthState State { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string? LastErrorCode { get; init; }
    public int ConsecutiveFailures { get; init; }
    public int Pending { get; init; }
}
