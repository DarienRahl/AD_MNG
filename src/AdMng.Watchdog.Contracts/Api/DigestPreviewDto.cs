namespace AdMng.Watchdog.Contracts.Api;

/// <summary><c>GET /api/v1/digest/preview</c>: the digest as it would be sent now.</summary>
public sealed record DigestPreviewDto
{
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required string Subject { get; init; }
    public required string Text { get; init; }    // plain-text alternative (shown in the GUI)
    public required string Html { get; init; }
    public int EventCount { get; init; }
    public DateTimeOffset? NextScheduledAt { get; init; }
    public DateTimeOffset? LastSentAt { get; init; }
}
