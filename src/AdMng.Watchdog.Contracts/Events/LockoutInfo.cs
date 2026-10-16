namespace AdMng.Watchdog.Contracts.Events;

/// <summary>Where the bad passwords came from (4740 caller, 4771 client address, 4776 workstation, 4625).</summary>
public sealed record LockoutSource(string? IpAddress, string? Workstation, int Count, int EventCode);

/// <summary>Details of an AccountLockedOut event.</summary>
public sealed record LockoutInfo
{
    public required DateTimeOffset LockedAt { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }   // null = until an admin unlocks; effective duration incl. PSO
    public string? CallerComputer { get; init; }        // from 4740 (client-supplied; may be empty or a front-end server)
    /// <summary>Aggregated failed-logon sources in [LockedAt − lockOutObservationWindow, LockedAt] (≤ 10, by count).</summary>
    public IReadOnlyList<LockoutSource> Sources { get; init; } = [];
    /// <summary>Lockouts of the same account since local midnight, including this one (digest roll-up, repeat suppression).</summary>
    public int LockoutsToday { get; init; } = 1;
}
