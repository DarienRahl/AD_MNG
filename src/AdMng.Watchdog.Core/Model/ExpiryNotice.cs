using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>Dedup record of one expiry threshold notification.</summary>
public sealed record ExpiryNotice(Guid ObjectGuid, ExpiryKind Kind, long ExpiresAtFileTime, int ThresholdDays, DateTimeOffset NotifiedAt);

/// <summary>An upcoming expiry (digest section "wygasające").</summary>
public sealed record UpcomingExpiry
{
    public required ObjectRef Object { get; init; }
    public required ExpiryKind Kind { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
