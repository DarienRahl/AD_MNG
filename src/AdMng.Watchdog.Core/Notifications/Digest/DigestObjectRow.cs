using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>"Zmiany parametrów kont" grouped per object: every change (attribute old → new, who, when) of one object
/// in the period, oldest first.</summary>
public sealed record DigestObjectRow(ObjectRef Object, IReadOnlyList<WatchEvent> Events) : DigestRow
{
    /// <inheritdoc/>
    public override DateTimeOffset? Time => Events.Count == 0 ? null : Events[^1].OriginatingTime ?? Events[^1].DetectedAt;

    /// <inheritdoc/>
    public override IReadOnlyList<long> EventIds => [.. Events.Select(e => e.Id)];
}
