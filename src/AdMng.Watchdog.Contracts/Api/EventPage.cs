using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>One page of <c>GET /api/v1/events</c>.</summary>
public sealed record EventPage
{
    public IReadOnlyList<WatchEvent> Items { get; init; } = [];
    public bool HasMore { get; init; }
    public long LatestId { get; init; }        // highest id in the store (polling cursor even when filtered)
}
