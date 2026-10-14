using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>Lockouts of one account on one local day: "zablokowane 5× dziś, ostatnio 10:42" plus the top bad-password
/// sources (by failure count) and the number of unlocks.</summary>
public sealed record DigestLockoutRow(ObjectRef Account, DateOnly Day, IReadOnlyList<WatchEvent> Lockouts, int Unlocks, IReadOnlyList<LockoutSource> TopSources)
    : DigestRow
{
    /// <summary>Lockouts of the account that day.</summary>
    public int Count => Lockouts.Count;

    /// <summary>The latest lockout.</summary>
    public DateTimeOffset LastAt => Lockouts.Max(e => e.Lockout?.LockedAt ?? e.DetectedAt);

    /// <inheritdoc/>
    public override DateTimeOffset? Time => LastAt;

    /// <inheritdoc/>
    public override IReadOnlyList<long> EventIds => [.. Lockouts.Select(e => e.Id)];
}
