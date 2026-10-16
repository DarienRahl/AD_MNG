using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Change of one watched attribute between two snapshots (absent side = null).</summary>
internal sealed record AttributeDelta(string Attribute, SnapshotAttribute? Old, SnapshotAttribute? New)
{
    /// <summary>Canonical values added (set semantics).</summary>
    public IReadOnlyList<string> AddedValues =>
        (New?.Values ?? []).Except(Old?.Values ?? [], StringComparer.Ordinal).ToList();

    /// <summary>Canonical values removed (set semantics).</summary>
    public IReadOnlyList<string> RemovedValues =>
        (Old?.Values ?? []).Except(New?.Values ?? [], StringComparer.Ordinal).ToList();

    /// <summary>First canonical value of the old side, or null.</summary>
    public string? OldValue => Old?.Values.Count > 0 ? Old.Values[0] : null;

    /// <summary>First canonical value of the new side, or null.</summary>
    public string? NewValue => New?.Values.Count > 0 ? New.Values[0] : null;
}

/// <summary>Difference of two snapshots of one object (section 10.6): DN change plus attribute deltas.</summary>
internal sealed record RawObjectDiff
{
    public required SnapshotObject Old { get; init; }
    public required SnapshotObject New { get; init; }
    /// <summary>The old DN after this cycle's container rewrites (10.7).</summary>
    public required string OldDn { get; init; }
    public bool Moved { get; init; }
    public bool Renamed { get; init; }
    public IReadOnlyList<AttributeDelta> Attributes { get; init; } = [];

    public bool IsEmpty => !Moved && !Renamed && Attributes.Count == 0;

    /// <summary>The delta of an attribute, or null.</summary>
    public AttributeDelta? Get(string attribute) =>
        Attributes.FirstOrDefault(a => a.Attribute.Equals(attribute, StringComparison.OrdinalIgnoreCase));
}
