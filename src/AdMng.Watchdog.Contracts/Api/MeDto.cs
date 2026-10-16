using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Contracts.Api;

public enum WatchdogRole { Viewer, Admin }

/// <summary><c>GET /api/v1/me</c>: the caller as the service sees it.</summary>
public sealed record MeDto
{
    public required string Account { get; init; }
    public string? Sid { get; init; }
    public required string AuthenticationType { get; init; }   // always "Kerberos" (other identities are rejected)
    public IReadOnlyList<WatchdogRole> Roles { get; init; } = [];
    /// <summary>Detail level applied to API responses for this caller (Admin: Full; Viewer: Authorization.ViewerDetailLevel).</summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Full;
}
