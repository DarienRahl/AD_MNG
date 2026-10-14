using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>The <c>snapshot_objects.attributes</c> blob: the parts of a <see cref="SnapshotObject"/> that have no column.</summary>
internal sealed record StoredSnapshotPayload
{
    public IReadOnlyList<string> ObjectClasses { get; init; } = [];

    public Dictionary<string, SnapshotAttribute> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
