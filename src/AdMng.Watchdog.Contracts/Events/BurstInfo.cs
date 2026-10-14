namespace AdMng.Watchdog.Contracts.Events;

/// <summary>Details of a burst event (MassLockout, MassDeletion, PasswordSprayingSuspected).</summary>
public sealed record BurstInfo
{
    public required int Count { get; init; }
    public required TimeSpan Window { get; init; }
    public IReadOnlyList<ObjectRef> Objects { get; init; } = [];  // capped at 50
}
