using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>The runtime configuration with its version (<c>GET/PUT /api/v1/config</c>).</summary>
public sealed record ConfigEnvelope
{
    public required long Version { get; init; }               // optimistic concurrency; also sent as ETag
    public required WatchdogConfiguration Configuration { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    /// <summary>True for Viewer responses: exclusions, recipient addresses, SMTP identity and channel URL hints removed
    /// (ConfigRedactor). A redacted envelope can never be PUT.</summary>
    public bool IsRedacted { get; init; }
}
