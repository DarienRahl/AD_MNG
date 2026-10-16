using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>A row of one event (Czas · Obiekt · Zmiana · Kto); <paramref name="Flagged"/> marks unusual rows (new
/// computers in the default container, created by a non-admin).</summary>
public sealed record DigestEventRow(WatchEvent Event, bool Flagged = false) : DigestRow
{
    /// <inheritdoc/>
    public override DateTimeOffset? Time => Event.OriginatingTime ?? Event.DetectedAt;

    /// <inheritdoc/>
    public override IReadOnlyList<long> EventIds => [Event.Id];
}
